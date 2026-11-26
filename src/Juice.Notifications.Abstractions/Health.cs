namespace Juice.Notifications
{
    /// <summary>
    /// How the pipeline is keeping up (phase 5, M49): a host's dead-man check reads it to tell a stalled pipeline from a quiet one. Registered
    /// by <c>AddNotificationsPipeline</c> (M49-0), in the service and in an embedded host alike.
    /// </summary>
    public interface INotificationsPipelineHealth
    {
        /// <summary>Reads the pipeline's health now.</summary>
        ValueTask<PipelineHealth> ReadAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// The pipeline's health (phase-5-waves-architecture §4, reshaped for M49-0): init properties only, so later additions aren't breaking.
    /// </summary>
    public sealed record PipelineHealth
    {
        /// <summary>Whether this host runs the stages (<c>Stages:Run</c>).</summary>
        public bool Running { get; init; }

        /// <summary>Every claimable stage, by its name: <c>planner</c>, <c>delivery-planner</c>, <c>digest</c>, <c>escalation</c>, <c>dispatcher</c>.</summary>
        public IReadOnlyDictionary<string, StageHealth> Stages { get; init; } = new Dictionary<string, StageHealth>();

        /// <summary>The largest <see cref="StageHealth.OldestDue"/> across the stages; null when nothing is due anywhere.</summary>
        public TimeSpan? OldestDueAny { get; init; }
    }

    /// <summary>One stage's health: what this replica saw of it, and what the database says across replicas.</summary>
    public sealed record StageHealth
    {
        /// <summary>This replica: when a pass last ended (null: none yet, or the stage doesn't run here).</summary>
        public DateTimeOffset? LastPassEndedAt { get; init; }

        /// <summary>This replica: when a step last finished, with any outcome (completed, failed, lease lost). Recorded after each step.</summary>
        public DateTimeOffset? LastProgressAt { get; init; }

        /// <summary>All replicas (database): how long the oldest row has been due and not leased (<c>next_at &lt;= now</c>, lease free); null when none.</summary>
        public TimeSpan? OldestDue { get; init; }

        /// <summary>
        /// All replicas (database): rows whose latest step failed and that are still to be retried (<c>step_failures &gt; 0</c>, <c>next_at</c>
        /// set), counted up to <see cref="MaxFailing"/>. A row stopped since (a resolve, an acknowledgement) isn't failing.
        /// </summary>
        public int Failing { get; init; }

        /// <summary>Where <see cref="Failing"/> stops counting.</summary>
        public const int MaxFailing = 1000;
    }
}
