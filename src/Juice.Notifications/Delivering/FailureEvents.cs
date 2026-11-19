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
    /// A recipient more can still be planned for (<see cref="RecipientPlanning"/>) isn't checked yet: the delivery's mark moves to them
    /// (<see cref="NotificationRecipient.FailureCheckDue"/>). Its partial index holds only settled recipients, so they enter it as whatever
    /// settles them (the planner when quiet hours end, a closed correlation, a step given up) saves them, and the worker's second scan takes
    /// them from there. Nothing that settles a recipient does anything for the event, and nothing waiting on quiet hours is scanned meanwhile.
    /// </para>
    /// <para>
    /// Locks: deliveries, then recipients, each in id order, as a retry and a replay take them. The worker never waits for either (<c>SKIP
    /// LOCKED</c>): a delivery one of whose recipients is held keeps its mark, and the drain pages past it, so copies of the worker in several
    /// instances, a retry and a replay can't deadlock with it or hold up the deliveries behind it.
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
                    await DrainAsync(stoppingToken);
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
        /// Every marked delivery once, in id order, paging past those it had to keep (a held recipient), then every settled recipient marked.
        /// Kept ones are taken again on the next drain.
        /// </summary>
        public async Task DrainAsync(CancellationToken ct)
        {
            Guid? after = null;
            while (await DeliveriesAsync(after, ct) is { Last: { } last } page)
            {
                if (page.Taken < Batch)
                {
                    break;
                }
                after = last;
            }
            while ((await RecipientsAsync(ct)).Taken == Batch)
            {
            }
        }

        /// <summary>One batch of marked deliveries and one of settled marked recipients; how many recipients it checked.</summary>
        public async Task<int> RunOnceAsync(CancellationToken ct) => (await DeliveriesAsync(null, ct)).Checked + (await RecipientsAsync(ct)).Checked;

        private sealed record Page(int Taken, int Checked, Guid? Last);

        /// <summary>One batch of marked deliveries after <paramref name="after"/>.</summary>
        private async Task<Page> DeliveriesAsync(Guid? after, CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var from = after ?? Guid.Empty;
            // In id order, as the partial index ix_deliveries_failure_check gives them.
            var marked = await db.Database.SqlQuery<MarkedRow>($"""
                SELECT tenant_id, id FROM notifications.deliveries WHERE failure_check_due AND id > {from}
                ORDER BY id LIMIT {Batch} FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
            if (marked.Count == 0)
            {
                return new Page(0, 0, null);
            }
            var now = clock.GetUtcNow();
            var cleared = new List<MarkedRow>();
            var checkedCount = 0;
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
                    SELECT tenant_id, id, failure_raised_at, outcome, next_at FROM notifications.notification_recipients
                    WHERE tenant_id = {tenant.Key} AND id = ANY({recipientIds}) ORDER BY id FOR UPDATE SKIP LOCKED
                    """).ToListAsync(ct);
                var later = new List<Guid>();
                foreach (var recipient in locked)
                {
                    if (recipient.FailureRaisedAt is not null)
                    {
                        continue; // reported already: nothing to check now or later
                    }
                    if (RecipientPlanning.CanStillBePlanned(recipient.Outcome, recipient.NextAt))
                    {
                        later.Add(recipient.Id); // the mark moves to them: checked once they settle
                    }
                    else
                    {
                        await CheckAsync(db, tenant.Key, recipient, now, ct);
                        checkedCount++;
                    }
                }
                if (later.Count > 0)
                {
                    await db.Recipients.Where(r => r.TenantId == tenant.Key && later.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.FailureCheckDue, true), ct);
                }
                // Held: their deliveries keep the mark for the next drain.
                var lockedIds = locked.Select(r => r.Id).ToHashSet();
                var kept = items.Where(i => !lockedIds.Contains(i.RecipientId)).Select(i => i.DeliveryId).ToHashSet();
                cleared.AddRange(tenant.Where(d => !kept.Contains(d.Id)));
            }
            foreach (var tenant in cleared.GroupBy(d => d.TenantId))
            {
                var ids = tenant.Select(d => d.Id).ToList();
                await db.Deliveries.Where(d => d.TenantId == tenant.Key && ids.Contains(d.Id)).ExecuteUpdateAsync(s => s.SetProperty(d => d.FailureCheckDue, false), ct);
            }
            await transaction.CommitAsync(ct);
            return new Page(marked.Count, checkedCount, marked[^1].Id);
        }

        /// <summary>
        /// Settled recipients whose check waited for them: the partial index's own predicate, as literals (constants of this code, no input), so
        /// the index serves the query.
        /// </summary>
        private static readonly string DueRecipients = $"""
            SELECT tenant_id, id, failure_raised_at, outcome, next_at FROM notifications.notification_recipients
            WHERE failure_check_due AND {RecipientPlanning.SettledSql}
            ORDER BY id LIMIT {Batch} FOR UPDATE SKIP LOCKED
            """;

        /// <summary>
        /// One batch of settled recipients whose check waited for them (from their partial index): how many it took, and how many of them it
        /// checked (none with events off: their marks are only cleared).
        /// </summary>
        private async Task<(int Taken, int Checked)> RecipientsAsync(CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var due = await db.Database.SqlQueryRaw<LockedRecipient>(DueRecipients).ToListAsync(ct);
            if (due.Count == 0)
            {
                return (0, 0);
            }
            if (events.Enabled)
            {
                var now = clock.GetUtcNow();
                foreach (var recipient in due)
                {
                    await CheckAsync(db, recipient.TenantId, recipient, now, ct);
                }
            }
            foreach (var tenant in due.GroupBy(r => r.TenantId))
            {
                var ids = tenant.Select(r => r.Id).ToList();
                await db.Recipients.Where(r => r.TenantId == tenant.Key && ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.FailureCheckDue, false), ct);
            }
            await transaction.CommitAsync(ct);
            return (due.Count, events.Enabled ? due.Count : 0);
        }

        private sealed record MarkedRow(Guid TenantId, Guid Id);

        private sealed record LockedRecipient(Guid TenantId, Guid Id, DateTimeOffset? FailureRaisedAt, string Outcome, DateTimeOffset? NextAt);

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
