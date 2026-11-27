using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.History
{
    /// <summary>Which failed deliveries a bulk replay takes: every field narrows it; <see cref="Limit"/> is at most <see cref="ReplayDeliveries.MaxLimit"/>.</summary>
    public sealed record ReplayFilter(string? Channel, string? Integration, string? Type, FailureCategory? Category, DateTimeOffset? From, DateTimeOffset? To,
        int? Limit);

    /// <summary>
    /// What a replay did: how many it put back, how many it passed over (no content to send, or the request expired), and whether more matched
    /// than the limit (run it again: replayed ones aren't failed any more).
    /// </summary>
    public sealed record ReplayedView(int Replayed, int Skipped, bool More);

    /// <summary>
    /// Bulk replay (design §14, phase-4-plan I44): failed deliveries matching a filter go back to <c>Pending</c>, each as the single retry does it
    /// (<see cref="RetryDelivery"/>): the same id and content, attempts kept and numbered on, a fresh budget. At most 10,000 a call, oldest first.
    /// Admins only; the call is audited as any change through the API.
    /// </summary>
    public sealed record ReplayDeliveries(Guid NotificationTenantId, ReplayFilter Filter) : MessageBase, Configuration.ITenantCommand, IRequest<Outcome<ReplayedView>>
    {
        public const int MaxLimit = 10_000;
    }

    internal sealed class ReplayDeliveriesHandler(NotificationsDbContext db, TimeProvider clock) : IRequestHandler<ReplayDeliveries, Outcome<ReplayedView>>
    {
        public async ValueTask<Outcome<ReplayedView>> Handle(ReplayDeliveries r, CancellationToken ct)
        {
            var f = r.Filter;
            var limit = f.Limit ?? 1000;
            if (limit is < 1 or > ReplayDeliveries.MaxLimit)
            {
                return Outcome<ReplayedView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/limit", $"From 1 to {ReplayDeliveries.MaxLimit}.")]);
            }
            if (f.From is { } from && f.To is { } to && from > to)
            {
                return Outcome<ReplayedView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.", [new FieldError("/from", "from is before to.")]);
            }
            var tenant = r.NotificationTenantId;
            var now = clock.UtcNow();
            var failed = db.Deliveries.Where(d => d.TenantId == tenant && d.Status == DeliveryStatus.Failed);
            if (f.Channel is { } channel)
            {
                failed = failed.Where(d => d.Channel == channel);
            }
            if (f.Integration is { } integrationKey)
            {
                failed = failed.Where(d => db.Integrations.Any(i => i.TenantId == tenant && i.Id == d.IntegrationId && i.Key == integrationKey));
            }
            if (f.Category is { } category)
            {
                failed = failed.Where(d => d.FailureCategory == category);
            }
            if (f.From is { } after)
            {
                failed = failed.Where(d => d.CreatedAt >= after);
            }
            if (f.To is { } before)
            {
                failed = failed.Where(d => d.CreatedAt < before);
            }
            if (f.Type is { } type)
            {
                failed = failed.Where(d => db.DeliveryItems.Any(i => i.TenantId == tenant && i.DeliveryId == d.Id
                    && db.Requests.Any(q => q.TenantId == tenant && q.Id == i.RequestId && q.TypeKey == type)));
            }
            var matched = await failed.OrderBy(d => d.Id).Take(limit + 1)
                .Select(d => new
                {
                    d.Id,
                    // DeliveryRules.HasContent, in SQL: only whether there is content, not the content (up to 10,000 rendered emails).
                    HasContent = d.Content != "" && d.Content != "{}",
                    Expired = db.DeliveryItems.Any(i => i.TenantId == tenant && i.DeliveryId == d.Id
                        && db.Requests.Any(q => q.TenantId == tenant && q.Id == i.RequestId && q.ExpiresAt != null && q.ExpiresAt <= now)),
                })
                .ToListAsync(ct);
            var more = matched.Count > limit;
            var batch = matched.Take(limit).ToList();
            var candidates = batch.Where(d => d.HasContent && !d.Expired).Select(d => d.Id).ToArray();
            var ids = new List<Guid>();
            if (candidates.Length > 0)
            {
                // Every one locked before any is changed, deliveries then recipients then requests, as a single retry locks them: no lock is
                // taken on a delivery while one on a recipient is held, so a replay can't deadlock with a retry or the delivery.failed worker.
                await DeliveryRules.LockAsync(db, tenant, candidates, ct);
                // Only those still failed now that they are locked (one retried or replayed meanwhile is left as it is), and no inbox delivery
                // of a redacted request (its item would show the masked values).
                var redacted = await DeliveryRules.Redacted(db, tenant, candidates).ToListAsync(ct);
                ids = await db.Deliveries.AsNoTracking().Where(d => d.TenantId == tenant && candidates.Contains(d.Id) && d.Status == DeliveryStatus.Failed
                        && !(d.Channel == Channels.Inbox && redacted.Contains(d.Id)))
                    .Select(d => d.Id).ToListAsync(ct);
            }
            var replayed = 0;
            // In chunks: one statement each, as the single retry's. Only the deliveries replayed reset their recipients' delivery.failed.
            foreach (var chunk in ids.Chunk(500))
            {
                replayed += await db.Deliveries.Where(d => d.TenantId == tenant && chunk.Contains(d.Id) && d.Status == DeliveryStatus.Failed)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.Status, DeliveryStatus.Pending)
                        .SetProperty(d => d.TenantHeld, false)
                        .SetProperty(d => d.FailureCategory, (FailureCategory?)null)
                        .SetProperty(d => d.LastError, (string?)null)
                        .SetProperty(d => d.ProviderMessageId, (string?)null)
                        .SetProperty(d => d.CompletedAt, (DateTimeOffset?)null)
                        .SetProperty(d => d.Replays, d => d.Replays + 1)
                        .SetProperty(d => d.Attempts, d => Math.Max(d.Attempts,
                            db.Attempts.Where(a => a.TenantId == d.TenantId && a.DeliveryId == d.Id).Max(a => (int?)a.Number) ?? 0))
                        .SetProperty(d => d.AttemptsAtReplay, d => Math.Max(d.Attempts,
                            db.Attempts.Where(a => a.TenantId == d.TenantId && a.DeliveryId == d.Id).Max(a => (int?)a.Number) ?? 0))
                        .SetProperty(d => d.ReplayedAt, now)
                        .SetProperty(d => d.StepFailures, 0)
                        .SetProperty(d => d.ClaimedBy, (string?)null)
                        .SetProperty(d => d.LeaseUntil, (DateTimeOffset?)null)
                        .SetProperty(d => d.NextAt, now)
                        .SetProperty(d => d.Version, d => d.Version + 1)
                        .SetProperty(d => d.FailureCheckDue, false), ct);
                // Replayed, they may fail again: their recipients can hear delivery.failed again.
                await db.Recipients.Where(x => x.TenantId == tenant && x.FailureRaisedAt != null
                        && db.DeliveryItems.Any(i => i.TenantId == tenant && chunk.Contains(i.DeliveryId) && i.RecipientId == x.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.FailureRaisedAt, (DateTimeOffset?)null), ct);
            }
            return Outcome<ReplayedView>.Ok(new ReplayedView(replayed, batch.Count - replayed, more));
        }
    }
}
