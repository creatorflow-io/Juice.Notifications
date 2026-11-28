using Juice.Notifications.Admin;
using Juice.Notifications.Api;
using Juice.Notifications.Data;
using Juice.Notifications.Hosting;
using Juice.Notifications.Inbox;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Notifications.Inbox
{
    public static class NotificationsInboxRegistration
    {
        internal const string MeKey = "notifications.me";

        /// <summary>
        /// The inbox's services: the person's reader and editor, SignalR with the inbox's JSON (camelCase enums, for this protocol only), and the
        /// hub that tells open sessions after a commit (<see cref="IInboxEvents"/>). The host registers its <see cref="INotificationsInboxAccess"/>.
        /// Returns SignalR's builder, so a host can add a backplane. Changes nothing of the host's: no JSON, authentication, authorization, CORS or
        /// problem-details setting.
        /// </summary>
        public static ISignalRServerBuilder AddJuiceNotificationsInbox(this IServiceCollection services)
        {
            services.TryAddScoped<PersonInboxReader>();
            services.TryAddScoped<PersonInboxEditor>();
            var signalR = services.AddSignalR().AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
            // Replaces the core's silent default: the inbox provider, the endpoints and the correlations tell sessions through the hub.
            services.Replace(ServiceDescriptor.Singleton<IInboxEvents, HubInboxEvents>());
            return signalR;
        }

        /// <summary>
        /// Maps the person's inbox under <paramref name="prefix"/> (<c>/notifications</c>): <c>{prefix}/api/v1/tenants/{tenantId}/me/inbox</c> (the
        /// list, <c>/counts</c>, <c>/{id}</c>, <c>/{id}/read</c>, <c>/{id}/archive</c>, <c>/read-all</c>; and, with <paramref name="interactions"/>,
        /// <c>/{id}/actions/{a}</c> and <c>/{id}/acknowledge</c>), and the hub at <c>{prefix}/hubs/inbox</c> (a session ends when its token does:
        /// <c>CloseOnAuthenticationExpiration</c>). Every endpoint needs a signed-in caller; embedded, a tenant other than the host's is
        /// <c>tenant_forbidden</c> before the host's access is asked, and the answers are written with <see cref="NotificationsJson.Options"/>.
        /// Returns the group of the API's endpoints, so a host can add its own policy. Throws when no <see cref="INotificationsInboxAccess"/> is
        /// registered, or when the prefix isn't empty or <c>/a/b</c> (no trailing <c>/</c>, no route parameters).
        /// </summary>
        public static RouteGroupBuilder MapJuiceNotificationsInbox(this IEndpointRouteBuilder app, string prefix = "", bool interactions = true)
        {
            ArgumentNullException.ThrowIfNull(prefix);
            if (prefix.Length > 0 && (!prefix.StartsWith('/') || prefix.EndsWith('/') || prefix.Contains('{') || prefix.Contains('}') || prefix.Contains("//")))
            {
                throw new ArgumentException($"The inbox prefix '{prefix}' is empty or a path such as /notifications: a leading '/', no trailing '/', no route parameters.",
                    nameof(prefix));
            }
            var services = app.ServiceProvider;
            if (services.GetService<IServiceProviderIsService>() is { } known && !known.IsService(typeof(INotificationsInboxAccess)))
            {
                throw new InvalidOperationException(
                    "MapJuiceNotificationsInbox needs an INotificationsInboxAccess: register who is a person of the tenant (SingleTenantInboxAccess for one tenant).");
            }
            var hosting = services.GetRequiredService<NotificationsHosting>();

            var inbox = prefix.Length == 0 ? app.MapGroup("") : app.MapGroup(prefix);
            inbox.RequireAuthorization();
            inbox.AddEndpointFilter(MessageContextAsync);
            if (hosting.Mode == NotificationsMode.Embedded)
            {
                // The service's global JSON options are these already; an embedded host's are its own.
                inbox.AddEndpointFilter(JsonAsync);
            }
            var me = inbox.MapGroup("/api/v1/tenants/{tenantId:guid}/me").WithTags("Inbox");
            me.AddEndpointFilter(PersonAsync);
            me.MapInboxEndpoints(interactions);

            // The hub is no route handler (no endpoint filters apply to it): its own authorization, and the same person check inside it.
            var hub = prefix.Length == 0 ? app.MapGroup("") : app.MapGroup(prefix);
            hub.RequireAuthorization();
            hub.MapHub<InboxHub>(InboxHub.Path, options => options.CloseOnAuthenticationExpiration = true);
            return inbox;
        }

        /// <summary>The ambient message context a command needs (Juice): the service sets it per request; an embedded host doesn't.</summary>
        private static async ValueTask<object?> MessageContextAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            using var message = NotificationsMessageContext.Begin("inbox");
            return await next(context);
        }

        /// <summary>The person of the path's tenant, or the refusal: before any endpoint reads or writes.</summary>
        private static async ValueTask<object?> PersonAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var (person, refusal) = Guid.TryParse(http.GetRouteValue("tenantId") as string, out var tenantId)
                ? await InboxPeople.ResolveAsync(http.User, tenantId, http.RequestServices, http.RequestAborted)
                : (null, InboxPeople.NotAMember());
            if (person is not { } subscriberId)
            {
                return refusal;
            }
            http.Items[MeKey] = subscriberId;
            return await next(context);
        }

        /// <summary>The answer written with <see cref="NotificationsJson.Options"/>: a value (with its status) or a problem.</summary>
        private static async ValueTask<object?> JsonAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var result = await next(context);
            return result switch
            {
                Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult problem
                    => Results.Json(problem.ProblemDetails, NotificationsJson.Options, problem.ContentType, problem.StatusCode),
                IValueHttpResult { Value: { } value } => Results.Json(value, NotificationsJson.Options, statusCode: (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK),
                _ => result,
            };
        }
    }
}
