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
    public sealed record RetryDelivery(Guid NotificationTenantId, Guid DeliveryId) : MessageBase, IRequest<Outcome<RetriedView>>;

    internal sealed class RetryDeliveryHandler(NotificationsDbContext db, TimeProvider clock) : IRequestHandler<RetryDelivery, Outcome<RetriedView>>
    {
        public async ValueTask<Outcome<RetriedView>> Handle(RetryDelivery r, CancellationToken ct)
        {
            var delivery = await db.Deliveries.SingleOrDefaultAsync(d => d.TenantId == r.NotificationTenantId && d.Id == r.DeliveryId, ct);
            if (delivery is null)
            {
                return Outcome<RetriedView>.NotFound($"There is no delivery {r.DeliveryId} in this tenant.");
            }
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
            delivery.StepFailures = 0;
            delivery.ClaimedBy = null;
            delivery.LeaseUntil = null;
            delivery.NextAt = now;
            delivery.Version++;
            return Outcome<RetriedView>.Ok(new RetriedView(delivery.Id, delivery.Status, delivery.Attempts, delivery.Replays));
        }
    }

    public static class DeliveryRules
    {
        /// <summary>Content the dispatcher can send: a delivery that failed while it was planned (its template failed) stores none.</summary>
        /// <c>{}</c> is what deliveries planned before 12 Nov 2026 stored instead; no rendered content is ever exactly that (it is wrapped per channel).
        public static bool HasContent(Delivery delivery) => delivery.Content is { Length: > 0 } and not "{}";

        /// <summary>When the request a delivery carries expires, if it does (the earliest, for a digest).</summary>
        public static Task<DateTimeOffset?> ExpiresAtAsync(NotificationsDbContext db, Guid tenantId, Guid deliveryId, CancellationToken ct)
            => (from item in db.DeliveryItems.Where(i => i.TenantId == tenantId && i.DeliveryId == deliveryId)
                join recipient in db.Recipients on new { item.TenantId, Id = item.RecipientId } equals new { recipient.TenantId, recipient.Id }
                join notification in db.Notifications on new { recipient.TenantId, Id = recipient.NotificationId } equals new { notification.TenantId, notification.Id }
                join request in db.Requests on new { notification.TenantId, Id = notification.RequestId } equals new { request.TenantId, request.Id }
                where request.ExpiresAt != null
                select request.ExpiresAt).MinAsync(ct);
    }
}
