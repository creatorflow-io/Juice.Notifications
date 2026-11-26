using System.Text.Json;
using System.Text.Json.Serialization;
using Juice.Notifications.Api;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Notifications.Admin
{
    /// <summary>The admin page an endpoint belongs to (<see cref="AdminPages"/>): mapped only when the mode has the page.</summary>
    public sealed record AdminPageMetadata(string Page);

    /// <summary>The right an endpoint needs, when it isn't the rule (Read for GET, Configure for any change): retry and replay, template preview.</summary>
    public sealed record AdminRightMetadata(AdminRights Right);

    /// <summary>An endpoint a producer also calls for its own rows: a caller with no admin right goes on to it, where the service has the hook.</summary>
    internal sealed record ProducerAlsoMetadata;

    internal static class AdminEndpointConventions
    {
        public static TBuilder Needs<TBuilder>(this TBuilder builder, AdminRights right) where TBuilder : IEndpointConventionBuilder
            => builder.WithMetadata(new AdminRightMetadata(right));

        public static TBuilder ProducerToo<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
            => builder.WithMetadata(new ProducerAlsoMetadata());
    }

    /// <summary>
    /// The admin API's JSON (m44-architecture §6.5): web defaults with camelCase enums, as the service writes. An embedded host's global JSON
    /// options aren't changed, so its own API stays as it is; the admin's answers are written with these instead.
    /// </summary>
    public static class NotificationsJson
    {
        public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
    }

    public static class NotificationsAdminRegistration
    {
        /// <summary>
        /// The admin endpoints' services (the correlation owners, the mutes' reader). The mode comes from the pipeline (<see cref="NotificationsHosting"/>);
        /// the host registers its <see cref="INotificationsAdminAccess"/>. Changes nothing of the host's: no JSON, authentication, authorization,
        /// CORS or problem-details setting.
        /// </summary>
        public static IServiceCollection AddJuiceNotificationsAdmin(this IServiceCollection services)
        {
            services.TryAddScoped<CorrelationEndpoints.Owners>();
            services.TryAddScoped<People.PreferenceReader>();
            return services;
        }

        /// <summary>
        /// Maps the tenant-admin API under <paramref name="prefix"/> (<c>/notifications</c>): <c>{prefix}/api/v1/access</c> and
        /// <c>{prefix}/api/v1/tenants/{tenantId}/…</c> for the pages the mode has (<see cref="AdminPages"/>). Every endpoint needs a signed-in caller
        /// (<c>RequireAuthorization()</c>), then its right (<see cref="AdminRights"/>): none is <c>not_tenant_admin</c>, too few is
        /// <c>not_allowed</c>; embedded, a tenant other than the host's is <c>tenant_forbidden</c> before the host's access is asked. Returns the
        /// group, so a host can add its own policy. The admin assumes bearer tokens: a host on cookies must add antiforgery for POST, PUT and DELETE.
        /// Throws when no <see cref="INotificationsAdminAccess"/> is registered, or when the prefix isn't empty or <c>/a/b</c> (no trailing <c>/</c>,
        /// no route parameters).
        /// </summary>
        public static RouteGroupBuilder MapJuiceNotificationsAdmin(this IEndpointRouteBuilder app, string prefix = "")
        {
            ArgumentNullException.ThrowIfNull(prefix);
            if (prefix.Length > 0 && (!prefix.StartsWith('/') || prefix.EndsWith('/') || prefix.Contains('{') || prefix.Contains('}') || prefix.Contains("//")))
            {
                throw new ArgumentException($"The admin prefix '{prefix}' is empty or a path such as /notifications: a leading '/', no trailing '/', no route parameters.",
                    nameof(prefix));
            }
            var services = app.ServiceProvider;
            if (services.GetService<IServiceProviderIsService>() is { } known && !known.IsService(typeof(INotificationsAdminAccess)))
            {
                throw new InvalidOperationException(
                    "MapJuiceNotificationsAdmin needs an INotificationsAdminAccess: register who may administer the notifications (SingleTenantAdminAccess for one tenant).");
            }
            var hosting = services.GetRequiredService<NotificationsHosting>();
            var pages = hosting.Mode == NotificationsMode.Embedded ? AdminPages.Embedded : AdminPages.Service;

            var admin = prefix.Length == 0 ? app.MapGroup("") : app.MapGroup(prefix);
            admin.RequireAuthorization();
            admin.AddEndpointFilter(MessageContextAsync);
            if (hosting.Mode == NotificationsMode.Embedded)
            {
                // The service's global JSON options are these already; an embedded host's are its own.
                admin.AddEndpointFilter(JsonAsync);
            }
            admin.MapAccessEndpoints(pages);

            var tenant = admin.MapGroup("/api/v1/tenants/{tenantId:guid}");
            tenant.AddEndpointFilter((context, next) => RightsAsync(context, next, hosting));
            bool Has(string page) => pages.Contains(page);
            if (Has(AdminPages.Overview) || Has(AdminPages.Notifications) || Has(AdminPages.Correlations))
            {
                tenant.MapInvestigationEndpoints();
            }
            tenant.MapHistoryEndpoints();
            tenant.MapCorrelationEndpoints();
            tenant.MapConfigurationEndpoints(Has(AdminPages.Resolvers));
            tenant.MapMuteEndpoints();
            return admin;
        }

        /// <summary>The ambient message context a command needs (Juice): the service sets it per request; an embedded host doesn't.</summary>
        private static async ValueTask<object?> MessageContextAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            using var message = NotificationsMessageContext.Begin("admin");
            // Who an embedded host's audit sink names for the changes this request makes (the service's audit reads its own user).
            using var actor = NotificationsActor.As(AdminCaller.Name(context.HttpContext.User));
            return await next(context);
        }

        /// <summary>
        /// Embedded: the path's tenant is the host's (defence in depth: a faulty access implementation can't open another tenant's rows), then
        /// the caller's rights for the endpoint.
        /// </summary>
        private static async ValueTask<object?> RightsAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next, NotificationsHosting hosting)
        {
            var http = context.HttpContext;
            if (!Guid.TryParse(http.GetRouteValue("tenantId") as string, out var tenantId))
            {
                return ApiResults.Problem(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "There is no such tenant.");
            }
            if (hosting.Mode == NotificationsMode.Embedded && tenantId != hosting.EmbeddedTenantId)
            {
                return ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.TenantForbidden, "This deployment's notifications are for one tenant only.");
            }
            var rights = await http.RequestServices.GetRequiredService<INotificationsAdminAccess>().RightsAsync(http.User, tenantId, http.RequestAborted);
            var endpoint = http.GetEndpoint();
            if (rights == AdminRights.None)
            {
                // A producer reading its own rows: the service's hook answers who it is (its handler refuses anyone else). Embedded, there is none.
                return endpoint?.Metadata.GetMetadata<ProducerAlsoMetadata>() is not null && http.RequestServices.GetService<IAdminFallbackCaller>() is not null
                    ? await next(context)
                    : ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotTenantAdmin, "The caller doesn't administer this tenant.");
            }
            var needed = Needed(endpoint, http.Request.Method);
            return (rights & needed) == needed
                ? await next(context)
                : ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotAllowed,
                    $"The caller may read this tenant's notifications but not do this (it needs the {needed.ToString().ToLowerInvariant()} right).");
        }

        /// <summary>The right an endpoint needs: what it names, or Read to read (GET) and Configure for any change.</summary>
        internal static AdminRights Needed(Endpoint? endpoint, string method)
            => endpoint?.Metadata.GetMetadata<AdminRightMetadata>()?.Right ?? (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ? AdminRights.Read : AdminRights.Configure);

        /// <summary>The answer written with <see cref="NotificationsJson.Options"/>: a value (with its status and location) or a problem.</summary>
        private static async ValueTask<object?> JsonAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var result = await next(context);
            return result switch
            {
                Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult problem
                    => Results.Json(problem.ProblemDetails, NotificationsJson.Options, problem.ContentType, problem.StatusCode),
                IValueHttpResult { Value: { } value } valued => new AdminJsonResult(value, (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK,
                    result.GetType().GetProperty("Location")?.GetValue(result) as string),
                _ => result,
            };
        }

        /// <summary>A value written with the admin's own JSON options, keeping its status and its <c>Location</c>.</summary>
        private sealed class AdminJsonResult(object value, int status, string? location) : IResult
        {
            public Task ExecuteAsync(HttpContext http)
            {
                if (location is not null)
                {
                    http.Response.Headers.Location = location;
                }
                return Results.Json(value, NotificationsJson.Options, statusCode: status).ExecuteAsync(http);
            }
        }
    }
}
