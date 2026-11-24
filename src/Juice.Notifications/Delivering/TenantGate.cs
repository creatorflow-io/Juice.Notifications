namespace Juice.Notifications.Delivering
{
    /// <summary>
    /// Whether a tenant's deliveries may go out now (the tenant pattern decision record): a tenant that isn't active (suspended, still being
    /// set up, unknown) has its deliveries held, not dropped; they go out once it is active again, and expire as they would anyway. The core
    /// doesn't know where tenants are kept: the host answers (Juice.Notifications.Api, from its tenant directory). Without a host's answer,
    /// every tenant is active.
    /// </summary>
    public interface ITenantGate
    {
        ValueTask<bool> IsActiveAsync(Guid tenantId, CancellationToken ct);
    }

    internal sealed class OpenTenantGate : ITenantGate
    {
        public ValueTask<bool> IsActiveAsync(Guid tenantId, CancellationToken ct) => ValueTask.FromResult(true);
    }
}
