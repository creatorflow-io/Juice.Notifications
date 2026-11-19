using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Delivering
{
    /// <summary>
    /// <c>delivery.failed</c> (design §5.4, phase-3-plan I36, I37): raised for a recipient once every one of their deliveries has ended without
    /// going out, at least one of them failed or bounced, and nothing more can be planned for them (a person in quiet hours has only their inbox
    /// delivery until the hours end). Whatever ends a delivery (the dispatcher, a step given up, a provider's bounce, a closed correlation, a
    /// planner that couldn't reach a destination, a planner settling a person whose quiet hours ended) marks it <see cref="Delivery.FailureCheckDue"/>
    /// in the same save; this worker takes marked deliveries in batches (skipping rows another transaction holds), checks each of their
    /// recipients with the recipient's row locked, raises the event at most once (<see cref="NotificationRecipient.FailureRaisedAt"/>, cleared when
    /// a delivery is retried), and clears the mark, all in one transaction: nothing is lost to a crash between the delivery ending and the check,
    /// and two deliveries of a recipient ending at once can't both miss it.
    /// </summary>
    /// <remarks>
    /// Locks: deliveries, then recipients, each in id order, as a retry and a replay take them. The worker never waits for either (<c>SKIP
    /// LOCKED</c>): a delivery one of whose recipients is held keeps its mark for a later pass, so copies of the worker in several instances, a
    /// retry and a replay can't deadlock with it.
    /// </remarks>
    public sealed class FailureEvents(IServiceScopeFactory scopes, Events.ProducerEvents events, TimeProvider clock, ILogger<FailureEvents> logger) : BackgroundService
    {
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
        private const int Batch = 100;

        private static readonly DeliveryStatus[] Ended = [DeliveryStatus.Failed, DeliveryStatus.Bounced, DeliveryStatus.Expired, DeliveryStatus.Cancelled];

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    while (await RunOnceAsync(stoppingToken) == Batch)
                    {
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Checking ended deliveries for delivery.failed failed; it runs again shortly.");
                }
                try
                {
                    await Task.Delay(Interval, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// One batch of marked deliveries; how many marks it cleared (fewer than it took when some recipients were held: those wait for the next
        /// pass rather than being taken again at once).
        /// </summary>
        public async Task<int> RunOnceAsync(CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var marked = await db.Database.SqlQuery<MarkedDelivery>($"""
                SELECT tenant_id, id FROM notifications.deliveries WHERE failure_check_due
                ORDER BY tenant_id, id LIMIT {Batch} FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
            if (marked.Count == 0)
            {
                return 0;
            }
            var now = clock.GetUtcNow();
            var cleared = new List<MarkedDelivery>();
            foreach (var tenant in marked.GroupBy(d => d.TenantId))
            {
                var deliveryIds = tenant.Select(d => d.Id).ToList();
                var items = await db.DeliveryItems.AsNoTracking().Where(i => i.TenantId == tenant.Key && deliveryIds.Contains(i.DeliveryId))
                    .Select(i => new { i.DeliveryId, i.RecipientId }).ToListAsync(ct);
                var held = new HashSet<Guid>();
                if (events.Enabled)
                {
                    // Every recipient of the batch at once, in id order, skipping those another transaction holds.
                    var recipientIds = items.Select(i => i.RecipientId).Distinct().Order().ToArray();
                    var locked = await db.Database.SqlQuery<LockedRecipient>($"""
                        SELECT id, failure_raised_at, outcome, next_at FROM notifications.notification_recipients
                        WHERE tenant_id = {tenant.Key} AND id = ANY({recipientIds}) ORDER BY id FOR UPDATE SKIP LOCKED
                        """).ToListAsync(ct);
                    var lockedIds = locked.Select(r => r.Id).ToHashSet();
                    held = recipientIds.Where(id => !lockedIds.Contains(id)).ToHashSet();
                    foreach (var recipient in locked)
                    {
                        await CheckAsync(db, tenant.Key, recipient, now, ct);
                    }
                }
                cleared.AddRange(tenant.Where(d => !items.Any(i => i.DeliveryId == d.Id && held.Contains(i.RecipientId))));
            }
            foreach (var tenant in cleared.GroupBy(d => d.TenantId))
            {
                var ids = tenant.Select(d => d.Id).ToList();
                await db.Deliveries.Where(d => d.TenantId == tenant.Key && ids.Contains(d.Id)).ExecuteUpdateAsync(s => s.SetProperty(d => d.FailureCheckDue, false), ct);
            }
            await transaction.CommitAsync(ct);
            return cleared.Count;
        }

        private sealed record MarkedDelivery(Guid TenantId, Guid Id);

        private sealed record LockedRecipient(Guid Id, DateTimeOffset? FailureRaisedAt, string Outcome, DateTimeOffset? NextAt);

        /// <summary>
        /// Through being planned: nothing more can be added to the recipient's deliveries (not pending, not held by quiet hours, not due at a
        /// stage). The delivery planner marks a deferred person's ended deliveries again when it settles them.
        /// </summary>
        private static bool Settled(LockedRecipient r)
            => r.NextAt is null && r.Outcome is not (nameof(RecipientOutcome.Pending) or nameof(RecipientOutcome.Deferred));

        private async Task CheckAsync(NotificationsDbContext db, Guid tenantId, LockedRecipient recipient, DateTimeOffset at, CancellationToken ct)
        {
            if (recipient.FailureRaisedAt is not null || !Settled(recipient))
            {
                return; // raised already, or more may still be planned for them
            }
            var all = await (from item in db.DeliveryItems
                             join d in db.Deliveries on new { item.TenantId, Id = item.DeliveryId } equals new { d.TenantId, d.Id }
                             where item.TenantId == tenantId && item.RecipientId == recipient.Id
                             select new { d.Status, d.Channel }).AsNoTracking().ToListAsync(ct);
            if (all.Any(d => !Ended.Contains(d.Status)) || !all.Any(d => d.Status is DeliveryStatus.Failed or DeliveryStatus.Bounced))
            {
                return; // one went out or still may, or none failed (expired or cancelled only: not a failure to report)
            }
            var x = await (from r in db.Recipients
                           join n in db.Notifications on new { r.TenantId, Id = r.NotificationId } equals new { n.TenantId, n.Id }
                           join q in db.Requests on new { n.TenantId, Id = n.RequestId } equals new { q.TenantId, q.Id }
                           where r.TenantId == tenantId && r.Id == recipient.Id
                           select new { Recipient = r, Request = q }).AsNoTracking().SingleAsync(ct);
            var channels = all.Select(d => d.Channel).Distinct().Order(StringComparer.Ordinal).ToList();
            await events.RaiseAsync(db, tenantId, x.Request.ApplicationId, app => new Events.NotificationEvent
            {
                Event = Events.ProducerEventNames.DeliveryFailed, Application = app, NotificationTenantId = tenantId, CorrelationKey = x.Request.CorrelationKey,
                NotificationId = x.Recipient.NotificationId, RequestId = x.Request.Id, Type = x.Request.TypeKey, RecipientId = x.Recipient.Id,
                SubscriberId = x.Recipient.SubscriberId, NamedAs = x.Recipient.NamedAs, Channels = channels, OccurredAt = at,
            }, ct);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            await db.Recipients.Where(r => r.TenantId == tenantId && r.Id == recipient.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.FailureRaisedAt, at), ct);
        }
    }
}
