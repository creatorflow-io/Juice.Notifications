namespace Juice.Notifications.Api.Contracts
{
    /// <summary>
    /// What the signed-in caller may administer (<c>GET /api/v1/access</c>, phase-4c-plan I50): the Console builds its tenant switcher and menu
    /// from it. <see cref="AllTenants"/>: every tenant (<see cref="Tenants"/> then lists the ones the service knows, to pick from, not a
    /// limit); otherwise exactly <see cref="Tenants"/>. Both come from <c>ITenantAccess</c>. <see cref="PlatformAdmin"/>: the platform admin
    /// role, which the applications API requires (applications and their tenant links).
    /// </summary>
    public sealed record AccessResponse(bool PlatformAdmin, bool AllTenants, IReadOnlyList<AccessTenantResponse> Tenants);

    /// <summary>A tenant the caller administers; its name when the deployment knows it, null otherwise.</summary>
    public sealed record AccessTenantResponse(Guid Id, string? Name);
}
