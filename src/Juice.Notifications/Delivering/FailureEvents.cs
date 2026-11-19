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
    /// planner that couldn't reach a destination) marks it <see cref="Delivery.FailureCheckDue"/> in the same save; this worker takes marked
    /// deliveries in batches, checks each of their recipients with the recipient's row locked, raises the event at most once
    /// (<see cref="NotificationRecipient.FailureRaisedAt"/>, cleared when a delivery is retried), and clears the mark, all in one transaction:
    /// nothing is lost to a crash between the delivery ending and the check, and two deliveries of a recipient ending at once can't both miss it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A marked delivery is left alone, mark and all, while any of its recipients can still be planned for (<see cref="Unsettled"/>): whatever
    /// settles them later (the planner when quiet hours end, a correlation closed, a step given up) needs to do nothing for the event, and the
    /// mark is taken once they are settled.
    /// </para>
    /// <para>
    /// Locks: deliveries, then recipients, each in id order, as a retry and a replay take them. The worker never waits for either (<c>SKIP
    /// LOCKED</c>): a delivery one of whose recipients is held keeps its mark for a later pass, so copies of the worker in several instances, a
    /// retry and a replay can't deadlock with it.
    /// </para>
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
                    // On while a pass takes a full batch and gets somewhere: one whose rows are all held waits for the next interval.
                    (int Taken, int Cleared) pass;
                    do
                    {
                        pass = await PassAsync(stoppingToken);
                    }
                    while (pass.Taken == Batch && pass.Cleared > 0);
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

        /// <summary>One batch of marked deliveries; how many marks it cleared (a delivery with a held or unsettled recipient keeps its mark).</summary>
        public async Task<int> RunOnceAsync(CancellationToken ct) => (await PassAsync(ct)).Cleared;

        private static readonly string Pending = nameof(RecipientOutcome.Pending);
        private static readonly string Deferred = nameof(RecipientOutcome.Deferred);

        /// <summary>One batch: how many marked deliveries it took, and how many marks it cleared.</summary>
        private async Task<(int Taken, int Cleared)> PassAsync(CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // In id order, as the partial index ix_deliveries_failure_check gives them; none any of whose recipients can still be planned for.
            var marked = await db.Database.SqlQuery<MarkedDelivery>($"""
                SELECT d.tenant_id, d.id FROM notifications.deliveries d
                WHERE d.failure_check_due AND NOT EXISTS (
                    SELECT 1 FROM notifications.delivery_items i
                    JOIN notifications.notification_recipients r ON r.tenant_id = i.tenant_id AND r.id = i.recipient_id
                    WHERE i.tenant_id = d.tenant_id AND i.delivery_id = d.id AND (r.outcome IN ({Pending}, {Deferred}) OR r.next_at IS NOT NULL))
                ORDER BY d.id LIMIT {Batch} FOR UPDATE OF d SKIP LOCKED
                """).ToListAsync(ct);
            if (marked.Count == 0)
            {
                return (0, 0);
            }
            var now = clock.GetUtcNow();
            var cleared = new List<MarkedDelivery>();
            foreach (var tenant in marked.GroupBy(d => d.TenantId))
            {
                if (!events.Enabled)
                {
                    cleared.AddRange(tenant);
                    continue;
                }
                var deliveryIds = tenant.Select(d => d.Id).ToList();
                var items = await db.DeliveryItems.AsNoTracking().Where(i => i.TenantId == tenant.Key && deliveryIds.Contains(i.DeliveryId))
                    .Select(i => new { i.DeliveryId, i.RecipientId }).ToListAsync(ct);
                // Every recipient of the batch at once, in id order, skipping those another transaction holds.
                var recipientIds = items.Select(i => i.RecipientId).Distinct().Order().ToArray();
                var locked = await db.Database.SqlQuery<LockedRecipient>($"""
                    SELECT id, failure_raised_at, outcome, next_at FROM notifications.notification_recipients
                    WHERE tenant_id = {tenant.Key} AND id = ANY({recipientIds}) ORDER BY id FOR UPDATE SKIP LOCKED
                    """).ToListAsync(ct);
                // Held, or (read again under the lock) no longer settled: their deliveries keep the mark for a later pass.
                var checkable = locked.Where(r => !Unsettled(r)).Select(r => r.Id).ToHashSet();
                foreach (var recipient in locked.Where(r => checkable.Contains(r.Id)))
                {
                    await CheckAsync(db, tenant.Key, recipient, now, ct);
                }
                var kept = items.Where(i => !checkable.Contains(i.RecipientId)).Select(i => i.DeliveryId).ToHashSet();
                cleared.AddRange(tenant.Where(d => !kept.Contains(d.Id)));
            }
            foreach (var tenant in cleared.GroupBy(d => d.TenantId))
            {
                var ids = tenant.Select(d => d.Id).ToList();
                await db.Deliveries.Where(d => d.TenantId == tenant.Key && ids.Contains(d.Id)).ExecuteUpdateAsync(s => s.SetProperty(d => d.FailureCheckDue, false), ct);
            }
            await transaction.CommitAsync(ct);
            return (marked.Count, cleared.Count);
        }

        private sealed record MarkedDelivery(Guid TenantId, Guid Id);

        private sealed record LockedRecipient(Guid Id, DateTimeOffset? FailureRaisedAt, string Outcome, DateTimeOffset? NextAt);

        /// <summary>
        /// More can still be planned for the recipient: pending, held by quiet hours (only their inbox so far), or due at a stage. The same test
        /// as the batch query's, in SQL there.
        /// </summary>
        private static bool Unsettled(LockedRecipient r) => r.NextAt is not null || r.Outcome == Pending || r.Outcome == Deferred;

        private async Task CheckAsync(NotificationsDbContext db, Guid tenantId, LockedRecipient recipient, DateTimeOffset at, CancellationToken ct)
        {
            if (recipient.FailureRaisedAt is not null)
            {
                return; // raised already
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
