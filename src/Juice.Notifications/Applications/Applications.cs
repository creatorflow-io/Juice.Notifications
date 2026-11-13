using System.Text.RegularExpressions;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Applications
{
    /// <summary>An application as the API shows it.</summary>
    public sealed record ApplicationView(Guid Id, string Key, string Name, string ClientId, string? BrokerUser, bool Enabled, IReadOnlyList<Guid> Tenants,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    /// <summary>What a key, name or client id may be.</summary>
    public static partial class ApplicationRules
    {
        /// <summary>A type namespace: lowercase, starts with a letter, 2–63 of <c>[a-z0-9-]</c>.</summary>
        [GeneratedRegex(@"^[a-z][a-z0-9-]{1,62}\z")]
        public static partial Regex Key();

        [GeneratedRegex(@"^[\x21-\x7E]{1,200}\z")]
        private static partial Regex ClientIdPattern();

        public static string? CheckKey(string? key)
            => key is not null && Key().IsMatch(key) ? null : "The key is 2–63 lowercase letters, digits or hyphens, starting with a letter.";

        public static string? CheckName(string? name)
            => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 200 ? null : "The name is 1–200 characters.";

        public static string? CheckClientId(string? clientId)
            => clientId is not null && ClientIdPattern().IsMatch(clientId) ? null : "The client id is 1–200 printable characters without spaces.";

        public static string? CheckBrokerUser(string? brokerUser)
            => brokerUser is not null && ClientIdPattern().IsMatch(brokerUser) ? null : "The broker user is 1–200 printable characters without spaces.";
    }

    // Commands are Juice messages (MessageBase), whose own TenantId is the messaging tenant; the tenant a command acts on is named NotificationTenantId.

    // Races between these commands are answered by the API from the constraint that stopped them (ApplicationEndpoints); nothing re-runs.

    /// <summary>
    /// Registers an application. Losing a race on the key, client id or broker user answers <c>application_exists</c>, <c>client_id_in_use</c> or
    /// <c>broker_user_in_use</c>.
    /// </summary>
    public sealed record CreateApplication(string Key, string Name, string ClientId, string? BrokerUser = null) : MessageBase, IRequest<Outcome<ApplicationView>>;

    /// <summary>Changes the fields given. <see cref="BrokerUser"/> <c>""</c> removes it: the application then sends over HTTP only.</summary>
    public sealed record UpdateApplication(string Key, string? Name, string? ClientId, bool? Enabled, string? BrokerUser = null)
        : MessageBase, IRequest<Outcome<ApplicationView>>;

    /// <summary>Lets the application send for the tenant. Linking twice, or twice at once, is fine.</summary>
    public sealed record LinkTenant(string Key, Guid NotificationTenantId) : MessageBase, IRequest<Outcome<ApplicationView>>;

    /// <summary>
    /// Stops the application sending for the tenant; unlinking twice, or twice at once, is fine. Refused once the application has sent for it: its
    /// requests reference the link. A request accepted while the unlink runs makes the delete fail on that reference, which the API answers as tenant_in_use.
    /// </summary>
    public sealed record UnlinkTenant(string Key, Guid NotificationTenantId) : MessageBase, IRequest<Outcome<ApplicationView>>;

    internal static class ApplicationQueries
    {
        public static async Task<ApplicationView> ViewAsync(NotificationsDbContext db, Application app, CancellationToken cancellationToken)
        {
            var saved = await db.ApplicationTenants.AsNoTracking().Where(t => t.ApplicationId == app.Id).Select(t => t.TenantId).ToListAsync(cancellationToken);
            // Links added or removed in this unit of work but not saved yet, so a command's result shows its own change.
            var added = db.ChangeTracker.Entries<ApplicationTenant>().Where(e => e.Entity.ApplicationId == app.Id && e.State == EntityState.Added).Select(e => e.Entity.TenantId);
            var removed = db.ChangeTracker.Entries<ApplicationTenant>().Where(e => e.Entity.ApplicationId == app.Id && e.State == EntityState.Deleted).Select(e => e.Entity.TenantId).ToHashSet();
            var tenants = saved.Concat(added).Where(t => !removed.Contains(t)).Distinct().Order().ToList();
            return new ApplicationView(app.Id, app.Key, app.Name, app.ClientId, app.BrokerUser, app.Enabled, tenants, app.CreatedAt, app.UpdatedAt);
        }
    }

    internal sealed class ApplicationHandlers(NotificationsDbContext db, TimeProvider clock)
        : IRequestHandler<CreateApplication, Outcome<ApplicationView>>, IRequestHandler<UpdateApplication, Outcome<ApplicationView>>,
          IRequestHandler<LinkTenant, Outcome<ApplicationView>>, IRequestHandler<UnlinkTenant, Outcome<ApplicationView>>
    {
        public async ValueTask<Outcome<ApplicationView>> Handle(CreateApplication request, CancellationToken cancellationToken)
        {
            if ((ApplicationRules.CheckKey(request.Key) ?? ApplicationRules.CheckName(request.Name) ?? ApplicationRules.CheckClientId(request.ClientId)
                ?? (request.BrokerUser is null ? null : ApplicationRules.CheckBrokerUser(request.BrokerUser))) is { } invalid)
            {
                return Outcome<ApplicationView>.Invalid(ErrorCodes.InvalidApplication, invalid);
            }
            // One query for all three, so an identical create committing between two checks can't make this answer client_id_in_use for what is
            // really the same application: the key is reported first, as when they are checked one after the other at rest.
            var taken = await db.Applications
                .Where(a => a.Key == request.Key || a.ClientId == request.ClientId || (request.BrokerUser != null && a.BrokerUser == request.BrokerUser))
                .Select(a => new { a.Key, a.ClientId }).ToListAsync(cancellationToken);
            if (taken.Any(a => a.Key == request.Key))
            {
                return Outcome<ApplicationView>.Conflict(ErrorCodes.ApplicationExists, $"An application with key '{request.Key}' already exists.");
            }
            if (taken.Any(a => a.ClientId == request.ClientId))
            {
                return Outcome<ApplicationView>.Conflict(ErrorCodes.ClientIdInUse, "Another application already uses this client id.");
            }
            if (taken.Count > 0)
            {
                return Outcome<ApplicationView>.Conflict(ErrorCodes.BrokerUserInUse, "Another application already publishes as this broker user.");
            }
            var now = clock.UtcNow();
            var app = new Application { Key = request.Key, Name = request.Name.Trim(), ClientId = request.ClientId, BrokerUser = request.BrokerUser, CreatedAt = now,
                UpdatedAt = now };
            db.Applications.Add(app);
            // A new application has no tenants yet.
            return Outcome<ApplicationView>.Created(new ApplicationView(app.Id, app.Key, app.Name, app.ClientId, app.BrokerUser, app.Enabled, [], app.CreatedAt, app.UpdatedAt));
        }

        public async ValueTask<Outcome<ApplicationView>> Handle(UpdateApplication request, CancellationToken cancellationToken)
        {
            var app = await db.Applications.SingleOrDefaultAsync(a => a.Key == request.Key, cancellationToken);
            if (app is null)
            {
                return Outcome<ApplicationView>.NotFound($"There is no application '{request.Key}'.");
            }
            if (request.Name is not null && ApplicationRules.CheckName(request.Name) is { } badName)
            {
                return Outcome<ApplicationView>.Invalid(ErrorCodes.InvalidApplication, badName);
            }
            if (request.ClientId is not null)
            {
                if (ApplicationRules.CheckClientId(request.ClientId) is { } badClient)
                {
                    return Outcome<ApplicationView>.Invalid(ErrorCodes.InvalidApplication, badClient);
                }
                if (request.ClientId != app.ClientId && await db.Applications.AnyAsync(a => a.ClientId == request.ClientId, cancellationToken))
                {
                    return Outcome<ApplicationView>.Conflict(ErrorCodes.ClientIdInUse, "Another application already uses this client id.");
                }
            }
            // "" removes it; anything else is a broker user no other application has.
            var brokerUser = request.BrokerUser == "" ? null : request.BrokerUser;
            if (brokerUser is not null)
            {
                if (ApplicationRules.CheckBrokerUser(brokerUser) is { } badBroker)
                {
                    return Outcome<ApplicationView>.Invalid(ErrorCodes.InvalidApplication, badBroker);
                }
                if (brokerUser != app.BrokerUser && await db.Applications.AnyAsync(a => a.BrokerUser == brokerUser, cancellationToken))
                {
                    return Outcome<ApplicationView>.Conflict(ErrorCodes.BrokerUserInUse, "Another application already publishes as this broker user.");
                }
            }
            var changed = false;
            if (request.BrokerUser is not null && brokerUser != app.BrokerUser)
            {
                app.BrokerUser = brokerUser;
                changed = true;
            }
            if (request.ClientId is not null && request.ClientId != app.ClientId)
            {
                app.ClientId = request.ClientId;
                changed = true;
            }
            if (request.Name is not null && request.Name.Trim() != app.Name)
            {
                app.Name = request.Name.Trim();
                changed = true;
            }
            if (request.Enabled is { } enabled && enabled != app.Enabled)
            {
                app.Enabled = enabled;
                changed = true;
            }
            if (changed)
            {
                // Only a real change moves UpdatedAt, so consumers that watch it see no change that didn't happen.
                app.UpdatedAt = clock.UtcNow();
            }
            return Outcome<ApplicationView>.Ok(await ApplicationQueries.ViewAsync(db, app, cancellationToken));
        }

        public async ValueTask<Outcome<ApplicationView>> Handle(LinkTenant request, CancellationToken cancellationToken)
        {
            // Input first: a bad id is a 400 whether or not the key exists, and costs no query.
            if (request.NotificationTenantId == Guid.Empty)
            {
                return Outcome<ApplicationView>.Invalid(ErrorCodes.InvalidTenant, "The tenant id can't be empty.");
            }
            var app = await db.Applications.SingleOrDefaultAsync(a => a.Key == request.Key, cancellationToken);
            if (app is null)
            {
                return Outcome<ApplicationView>.NotFound($"There is no application '{request.Key}'.");
            }
            // Two identical links at once both pass this check; the second commit hits the link's key, and the API answers it as the first (200,
            // read fresh). The write stays a tracked change, saved and committed by the transaction behavior.
            if (!await db.ApplicationTenants.AnyAsync(t => t.ApplicationId == app.Id && t.TenantId == request.NotificationTenantId, cancellationToken))
            {
                db.ApplicationTenants.Add(new ApplicationTenant { ApplicationId = app.Id, TenantId = request.NotificationTenantId, LinkedAt = clock.UtcNow() });
            }
            return Outcome<ApplicationView>.Ok(await ApplicationQueries.ViewAsync(db, app, cancellationToken));
        }

        public async ValueTask<Outcome<ApplicationView>> Handle(UnlinkTenant request, CancellationToken cancellationToken)
        {
            // Input first: a bad id is a 400 whether or not the key exists, and costs no query.
            if (request.NotificationTenantId == Guid.Empty)
            {
                return Outcome<ApplicationView>.Invalid(ErrorCodes.InvalidTenant, "The tenant id can't be empty.");
            }
            var app = await db.Applications.SingleOrDefaultAsync(a => a.Key == request.Key, cancellationToken);
            if (app is null)
            {
                return Outcome<ApplicationView>.NotFound($"There is no application '{request.Key}'.");
            }
            var link = await db.ApplicationTenants.SingleOrDefaultAsync(t => t.ApplicationId == app.Id && t.TenantId == request.NotificationTenantId, cancellationToken);
            if (link is not null)
            {
                if (await db.Requests.AnyAsync(r => r.ApplicationId == app.Id && r.TenantId == request.NotificationTenantId, cancellationToken))
                {
                    return Outcome<ApplicationView>.Conflict(ErrorCodes.TenantInUse,
                        "The application has sent notifications for this tenant, so the link stays. Disable the application to stop it sending.");
                }
                db.ApplicationTenants.Remove(link);
            }
            return Outcome<ApplicationView>.Ok(await ApplicationQueries.ViewAsync(db, app, cancellationToken));
        }
    }

    /// <summary>Reads, outside any command.</summary>
    public sealed class ApplicationReader(NotificationsDbContext db)
    {
        public async Task<IReadOnlyList<ApplicationView>> ListAsync(CancellationToken cancellationToken)
        {
            // One query: each application with its own tenant ids.
            var rows = await db.Applications.AsNoTracking().OrderBy(a => a.Key)
                .Select(a => new { App = a, Tenants = db.ApplicationTenants.Where(t => t.ApplicationId == a.Id).OrderBy(t => t.TenantId).Select(t => t.TenantId).ToList() })
                .ToListAsync(cancellationToken);
            return rows.Select(r => new ApplicationView(r.App.Id, r.App.Key, r.App.Name, r.App.ClientId, r.App.BrokerUser, r.App.Enabled, r.Tenants, r.App.CreatedAt, r.App.UpdatedAt)).ToList();
        }

        public async Task<ApplicationView?> GetAsync(string key, CancellationToken cancellationToken)
            => await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.Key == key, cancellationToken) is { } app
                ? await ApplicationQueries.ViewAsync(db, app, cancellationToken)
                : null;
    }
}
