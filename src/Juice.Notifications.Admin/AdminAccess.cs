using System.Security.Claims;
using Juice.Notifications.Api.Contracts;

namespace Juice.Notifications.Admin
{
    /// <summary>
    /// What a caller may do in a tenant's notifications (phase-5-plan p5-d6). Read: pages and history. Operate: retry and replay deliveries.
    /// Configure: every change (integrations, targets, routes, mutes, policies, allowlist, templates, settings, correlation actions).
    /// </summary>
    [Flags]
    public enum AdminRights { None = 0, Read = 1, Operate = 2, Configure = 4, All = Read | Operate | Configure }

    /// <summary>
    /// Who may administer which tenant's notifications. The service answers from its tenant access (<c>ITenantAccess</c>); an embedded host
    /// answers from its own roles (<see cref="SingleTenantAdminAccess"/>).
    /// </summary>
    public interface INotificationsAdminAccess
    {
        ValueTask<AdminRights> RightsAsync(ClaimsPrincipal user, Guid tenantId, CancellationToken ct = default);

        /// <summary>The tenants the caller has any right in: every one (<see cref="AdminTenants.Every"/>), or those listed. Agrees with RightsAsync.</summary>
        ValueTask<AdminTenants> TenantsAsync(ClaimsPrincipal user, CancellationToken ct = default);
    }

    public sealed record AdminTenants(bool Every, IReadOnlyList<Guid> Ids);

    /// <summary>
    /// For an embedded host (phase-5-plan I61): its one tenant; any other tenant is <see cref="AdminRights.None"/>. The host maps its roles in
    /// <see cref="RightsAsync(ClaimsPrincipal, CancellationToken)"/>.
    /// </summary>
    public abstract class SingleTenantAdminAccess(Guid tenantId) : INotificationsAdminAccess
    {
        /// <summary>The host's tenant.</summary>
        protected Guid TenantId { get; } = tenantId;

        /// <summary>What the caller may do in the host's tenant, from the host's own roles.</summary>
        protected abstract ValueTask<AdminRights> RightsAsync(ClaimsPrincipal user, CancellationToken ct);

        public async ValueTask<AdminRights> RightsAsync(ClaimsPrincipal user, Guid tenantId, CancellationToken ct = default)
            => tenantId == TenantId ? await RightsAsync(user, ct) : AdminRights.None;

        public async ValueTask<AdminTenants> TenantsAsync(ClaimsPrincipal user, CancellationToken ct = default)
            => new(false, await RightsAsync(user, ct) != AdminRights.None ? [TenantId] : []);
    }

    /// <summary>The rights as the access answer names them (<see cref="AccessRights"/>).</summary>
    internal static class AdminRightNames
    {
        public static IReadOnlyList<string> Of(AdminRights rights)
            => [.. new[] { (AdminRights.Read, AccessRights.Read), (AdminRights.Operate, AccessRights.Operate), (AdminRights.Configure, AccessRights.Configure) }
                .Where(r => rights.HasFlag(r.Item1)).Select(r => r.Item2)];
    }

    /// <summary>
    /// The service's hook for the endpoints a producer also reads (its own deliveries; its correlation by key): a caller with no admin right in
    /// the tenant is its producer application, or the refusal. An embedded host registers none, so such a caller is refused as no admin.
    /// </summary>
    internal interface IAdminFallbackCaller
    {
        ValueTask<(Guid? ApplicationId, IResult? Refusal)> ResolveAsync(ClaimsPrincipal user, CancellationToken ct);
    }

    /// <summary>The service's platform role, for the access answer's <c>platformAdmin</c>; an embedded host has none.</summary>
    internal interface IAdminPlatformRole
    {
        bool IsPlatformAdmin(ClaimsPrincipal user);
    }

    /// <summary>
    /// Who a change names (audit actors, <c>by</c>): a person by their user name; a client by its client id (<c>client:{id}</c>); else the
    /// subject. A client-credentials token often has no user name (Keycloak's carry <c>sub</c>, <c>azp</c> and <c>client_id</c> only).
    /// </summary>
    internal static class AdminCaller
    {
        public static string? Name(ClaimsPrincipal user)
        {
            if (user.Identity?.IsAuthenticated != true)
            {
                return null;
            }
            string? Claim(string type) => user.FindFirst(type)?.Value is { Length: > 0 } value ? value : null;
            if ((Claim("preferred_username") ?? Claim("name")) is { } name)
            {
                return name;
            }
            if ((Claim("client_id") ?? Claim("azp")) is { } client)
            {
                return "client:" + client;
            }
            return Claim("sub");
        }
    }
}
