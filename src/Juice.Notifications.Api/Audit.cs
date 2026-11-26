using Juice.Audit;
using Juice.Audit.Api.Scopes;
using Juice.Audit.AspNetCore.Extensions;
using Juice.Notifications.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// Configuration section <c>Audit</c> (p1-09; design §12, §13): changes to a tenant's setup, applications and dead-letter retries are recorded
    /// by <c>services/audit</c>, sent to its gRPC host. On unless turned off, and then the endpoint is required: a deployment doesn't lose its
    /// audit trail by leaving a setting out.
    /// </summary>
    public sealed class AuditOptions
    {
        public const string Section = "Audit";

        public bool Enabled { get; set; } = true;

        /// <summary>The audit service's gRPC address, such as <c>http://audit:8080</c>.</summary>
        public string? Endpoint { get; set; }

        /// <summary>The app on the records.</summary>
        public string AppName { get; set; } = "juice-notifications";
    }

    public static class NotificationsAudit
    {
        /// <summary>
        /// What is recorded, with Juice.Audit's <c>UseAudit()</c>:
        /// <list type="bullet">
        /// <item>an access record for every change through the API (POST, PUT, PATCH, DELETE under <c>/api</c>): who, the path (which names the
        /// tenant, the row, the delivery retried), the outcome, the trace id; refusals included;</item>
        /// <item>a data change for each configuration row it changed (the rows marked <see cref="Juice.Domain.IAuditable"/>), with the values
        /// before and after, once its transaction commits;</item>
        /// <item>configuration provisioning's changes, one record per tenant, under <see cref="ConfigurationProvisioner.Actor"/>.</item>
        /// </list>
        /// Not recorded: producers' notification requests (<c>POST …/notifications</c>), which are traffic, not changes to the service; their
        /// history is the request itself. Saving a record is best effort (Juice.Audit logs a failure and the change stands): deferred #22.
        /// </summary>
        public static IServiceCollection AddNotificationsAudit(this IServiceCollection services, IConfiguration configuration)
        {
            var options = configuration.GetSection(AuditOptions.Section).Get<AuditOptions>() ?? new AuditOptions();
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
            if (!options.Enabled)
            {
                return services;
            }
            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException(
                    "Audit:Endpoint is required: the audit service's gRPC address (http://audit:8080). Set Audit:Enabled to false to run without audit records.");
            }
            services.ConfigureAuditGrpcClient(grpc => grpc.Address = endpoint);
            services.AddAuditScopeRunner(scope => scope.AppName = options.AppName);
            services.Replace(ServiceDescriptor.Scoped<IProvisioningScope, AuditedProvisioningScope>());
            return services;
        }

        /// <summary>Records changes through the API; and names the path's tenant on the record. Does nothing when audit is off.</summary>
        public static IApplicationBuilder UseNotificationsAudit(this IApplicationBuilder app)
        {
            var options = app.ApplicationServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuditOptions>>().Value;
            if (!options.Enabled)
            {
                return app;
            }
            app.UseAudit(options.AppName, filter => filter.Clear()
                .Include("/api/#", "POST", "PUT", "PATCH", "DELETE")
                // Later rules win: a producer's request is traffic, not a change.
                .Exclude("/api/v1/tenants/{tenantId}/notifications", "POST")
                // Nor is a person reading or archiving their own inbox (phase-2-plan M15): their own items, many times a day.
                .Exclude("/api/v1/tenants/{tenantId}/me/inbox/#", "POST", "DELETE")
                // Nor are providers' callbacks (phase-4-plan I40): delivery receipts and bounces, recorded in provider_events.
                .Exclude("/api/v1/callbacks/#", "POST"));
            app.Use(async (http, next) =>
            {
                if (http.RequestServices.GetService<IAuditContextAccessor>() is { IsInitialized: true } accessor)
                {
                    // The tenant comes from the path (phase-1-plan I2), not from a tenant resolver Juice.Audit would ask: a tenant-scoped route's, or the
                    // tenant an application is linked to or unlinked from (a route the tenant pattern leaves alone).
                    if (Guid.TryParse((http.GetRouteValue("tenantId") ?? http.GetRouteValue("linkedTenantId")) as string, out var tenantId))
                    {
                        accessor.AuditContext.SetTenant(tenantId.ToString());
                    }
                    if (accessor.AuditContext.AccessRecord.User is null && Caller(http.User) is { } caller)
                    {
                        accessor.AuditContext.SetUser(caller);
                    }
                }
                await next(http);
            });
            return app;
        }

        /// <summary>
        /// Who a record names. Juice.Audit reads only a user name (<c>preferred_username</c>, <c>name</c>), which a client-credentials token
        /// often lacks: Keycloak's carry <c>sub</c>, <c>azp</c> and <c>client_id</c> only. A client is named by its client id, so a producer's
        /// or an operator tool's changes say which client made them; anything else by its subject.
        /// </summary>
        internal static string? Caller(System.Security.Claims.ClaimsPrincipal user) => Admin.AdminCaller.Name(user);
    }

    /// <summary>One audit record per tenant provisioned, with the rows it changed, under <see cref="ConfigurationProvisioner.Actor"/>.</summary>
    internal sealed class AuditedProvisioningScope(AuditScopeRunner runner) : IProvisioningScope
    {
        public async ValueTask RunAsync(Guid tenantId, Func<ValueTask> work)
            => await runner.RunAsync(new AuditScopeRequest("Provisioning", AuditScopeMethods.Request, "configuration-provisioning", tenantId.ToString()),
                async () =>
                {
                    await work();
                    return true;
                },
                _ => AuditScopeOutcome.Ok,
                context => context.SetUser(ConfigurationProvisioner.Actor));
    }
}
