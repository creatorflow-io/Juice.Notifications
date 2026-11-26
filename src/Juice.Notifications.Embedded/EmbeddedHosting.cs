using Juice.Domain.Events;
using Juice.MediatR;
using Juice.Notifications.Data;
using Juice.Notifications.Types;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Embedded
{
    /// <summary>
    /// Each change to an audited row (<see cref="Juice.Domain.IAuditable"/>: the setup), to the host's <see cref="INotificationsAuditSink"/>
    /// when it registered one (phase-5-plan I62). Raised inside the change's transaction, before it commits. Without a sink, nothing is kept.
    /// </summary>
    internal sealed class AuditForwarder<T>(INotificationsAuditSink? sink = null) : INotificationHandler<T>
        where T : AuditEvent
    {
        public async ValueTask Handle(T notification, CancellationToken cancellationToken)
        {
            // This service's rows only: the host's own context raises its changes through the same mediator.
            if (sink is null || notification.AuditRecord is not { } record || record.Entity?.GetType().Assembly != typeof(NotificationsDbContext).Assembly)
            {
                return;
            }
            // The row's own tenant (in its key, for most): the context has no tenant accessor, so the event names none (empty).
            var tenant = !string.IsNullOrEmpty(notification.TenantId) ? notification.TenantId
                : new[] { record.KeyValues, record.CurrentValues, record.OriginalValues }.Select(v => v.GetValueOrDefault("TenantId")).FirstOrDefault(v => v is not null)?.ToString();
            await sink.RecordAsync(new NotificationsDataChange(record.Table, notification.Name, tenant, record.User ?? EmbeddedActor.Current ?? Hosting.NotificationsActor.Current,
                record.KeyValues, record.OriginalValues, record.CurrentValues), cancellationToken);
        }
    }

    /// <summary>The provisioning file's changes (phase-1-plan I12), recorded as its actor's (<see cref="Configuration.ConfigurationProvisioner.Actor"/>).</summary>
    internal sealed class EmbeddedProvisioningScope : Configuration.IProvisioningScope
    {
        public async ValueTask RunAsync(Guid tenantId, Func<ValueTask> work)
        {
            using var actor = EmbeddedActor.As(Configuration.ConfigurationProvisioner.Actor);
            await work();
        }
    }

    /// <summary>Who changes the setup when no caller does: provisioning at start.</summary>
    internal static class EmbeddedActor
    {
        private static readonly AsyncLocal<string?> Actor = new();

        public static string? Current => Actor.Value;

        public static IDisposable As(string actor)
        {
            var previous = Actor.Value;
            Actor.Value = actor;
            return new Restore(previous);
        }

        private sealed class Restore(string? previous) : IDisposable
        {
            public void Dispose() => Actor.Value = previous;
        }
    }

    /// <summary>
    /// The host's application, linked to its tenant, and its types declared (phase-5-plan I63), at start: after the migrations, before the
    /// stages take anything. Idempotent: an unchanged type is no new version, and replicas starting together end with one of each.
    /// </summary>
    internal sealed class EmbeddedApplicationProvisioner(IServiceScopeFactory scopes, NotificationsEmbeddedOptions options, TimeProvider clock,
        ILogger<EmbeddedApplicationProvisioner> logger) : IHostedService
    {
        public const string Actor = "provisioning";

        /// <summary>A client id no token carries: the host is the only caller, in-process.</summary>
        public static string ClientId(string applicationKey) => $"urn:juice-notifications:embedded:{applicationKey}";

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var actor = EmbeddedActor.As(Actor);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var now = clock.UtcNow();
            var clientId = ClientId(options.ApplicationKey);
            var name = options.ApplicationName.Trim();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.applications (id, key, name, client_id, broker_user, enabled, created_at, updated_at)
                VALUES ({Guid.CreateVersion7()}, {options.ApplicationKey}, {name}, {clientId}, NULL, TRUE, {now}, {now})
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            // Its name as the host now gives it (the key and client id never change).
            await db.Database.ExecuteSqlAsync($"""
                UPDATE notifications.applications SET name = {name}, updated_at = {now}
                WHERE key = {options.ApplicationKey} AND client_id = {clientId} AND name <> {name}
                """, cancellationToken);
            var app = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.Key == options.ApplicationKey, cancellationToken);
            if (app is null || app.ClientId != clientId)
            {
                throw new InvalidOperationException(app is null
                    ? $"The application '{options.ApplicationKey}' couldn't be provisioned: another application uses the client id {clientId}."
                    : $"An application named '{options.ApplicationKey}' (client {app.ClientId}) exists that this host didn't make.");
            }
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.application_tenants (application_id, tenant_id, linked_at)
                VALUES ({app.Id}, {options.TenantId}, {now})
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            using var context = NotificationsMessageContext.Begin(Actor);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            foreach (var declaration in options.Types)
            {
                var declared = await DeclareAsync(mediator, db, app, declaration, cancellationToken);
                if (declared.Value is not { } type)
                {
                    throw new InvalidOperationException($"The type {declaration.Key} was refused: {declared.Message} "
                        + string.Join("; ", declared.Errors?.Select(e => $"{e.Pointer}: {e.Message}") ?? []));
                }
                if (type.IsNew)
                {
                    logger.LogInformation("Declared {Type} v{Version}", type.Key, type.Version);
                }
            }
        }

        private static async Task<Outcome<TypeVersionView>> DeclareAsync(IMediator mediator, NotificationsDbContext db, Data.Application app,
            TypeDeclaration declaration, CancellationToken cancellationToken)
        {
            for (var tries = 0; ; tries++)
            {
                try
                {
                    return await mediator.Send(new DeclareType(app.Id, app.Key, declaration), cancellationToken);
                }
                catch (Exception ex) when (tries < 3 && DbConflicts.Classify(ex) is { Kind: ConflictKind.Unique })
                {
                    // Replicas starting together declare the same version; the loser finds the winner's the next time (the same declaration).
                    db.ChangeTracker.Clear();
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
