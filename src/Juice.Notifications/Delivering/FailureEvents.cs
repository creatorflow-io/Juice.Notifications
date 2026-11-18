using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Delivering
{
    /// <summary>
    /// <c>delivery.failed</c> (design §5.4, phase-3-plan I36, I37): raised for a recipient once every one of their deliveries has ended without
    /// going out, at least one of them failed or bounced. Whatever ends a delivery (the dispatcher, a step given up, a provider's bounce, a
    /// closed correlation, a planner that couldn't reach a destination) marks it <see cref="Delivery.FailureCheckDue"/> in the same save; this
    /// worker takes marked deliveries in batches (skipping rows another worker holds), checks each of their recipients with the recipient's row
    /// locked, raises the event at most once (<see cref="NotificationRecipient.FailureRaisedAt"/>, cleared when a delivery is retried), and clears
    /// the mark, all in one transaction: nothing is lost to a crash between the delivery ending and the check, and two deliveries of a recipient
    /// ending at once can't both miss it.
    /// </summary>
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

        /// <summary>One batch of marked deliveries; how many it took.</summary>
        public async Task<int> RunOnceAsync(CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var marked = await db.Database.SqlQuery<MarkedDelivery>($"""
                SELECT tenant_id, id FROM notifications.deliveries WHERE failure_check_due
                ORDER BY id LIMIT {Batch} FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
            if (marked.Count == 0)
            {
                return 0;
            }
            var now = clock.GetUtcNow();
            if (events.Enabled)
            {
                foreach (var delivery in marked)
                {
                    await CheckAsync(db, delivery.TenantId, delivery.Id, now, ct);
                }
            }
            foreach (var tenant in marked.GroupBy(d => d.TenantId))
            {
                var ids = tenant.Select(d => d.Id).ToList();
                await db.Deliveries.Where(d => d.TenantId == tenant.Key && ids.Contains(d.Id)).ExecuteUpdateAsync(s => s.SetProperty(d => d.FailureCheckDue, false), ct);
            }
            await transaction.CommitAsync(ct);
            return marked.Count;
        }

        private sealed record MarkedDelivery(Guid TenantId, Guid Id);

        private async Task CheckAsync(NotificationsDbContext db, Guid tenantId, Guid deliveryId, DateTimeOffset at, CancellationToken ct)
        {
            var recipients = await (from item in db.DeliveryItems
                                    join r in db.Recipients on new { item.TenantId, Id = item.RecipientId } equals new { r.TenantId, r.Id }
                                    join n in db.Notifications on new { r.TenantId, Id = r.NotificationId } equals new { n.TenantId, n.Id }
                                    join q in db.Requests on new { n.TenantId, Id = n.RequestId } equals new { q.TenantId, q.Id }
                                    where item.TenantId == tenantId && item.DeliveryId == deliveryId
                                    select new { Recipient = r, Request = q }).AsNoTracking().ToListAsync(ct);
            foreach (var x in recipients)
            {
                var raised = await db.Database.SqlQuery<DateTimeOffset?>($"""
                    SELECT failure_raised_at AS "Value" FROM notifications.notification_recipients WHERE tenant_id = {tenantId} AND id = {x.Recipient.Id} FOR UPDATE
                    """).ToListAsync(ct);
                if (raised is not [null])
                {
                    continue; // raised already (or the recipient is gone)
                }
                var all = await (from item in db.DeliveryItems
                                 join d in db.Deliveries on new { item.TenantId, Id = item.DeliveryId } equals new { d.TenantId, d.Id }
                                 where item.TenantId == tenantId && item.RecipientId == x.Recipient.Id
                                 select new { d.Status, d.Channel }).AsNoTracking().ToListAsync(ct);
                if (all.Any(d => !Ended.Contains(d.Status)) || !all.Any(d => d.Status is DeliveryStatus.Failed or DeliveryStatus.Bounced))
                {
                    continue; // one went out or still may, or none failed (expired or cancelled only: not a failure to report)
                }
                var channels = all.Select(d => d.Channel).Distinct().Order(StringComparer.Ordinal).ToList();
                await events.RaiseAsync(db, tenantId, x.Request.ApplicationId, app => new Events.NotificationEvent
                {
                    Event = Events.ProducerEventNames.DeliveryFailed, Application = app, NotificationTenantId = tenantId, CorrelationKey = x.Request.CorrelationKey,
                    NotificationId = x.Recipient.NotificationId, RequestId = x.Request.Id, Type = x.Request.TypeKey, RecipientId = x.Recipient.Id,
                    SubscriberId = x.Recipient.SubscriberId, NamedAs = x.Recipient.NamedAs, Channels = channels, OccurredAt = at,
                }, ct);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                await db.Recipients.Where(r => r.TenantId == tenantId && r.Id == x.Recipient.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.FailureRaisedAt, at), ct);
            }
        }
    }
}
