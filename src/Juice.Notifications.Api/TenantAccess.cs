using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// Who may administer a tenant's notifications: its integrations, targets, routes and allowlist (owner's decision, 11 Nov). Every tenant-scoped
    /// admin endpoint asks this one question, so how it is answered can change without touching them: today from the token
    /// (<see cref="ClaimTenantAccess"/>); later from juice-tenants' membership, or from grants kept here, by registering another implementation.
    /// </summary>
    public interface ITenantAccess
    {
        ValueTask<bool> CanAdministerAsync(ClaimsPrincipal user, Guid tenantId, CancellationToken ct);

        /// <summary>
        /// Whether the caller belongs to the tenant, for their own notifications (<c>me</c>): today, the token names it. An implementation
        /// written before <c>me</c> existed answers no, so it grants no <c>me</c> it never decided on.
        /// </summary>
        ValueTask<bool> IsMemberAsync(ClaimsPrincipal user, Guid tenantId, CancellationToken ct) => ValueTask.FromResult(false);

        /// <summary>
        /// Which tenants the caller administers, for the Console's tenant switcher (phase-4c-plan I50): every tenant, or the ones listed. It must
        /// agree with <see cref="CanAdministerAsync"/>. An implementation written before the Console answers none, so the Console offers nothing
        /// it never decided on (the API still answers by <see cref="CanAdministerAsync"/>).
        /// </summary>
        ValueTask<AdministeredTenants> AdministeredAsync(ClaimsPrincipal user, CancellationToken ct) => ValueTask.FromResult(AdministeredTenants.None);
    }

    /// <summary>The tenants a caller administers: <see cref="Every"/> (a platform admin), or those in <see cref="Ids"/>.</summary>
    public sealed record AdministeredTenants(bool Every, IReadOnlyList<Guid> Ids)
    {
        public static readonly AdministeredTenants None = new(false, []);

        public static AdministeredTenants All { get; } = new(true, []);
    }

    /// <summary>
    /// The token decides, as in Juice.Observability: a platform admin administers every tenant; a tenant admin (the
    /// <see cref="AuthOptions.TenantAdminRole"/> role) administers the one tenant its <see cref="AuthOptions.TenantClaim"/> claim names, by id.
    /// One tenant per token: someone who administers several signs in to each.
    /// </summary>
    public sealed class ClaimTenantAccess(IOptions<AuthOptions> auth) : ITenantAccess
    {
        public ValueTask<bool> CanAdministerAsync(ClaimsPrincipal user, Guid tenantId, CancellationToken ct)
        {
            var administered = Administered(user);
            return ValueTask.FromResult(administered.Every || administered.Ids.Contains(tenantId));
        }

        public ValueTask<bool> IsMemberAsync(ClaimsPrincipal user, Guid tenantId, CancellationToken ct)
            => ValueTask.FromResult(Guid.TryParse(user.FindFirst(auth.Value.TenantClaim)?.Value, out var id) && id == tenantId);

        public ValueTask<AdministeredTenants> AdministeredAsync(ClaimsPrincipal user, CancellationToken ct) => ValueTask.FromResult(Administered(user));

        /// <summary>The one rule both answers come from, so they can't disagree.</summary>
        private AdministeredTenants Administered(ClaimsPrincipal user)
        {
            if (user.IsInRole(Roles.PlatformAdmin))
            {
                return AdministeredTenants.All;
            }
            return user.IsInRole(auth.Value.TenantAdminRole) && Guid.TryParse(user.FindFirst(auth.Value.TenantClaim)?.Value, out var id)
                ? new AdministeredTenants(false, [id])
                : AdministeredTenants.None;
        }
    }

    internal static class TenantAccessFilter
    {
        /// <summary>
        /// The path's tenant must be one the caller administers. An unknown tenant and another tenant get the same 403, so responses don't reveal
        /// which tenants exist.
        /// </summary>
        public static RouteGroupBuilder RequireTenantAdmin(this RouteGroupBuilder group)
        {
            group.AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                var allowed = Guid.TryParse(http.GetRouteValue("tenantId") as string, out var tenantId)
                    && await http.RequestServices.GetRequiredService<ITenantAccess>().CanAdministerAsync(http.User, tenantId, http.RequestAborted);
                return allowed
                    ? await next(context)
                    : ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotTenantAdmin, "The caller doesn't administer this tenant.");
            });
            return group;
        }
    }
}
