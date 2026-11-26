using Juice.MediatR;
using Juice.Messaging.Outbox.Delivery.Internal;
using Juice.Messaging.Policies;
using Juice.Notifications.Applications;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Delivering;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Hosting
{
    /// <summary>Which parts of the pipeline a host has (phase-5-plan I57).</summary>
    public sealed class NotificationsPipelineOptions
    {
        /// <summary>
        /// People as recipients: the local, topic and identity resolvers (and the identity directory). Off, a request names routes and
        /// addresses only (design §4.2, embedded): a person recipient is refused at intake (no resolver handles its kind), and a resolver of a
        /// people kind can't be configured (none is installed).
        /// </summary>
        public bool People { get; set; } = true;

        /// <summary>
        /// The host's own services that must start after the migrations and the provisioning file but before the stages take anything
        /// (hosted services start in the order they are registered): an embedded host's application and types.
        /// </summary>
        public Action<IServiceCollection>? BeforeStages { get; set; }
    }

    /// <summary>Warns at start when the dispatcher is in dry run (phase-5-plan I67): deliveries planned meanwhile are recorded, not sent.</summary>
    internal sealed class DryRunNotice(Microsoft.Extensions.Options.IOptions<Providers.DeliveryOptions> options, ILogger<DryRunNotice> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (options.Value.DryRun)
            {
                logger.LogWarning("Dispatcher in dry run: deliveries are recorded as would-send and not sent");
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public static class NotificationsPipelineRegistration
    {
        /// <summary>
        /// The pipeline both hosting modes run (design §4.1, phase-5-plan I57): its database (<paramref name="database"/> configures
        /// <see cref="NotificationsDbContext"/>), the mediator with the transaction behavior and the outbox's local route, intake and the reads
        /// over it, configuration provisioning, the stages (planner with workflow and escalation, delivery planner and digests, dispatcher),
        /// storms, retention and the backlog monitor. <paramref name="configuration"/> is this service's settings (<c>Stages</c>,
        /// <c>Retention</c>, <c>Storms</c>, <c>Delivery</c>, <c>Database:Migrate</c>, …): the service's root, or an embedded host's section.
        /// The service's own parts (authentication, tenancy, people's pages, the inbox hub, callbacks, audit) are the host's to add.
        /// </summary>
        public static IServiceCollection AddNotificationsPipeline(this IServiceCollection services, IConfiguration configuration,
            Action<IServiceProvider, DbContextOptionsBuilder> database, NotificationsPipelineOptions? options = null)
        {
            options ??= new NotificationsPipelineOptions();
            services.AddDbContext<NotificationsDbContext>(database);
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton(new DatabaseMigrator.Settings(configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions()));
            services.AddHostedService<DatabaseMigrator>();
            // After the migrator, before the stages: hosted services start in order (phase-1-plan I12).
            services.AddConfigurationProvisioning(configuration);
            options.BeforeStages?.Invoke(services);

            services.AddMediatR(mediator => mediator.RegisterServicesFromAssemblyContaining<CreateApplication>(includeNonPublicTypes: true));
            // The transaction wraps the pipeline's own commands only: an embedded host's commands go through its own behavior (U2).
            services.AddScoped(typeof(NotificationsTransactionBehavior<,>));
            services.AddSingleton(new NotificationsCommands(NotificationsPipeline.CommandAssemblies));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(NotificationsPipelineTransaction<,>));
            services.AddNotificationsMessaging(configuration);

            // Channels (design D24): webhook and email built in; provider packages add theirs.
            services.AddBuiltInChannels();
            services.AddSingleton<Types.DataSchemas>();
            services.AddScoped<Types.TypeReader>();
            services.AddScoped<Types.DeclarationHash>();
            services.AddScoped<Intake.NotificationReader>();
            services.AddScoped<ConfigurationReader>();
            services.AddScoped<TenantSettingsReader>();
            services.AddScoped<Types.TenantTemplateReader>();
            services.AddScoped<History.DeliveryHistory>();
            // The Console's reads (phase-4c-plan I52); the overview is kept 30 seconds per tenant.
            services.AddScoped<History.Investigation>();
            services.AddMemoryCache();
            // Correlations (phase-3-plan M20).
            services.AddScoped<Correlations.CorrelationLifecycle>();
            services.Configure<Storms.StormOptions>(configuration.GetSection(Storms.StormOptions.Section));
            services.Configure<Retention.RetentionOptions>(configuration.GetSection(Retention.RetentionOptions.Section));
            services.Configure<Providers.SecretsOptions>(configuration.GetSection(Providers.SecretsOptions.Section));

            // The pipeline stages (design §4.1). Their workers run unless Stages:Run is false (a host that only takes requests).
            var runStages = configuration.GetValue("Stages:Run", true);
            // How the pipeline keeps up (phase-5 M49-0), for a host's dead-man check: the stages' passes here and the oldest due rows.
            services.TryAddSingleton<Stages.StagePasses>();
            services.AddSingleton(new Stages.StagesRunning(runStages));
            services.TryAddScoped<INotificationsPipelineHealth, Stages.NotificationsPipelineHealth>();
            services.AddPlanning(configuration.GetSection("Stages:Planner").Get<Stages.StageOptions>() ?? new Stages.StageOptions(), runStages, options.People);
            services.Configure<ResolverOptions>(configuration.GetSection(ResolverOptions.Section));
            if (options.People)
            {
                services.Configure<IdentityOptions>(configuration.GetSection(IdentityOptions.Section));
            }
            services.AddDeliveryPlanning(configuration.GetSection("Stages:DeliveryPlanner").Get<Stages.StageOptions>() ?? new Stages.StageOptions(), runStages);
            services.Configure<Providers.DeliveryOptions>(configuration.GetSection(Providers.DeliveryOptions.Section));
            // The API's and the alias's secret limits never overlap (M46-3): checked as the host resolves them, after any PostConfigure of its own.
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services,
                ServiceDescriptor.Singleton<Microsoft.Extensions.Options.IValidateOptions<Providers.DeliveryOptions>, SecretScopeValidation>());
            services.AddOptions<Providers.DeliveryOptions>().ValidateOnStart();
            // And as the dispatcher and the handlers read it: a host may register IOptions<DeliveryOptions> itself, past the options factory and
            // its validation (QA on #11, as QA D1 found for dry run). Checked as the host starts, before any hosted service starts.
            services.AddHostedService<SecretScopeGuard>();
            // Dry run (I67) is loud: a host in it says so at start.
            services.AddHostedService<DryRunNotice>();
            // A dispatcher lease covers connect + request timeouts with room to spare (design §11).
            var dispatcher = new Stages.StageOptions { Lease = TimeSpan.FromSeconds(60) };
            configuration.GetSection("Stages:Dispatcher").Bind(dispatcher); // settings override the defaults one by one
            services.AddDispatching(dispatcher, runStages);
            if (runStages)
            {
                services.AddSingleton<Retention.RetentionWorker>();
                services.AddHostedService(sp => sp.GetRequiredService<Retention.RetentionWorker>());
            }
            services.AddSingleton<Stages.BacklogMonitor>();
            services.AddHostedService(sp => sp.GetRequiredService<Stages.BacklogMonitor>());
            return services;
        }

        /// <summary>
        /// The outbox in this database, on the <c>local</c> route: a message is dispatched in this process right after its transaction commits, and
        /// delivered from the outbox, with retries, if that fails or the process stops first. Stage hand-offs don't use it (phase-1-plan I3); messages
        /// that leave the service will (events to producers, phase 3).
        /// </summary>
        public static IServiceCollection AddNotificationsMessaging(this IServiceCollection services, IConfiguration? configuration = null)
        {
            // Events to producers (phase-3-plan I37): raised as rows, moved into the outbox by the relay, published on the events route.
            var events = configuration?.GetSection(Events.EventsOptions.Section).Get<Events.EventsOptions>() ?? new Events.EventsOptions();
            if (configuration is not null)
            {
                services.Configure<Events.EventsOptions>(configuration.GetSection(Events.EventsOptions.Section));
            }
            Events.ProducerEventsRegistration.AddProducerEvents(services, runRelay: events.Enabled && configuration?.GetValue("Stages:Run", true) != false);
            var messaging = services.AddMessaging();
            messaging.AddOutbox();
            messaging.AddLocalChannel();
            messaging.AddLocalPublisher();
            messaging.AddIdempotencyInMemory();
            messaging.AddMessageService<NotificationsDbContext>();
            messaging.AddPublishingPolicies<NotificationsRoutes>();
            messaging.AddDelivery(delivery =>
            {
                delivery.AddDeliveryPolicies(options => options.DefaultPolicy = new PolicyConfiguration
                {
                    Interval = TimeSpan.FromSeconds(2), BatchSize = 50, Timeout = TimeSpan.FromMinutes(1),
                    InitialRetryDelay = TimeSpan.FromSeconds(5), RetryDelayMultiplier = 2, MaxRetryAttempts = 10,
                });
                delivery.AddDeliveryProcessor<NotificationsDbContext>(NotificationsRoutes.Local);
                if (events.Enabled)
                {
                    delivery.AddDeliveryProcessor<NotificationsDbContext>(Events.EventsRouting.Publisher);
                }
            });
            return services;
        }

        /// <summary>
        /// Events to producers go to RabbitMQ (the <c>events</c> producer, its topic exchange; the routing key is the event's name,
        /// <c>{app}.notification.{event}</c>); the pipeline's other messages take the <c>local</c> route. A message that isn't the pipeline's
        /// (not of <see cref="NotificationsPipeline.Domain"/>: an embedded host's) gets no route here, so its own policy alone routes it (U2).
        /// </summary>
        private sealed class NotificationsRoutes(Microsoft.Extensions.Options.IOptions<Events.EventsOptions> events) : IMessagePublishingPolicy
        {
            public const string Local = "local";
            private static readonly IReadOnlyCollection<PublishRoute> LocalRoutes = [new PublishRoute(Local, string.Empty)];

            public ValueTask<IReadOnlyCollection<PublishRoute>> ResolveAsync(PolicyResolveContext context)
                => ValueTask.FromResult<IReadOnlyCollection<PublishRoute>>(context.Domain != NotificationsPipeline.Domain ? []
                    : events.Value.Enabled && context.EventType == nameof(Events.NotificationEvent)
                        ? [new PublishRoute(Events.EventsRouting.Publisher, events.Value.Exchange)]
                        : LocalRoutes);
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
    internal sealed class DatabaseMigrator(IServiceScopeFactory scopes, DatabaseMigrator.Settings settings, ILogger<DatabaseMigrator> logger) : IHostedService
    {
        internal sealed record Settings(DatabaseOptions Options);

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (!settings.Options.Migrate)
            {
                return;
            }
            await NotificationsDatabase.MigrateAsync(scopes, logger, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>The service's schema (<c>notifications</c>, its own migrations history table in it: phase-5-plan I60).</summary>
    public static class NotificationsDatabase
    {
        /// <summary>
        /// Applies this service's pending migrations: a host that migrates its own schema first calls this after (phase-5-plan I60), instead of
        /// <c>Database:Migrate</c> at start.
        /// </summary>
        public static async Task MigrateAsync(IServiceScopeFactory scopes, ILogger? logger, CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger?.LogInformation("Applying {Count} notifications migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                await db.Database.MigrateAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// The overlap check on the options the pipeline reads (<see cref="Providers.SecretReferences.ScopeOverlap"/>), however they were registered:
    /// fails the host's start, before any hosted service starts.
    /// </summary>
    internal sealed class SecretScopeGuard(Microsoft.Extensions.Options.IOptions<Providers.DeliveryOptions> options) : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken)
            => Providers.SecretReferences.ScopeOverlap(options.Value) is { } overlap
                ? throw new Microsoft.Extensions.Options.OptionsValidationException(Microsoft.Extensions.Options.Options.DefaultName, typeof(Providers.DeliveryOptions), [overlap])
                : Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Fails the host's start when the API's and the alias's secret limits overlap (<see cref="Providers.SecretReferences.ScopeOverlap"/>).</summary>
    internal sealed class SecretScopeValidation : Microsoft.Extensions.Options.IValidateOptions<Providers.DeliveryOptions>
    {
        public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, Providers.DeliveryOptions options)
            => Providers.SecretReferences.ScopeOverlap(options) is { } overlap
                ? Microsoft.Extensions.Options.ValidateOptionsResult.Fail(overlap)
                : Microsoft.Extensions.Options.ValidateOptionsResult.Success;
    }
}
