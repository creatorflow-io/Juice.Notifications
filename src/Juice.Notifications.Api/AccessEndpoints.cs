using System.Security.Claims;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Api
{
    /// <summary><c>GET /api/v1/access</c> (phase-4c-plan I50): the tenants the caller administers, as <see cref="ITenantAccess"/> decides.</summary>
    internal static class AccessEndpoints
    {
        public static void MapAccessEndpoints(this IEndpointRouteBuilder app)
            => app.MapGet("/api/v1/access", GetAsync).RequireAuthorization().WithTags("Access");

        private static async Task<IResult> GetAsync(ClaimsPrincipal user, ITenantAccess access, NotificationsDbContext db, CancellationToken ct)
        {
            var administered = await access.AdministeredAsync(user, ct);
            // Every tenant: those the service knows, which are those with a linked application (a tenant without one has nothing to administer
            // yet). In id order, so the switcher's order is stable.
            IReadOnlyList<Guid> ids = administered.Every
                ? await db.ApplicationTenants.AsNoTracking().Select(t => t.TenantId).Distinct().OrderBy(id => id).ToListAsync(ct)
                : administered.Ids.Distinct().Order().ToList();
            return Results.Ok(new AccessResponse(user.IsInRole(Roles.PlatformAdmin), ids.Select(id => new AccessTenantResponse(id, null)).ToList()));
        }
    }
}
