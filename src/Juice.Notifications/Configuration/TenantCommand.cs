namespace Juice.Notifications.Configuration
{
    /// <summary>
    /// A command on one tenant's setup (integrations, targets, routes, allowlist, policies, resolvers, settings): a caller that is scoped to one
    /// tenant (an embedded host's scope, phase-5 M46-2) refuses one for another tenant before it runs.
    /// </summary>
    public interface ITenantCommand
    {
        /// <summary>The tenant whose setup the command changes.</summary>
        Guid NotificationTenantId { get; }
    }
}
