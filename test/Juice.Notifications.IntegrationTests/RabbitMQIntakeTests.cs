using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Juice.Messaging;
using Juice.Messaging.Integrations;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using static Juice.Notifications.IntegrationTests.DeliveryTests;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The host with the RabbitMQ intake on a virtual host of its own on the local broker, created for the test class and deleted after it, with
    /// a producer broker user that may only publish to the intake exchange and a second broker user no application publishes as.
    /// </summary>
    public sealed class RabbitMQIntakeApp : IAsyncLifetime
    {
        public const string Key = ProducerApp.Key;
        public static readonly Guid Tenant = ProducerApp.Tenant;
        private const string Management = "http://127.0.0.1:15673/api/";

        private readonly string _run = Guid.NewGuid().ToString("N")[..8];
        public string VirtualHost => "notifications-test-" + _run;
        public string Producer => "producer-" + _run;
        public string Stranger => "stranger-" + _run;
        public string Password { get; } = Guid.NewGuid().ToString("N");

        /// <summary>Messages (by idempotency key) the intake is to fail once, and how often each was handled.</summary>
        public FailOnce Failures { get; } = new();

        public NotificationsApp App { get; }
        internal Receiver? Receiver { get; private set; }
        private bool _brokerReady;

        public RabbitMQIntakeApp()
        {
            App = new NotificationsApp(host =>
            {
                host.UseSetting("Intake:RabbitMQ:Enabled", "true");
                host.UseSetting("Intake:RabbitMQ:Host", "127.0.0.1");
                host.UseSetting("Intake:RabbitMQ:Port", "5673");
                host.UseSetting("Intake:RabbitMQ:VirtualHost", VirtualHost);
                host.UseSetting("Intake:RabbitMQ:UserName", "notifications");
                host.UseSetting("Intake:RabbitMQ:Password", LocalStack.Available ? LocalStack.Secret("RABBITMQ_PASSWORD") : "unused");
                host.UseSetting("Intake:RabbitMQ:RetryDelay", "00:00:01");
                host.ConfigureServices(services => services.AddSingleton(Failures).AddScoped<IIntegrationEventPipelineBehavior>(sp => sp.GetRequiredService<FailOnce>()));
            });
        }

        public async ValueTask InitializeAsync()
        {
            if (!LocalStack.Available)
            {
                await App.InitializeAsync();
                return;
            }
            using (var broker = ManagementClient())
            {
                await Put(broker, $"vhosts/{VirtualHost}", new { });
                await Put(broker, $"permissions/{VirtualHost}/notifications", new { configure = ".*", write = ".*", read = ".*" });
                foreach (var user in new[] { Producer, Stranger })
                {
                    await Put(broker, $"users/{user}", new { password = Password, tags = "" });
                    // A producer publishes to the intake exchange and does nothing else.
                    await Put(broker, $"permissions/{VirtualHost}/{user}", new { configure = "^$", write = "^notifications$", read = "^$" });
                }
            }
            _brokerReady = true;
            await App.InitializeAsync();

            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            using var producer = App.Client(await LocalStack.ProducerTokenAsync());
            Receiver = await Receiver.StartAsync();
            await Ok(admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(Key, "Sample", "sample-producer", Producer)));
            await Ok(admin.PutAsync($"/api/v1/applications/{Key}/tenants/{Tenant}", null));
            var t = $"/api/v1/tenants/{Tenant}";
            await Ok(admin.PutAsJsonAsync($"{t}/allowlist", new PutAllowlistBody(Receiver.Address.ToString(), [Receiver.Port])));
            await Ok(admin.PutAsJsonAsync($"{t}/integrations/hooks", new PutIntegrationBody("webhook", "webhook", null, TestHooks.SecretRef, null, null)));
            await Ok(admin.PutAsJsonAsync($"{t}/integrations/hooks/targets/receiver", new PutTargetBody(Receiver.Url, null, null)));
            await Ok(admin.PutAsJsonAsync($"{t}/routes/all", new PutRouteBody(DeliveryTests.Json($$"""{ "type": "{{Key}}.*" }"""), ["receiver"], null)));
            await Ok(producer.PutAsJsonAsync($"/api/v1/applications/{Key}/types", new DeclareTypeBody($"{Key}.queued", "Queued", "alerts", null, null, null,
                null, DeliveryTests.Json("""{ "type": "object", "required": ["at"] }"""), null, null, null, DeliveryTests.Json("""{ "webhook": "passthrough" }"""))));
        }

        private static HttpClient ManagementClient()
        {
            var client = new HttpClient { BaseAddress = new Uri(Management) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("notifications:" + LocalStack.Secret("RABBITMQ_PASSWORD"))));
            return client;
        }

        private static async Task Put(HttpClient broker, string path, object body)
        {
            var response = await broker.PutAsJsonAsync(path, body);
            Assert.True(response.IsSuccessStatusCode, $"PUT {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        /// <summary>A connection to the test's virtual host as the given broker user.</summary>
        public Task<IConnection> ConnectAsync(string user) => new ConnectionFactory
        {
            HostName = "127.0.0.1", Port = 5673, VirtualHost = VirtualHost, UserName = user, Password = Password,
        }.CreateConnectionAsync(Ct);

        /// <summary>A connection to the test's virtual host as the service's own broker user, which may declare anything there.</summary>
        public Task<IConnection> ConnectAsServiceAsync() => new ConnectionFactory
        {
            HostName = "127.0.0.1", Port = 5673, VirtualHost = VirtualHost, UserName = "notifications", Password = LocalStack.Secret("RABBITMQ_PASSWORD"),
        }.CreateConnectionAsync(Ct);

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            if (Receiver is not null)
            {
                await Receiver.DisposeAsync();
            }
            if (_brokerReady)
            {
                using var broker = ManagementClient();
                await broker.DeleteAsync($"vhosts/{VirtualHost}");
                await broker.DeleteAsync($"users/{Producer}");
                await broker.DeleteAsync($"users/{Stranger}");
            }
        }

        /// <summary>Fails the first handling of the messages named, as a database outage would, and counts every handling.</summary>
        public sealed class FailOnce : IIntegrationEventPipelineBehavior
        {
            public ConcurrentDictionary<string, int> Handled { get; } = new();
            public ConcurrentDictionary<string, string?> Publishers { get; } = new();
            public HashSet<string> Keys { get; } = [];

            public int Order => 0;

            public ValueTask<EventDispatchResult> Handle(IIntegrationEvent evt, EventDispatchContext context, IntegrationEventHandlerDelegate next,
                CancellationToken cancellationToken)
            {
                if (evt is NotificationRequested { IdempotencyKey: { } key })
                {
                    var times = Handled.AddOrUpdate(key, 1, (_, n) => n + 1);
                    Publishers[key + "#" + times] = MessageContext.Current.PublisherId;
                    lock (Keys)
                    {
                        if (times == 1 && Keys.Contains(key))
                        {
                            return ValueTask.FromResult(EventDispatchResult.Failure);
                        }
                    }
                }
                return next(evt, context, cancellationToken);
            }
        }
    }

    /// <summary>The RabbitMQ intake (phase-1-plan M2, p1-05): <c>NotificationRequested</c> sends the same command as the HTTP API.</summary>
    public sealed class RabbitMQIntakeTests(RabbitMQIntakeApp intake) : IClassFixture<RabbitMQIntakeApp>
    {
        private static readonly string Type = $"{RabbitMQIntakeApp.Key}.queued";

        private static string Message(string key, string data = """{ "at": "now" }""", string? type = null, Guid? tenant = null) => $$"""
            {
              "MessageId": "{{Guid.NewGuid()}}",
              "NotificationTenantId": "{{tenant ?? RabbitMQIntakeApp.Tenant}}",
              "Type": "{{type ?? Type}}",
              "IdempotencyKey": "{{key}}",
              "Labels": { "env": "test" },
              "Data": {{data}}
            }
            """;

        /// <summary>Publishes to the intake exchange as <paramref name="user"/>, stamping <paramref name="userId"/> as the message's <c>user_id</c>.</summary>
        private async Task PublishAsync(string user, string? userId, string body, IDictionary<string, object?>? headers = null)
        {
            await using var connection = await intake.ConnectAsync(user);
            await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true), Ct);
            await channel.BasicPublishAsync("notifications", RabbitMQIntake.RoutingKey, mandatory: true,
                new BasicProperties { MessageId = Guid.NewGuid().ToString(), UserId = userId, ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent,
                    Headers = headers },
                Encoding.UTF8.GetBytes(body), Ct);
        }

        /// <summary>The request accepted under the key, once it is there.</summary>
        private async Task<Data.NotificationRequest> AcceptedAsync(string key)
        {
            for (var i = 0; i < 150; i++)
            {
                await using var db = intake.App.Database.Context();
                if (await db.Requests.AsNoTracking().SingleOrDefaultAsync(r => r.IdempotencyKey == key, Ct) is { } request)
                {
                    return request;
                }
                await Task.Delay(100, Ct);
            }
            throw new TimeoutException($"No request was accepted under {key}.");
        }

        private async Task<bool> StoredAsync(string key)
        {
            await using var db = intake.App.Database.Context();
            return await db.Requests.AnyAsync(r => r.IdempotencyKey == key, Ct);
        }

        [Fact]
        public async Task A_request_published_as_the_applications_broker_user_is_accepted_and_delivered_with_its_data_as_sent()
        {
            LocalStack.RequireAvailable();
            var key = "q-" + Short();
            // Juice's serializer reads date-like strings as dates; the data must reach the type with its strings as written and integers exact.
            await PublishAsync(intake.Producer, intake.Producer, Message(key, """{ "at": "2026-11-13T08:00:00Z", "ratio": 1.5, "big": 12345678901234567890 }"""));

            var request = await AcceptedAsync(key);
            await using (var db = intake.App.Database.Context())
            {
                var app = await db.Applications.SingleAsync(a => a.Key == RabbitMQIntakeApp.Key, Ct);
                Assert.Equal((app.Id, Type, RabbitMQIntakeApp.Tenant), (request.ApplicationId, request.TypeKey, request.TenantId));
            }
            Assert.Contains("\"2026-11-13T08:00:00Z\"", request.Data);
            Assert.Contains("1.5", request.Data);
            Assert.Contains("12345678901234567890", request.Data);

            using var producer = intake.App.Client(await LocalStack.ProducerTokenAsync());
            var detail = await DeliveredAsync(producer, request.Id);
            Assert.Equal(DeliveryStatus.Delivered, Assert.Single(detail.Deliveries).Status);
            Assert.Contains(intake.Receiver!.Requests, r => r.Body.Contains("2026-11-13T08:00:00Z") && r.Body.Contains(request.Id.ToString()));
        }

        [Fact]
        public async Task Readiness_includes_the_broker_and_is_healthy_when_connected()
        {
            LocalStack.RequireAvailable();
            using var anonymous = intake.App.Client();
            var ready = await anonymous.GetAsync("/health/ready", Ct);
            Assert.Equal((HttpStatusCode.OK, "Healthy"), (ready.StatusCode, await ready.Content.ReadAsStringAsync(Ct)));

            // And the broker's check is the intake's own, which reports an outage as Degraded (BrokerHealthCheckTests): Juice's answers Unhealthy,
            // and /health/ready would then be 503 while HTTP intake is fine.
            var checks = intake.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>().Value;
            var broker = Assert.Single(checks.Registrations, r => r.Name == "rabbitmq");
            Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, broker.FailureStatus);
            Assert.Contains("ready", broker.Tags);
            Assert.IsType<BrokerHealthCheck>(broker.Factory(intake.App.Services));
        }

        [Fact]
        public async Task The_request_carries_the_trace_the_producer_published_in()
        {
            LocalStack.RequireAvailable();
            // No exporter in this test: the intake keeps the producer's trace whether or not the host records traces.
            var trace = System.Diagnostics.ActivityTraceId.CreateRandom();
            var key = "q-" + Short();
            await PublishAsync(intake.Producer, intake.Producer, Message(key),
                new Dictionary<string, object?> { ["traceparent"] = $"00-{trace}-{System.Diagnostics.ActivitySpanId.CreateRandom()}-01" });
            var request = await AcceptedAsync(key);
            Assert.NotNull(request.TraceParent);
            Assert.Equal(trace.ToString(), request.TraceParent.Split('-')[1]);
        }

        [Fact]
        public async Task Messages_without_a_verified_publisher_from_an_unknown_broker_user_or_against_the_types_rules_are_refused()
        {
            LocalStack.RequireAvailable();
            var unstamped = "q-" + Short();
            var stranger = "q-" + Short();
            var unknownType = "q-" + Short();
            var badData = "q-" + Short();
            var otherTenant = "q-" + Short();
            var marker = "q-" + Short();
            await PublishAsync(intake.Producer, null, Message(unstamped)); // no user_id: whatever it says, nobody vouches for it
            await PublishAsync(intake.Stranger, intake.Stranger, Message(stranger)); // a broker user no application publishes as
            await PublishAsync(intake.Producer, intake.Producer, Message(unknownType, type: $"{RabbitMQIntakeApp.Key}.missing"));
            await PublishAsync(intake.Producer, intake.Producer, Message(badData, data: """{ "other": 1 }"""));
            await PublishAsync(intake.Producer, intake.Producer, Message(otherTenant, tenant: ProducerApp.OtherTenant));
            // One consumer takes the queue in order: once the marker is in, the messages before it were handled.
            await PublishAsync(intake.Producer, intake.Producer, Message(marker));
            await AcceptedAsync(marker);
            // A refusal that wrongly failed would be back after RetryDelay (1 s): wait past it, then past one more message.
            await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
            var after = "q-" + Short();
            await PublishAsync(intake.Producer, intake.Producer, Message(after));
            await AcceptedAsync(after);

            foreach (var key in new[] { unstamped, stranger, unknownType, badData, otherTenant })
            {
                Assert.False(await StoredAsync(key), $"{key} was refused");
                Assert.Equal(1, intake.Failures.Handled[key]); // refused once and taken off the queue, not retried
            }
        }

        [Fact]
        public async Task The_broker_refuses_a_user_id_that_isnt_the_publishing_users()
        {
            LocalStack.RequireAvailable();
            // What the publisher check rests on: RabbitMQ validates user_id against the connection's user.
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => PublishAsync(intake.Stranger, intake.Producer, Message("q-" + Short())));
            Assert.Contains("user_id", refused.ToString());
        }

        [Fact]
        public async Task A_request_published_twice_and_sent_over_http_with_the_same_key_is_one_request()
        {
            LocalStack.RequireAvailable();
            var key = "q-" + Short();
            await PublishAsync(intake.Producer, intake.Producer, Message(key));
            await PublishAsync(intake.Producer, intake.Producer, Message(key));
            var request = await AcceptedAsync(key);

            using var producer = intake.App.Client(await LocalStack.ProducerTokenAsync());
            var response = await producer.PostAsJsonAsync($"/api/v1/tenants/{RabbitMQIntakeApp.Tenant}/notifications", new SendNotificationBody(Type, null, key,
                null, null, null, null, null, DeliveryTests.Json("""{ "at": "later" }"""), null, null), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the original
            Assert.Equal(request.Id, (await response.Content.ReadFromJsonAsync<AcceptedResponse>(Ct))!.Id);

            var marker = "q-" + Short();
            await PublishAsync(intake.Producer, intake.Producer, Message(marker));
            await AcceptedAsync(marker);
            await using var db = intake.App.Database.Context();
            Assert.Equal(1, await db.Requests.CountAsync(r => r.IdempotencyKey == key, Ct));
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.RequestId == request.Id, Ct));
        }

        [Fact]
        public async Task The_host_refuses_to_start_at_once_when_the_broker_has_the_queues_with_other_arguments()
        {
            LocalStack.RequireAvailable();
            var queue = "other-" + Short();
            await using (var connection = await intake.ConnectAsServiceAsync())
            await using (var channel = await connection.CreateChannelAsync(cancellationToken: Ct))
            {
                // As a first deployment with another RetryDelay left it.
                await channel.QueueDeclareAsync(queue + ".retry", durable: true, exclusive: false, autoDelete: false,
                    arguments: new Dictionary<string, object?> { ["x-message-ttl"] = 5000 }, cancellationToken: Ct);
            }
            var app = new NotificationsApp(host =>
            {
                host.UseSetting("Intake:RabbitMQ:Enabled", "true");
                host.UseSetting("Intake:RabbitMQ:Host", "127.0.0.1");
                host.UseSetting("Intake:RabbitMQ:Port", "5673");
                host.UseSetting("Intake:RabbitMQ:VirtualHost", intake.VirtualHost);
                host.UseSetting("Intake:RabbitMQ:UserName", "notifications");
                host.UseSetting("Intake:RabbitMQ:Password", LocalStack.Secret("RABBITMQ_PASSWORD"));
                host.UseSetting("Intake:RabbitMQ:Queue", queue);
                host.UseSetting("Intake:RabbitMQ:RetryDelay", "00:00:01");
            });
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var refused = await Assert.ThrowsAnyAsync<Exception>(async () => await app.InitializeAsync());
                Assert.Contains("other arguments", refused.ToString());
                Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"refused after {started.Elapsed}, not at once"); // not after StartTimeout (60 s)
            }
            finally
            {
                await app.DisposeAsync();
            }
        }

        [Fact]
        public async Task A_message_whose_handling_failed_comes_back_after_the_delay_still_from_its_publisher()
        {
            LocalStack.RequireAvailable();
            var key = "q-" + Short();
            lock (intake.Failures.Keys)
            {
                intake.Failures.Keys.Add(key);
            }
            await PublishAsync(intake.Producer, intake.Producer, Message(key));

            await AcceptedAsync(key);
            Assert.Equal(2, intake.Failures.Handled[key]);
            // The broker dead-lettered it and brought it back: user_id kept, so the second handling still knew the application.
            Assert.Equal(intake.Producer, intake.Failures.Publishers[key + "#2"]);
        }
    }
}
