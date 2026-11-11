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
            if (user.IsInRole(Roles.PlatformAdmin))
            {
                return ValueTask.FromResult(true);
            }
            var claimed = user.FindFirst(auth.Value.TenantClaim)?.Value;
            return ValueTask.FromResult(user.IsInRole(auth.Value.TenantAdminRole) && Guid.TryParse(claimed, out var id) && id == tenantId);
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
