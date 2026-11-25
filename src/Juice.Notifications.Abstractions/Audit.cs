namespace Juice.Notifications
{
    /// <summary>
    /// Where an embedded host records changes to its notification setup (phase-5-plan I62): each configuration row inserted, changed or
    /// deleted (integrations, targets, routes, mutes, escalation policies, the allowlist, settings, applications), with its values before and
    /// after. Called inside the transaction that makes the change, before it commits, so a host that writes its own audit rows commits them
    /// with it. The service records the same through <c>services/audit</c> instead.
    /// </summary>
    public interface INotificationsAuditSink
    {
        ValueTask RecordAsync(NotificationsDataChange change, CancellationToken cancellationToken);
    }

    /// <summary>
    /// One row's change: its table (<c>integrations</c>), the change (<c>Inserted</c>, <c>Modified</c>, <c>Deleted</c>), the tenant, who
    /// made it (null when unknown: the host knows its caller), its key and the values before and after (empty where there are none).
    /// </summary>
    public sealed record NotificationsDataChange(string Table, string Change, string? TenantId, string? User, IReadOnlyDictionary<string, object?> Keys,
        IReadOnlyDictionary<string, object?> Before, IReadOnlyDictionary<string, object?> After);
}
