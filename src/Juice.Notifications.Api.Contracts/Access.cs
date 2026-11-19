namespace Juice.Notifications.Api.Contracts
{
    /// <summary>
    /// What the signed-in caller may administer (<c>GET /api/v1/access</c>, phase-4c-plan I50): the Console builds its tenant switcher and menu
    /// from it. <see cref="PlatformAdmin"/>: applications and their tenant links too.
    /// </summary>
    public sealed record AccessResponse(bool PlatformAdmin, IReadOnlyList<AccessTenantResponse> Tenants);

    /// <summary>A tenant the caller administers; its name when the deployment knows it, null otherwise.</summary>
    public sealed record AccessTenantResponse(Guid Id, string? Name);
}
