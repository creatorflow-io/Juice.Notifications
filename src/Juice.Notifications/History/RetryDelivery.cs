using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.History
{
    public sealed record RetriedView(Guid Id, DeliveryStatus Status, int Attempts, int Replays);

    /// <summary>
    /// Dead-letter retry of one delivery (phase-1-plan M8): a failed delivery goes back to <c>Pending</c> under the same id, with the same stored
    /// content, so receivers that deduplicate on its id still can. Its attempts so far stay, numbered on from them, and it gets a fresh attempt budget.
    /// </summary>
    public sealed record RetryDelivery(Guid NotificationTenantId, Guid DeliveryId) : MessageBase, Configuration.ITenantCommand, IRequest<Outcome<RetriedView>>;

    internal sealed class RetryDeliveryHandler(NotificationsDbContext db, TimeProvider clock) : IRequestHandler<RetryDelivery, Outcome<RetriedView>>
    {
        public async ValueTask<Outcome<RetriedView>> Handle(RetryDelivery r, CancellationToken ct)
        {
            // The delivery first, then its recipients and requests (DeliveryRules.LockAsync): the order the delivery.failed worker takes them in,
            // so a retry just after a failure waits for the worker's check (or the worker passes this delivery over) rather than deadlocking.
            if (!await DeliveryRules.LockAsync(db, r.NotificationTenantId, [r.DeliveryId], ct))
            {
                return Outcome<RetriedView>.NotFound($"There is no delivery {r.DeliveryId} in this tenant.");
            }
            var delivery = await db.Deliveries.SingleAsync(d => d.TenantId == r.NotificationTenantId && d.Id == r.DeliveryId, ct);
            if (delivery.Status != DeliveryStatus.Failed)
            {
                return Outcome<RetriedView>.Conflict(ErrorCodes.NotRetryable, $"Only a failed delivery can be retried; this one is {delivery.Status}.");
            }
            if (!DeliveryRules.HasContent(delivery))
            {
                return Outcome<RetriedView>.Conflict(ErrorCodes.NotRetryable,
                    "The delivery's content was never rendered (" + (delivery.LastError ?? "render failed") + "). Fix the template and send the request again.");
            }
            var now = clock.UtcNow();
            var expires = await DeliveryRules.ExpiresAtAsync(db, delivery.TenantId, delivery.Id, ct);
            if (expires is { } at && at <= now)
            {
                return Outcome<RetriedView>.Conflict(ErrorCodes.NotRetryable, $"The request expired at {at:O}; a retry would expire at once.");
            }
            if (delivery.Channel == Channels.Inbox && await DeliveryRules.Redacted(db, delivery.TenantId, [delivery.Id]).AnyAsync(ct))
            {
                return Outcome<RetriedView>.Conflict(ErrorCodes.NotRetryable,
                    "The request has been redacted; its inbox item would show the values redaction masked. Send the request again.");
            }
            // Not claimable while Failed (no NextAt), so no worker holds it; the version still fences two retries at once.
            delivery.Status = DeliveryStatus.Pending;
            delivery.FailureCategory = null;
            delivery.LastError = null; // the attempts keep each error; the row shows the current one
            delivery.ProviderMessageId = null;
            delivery.CompletedAt = null;
            delivery.Replays++;
            // Past every recorded attempt, even one the row never took in (a stale worker's): the dispatcher would apply it instead of sending.
            delivery.Attempts = Math.Max(delivery.Attempts, await db.Attempts.Where(a => a.TenantId == delivery.TenantId && a.DeliveryId == delivery.Id)
                .MaxAsync(a => (int?)a.Number, ct) ?? 0);
            delivery.AttemptsAtReplay = delivery.Attempts;
            delivery.ReplayedAt = now;
            delivery.StepFailures = 0;
            delivery.ClaimedBy = null;
            delivery.LeaseUntil = null;
            delivery.NextAt = now;
            delivery.Version++;
            delivery.FailureCheckDue = false;
            await db.Recipients.Where(x => x.TenantId == delivery.TenantId && x.FailureRaisedAt != null
                    && db.DeliveryItems.Any(i => i.TenantId == delivery.TenantId && i.DeliveryId == delivery.Id && i.RecipientId == x.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.FailureRaisedAt, (DateTimeOffset?)null), ct);
            return Outcome<RetriedView>.Ok(new RetriedView(delivery.Id, delivery.Status, delivery.Attempts, delivery.Replays));
        }
    }

    public static class DeliveryRules
    {
        /// <summary>Content the dispatcher can send: a delivery that failed while it was planned (its template failed) stores none.</summary>
        /// <c>{}</c> is what deliveries planned before 12 Nov 2026 stored instead; no rendered content is ever exactly that (it is wrapped per channel).
        public static bool HasContent(Delivery delivery) => delivery.Content is { Length: > 0 } and not "{}";

        /// <summary>
        /// Locks deliveries for a retry or replay, then their recipients, then their requests (shared), each in id order; whether any delivery
        /// was there. Deliveries before recipients is the order the <c>delivery.failed</c> worker takes them in (it never waits on either); the
        /// requests' shared lock holds off redaction (which takes them for update) until the retry commits, so redaction then sees the
        /// delivery pending and waits for it, or a retry after redaction sees the request redacted (<see cref="Redacted"/>).
        /// </summary>
        public static async Task<bool> LockAsync(NotificationsDbContext db, Guid tenantId, Guid[] deliveryIds, CancellationToken ct)
        {
            var ids = deliveryIds.Order().ToArray();
            var locked = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM notifications.deliveries WHERE tenant_id = {tenantId} AND id = ANY({ids}) ORDER BY id FOR UPDATE
                """).ToListAsync(ct);
            if (locked.Count == 0)
            {
                return false;
            }
            var lockedIds = locked.ToArray();
            await db.Database.SqlQuery<Guid>($"""
                SELECT r.id AS "Value" FROM notifications.notification_recipients r
                WHERE r.tenant_id = {tenantId} AND r.id IN (SELECT i.recipient_id FROM notifications.delivery_items i WHERE i.tenant_id = {tenantId} AND i.delivery_id = ANY({lockedIds}))
                ORDER BY r.id FOR UPDATE
                """).ToListAsync(ct);
            await db.Database.SqlQuery<Guid>($"""
                SELECT q.id AS "Value" FROM notifications.requests q
                WHERE q.tenant_id = {tenantId} AND q.id IN (SELECT i.request_id FROM notifications.delivery_items i WHERE i.tenant_id = {tenantId} AND i.delivery_id = ANY({lockedIds}))
                ORDER BY q.id FOR SHARE
                """).ToListAsync(ct);
            return true;
        }

        /// <summary>
        /// The deliveries among these that carry a redacted request. An inbox delivery among them can't be retried or replayed: the item it
        /// writes would show the values redaction masked (other channels can: they send the stored content, which redaction leaves).
        /// </summary>
        public static IQueryable<Guid> Redacted(NotificationsDbContext db, Guid tenantId, IReadOnlyCollection<Guid> deliveryIds)
            => db.DeliveryItems.Where(i => i.TenantId == tenantId && deliveryIds.Contains(i.DeliveryId)
                    && db.Requests.Any(q => q.TenantId == tenantId && q.Id == i.RequestId && q.RedactedAt != null))
                .Select(i => i.DeliveryId).Distinct();

        /// <summary>When the request a delivery carries expires, if it does (the earliest, for a digest).</summary>
        public static Task<DateTimeOffset?> ExpiresAtAsync(NotificationsDbContext db, Guid tenantId, Guid deliveryId, CancellationToken ct)
            => (from item in db.DeliveryItems.Where(i => i.TenantId == tenantId && i.DeliveryId == deliveryId)
                join request in db.Requests on new { item.TenantId, Id = item.RequestId } equals new { request.TenantId, request.Id }
                where request.ExpiresAt != null
                select request.ExpiresAt).MinAsync(ct);
    }
}
