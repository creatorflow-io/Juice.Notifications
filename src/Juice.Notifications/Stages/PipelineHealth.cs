using Juice.Notifications.Data;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Stages
{
    /// <summary>
    /// When each stage of this replica last finished a pass (phase-5 M49-0), in memory: a pass that claimed, ran and handed back its rows, or
    /// found nothing due. A pass whose claim failed (the database down) isn't one.
    /// </summary>
    public sealed class StagePasses
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

        internal void Record(string stage, DateTimeOffset at) => _last.AddOrUpdate(stage, at, (_, before) => at > before ? at : before);

        /// <summary>The end of the stage's last pass here, or null before its first.</summary>
        public DateTimeOffset? LastPassAt(string stage) => _last.TryGetValue(stage, out var at) ? at : null;
    }

    /// <summary>Whether this host runs the pipeline's stage workers (<c>Stages:Run</c>), as registered.</summary>
    internal sealed record StagesRunning(bool Value);

    /// <summary>
    /// <see cref="INotificationsPipelineHealth"/> (phase-5 M49-0): this replica's dispatcher's last pass, from <see cref="StagePasses"/>; how long
    /// the oldest delivery and notification due now, and not leased, have been due (the claim's own condition, every tenant), each one read of
    /// its stage's due index (<c>ix_deliveries_due</c>, <c>ix_notifications_due</c>: next_at in order, stopping at the first free row); and
    /// whether the stages run here.
    /// </summary>
    internal sealed class NotificationsPipelineHealth(NotificationsDbContext db, StagePasses passes, StagesRunning running, TimeProvider clock)
        : INotificationsPipelineHealth
    {
        public async ValueTask<PipelineHealth> ReadAsync(CancellationToken cancellationToken = default)
        {
            var now = clock.UtcNow();
            var delivery = await OldestDueAsync(db.Deliveries, now, cancellationToken);
            var notification = await OldestDueAsync(db.Notifications, now, cancellationToken);
            return new PipelineHealth(passes.LastPassAt(DeliveryStageNames.Dispatcher), now - delivery, now - notification, running.Value);
        }

        private static Task<DateTimeOffset?> OldestDueAsync<T>(IQueryable<T> rows, DateTimeOffset now, CancellationToken ct) where T : Claimable
            => rows.AsNoTracking().Where(r => r.NextAt <= now && (r.LeaseUntil == null || r.LeaseUntil < now))
                .OrderBy(r => r.NextAt).Select(r => r.NextAt).FirstOrDefaultAsync(ct);
    }
}
