using Juice.MediatR;
using Juice.Messaging.Outbox.Delivery.Internal;
using Juice.Messaging.Policies;
using Juice.Notifications.Applications;
using Juice.Notifications.Data;
using Juice.Notifications.Delivering;
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
        /// calling <c>keycloak:8080</c> while tokens name <c>localhost:8081</c>). The issuer is still <see cref="Authority"/>.
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
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, connectionString));
            // A send that is under way finishes and is recorded on shutdown (the dispatcher doesn't cancel sends): up to about 27 s for SMTP, plus
            // recording and saving. The container's stop grace period must be longer still (deploy/local: stop_grace_period).
            services.Configure<HostOptions>(host => host.ShutdownTimeout = host.ShutdownTimeout < TimeSpan.FromSeconds(45) ? TimeSpan.FromSeconds(45) : host.ShutdownTimeout);
            services.TryAddSingleton(TimeProvider.System);
            services.AddHostedService<DatabaseMigrator>();

            services.AddMediatR(mediator => mediator.RegisterServicesFromAssemblyContaining<CreateApplication>(includeNonPublicTypes: true));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(NotificationsTransactionBehavior<,>));
            services.AddNotificationsMessaging();

            services.AddScoped<ApplicationReader>();
            services.AddScoped<ApplicationEndpoints.Sender>();

            // Channels (design D24): phase 1 registers webhook and email; their provider packages take this over (phase-1-plan M6).
            services.AddSingleton(BuiltInChannels.Webhook);
            services.AddSingleton(BuiltInChannels.Email);
            services.TryAddSingleton<IChannelRegistry, ChannelRegistry>();
            services.AddSingleton<Types.DataSchemas>();
            services.AddScoped<Types.TypeReader>();
            services.AddScoped<Intake.NotificationReader>();
            services.AddScoped<Producers>();
            services.TryAddSingleton<ITenantAccess, ClaimTenantAccess>(); // replace to answer from juice-tenants or local grants
            services.AddScoped<Configuration.ConfigurationReader>();
            services.AddScoped<History.DeliveryHistory>();

            // The pipeline stages (design §4.1). Their workers run unless Stages:Run is false (a host that only takes requests).
            var runStages = configuration.GetValue("Stages:Run", true);
            services.AddPlanning(configuration.GetSection("Stages:Planner").Get<Stages.StageOptions>() ?? new Stages.StageOptions(), runStages);
            services.AddDeliveryPlanning(configuration.GetSection("Stages:DeliveryPlanner").Get<Stages.StageOptions>() ?? new Stages.StageOptions(), runStages);
            services.Configure<Providers.DeliveryOptions>(configuration.GetSection(Providers.DeliveryOptions.Section));
            // A dispatcher lease covers connect + request timeouts with room to spare (design §11).
            var dispatcher = new Stages.StageOptions { Lease = TimeSpan.FromSeconds(60) };
            configuration.GetSection("Stages:Dispatcher").Bind(dispatcher); // settings override the defaults one by one
            services.AddDispatching(dispatcher, runStages);
            services.AddSingleton<Stages.BacklogMonitor>();
            services.AddHostedService(sp => sp.GetRequiredService<Stages.BacklogMonitor>());
            services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

            services.AddNotificationsAuthentication(configuration);
            services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
                context.ProblemDetails.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier);
            services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
            return services;
        }

        /// <summary>
        /// The outbox in this database, on the <c>local</c> route: a message is dispatched in this process right after its transaction commits, and
        /// delivered from the outbox, with retries, if that fails or the process stops first. Stage hand-offs don't use it (phase-1-plan I3); messages
        /// that leave the service will (events to producers, phase 3).
        /// </summary>
        public static IServiceCollection AddNotificationsMessaging(this IServiceCollection services)
        {
            var messaging = services.AddMessaging();
            messaging.AddOutbox();
            messaging.AddLocalChannel();
            messaging.AddLocalPublisher();
            messaging.AddIdempotencyInMemory();
            messaging.AddMessageService<NotificationsDbContext>();
            messaging.AddPublishingPolicies<LocalRoute>();
            messaging.AddDelivery(delivery =>
            {
                delivery.AddDeliveryPolicies(options => options.DefaultPolicy = new PolicyConfiguration
                {
                    Interval = TimeSpan.FromSeconds(2), BatchSize = 50, Timeout = TimeSpan.FromMinutes(1),
                    InitialRetryDelay = TimeSpan.FromSeconds(5), RetryDelayMultiplier = 2, MaxRetryAttempts = 10,
                });
                delivery.AddDeliveryProcessor<NotificationsDbContext>(LocalRoute.Publisher);
            });
            return services;
        }

        /// <summary>Every message takes the <c>local</c> route until messages leave the service.</summary>
        private sealed class LocalRoute : IMessagePublishingPolicy
        {
            public const string Publisher = "local";
            private static readonly IReadOnlyCollection<PublishRoute> Routes = [new PublishRoute(Publisher, string.Empty)];

            public ValueTask<IReadOnlyCollection<PublishRoute>> ResolveAsync(PolicyResolveContext context) => ValueTask.FromResult(Routes);
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
            app.UseAuthentication();
            app.UseAuthorization();
            return app;
        }

        /// <summary><c>/health/live</c>, <c>/health/ready</c> (anonymous) and the API under <c>/api/v1</c>.</summary>
        public static IEndpointRouteBuilder MapJuiceNotifications(this IEndpointRouteBuilder app)
        {
            app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
            app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
            app.MapApplicationEndpoints();
            app.MapProducerEndpoints();
            app.MapConfigurationEndpoints();
            app.MapHistoryEndpoints();
            return app;
        }
    }

    /// <summary>Configuration section <c>Database</c>.</summary>
    public sealed class DatabaseOptions
    {
        public const string Section = "Database";

        /// <summary>Apply pending migrations at start. On for the local stack; a deployment may migrate separately instead.</summary>
        public bool Migrate { get; set; }
    }

    /// <summary>Applies migrations before anything else starts, when <c>Database:Migrate</c> is true.</summary>
    internal sealed class DatabaseMigrator(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<DatabaseMigrator> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var options = configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();
            if (!options.Migrate)
            {
                return;
            }
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                await db.Database.MigrateAsync(cancellationToken);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal sealed class DatabaseHealthCheck(NotificationsDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("database unreachable");
    }
}
