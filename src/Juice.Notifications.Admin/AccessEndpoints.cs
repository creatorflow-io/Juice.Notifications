using System.Security.Claims;
using Juice.Notifications.Admin;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// <c>GET {prefix}/api/v1/access</c> (phase-4c-plan I50, m44-architecture §3.4): the tenants the caller administers and its rights in each, as
    /// <see cref="INotificationsAdminAccess"/> decides, the hosting mode and the pages it has. A caller with no right gets no tenants (the
    /// Console's no-access page), not a refusal.
    /// </summary>
    internal static class AccessEndpoints
    {
        public static void MapAccessEndpoints(this IEndpointRouteBuilder admin, IReadOnlyList<string> pages)
            => admin.MapGet("/api/v1/access", (ClaimsPrincipal user, INotificationsAdminAccess access, NotificationsHosting hosting, NotificationsDbContext db,
                    IServiceProvider services, CancellationToken ct) => GetAsync(user, access, hosting, db, services, pages, ct))
                .WithTags("Access");

        private static async Task<IResult> GetAsync(ClaimsPrincipal user, INotificationsAdminAccess access, NotificationsHosting hosting, NotificationsDbContext db,
            IServiceProvider services, IReadOnlyList<string> pages, CancellationToken ct)
        {
            var administered = await access.TenantsAsync(user, ct);
            // Every tenant: listed are those the service knows, which are those with a linked application, in id order so the switcher's
            // order is stable. Any other tenant is still the caller's (AllTenants): a deep link to it is opened, as the API answers it.
            IReadOnlyList<Guid> ids = administered.Every
                ? await db.ApplicationTenants.AsNoTracking().Select(t => t.TenantId).Distinct().OrderBy(id => id).ToListAsync(ct)
                : administered.Ids.Distinct().Order().ToList();
            var tenants = new List<AccessTenantResponse>(ids.Count);
            foreach (var id in ids)
            {
                var rights = await access.RightsAsync(user, id, ct);
                if (rights != AdminRights.None)
                {
                    tenants.Add(new AccessTenantResponse(id, null, AdminRightNames.Of(rights)));
                }
            }
            var platformAdmin = hosting.Mode == NotificationsMode.Service && services.GetService<IAdminPlatformRole>()?.IsPlatformAdmin(user) == true;
            return Results.Ok(new AccessResponse(platformAdmin, administered.Every, tenants,
                hosting.Mode == NotificationsMode.Embedded ? AccessModes.Embedded : AccessModes.Service, pages));
        }
    }
}
