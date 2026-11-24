using Juice.MultiTenant.AspNetCore;
using Juice.Notifications.Delivering;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// The tenant pattern (juice-docs decision record), as this service uses it (Juice.MultiTenant.AspNetCore):
    /// <list type="bullet">
    /// <item>The request's tenant is the route's <c>tenantId</c> (or the <c>X-Tenant-Id</c> header). A token naming a tenant
    /// (<see cref="AuthOptions.TenantClaim"/>) is in that one: naming another is <c>tenant_forbidden</c>. A platform admin, and a client's own
    /// token (a producer: client credentials, without the tenant-admin role), name any tenant; what a producer may do there is
    /// <see cref="Producers"/>' decision (its application's link to the tenant). A producer that also holds the tenant-admin role is held to its
    /// claim's tenant.</item>
    /// <item>The tenant is then checked in the directory (<see cref="TenancySettings"/>): unknown is <c>tenant_not_found</c> to a platform admin or a
    /// client's own token, and <c>tenant_forbidden</c> to the rest; starting is 503 <c>tenant_starting</c>; otherwise not active is
    /// <c>tenant_inactive</c>. So any service account of the realm can learn whether a tenant exists and its status, though nothing in it.</item>
    /// <item>A request to a tenant that isn't active is refused over HTTP; one that arrives over RabbitMQ (no caller to answer) is taken, and its
    /// deliveries are held like any other of the tenant: they go out once it is active, or expire. With <c>Configured</c>, a tenant missing from
    /// the list counts as not active.</item>
    /// <item>Who may administer a tenant (<see cref="ITenantAccess"/>, owner's decision 11 Nov): a platform admin every tenant, the
    /// <see cref="AuthOptions.TenantAdminRole"/> the one its claim names. Every tenant-scoped admin endpoint asks it, so how it is answered can
    /// change (juice-tenants' membership, local grants) by registering another implementation.</item>
    /// </list>
    /// </summary>
    public static class NotificationsTenancy
    {
        public static IServiceCollection AddNotificationsTenancy(this IServiceCollection services, IConfiguration configuration)
        {
            var settings = configuration.GetSection(TenancySettings.Section).Get<TenancySettings>() ?? new TenancySettings();
            var tenancy = services.AddJuiceTenancy(configuration);
            // The names come from Auth, as before the pattern; the platform admin is this service's role.
            services.AddOptions<TenancyOptions>().Configure<IOptions<AuthOptions>>((o, auth) =>
            {
                o.TenantClaim = auth.Value.TenantClaim;
                o.TenantAdminRole = auth.Value.TenantAdminRole;
                o.PlatformAdminRole = Roles.PlatformAdmin;
                // A producer's own token names the tenant it sends to; its claim, if it has one, is its own deployment's. A tenant admin's service
                // account is a client too, but bound to the tenant its claim names.
                o.CrossTenantCaller = user => Producers.ActingClient(user, auth.Value) is not null && !user.IsInRole(auth.Value.TenantAdminRole);
            });
            switch (settings.Directory)
            {
                case TenancyDirectory.Configured:
                    tenancy.WithTenants(configuration);
                    break;
                default:
                    tenancy.WithDirectory<OpenTenantDirectory>();
                    break;
            }
            services.AddSingleton<ITenantRejectionWriter, NotificationsTenantRejectionWriter>();
            services.AddSingleton<ITenantGate, DirectoryTenantGate>();
            return services;
        }
    }

    public enum TenancyDirectory
    {
        /// <summary>Every tenant is active: tenants aren't checked. The default until a deployment names its tenants.</summary>
        Open,

        /// <summary>The tenants listed under <c>Tenancy:Tenants</c> (<c>Id</c>, <c>Identifier</c>, <c>Name</c>, <c>Status</c>); others are unknown.</summary>
        Configured,
    }

    /// <summary>Configuration section <c>Tenancy</c> (the shared <c>TenancyOptions</c> are read from it too).</summary>
    public sealed class TenancySettings
    {
        public const string Section = "Tenancy";

        public TenancyDirectory Directory { get; set; } = TenancyDirectory.Open;
    }

    /// <summary>Every tenant is known and active (<see cref="TenancyDirectory.Open"/>).</summary>
    internal sealed class OpenTenantDirectory : ITenantDirectory
    {
        public ValueTask<TenantRecord?> FindAsync(Guid id, CancellationToken ct = default)
            => ValueTask.FromResult<TenantRecord?>(new TenantRecord(id, null, null, MultiTenant.TenantAvailability.Active));

        public ValueTask<TenantRecord?> FindByIdentifierAsync(string identifier, CancellationToken ct = default) => ValueTask.FromResult<TenantRecord?>(null);
    }

    /// <summary>The dispatcher's question, answered by the directory: only an active tenant's deliveries go out.</summary>
    internal sealed class DirectoryTenantGate(ITenantDirectory directory) : ITenantGate
    {
        public async ValueTask<bool> IsActiveAsync(Guid tenantId, CancellationToken ct)
            => await directory.AvailabilityAsync(tenantId, ct) == MultiTenant.TenantAvailability.Active;
    }

    /// <summary>
    /// A refused tenant in this API's problem shape (<c>code</c>, <c>detail</c>). A tenant that isn't an Id keeps this API's own code
    /// (<see cref="ErrorCodes.InvalidTenant"/>), which says the same.
    /// </summary>
    internal sealed class NotificationsTenantRejectionWriter : ITenantRejectionWriter
    {
        public Task WriteAsync(HttpContext http, TenantRejection rejection)
            => ApiResults.Problem(rejection.StatusCode, rejection.Code == "tenant_invalid" ? ErrorCodes.InvalidTenant : rejection.Code, rejection.Title)
                .ExecuteAsync(http);
    }

    internal static class TenantAccessFilter
    {
        /// <summary>
        /// The path's tenant must be one the caller administers (<c>not_tenant_admin</c>): a person of the tenant who isn't its admin, or a producer.
        /// Another tenant than the token's was already refused (<c>tenant_forbidden</c>), so responses don't reveal which tenants exist.
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
