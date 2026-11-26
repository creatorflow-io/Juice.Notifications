using Juice.Notifications.Data;
using Juice.Notifications.Delivering;
using Juice.Notifications.Planning;
using Juice.Notifications.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Stages
{
    /// <summary>
    /// What each stage did on this replica (phase-5 M49-0), in memory: when its last pass ended (a pass that claimed, empty or not; never one
    /// whose claim failed), and when its last step finished, with any outcome. Times are kept as recorded, the latest record winning, never the
    /// largest: a clock set back can't keep a stopped stage looking fresh until the clock catches up.
    /// </summary>
    public sealed class StagePasses
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _passes = new(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _progress = new(StringComparer.Ordinal);

        internal void Record(string stage, DateTimeOffset at) => _passes[stage] = at;

        internal void Progress(string stage, DateTimeOffset at) => _progress[stage] = at;

        /// <summary>The end of the stage's last pass here, or null before its first.</summary>
        public DateTimeOffset? LastPassAt(string stage) => _passes.TryGetValue(stage, out var at) ? at : null;

        /// <summary>When the stage's last step here finished, or null before its first.</summary>
        public DateTimeOffset? LastProgressAt(string stage) => _progress.TryGetValue(stage, out var at) ? at : null;
    }

    /// <summary>Whether this host runs the pipeline's stage workers (<c>Stages:Run</c>), as registered.</summary>
    internal sealed record StagesRunning(bool Value);

    /// <summary>
    /// <see cref="INotificationsPipelineHealth"/> (phase-5 M49-0, waves §4): for each claimable stage, this replica's pass and progress times
    /// (<see cref="StagePasses"/>), and from the database, across replicas and tenants, how long its oldest row due now and not leased has been
    /// due (the claim's own condition, through <c>ix_&lt;table&gt;_due</c>: next_at in order, stopping at the first free row) and how many of its
    /// rows are backing off after a failed step (<c>step_failures &gt; 0</c>, through <c>ix_&lt;table&gt;_failing</c>, counted up to 1000).
    /// A row that fails every step moves its next_at ahead, so it is never due for long: <see cref="StageHealth.Failing"/> is what shows it.
    /// </summary>
    internal sealed class NotificationsPipelineHealth(NotificationsDbContext db, StagePasses passes, StagesRunning running, TimeProvider clock)
        : INotificationsPipelineHealth
    {
        public async ValueTask<PipelineHealth> ReadAsync(CancellationToken cancellationToken = default)
        {
            var now = clock.UtcNow();
            var stages = new Dictionary<string, StageHealth>(StringComparer.Ordinal)
            {
                [PlannerStage.StageName] = await StageAsync(PlannerStage.StageName, db.Notifications, now, cancellationToken),
                [DeliveryStageNames.DeliveryPlanner] = await StageAsync(DeliveryStageNames.DeliveryPlanner, db.Recipients, now, cancellationToken),
                [DigestStage.StageName] = await StageAsync(DigestStage.StageName, db.Digests, now, cancellationToken),
                [EscalationStage.StageName] = await StageAsync(EscalationStage.StageName, db.Escalations, now, cancellationToken),
                [DeliveryStageNames.Dispatcher] = await StageAsync(DeliveryStageNames.Dispatcher, db.Deliveries, now, cancellationToken),
            };
            return new PipelineHealth
            {
                Running = running.Value,
                Stages = stages,
                OldestDueAny = stages.Values.Max(s => s.OldestDue),
            };
        }

        private async Task<StageHealth> StageAsync<T>(string stage, IQueryable<T> rows, DateTimeOffset now, CancellationToken ct) where T : Claimable
        {
            var oldest = await rows.AsNoTracking().Where(r => r.NextAt <= now && (r.LeaseUntil == null || r.LeaseUntil < now))
                .OrderBy(r => r.NextAt).Select(r => r.NextAt).FirstOrDefaultAsync(ct);
            // Still to be retried: a row a resolve or an ack stopped (next_at null) keeps its count, but isn't failing.
            var failing = await rows.AsNoTracking().Where(r => r.StepFailures > 0 && r.NextAt != null).Take(StageHealth.MaxFailing).CountAsync(ct);
            return new StageHealth
            {
                LastPassEndedAt = passes.LastPassAt(stage),
                LastProgressAt = passes.LastProgressAt(stage),
                OldestDue = now - oldest,
                Failing = failing,
            };
        }
    }
}
