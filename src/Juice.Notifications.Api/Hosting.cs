using Juice.MediatR;
using Juice.Messaging.Outbox.Delivery.Internal;
using Juice.Messaging.Policies;
using Juice.Notifications.Applications;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Delivering;
using Juice.Notifications.Hosting;
using Juice.Notifications.Planning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Juice.Notifications.Api
{
    /// <summary>Configuration section <c>Auth</c>: the OIDC issuer whose tokens the API accepts.</summary>
    public sealed class AuthOptions
    {
        public const string Section = "Auth";

        public string Authority { get; set; } = "";
        public string Audience { get; set; } = "juice-notifications";
        public bool RequireHttpsMetadata { get; set; } = true;

        /// <summary>
        /// Where to read the identity provider's metadata when the service reaches it by another address than the token's issuer (a container
        /// calling <c>keycloak:8080</c> while tokens name <c>localhost:8080</c>). The issuer is still <see cref="Authority"/>.
        /// </summary>
        public string? MetadataAddress { get; set; }

        /// <summary>
        /// How a producer token shows it was issued to a client acting as itself (client credentials), not to a person signing in through that
        /// client. <see cref="ProducerProofs.ClientIdClaim"/> (default): the token's <c>client_id</c> equals <c>azp</c>, and the identity
        /// provider puts <c>client_id</c> on client-credentials tokens only (Keycloak: a user-session-note mapper, deploy/local).
        /// <see cref="ProducerProofs.SubjectIsClient"/>: <c>sub</c> equals <c>azp</c>, as RFC 9068 issuers do for client credentials.
        /// </summary>
        public string ProducerProof { get; set; } = ProducerProofs.ClientIdClaim;

        /// <summary>The claim naming the one tenant a tenant admin's token is for, by its id (as Juice.Observability's <c>platform_tenant</c>).</summary>
        public string TenantClaim { get; set; } = "platform_tenant";

        /// <summary>The role that makes the holder an admin of the tenant its <see cref="TenantClaim"/> names.</summary>
        public string TenantAdminRole { get; set; } = "notifications-tenant-admin";
    }

    public static class ProducerProofs
    {
        public const string ClientIdClaim = "client-id-claim";
        public const string SubjectIsClient = "subject-is-client";
    }

    /// <summary>Roles the API checks, from the token's <c>roles</c> claim.</summary>
    public static class Roles
    {
        /// <summary>Registers applications and links them to tenants.</summary>
        public const string PlatformAdmin = "notifications-platform-admin";
    }

    public static class Policies
    {
        public const string PlatformAdmin = "platform-admin";
    }

    public static class NotificationsServiceCollectionExtensions
    {
        /// <summary>
        /// The notification service: its database (<c>ConnectionStrings:Notifications</c>), the mediator with the transaction behavior and the outbox,
        /// authentication, problem details and health checks. Applies migrations at start when <c>Database:Migrate</c> is true.
        /// </summary>
        public static IServiceCollection AddJuiceNotifications(this IServiceCollection services, IConfiguration configuration)
        {
            var connectionString = configuration.GetConnectionString("Notifications")
                ?? throw new InvalidOperationException("ConnectionStrings:Notifications is required.");
            // The pipeline (Juice.Notifications.Hosting), with people; then the service's own parts.
            services.AddNotificationsPipeline(configuration, (_, options) => NotificationsDbContext.Configure(options, connectionString));
            // A send that is under way finishes and is recorded on shutdown (the dispatcher doesn't cancel sends): up to about 27 s for SMTP, plus
            // recording and saving. The container's stop grace period must be longer still (deploy/local: stop_grace_period).
            services.Configure<HostOptions>(host => host.ShutdownTimeout = host.ShutdownTimeout < TimeSpan.FromSeconds(45) ? TimeSpan.FromSeconds(45) : host.ShutdownTimeout);
            // Browser apps on other origins (Cors:Origins; none by default).
            services.AddNotificationsCors(configuration);

            services.AddScoped<ApplicationReader>();
            services.AddScoped<ApplicationEndpoints.Sender>();
            services.AddScoped<Producers>();
            // The tenant pattern: the request's tenant, its status, and who may administer it (TenantAccess.cs).
            services.AddNotificationsTenancy(configuration);
            services.AddScoped<People.SubscriberReader>();
            services.AddScoped<People.DirectoryReader>();
            services.AddScoped<People.PreferenceReader>();
            // Contact points and devices (phase-2-plan M13): their encryption keys are checked at start, with the service's own types.
            services.AddOptions<People.EncryptionOptions>().Bind(configuration.GetSection(People.EncryptionOptions.Section));
            People.ContactEncryptionRegistration.AddContactEncryption(services);
            services.AddScoped<People.ContactPointReader>();
            services.AddScoped<People.ContactVerifier>();
            services.AddScoped<People.DeviceRegistry>();
            services.AddHostedService<People.ServiceTypesProvisioner>();
            services.Configure<People.DeviceOptions>(configuration.GetSection(People.DeviceOptions.Section));
            // The tenant-admin API (Juice.Notifications.Admin): its access from the tenant pattern, producers on the endpoints they share.
            services.AddJuiceNotificationsAdmin();
            services.AddScoped<Admin.INotificationsAdminAccess, TenantAccessAdminAccess>();
            services.AddScoped<Admin.IAdminFallbackCaller, ProducerFallbackCaller>();
            services.AddSingleton<Admin.IAdminPlatformRole, PlatformAdminRole>();
            // Display groups and the inbox (phase-2-plan M15).
            services.AddScoped<Inbox.DisplayGroupReader>();
            services.AddScoped<Inbox.PersonInboxReader>();
            services.AddScoped<Inbox.PersonInboxEditor>();
            services.AddScoped<Inbox.DisplayGroupEditor>();
            // Provider callbacks (phase-4-plan I40): anonymous, verified per provider, limited per integration.
            services.Configure<Callbacks.CallbackOptions>(configuration.GetSection(Callbacks.CallbackOptions.Section));
            services.AddScoped<Callbacks.ProviderCallbacks>();
            services.AddScoped<People.ChatLinks>();
            services.AddCallbackLimits(configuration);
            // After the dispatcher's silent default: inbox changes are told through the hub.
            services.AddInboxHub(configuration);
            if (configuration.GetValue("Stages:Run", true))
            {
                services.AddHostedService<People.DeviceSweep>();
            }
            services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

            services.AddNotificationsAuthentication(configuration);
            services.AddNotificationsAudit(configuration);
            services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
                context.ProblemDetails.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
            services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
            return services;
        }

        /// <summary>JWT bearer from the configured issuer; every endpoint needs a signed-in caller unless it says otherwise.</summary>
        public static IServiceCollection AddNotificationsAuthentication(this IServiceCollection services, IConfiguration configuration)
        {
            var auth = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
            if (auth.ProducerProof is not (ProducerProofs.ClientIdClaim or ProducerProofs.SubjectIsClient))
            {
                throw new InvalidOperationException($"Auth:ProducerProof is {ProducerProofs.ClientIdClaim} or {ProducerProofs.SubjectIsClient}.");
            }
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(auth));
            // Fail at start, not on the first request: JwtBearer only checks these when a token arrives.
            if (string.IsNullOrWhiteSpace(auth.Authority))
            {
                throw new InvalidOperationException("Auth:Authority is required: the OIDC issuer whose tokens the API accepts.");
            }
            if (auth.RequireHttpsMetadata && !auth.Authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Auth:Authority '{auth.Authority}' is not https. Use https, or set Auth:RequireHttpsMetadata to false for local development.");
            }
            // Refused here, at start: JwtBearer would only refuse it on the first request, and then answer 500 to every one.
            if (auth.RequireHttpsMetadata && !string.IsNullOrWhiteSpace(auth.MetadataAddress)
                && !auth.MetadataAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Auth:MetadataAddress '{auth.MetadataAddress}' is not https. Use https, or set Auth:RequireHttpsMetadata to false.");
            }
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = auth.Authority;
                    options.Audience = auth.Audience;
                    options.RequireHttpsMetadata = auth.RequireHttpsMetadata;
                    if (!string.IsNullOrWhiteSpace(auth.MetadataAddress))
                    {
                        options.MetadataAddress = auth.MetadataAddress;
                        options.TokenValidationParameters.ValidIssuer = auth.Authority;
                    }
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters.NameClaimType = "preferred_username";
                    options.TokenValidationParameters.RoleClaimType = "roles";
                    options.TokenValidationParameters.ValidateIssuer = true;
                    options.TokenValidationParameters.ValidateAudience = true;
                    options.TokenValidationParameters.ValidateLifetime = true;
                    // WebSockets and server-sent events can't carry a header: the inbox hub takes the token from the query, and nothing else does.
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            if (context.HttpContext.Request.Path.StartsWithSegments(InboxHub.Path) && context.Request.Query["access_token"] is { Count: 1 } token)
                            {
                                context.Token = token;
                            }
                            return Task.CompletedTask;
                        },
                    };
                });
            services.AddAuthorizationBuilder()
                .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
                .AddPolicy(Policies.PlatformAdmin, p => p.RequireRole(Roles.PlatformAdmin));
            return services;
        }

        /// <summary>The request pipeline the API needs, in order: the message context (correlation id; outermost, so error responses and error logs
        /// keep it), problem details for errors, then authentication and authorization. Call before <see cref="MapJuiceNotifications"/>.</summary>
        public static IApplicationBuilder UseJuiceNotifications(this IApplicationBuilder app)
        {
            app.UseNotificationsMessageContext();
            app.UseExceptionHandler();
            app.UseStatusCodePages();
            // Before authentication: a preflight carries no token, and is answered here.
            if (app.ApplicationServices.GetService<CorsSettings>() is { Origins.Count: > 0 })
            {
                app.UseCors(CorsSettings.Policy);
            }
            app.UseAuthentication();
            // After authentication, so a record names its user; before authorization, so refused changes are recorded too.
            app.UseNotificationsAudit();
            // The request's tenant, checked (the tenant pattern), for the caller authentication found; before authorization, which may ask about it.
            app.UseJuiceTenancy();
            app.UseAuthorization();
            app.UseRateLimiter();
            return app;
        }

        /// <summary><c>/health/live</c>, <c>/health/ready</c> (anonymous) and the API under <c>/api/v1</c>.</summary>
        public static IEndpointRouteBuilder MapJuiceNotifications(this IEndpointRouteBuilder app)
        {
            app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
            app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
            // The tenant-admin API at the root, as before the admin package: the same routes and answers (M44-3).
            app.MapJuiceNotificationsAdmin("");
            app.MapApplicationEndpoints();
            app.MapProducerEndpoints();
            app.MapCorrelationHoldEndpoints();
            app.MapPeopleEndpoints();
            app.MapMeEndpoints();
            app.MapDisplayGroupEndpoints();
            app.MapCallbackEndpoints();
            // A session ends when its token does (a person removed from the tenant gets no new one), and reconnects with a fresh one.
            app.MapHub<InboxHub>(InboxHub.Path, options => options.CloseOnAuthenticationExpiration = true);
            return app;
        }
    }

    internal sealed class DatabaseHealthCheck(NotificationsDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("database unreachable");
    }
}
