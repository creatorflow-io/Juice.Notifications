using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Juice.Notifications.Messaging
{
    /// <summary>
    /// Configuration section <c>Intake:RabbitMQ</c>: the broker the <see cref="NotificationRequested"/> intake consumes from. Off unless
    /// <see cref="Enabled"/>.
    /// </summary>
    public sealed class RabbitMQIntakeOptions
    {
        public const string Section = "Intake:RabbitMQ";

        public bool Enabled { get; set; }

        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 5672;
        public string? VirtualHost { get; set; }

        /// <summary>The service's own broker user: it declares the intake's exchanges and queues and consumes from them.</summary>
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";

        /// <summary>Where producers publish (direct, routing key <c>NotificationRequested</c>). A producer's broker user needs write on it only.</summary>
        public string Exchange { get; set; } = "notifications";
        public string Queue { get; set; } = "notifications.requests";

        /// <summary>
        /// How long a message whose handling failed waits before it comes back. It is the delay queue's message TTL: to change it, delete the
        /// delay queue first, or the service refuses to start.
        /// </summary>
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Unacknowledged messages the broker sends ahead; they are handled one at a time. Passed to Juice, which in 10.13.0 sets it only after
        /// consuming has started, so the broker ignores it and sends the whole queue ahead (docs/deferred.md #19).
        /// </summary>
        public ushort Prefetch { get; set; } = 10;

        /// <summary>How long start-up waits for the broker before the host fails to start.</summary>
        public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>The delay queue and its exchange.</summary>
        public string RetryName => Queue + ".retry";
    }

    /// <summary>The RabbitMQ intake's topology and consumer (phase-1-plan I5).</summary>
    /// <remarks>
    /// <para><b>Topology.</b> Producers publish to <see cref="RabbitMQIntakeOptions.Exchange"/> (direct) with the routing key
    /// <c>NotificationRequested</c>, bound to <see cref="RabbitMQIntakeOptions.Queue"/>. That queue dead-letters to a fanout exchange of the same
    /// name plus <c>.retry</c>, bound to a delay queue whose messages expire after <see cref="RabbitMQIntakeOptions.RetryDelay"/> and dead-letter
    /// back to the intake exchange with the original routing key.</para>
    /// <para><b>Why not Juice's retry tiers.</b> The consumer knows the publishing application only from the message's <c>user_id</c>, which
    /// RabbitMQ checks against the publishing connection. Juice's tiers republish a failed message as the consumer, without <c>user_id</c>, so a
    /// retried message would have no publisher. The broker's own dead-lettering keeps every property, <c>user_id</c> included. So the intake
    /// configures no Juice retry policy for its exchange: a failure is a <c>nack</c> without requeue, which the queue dead-letters. A host that
    /// sets a default consumer retry policy (<c>AddConsumerRetryPolicies</c>) must give this exchange none.</para>
    /// <para><b>No limit.</b> A message that fails every time comes back every <see cref="RabbitMQIntakeOptions.RetryDelay"/> until it is handled
    /// or removed by hand: Juice handlers can't see the broker's <c>x-death</c> count to give up (docs/deferred.md #17). Refusals never fail:
    /// they are logged and acknowledged.</para>
    /// </remarks>
    public static class RabbitMQIntake
    {
        /// <summary>The routing key: <see cref="NotificationRequested"/>'s event name.</summary>
        public const string RoutingKey = nameof(NotificationRequested);

        internal const string ConnectionName = "notifications-intake";
        internal const string ConsumerKey = "notifications-intake";

        /// <summary>
        /// Adds the intake when <c>Intake:RabbitMQ:Enabled</c> is true: the topology, declared at start (the host fails to start if the broker
        /// can't be reached within <see cref="RabbitMQIntakeOptions.StartTimeout"/>), the consumer, and a <c>ready</c> health check on the
        /// connection that reports Degraded, never unhealthy. Call after <c>AddJuiceNotifications</c>, which adds the mediator, the database
        /// and Juice messaging the consumer uses.
        /// </summary>
        public static IServiceCollection AddNotificationsRabbitMQIntake(this IServiceCollection services, IConfiguration configuration)
        {
            var options = configuration.GetSection(RabbitMQIntakeOptions.Section).Get<RabbitMQIntakeOptions>() ?? new RabbitMQIntakeOptions();
            if (!options.Enabled)
            {
                return services;
            }
            Validate(options);
            TracePropagation.Ensure();
            services.AddSingleton(options);
            // Registered before the consumer, so the topology exists before it starts consuming (hosted services start in order).
            services.AddHostedService<TopologyStarter>();
            services.AddEventBus(bus => bus.AddRabbitMQ(rabbit =>
            {
                rabbit.AddConnection(ConnectionName, c =>
                {
                    c.Connection = options.Host;
                    c.Port = options.Port;
                    c.VirtualHost = options.VirtualHost;
                    c.UserName = options.UserName;
                    c.Password = options.Password;
                });
                rabbit.AddInfrastructureTopology(ConnectionName, topology => Declare(topology, options));
                rabbit.AddConsumer(ConsumerKey, options.Queue, ConnectionName, consumer => consumer
                    .ConfigureQos(options.Prefetch)
                    .Subscribe<NotificationRequested, NotificationRequestedHandler>(RoutingKey));
            }));
            // Not Juice's RabbitMQ check: it answers Unhealthy itself (so a registration's failure status never applies) and reconnects inside
            // the probe. A broker outage mustn't take the HTTP API out of rotation.
            services.AddHealthChecks().Add(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration("rabbitmq",
                sp => new BrokerHealthCheck(sp.GetRequiredKeyedService<Juice.EventBus.RabbitMQ.IRabbitMQPersistentConnection>(ConnectionName)),
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, ["ready"], TimeSpan.FromSeconds(5)));
            return services;
        }

        /// <summary>
        /// Juice starts the consumer span, which continues the producer's trace from the message's <c>traceparent</c>, only when its activity
        /// source (<c>Juice</c>) has a listener. Without one the handler sees no trace and the request is stored without it. This listener keeps
        /// the context (propagation only, nothing recorded) whether or not a host exports traces; an exporter that listens too records them.
        /// </summary>
        private static class TracePropagation
        {
            private static int _added;

            public static void Ensure()
            {
                if (Interlocked.Exchange(ref _added, 1) == 0)
                {
                    System.Diagnostics.ActivitySource.AddActivityListener(new System.Diagnostics.ActivityListener
                    {
                        ShouldListenTo = source => source.Name == Juice.Messaging.Diagnostics.JuiceActivitySource.Name,
                        Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.PropagationData,
                        SampleUsingParentId = (ref System.Diagnostics.ActivityCreationOptions<string> _) => System.Diagnostics.ActivitySamplingResult.PropagationData,
                    });
                }
            }
        }

        private static void Validate(RabbitMQIntakeOptions options)
        {
            string? problem = null;
            if (string.IsNullOrWhiteSpace(options.Host))
            {
                problem = "Intake:RabbitMQ:Host is required.";
            }
            else if (string.IsNullOrWhiteSpace(options.UserName) || string.IsNullOrEmpty(options.Password))
            {
                problem = "Intake:RabbitMQ:UserName and Password are required: the service's own broker user.";
            }
            else if (string.IsNullOrWhiteSpace(options.Exchange) || string.IsNullOrWhiteSpace(options.Queue))
            {
                problem = "Intake:RabbitMQ:Exchange and Queue can't be empty.";
            }
            else if (options.RetryDelay < TimeSpan.FromSeconds(1) || options.RetryDelay > TimeSpan.FromDays(1))
            {
                problem = "Intake:RabbitMQ:RetryDelay is between 1 second and 1 day.";
            }
            else if (options.Prefetch == 0)
            {
                problem = "Intake:RabbitMQ:Prefetch is at least 1.";
            }
            if (problem is not null)
            {
                throw new InvalidOperationException(problem);
            }
        }

        internal static void Declare(Juice.EventBus.RabbitMQ.Infrastructure.RabbitMQInfrastructureBuilder topology, RabbitMQIntakeOptions options)
        {
            topology.DeclareExchange(options.Exchange, ExchangeType.Direct)
                .DeclareExchange(options.RetryName, ExchangeType.Fanout)
                // No message TTL on the intake queue: a request waits there for as long as the service is away.
                .DeclareQueue(options.Queue, ttlMilliseconds: null, arguments: new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = options.RetryName,
                })
                .DeclareQueue(options.RetryName, ttlMilliseconds: (int)options.RetryDelay.TotalMilliseconds, arguments: new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = options.Exchange,
                    // The message's own routing key would do too; set, so a message dead-lettered from elsewhere still reaches the intake.
                    ["x-dead-letter-routing-key"] = RoutingKey,
                })
                .BindQueue(options.Queue, options.Exchange, RoutingKey)
                .BindQueue(options.RetryName, options.RetryName, "");
        }

        /// <summary>Declares the topology at start, retrying until the broker answers or <see cref="RabbitMQIntakeOptions.StartTimeout"/> passes.</summary>
        private sealed class TopologyStarter(IServiceProvider services, RabbitMQIntakeOptions options, ILogger<TopologyStarter> logger) : IHostedService
        {
            public async Task StartAsync(CancellationToken cancellationToken)
            {
                var deadline = DateTime.UtcNow + options.StartTimeout;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        // Juice's initializer takes no token and retries inside (about a minute with the broker down): bound it here.
                        var remaining = deadline - DateTime.UtcNow;
                        await services.InitRabbitMQInfrastructureAsync()
                            .WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken);
                        return;
                    }
                    catch (TimeoutException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new InvalidOperationException($"The RabbitMQ intake's broker {options.Host}:{options.Port} wasn't reachable within {options.StartTimeout}.", ex);
                    }
                    catch (Exception ex) when (IsInequivalent(ex))
                    {
                        // Waiting won't change the broker's answer: the queues exist with other arguments.
                        throw new InvalidOperationException(
                            $"The broker already has the intake's exchanges or queues ({options.Exchange}, {options.Queue}, {options.RetryName}) with other " +
                            "arguments, such as another RetryDelay or queue type. Delete the ones that differ (an empty delay queue can go at any time) " +
                            "and start again.", ex);
                    }
                    catch (Exception ex) when (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "The RabbitMQ intake's broker {Host}:{Port} isn't reachable yet (attempt {Attempt}); trying again.",
                            options.Host, options.Port, attempt);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)), cancellationToken);
                    }
                }
            }

            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            /// <summary>The broker refused a declaration that differs from what it has (406 PRECONDITION_FAILED, "inequivalent arg").</summary>
            internal static bool IsInequivalent(Exception ex)
            {
                for (var e = ex; e is not null; e = e.InnerException)
                {
                    if (e is RabbitMQ.Client.Exceptions.OperationInterruptedException { ShutdownReason.ReplyCode: 406 })
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
