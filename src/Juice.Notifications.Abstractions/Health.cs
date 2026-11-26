namespace Juice.Notifications
{
    /// <summary>
    /// How the pipeline is keeping up (phase 5, M49): a host's dead-man check reads it to tell a stalled pipeline from a quiet one. Contract only
    /// until M49-0 registers the implementation.
    /// </summary>
    public interface INotificationsPipelineHealth
    {
        /// <summary>Reads the pipeline's health now.</summary>
        ValueTask<PipelineHealth> ReadAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// The pipeline's health: when this replica's dispatcher last made a pass (null before the first), how long the oldest due delivery and the
    /// oldest notification waiting for the planner have been due (null when none is), and whether the pipeline's stages are running here.
    /// </summary>
    public sealed record PipelineHealth(DateTimeOffset? DispatcherLastPassAt, TimeSpan? OldestDueDelivery, TimeSpan? OldestDueNotification, bool Running);
}
