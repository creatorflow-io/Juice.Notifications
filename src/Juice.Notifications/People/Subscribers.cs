using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.People
{
    // Subscribers (design D23): people by the source that first produced them and its id for them, created as resolvers return them. A tenant
    // admin links another source's identity to one (phase-2-plan p2-d2): resolving that identity then reaches this subscriber.

    public sealed record AliasView(string Source, string ExternalId, string? LinkedBy, DateTimeOffset LinkedAt);

    public sealed record SubscriberView(Guid Id, string Source, string ExternalId, string? Name, string? Locale, string? TimeZone, Guid? MergedInto,
        IReadOnlyList<AliasView> Aliases, DateTimeOffset CreatedAt)
    {
        /// <summary>The person's quiet hours as stored (JSON), or null.</summary>
        public string? QuietHours { get; init; }
    }

    /// <summary>
    /// Links another source's identity to a subscriber. If that identity is already a subscriber of its own, it is linked into this one
    /// (<see cref="Subscriber.MergedInto"/>): its history stays where it is, and from now on resolving it reaches this subscriber.
    /// </summary>
    public sealed record LinkAlias(Guid NotificationTenantId, Guid SubscriberId, string? Source, string? ExternalId, string? LinkedBy)
        : MessageBase, IRequest<Outcome<SubscriberView>>;

    /// <summary>Unlinks an identity; a subscriber that was linked into this one stands on its own again.</summary>
    public sealed record UnlinkAlias(Guid NotificationTenantId, Guid SubscriberId, string Source, string ExternalId) : MessageBase, IRequest<Outcome<bool>>;

    internal sealed class SubscriberHandlers(NotificationsDbContext db, TimeProvider clock) :
        IRequestHandler<LinkAlias, Outcome<SubscriberView>>, IRequestHandler<UnlinkAlias, Outcome<bool>>
    {
        public async ValueTask<Outcome<SubscriberView>> Handle(LinkAlias r, CancellationToken ct)
        {
            var errors = new List<FieldError>();
            if (r.Source is null || !ConfigurationRules.Key().IsMatch(r.Source))
            {
                errors.Add(new FieldError("/source", "A source is 1–63 lowercase letters, digits or hyphens, starting with a letter."));
            }
            if (!Intake.RequestRules.IsRecipientId(r.ExternalId))
            {
                errors.Add(new FieldError("/externalId", $"An external id is 1–{Intake.RequestRules.MaxRecipientIdLength} characters, no control characters."));
            }
            if (errors.Count > 0)
            {
                return Outcome<SubscriberView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.", errors);
            }
            var subscriber = await db.Subscribers.SingleOrDefaultAsync(s => s.TenantId == r.NotificationTenantId && s.Id == r.SubscriberId, ct);
            if (subscriber is null)
            {
                return Outcome<SubscriberView>.NotFound("No such subscriber.");
            }
            if (subscriber.MergedInto is { } into)
            {
                return Outcome<SubscriberView>.Conflict(ErrorCodes.SubscriberMerged, $"This subscriber is linked into {into}; link the identity there.");
            }
            if (subscriber.Source == r.Source && subscriber.ExternalId == r.ExternalId)
            {
                return Outcome<SubscriberView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/externalId", "This is the subscriber's own identity.")]);
            }
            var alias = await db.SubscriberAliases.SingleOrDefaultAsync(a => a.TenantId == r.NotificationTenantId && a.Source == r.Source && a.ExternalId == r.ExternalId, ct);
            if (alias is not null)
            {
                return alias.SubscriberId == subscriber.Id
                    ? Outcome<SubscriberView>.Ok(await SubscriberQueries.ViewAsync(db, subscriber, ct))
                    : Outcome<SubscriberView>.Conflict(ErrorCodes.AliasInUse, $"This identity is linked to subscriber {alias.SubscriberId}; unlink it there first.");
            }
            var other = await db.Subscribers.SingleOrDefaultAsync(s => s.TenantId == r.NotificationTenantId && s.Source == r.Source && s.ExternalId == r.ExternalId, ct);
            if (other is not null)
            {
                // One level only: a subscriber others are linked into would leave them pointing at one that points elsewhere.
                if (await db.SubscriberAliases.AnyAsync(a => a.TenantId == r.NotificationTenantId && a.SubscriberId == other.Id, ct))
                {
                    return Outcome<SubscriberView>.Conflict(ErrorCodes.SubscriberMerged,
                        $"Identities are linked to {other.Id}; link this subscriber into that one instead, or unlink them first.");
                }
                other.MergedInto = subscriber.Id;
                other.Version++;
            }
            // Written too, so a concurrent link of this subscriber into another (which writes it as its merged one) and this one can't both
            // commit: the loser's save finds the version moved (409), and no chain of two links forms. The checks above read what they fence.
            subscriber.Version++;
            var linkedAt = clock.UtcNow();
            db.SubscriberAliases.Add(new SubscriberAlias
            {
                TenantId = r.NotificationTenantId, SubscriberId = subscriber.Id, Source = r.Source!, ExternalId = r.ExternalId!, LinkedBy = r.LinkedBy,
                LinkedAt = linkedAt,
            });
            var view = await SubscriberQueries.ViewAsync(db, subscriber, ct);
            return Outcome<SubscriberView>.Ok(view with
            {
                Aliases = [.. view.Aliases.Append(new AliasView(r.Source!, r.ExternalId!, r.LinkedBy, linkedAt)).OrderBy(a => a.Source, StringComparer.Ordinal)
                    .ThenBy(a => a.ExternalId, StringComparer.Ordinal)],
            });
        }

        public async ValueTask<Outcome<bool>> Handle(UnlinkAlias r, CancellationToken ct)
        {
            var alias = await db.SubscriberAliases.SingleOrDefaultAsync(a => a.TenantId == r.NotificationTenantId && a.SubscriberId == r.SubscriberId
                && a.Source == r.Source && a.ExternalId == r.ExternalId, ct);
            if (alias is null)
            {
                return Outcome<bool>.Ok(false);
            }
            db.SubscriberAliases.Remove(alias);
            var other = await db.Subscribers.SingleOrDefaultAsync(s => s.TenantId == r.NotificationTenantId && s.Source == r.Source && s.ExternalId == r.ExternalId
                && s.MergedInto == r.SubscriberId, ct);
            if (other is not null)
            {
                other.MergedInto = null;
                other.Version++;
            }
            return Outcome<bool>.Ok(true);
        }
    }

    public static class SubscriberQueries
    {
        internal static async Task<SubscriberView> ViewAsync(NotificationsDbContext db, Subscriber s, CancellationToken ct)
        {
            var aliases = await db.SubscriberAliases.AsNoTracking().Where(a => a.TenantId == s.TenantId && a.SubscriberId == s.Id).ToListAsync(ct);
            return View(s, aliases);
        }

        /// <summary>The subscriber with its aliases, in ordinal order by source and id.</summary>
        internal static SubscriberView View(Subscriber s, IEnumerable<SubscriberAlias> aliases)
            => new(s.Id, s.Source, s.ExternalId, s.Name, s.Locale, s.TimeZone, s.MergedInto,
                aliases.OrderBy(a => a.Source, StringComparer.Ordinal).ThenBy(a => a.ExternalId, StringComparer.Ordinal)
                    .Select(a => new AliasView(a.Source, a.ExternalId, a.LinkedBy, a.LinkedAt)).ToList(), s.CreatedAt) { QuietHours = s.QuietHours };
    }

    /// <summary>Reads a tenant's subscribers, outside any command.</summary>
    public sealed class SubscriberReader(NotificationsDbContext db)
    {
        public const int MaxPage = 200;

        /// <summary>
        /// A page of subscribers in id order (creation order: ids are UUIDv7), after <paramref name="after"/>, optionally of one source or one
        /// identity (an alias's identity finds the subscriber it is linked to).
        /// </summary>
        public async Task<IReadOnlyList<SubscriberView>> ListAsync(Guid tenantId, string? source, string? externalId, Guid? after, int limit, CancellationToken ct)
        {
            var query = db.Subscribers.AsNoTracking().Where(s => s.TenantId == tenantId);
            if (externalId is not null)
            {
                var linked = db.SubscriberAliases.Where(a => a.TenantId == tenantId && a.ExternalId == externalId && (source == null || a.Source == source))
                    .Select(a => a.SubscriberId);
                query = query.Where(s => (s.ExternalId == externalId && (source == null || s.Source == source)) || linked.Contains(s.Id));
            }
            else if (source is not null)
            {
                query = query.Where(s => s.Source == source);
            }
            if (after is { } cursor)
            {
                query = query.Where(s => s.Id.CompareTo(cursor) > 0);
            }
            var page = await query.OrderBy(s => s.Id).Take(Math.Clamp(limit, 1, MaxPage)).ToListAsync(ct);
            var ids = page.Select(s => s.Id).ToArray();
            var aliases = (await db.SubscriberAliases.AsNoTracking().Where(a => a.TenantId == tenantId && ids.Contains(a.SubscriberId)).ToListAsync(ct))
                .ToLookup(a => a.SubscriberId);
            return page.Select(s => SubscriberQueries.View(s, aliases[s.Id])).ToList();
        }

        public async Task<SubscriberView?> GetAsync(Guid tenantId, Guid id, CancellationToken ct)
            => await db.Subscribers.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == id, ct) is { } s
                ? await SubscriberQueries.ViewAsync(db, s, ct)
                : null;
    }
}
