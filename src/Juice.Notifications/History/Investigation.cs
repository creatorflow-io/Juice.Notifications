using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.History
{
    public sealed record NotificationFilter(string? Type = null, string? Application = null, string? Correlation = null, IReadOnlyList<NotificationState>? States = null,
        DateTimeOffset? From = null, DateTimeOffset? To = null, Guid? Before = null, int Limit = Investigation.DefaultLimit);

    public sealed record NotificationListRow(Guid Id, string Type, int TypeVersion, string Application, string? CorrelationKey, NotificationPriority Priority,
        NotificationState State, string? Error, string? Actor, DateTimeOffset AcceptedAt);

    public sealed record RecipientDeliveryView(Guid Id, string Channel, string DestinationKey, DeliveryStatus Status, FailureCategory? FailureCategory);

    public sealed record RecipientDigestView(Guid Id, DigestState State, DateTimeOffset ClosesAt, DateTimeOffset? ClosedAt);

    public sealed record RecipientView(Guid Id, RecipientKind Kind, string NamedAs, IReadOnlyList<string> Via, Guid? SubscriberId, string? Route,
        string? Channel, string? Address, string Resolver, RecipientOutcome Outcome, IReadOnlyList<string> Bypassed, int EscalationStep,
        RecipientDigestView? Digest, DateTimeOffset CreatedAt, IReadOnlyList<RecipientDeliveryView> Deliveries, string? Name = null);

    public sealed record CorrelationFilter(string? Application = null, string? Key = null, CorrelationState? State = null, Guid? Before = null,
        int Limit = Investigation.DefaultLimit);

    public sealed record CorrelationListRow(Guid Id, string Application, string Key, CorrelationState State, string? AcknowledgedBy,
        DateTimeOffset? AcknowledgedAt, DateTimeOffset? ResolvedAt, string? Outcome, DateTimeOffset CreatedAt, DateTimeOffset LastRequestAt,
        DateTimeOffset? HeldAt);

    public sealed record Page<T>(IReadOnlyList<T> Items, Guid? Next);

    public sealed record OverviewDeliveries(string Channel, DeliveryStatus Status, int Count);

    public sealed record OverviewIntegration(string Integration, int Failed, DateTimeOffset LastFailedAt, string? LastError);

    public sealed record Overview(DateTimeOffset At, DateTimeOffset Since, IReadOnlyList<OverviewDeliveries> Deliveries,
        IReadOnlyList<OverviewIntegration> FailingIntegrations, int OpenStorms, int Unfinished);

    /// <summary>
    /// What a tenant's admins read to investigate (phase-4c-plan I52): its notifications, each one's recipients and why (deferred #36), its
    /// correlations, and the overview. Every list is cursor-paged over version-7 ids (their creation order); none is a producer's: the API
    /// serves these to tenant admins only.
    /// </summary>
    public sealed class Investigation(NotificationsDbContext db, TimeProvider clock)
    {
        public const int DefaultLimit = 50;
        public const int MaxLimit = 200;
        /// <summary>The overview's window.</summary>
        public static readonly TimeSpan OverviewWindow = TimeSpan.FromHours(24);

        /// <summary>The tenant's notifications, newest first.</summary>
        public async Task<Page<NotificationListRow>> NotificationsAsync(Guid tenantId, NotificationFilter filter, CancellationToken ct)
        {
            var limit = Math.Clamp(filter.Limit, 1, MaxLimit);
            var requests = db.Requests.AsNoTracking().Where(r => r.TenantId == tenantId);
            if (filter.Type is { } type)
            {
                requests = requests.Where(r => r.TypeKey == type);
            }
            if (filter.Application is { } application)
            {
                requests = requests.Where(r => db.Applications.Any(a => a.Id == r.ApplicationId && a.Key == application));
            }
            if (filter.Correlation is { } correlation)
            {
                requests = requests.Where(r => r.CorrelationKey == correlation);
            }
            if (filter.From is { } from)
            {
                var utcFrom = from.ToUniversalTime();
                requests = requests.Where(r => r.AcceptedAt >= utcFrom);
            }
            if (filter.To is { } to)
            {
                var utcTo = to.ToUniversalTime();
                requests = requests.Where(r => r.AcceptedAt < utcTo);
            }
            if (filter.Before is { } before)
            {
                requests = requests.Where(r => r.Id.CompareTo(before) < 0);
            }
            var rows = from request in requests
                       join notification in db.Notifications on new { request.TenantId, RequestId = request.Id } equals new { notification.TenantId, notification.RequestId }
                       join app in db.Applications on request.ApplicationId equals app.Id
                       select new { request, notification.State, notification.Error, Application = app.Key };
            if (filter.States is { Count: > 0 } states)
            {
                rows = rows.Where(r => states.Contains(r.State));
            }
            var found = await rows.OrderByDescending(r => r.request.Id).Take(limit + 1)
                .Select(r => new NotificationListRow(r.request.Id, r.request.TypeKey, r.request.TypeVersion, r.Application, r.request.CorrelationKey,
                    r.request.Priority, r.State, r.Error, r.request.Actor, r.request.AcceptedAt))
                .ToListAsync(ct);
            var page = found.Take(limit).ToList();
            return new Page<NotificationListRow>(page, found.Count > limit ? page[^1].Id : null);
        }

        /// <summary>
        /// A notification's recipients (by its request's id, as the API names notifications), oldest first, each with its digest and deliveries;
        /// null when the tenant has no such notification.
        /// </summary>
        public async Task<Page<RecipientView>?> RecipientsAsync(Guid tenantId, Guid requestId, Guid? after, int limit, CancellationToken ct)
        {
            limit = Math.Clamp(limit, 1, MaxLimit);
            var notificationId = await db.Notifications.AsNoTracking().Where(n => n.TenantId == tenantId && n.RequestId == requestId)
                .Select(n => (Guid?)n.Id).SingleOrDefaultAsync(ct);
            if (notificationId is null)
            {
                return null;
            }
            var recipients = db.Recipients.AsNoTracking().Where(r => r.TenantId == tenantId && r.NotificationId == notificationId);
            if (after is { } cursor)
            {
                recipients = recipients.Where(r => r.Id.CompareTo(cursor) > 0);
            }
            var found = await recipients.OrderBy(r => r.Id).Take(limit + 1).ToListAsync(ct);
            var page = found.Take(limit).ToList();
            var ids = page.Select(r => r.Id).ToList();
            var deliveries = (await (
                    from item in db.DeliveryItems.Where(i => i.TenantId == tenantId && ids.Contains(i.RecipientId))
                    join delivery in db.Deliveries on new { item.TenantId, Id = item.DeliveryId } equals new { delivery.TenantId, delivery.Id }
                    orderby delivery.Id
                    select new { item.RecipientId, View = new RecipientDeliveryView(delivery.Id, delivery.Channel, delivery.DestinationKey, delivery.Status, delivery.FailureCategory) })
                .AsNoTracking().ToListAsync(ct))
                .ToLookup(d => d.RecipientId, d => d.View);
            var digestIds = page.Where(r => r.DigestId is not null).Select(r => r.DigestId!.Value).Distinct().ToList();
            var digests = digestIds.Count == 0
                ? new Dictionary<Guid, RecipientDigestView>()
                : await db.Digests.AsNoTracking().Where(d => d.TenantId == tenantId && digestIds.Contains(d.Id))
                    .ToDictionaryAsync(d => d.Id, d => new RecipientDigestView(d.Id, d.State, d.ClosesAt, d.ClosedAt), ct);
            // A person by name, as their identity's source last gave it.
            var subscriberIds = page.Where(r => r.SubscriberId is not null).Select(r => r.SubscriberId!.Value).Distinct().ToList();
            var names = subscriberIds.Count == 0
                ? new Dictionary<Guid, string?>()
                : await db.Subscribers.AsNoTracking().Where(s => s.TenantId == tenantId && subscriberIds.Contains(s.Id))
                    .ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            var views = page.Select(r => new RecipientView(r.Id, r.Kind, r.NamedAs, r.Via, r.SubscriberId, r.RouteKey, r.Channel, r.Address, r.Resolver,
                r.Outcome, r.Bypassed, r.EscalationStep, r.DigestId is { } d && digests.TryGetValue(d, out var digest) ? digest : null, r.CreatedAt,
                deliveries[r.Id].ToList(), r.SubscriberId is { } s && names.TryGetValue(s, out var name) ? name : null)).ToList();
            return new Page<RecipientView>(views, found.Count > limit ? page[^1].Id : null);
        }

        /// <summary>The tenant's correlations, newest first.</summary>
        public async Task<Page<CorrelationListRow>> CorrelationsAsync(Guid tenantId, CorrelationFilter filter, CancellationToken ct)
        {
            var limit = Math.Clamp(filter.Limit, 1, MaxLimit);
            var correlations = db.Correlations.AsNoTracking().Where(c => c.TenantId == tenantId);
            if (filter.Key is { } key)
            {
                correlations = correlations.Where(c => c.Key == key);
            }
            if (filter.State is { } state)
            {
                correlations = correlations.Where(c => c.State == state);
            }
            if (filter.Before is { } before)
            {
                correlations = correlations.Where(c => c.Id.CompareTo(before) < 0);
            }
            var rows = from correlation in correlations
                       join app in db.Applications on correlation.ApplicationId equals app.Id
                       select new { correlation, Application = app.Key };
            if (filter.Application is { } application)
            {
                rows = rows.Where(r => r.Application == application);
            }
            var found = await rows.OrderByDescending(r => r.correlation.Id).Take(limit + 1)
                .Select(r => new CorrelationListRow(r.correlation.Id, r.Application, r.correlation.Key, r.correlation.State, r.correlation.AcknowledgedBy,
                    r.correlation.AcknowledgedAt, r.correlation.ResolvedAt, r.correlation.Outcome, r.correlation.CreatedAt, r.correlation.LastRequestAt,
                    r.correlation.HeldAt))
                .ToListAsync(ct);
            var page = found.Take(limit).ToList();
            return new Page<CorrelationListRow>(page, found.Count > limit ? page[^1].Id : null);
        }

        /// <summary>The id of the tenant's linked application <paramref name="key"/>, or null.</summary>
        public Task<Guid?> LinkedApplicationAsync(Guid tenantId, string key, CancellationToken ct)
            => (from app in db.Applications
                join link in db.ApplicationTenants on app.Id equals link.ApplicationId
                where app.Key == key && link.TenantId == tenantId
                select (Guid?)app.Id).SingleOrDefaultAsync(ct);

        /// <summary>
        /// The overview (phase-4c-plan p4c-d6), each count over an index: deliveries by tenant, status and creation time; failed ones by
        /// integration; open storms (the partial index of open ones); unfinished notifications (the partial index of those).
        /// </summary>
        public async Task<Overview> OverviewAsync(Guid tenantId, CancellationToken ct)
        {
            var at = clock.GetUtcNow();
            var since = at - OverviewWindow;
            var statuses = Enum.GetValues<DeliveryStatus>();
            var recent = db.Deliveries.AsNoTracking().Where(d => d.TenantId == tenantId && statuses.Contains(d.Status) && d.CreatedAt >= since);
            var deliveries = await recent.GroupBy(d => new { d.Channel, d.Status })
                .Select(g => new OverviewDeliveries(g.Key.Channel, g.Key.Status, g.Count())).ToListAsync(ct);
            var failedRecently = db.Deliveries.AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.Status == DeliveryStatus.Failed && d.CreatedAt >= since);
            var failed = await (
                    from counted in failedRecently.GroupBy(d => d.IntegrationId).Select(g => new { IntegrationId = g.Key, Failed = g.Count() })
                    join integration in db.Integrations on new { TenantId = tenantId, Id = counted.IntegrationId } equals new { integration.TenantId, integration.Id }
                    select new { counted.IntegrationId, integration.Key, counted.Failed })
                .ToListAsync(ct);
            // The latest failure of each (a tenant has a handful of integrations): one short read each, newest first over the same index.
            var failing = new List<OverviewIntegration>();
            foreach (var f in failed)
            {
                var last = await failedRecently.Where(d => d.IntegrationId == f.IntegrationId).OrderByDescending(d => d.CreatedAt)
                    .Select(d => new { At = d.CompletedAt ?? d.CreatedAt, d.LastError }).FirstAsync(ct);
                failing.Add(new OverviewIntegration(f.Key, f.Failed, last.At, last.LastError));
            }
            failing = failing.OrderByDescending(f => f.Failed).ThenBy(f => f.Integration, StringComparer.Ordinal).ToList();
            var openStorms = await db.Storms.AsNoTracking().CountAsync(s => s.TenantId == tenantId && s.EndedAt == null, ct);
            // Planning, or its workflow running, as literals: the partial index's own filter (a parameter wouldn't prove it).
            var unfinished = await db.Notifications.AsNoTracking()
                .CountAsync(n => n.TenantId == tenantId && (n.State == NotificationState.Planning || n.State == NotificationState.Running), ct);
            return new Overview(at, since, deliveries.OrderBy(d => d.Channel, StringComparer.Ordinal).ThenBy(d => d.Status).ToList(), failing, openStorms, unfinished);
        }
    }
}
