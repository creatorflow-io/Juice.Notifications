namespace Juice.Notifications
{
    /// <summary>
    /// What this build of the pipeline can do, for a host that checks at run time before relying on it (Observability's shadow mode reads it by
    /// reflection; an absent flag is "not supported"). Properties, not constants, so a consumer reads the referenced build's value.
    /// </summary>
    public static class NotificationsCapabilities
    {
        /// <summary>True when this pipeline can run with <c>Delivery:DryRun</c> (records would_send, never sends).</summary>
        public static bool DryRun { get; } = true;
    }
}
