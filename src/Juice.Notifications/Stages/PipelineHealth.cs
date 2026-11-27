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
    internal sealed class NotificationsPipelineHealth(NotificationsDbContext db, StagePasses passes, StagesRunning running, TimeProvider clock,
        Microsoft.Extensions.Options.IOptions<Providers.DeliveryOptions>? delivery = null)
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
                Sends = await SendsAsync(now, cancellationToken),
            };
        }

        private static readonly AttemptOutcome[] Succeeded = [AttemptOutcome.Delivered, AttemptOutcome.Accepted];
        private static readonly AttemptOutcome[] Failed = [AttemptOutcome.RetryableFailure, AttemptOutcome.PermanentFailure, AttemptOutcome.Unknown];
        /// <summary>
        /// When the oldest delivery in flight, not held by its tenant's gate, was created. The statuses are constants, not a parameter, so the SQL says
        /// <c>status IN ('Pending', 'Sending', 'Retrying')</c> as the partial index <c>ix_deliveries_in_flight</c> does: with a parameter, a
        /// generic or auto-prepared plan can't prove the index's filter and scans the table (357 ms against 0.06 ms on QA's 2,000,000 rows).
        /// </summary>
        internal static IQueryable<DateTimeOffset?> OldestInFlight(IQueryable<Delivery> deliveries) => deliveries
            .Where(d => (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Sending || d.Status == DeliveryStatus.Retrying) && !d.TenantHeld)
            .OrderBy(d => d.CreatedAt).Select(d => (DateTimeOffset?)d.CreatedAt);

        /// <summary>
        /// When the delivery a gate holds whose hold has lapsed (its <c>next_at</c> at or before <paramref name="lapsedBefore"/>, so nobody has
        /// looked at it again for a whole <c>TenantHold</c>) that was created first (the oldest, not the one longest overdue), read through
        /// <c>ix_deliveries_tenant_held</c>, which holds only the held rows. The
        /// statuses and the flag are literals for the same reason as in <see cref="OldestInFlight"/>.
        /// </summary>
        internal static IQueryable<DateTimeOffset?> OldestLapsedHold(IQueryable<Delivery> deliveries, DateTimeOffset lapsedBefore) => deliveries
            .Where(d => d.TenantHeld && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Sending || d.Status == DeliveryStatus.Retrying)
                && d.NextAt <= lapsedBefore)
            .OrderBy(d => d.CreatedAt).Select(d => (DateTimeOffset?)d.CreatedAt);

        /// <summary>
        /// The attempts of the last <see cref="SendHealth.DefaultWindow"/> (through <c>ix_delivery_attempts_at</c>, each count capped), and the oldest
        /// delivery in flight (through <c>ix_deliveries_in_flight</c>): what shows a provider refusing every send, which no stage shows.
        /// </summary>
        private async Task<SendHealth> SendsAsync(DateTimeOffset now, CancellationToken ct)
        {
            var since = now - SendHealth.DefaultWindow;
            var window = db.Attempts.AsNoTracking().Where(a => a.At >= since);
            var succeeded = await window.Where(a => Succeeded.Contains(a.Outcome)).Take(SendHealth.MaxCount).CountAsync(ct);
            var failed = await window.Where(a => Failed.Contains(a.Outcome)).Take(SendHealth.MaxCount).CountAsync(ct);
            var oldest = await OldestInFlight(db.Deliveries.AsNoTracking()).FirstOrDefaultAsync(ct);
            var lapsed = await OldestLapsedHold(db.Deliveries.AsNoTracking(), now - (delivery?.Value ?? new Providers.DeliveryOptions()).TenantHold).FirstOrDefaultAsync(ct);
            oldest = oldest is { } a && lapsed is { } b ? (a < b ? a : b) : oldest ?? lapsed;
            return new SendHealth
            {
                Window = SendHealth.DefaultWindow,
                Succeeded = succeeded,
                Failed = failed,
                OldestUndelivered = oldest is { } created ? now - created : null,
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
