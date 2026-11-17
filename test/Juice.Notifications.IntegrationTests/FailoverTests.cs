using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Delivering;
using Juice.Notifications.Providers;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Failover (D8, design §10.3, phase-4-plan I38, M25) in the dispatcher, on the real PostgreSQL and claim store: a tenant with two email
    /// integrations, <c>mail-a</c> (priority 0, provider kind <c>fake-a</c>) and <c>mail-b</c> (priority 1, <c>fake-b</c>), each answering from a
    /// script. Phase 4 proof points 1 and 2.
    /// </summary>
    public sealed class FailoverTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private static readonly StageOptions Options = new() { Batch = 1, Lease = TimeSpan.FromSeconds(60) };
        private const int MaxAttempts = 3;

        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
        private readonly ScriptedProvider _a = new(Channels.Email, "fake-a");
        private readonly ScriptedProvider _b = new(Channels.Email, "fake-b");
        private readonly ScriptedProvider _hook = new(Channels.Webhook, "fake-hook");
        private ServiceProvider? _services;
        private (Guid App, Guid Type, Guid A, Guid B, Guid HookA, Guid HookB) _seed;

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = await _database.MigratedAsync();
            var app = Seed.Application("failover");
            var type = Seed.Type(app.Id, "failover.thing");
            Integration Make(string key, string channel, string kind, int priority) => new()
            {
                TenantId = Tenant, Key = key, Channel = channel, Kind = kind, Priority = priority, Settings = "{}", DefinitionHash = "d", UpdatedAt = Seed.Now,
            };
            var a = Make("mail-a", Channels.Email, "fake-a", 0);
            var b = Make("mail-b", Channels.Email, "fake-b", 1);
            var hookA = Make("hook-a", Channels.Webhook, "fake-hook", 0);
            var hookB = Make("hook-b", Channels.Webhook, "fake-hook", 1);
            db.AddRange(app, type, a, b, hookA, hookB, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now });
            await db.SaveChangesAsync();
            _seed = (app.Id, type.Id, a.Id, b.Id, hookA.Id, hookB.Id);

            var services = new ServiceCollection().AddLogging();
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddPostgresClaimStore();
            services.Configure<DeliveryOptions>(o =>
            {
                o.MaxAttempts = MaxAttempts;
                o.RetryBase = TimeSpan.FromSeconds(1);
                o.RetryMax = TimeSpan.FromMinutes(1);
            });
            services.AddSingleton<INotificationProvider>(_a);
            services.AddSingleton<INotificationProvider>(_b);
            services.AddSingleton<INotificationProvider>(_hook);
            services.AddBuiltInChannels();
            People.ContactEncryptionRegistration.AddContactEncryption(services);
            services.AddStage<Delivery, DispatcherStage>(Options, runWorkers: false);
            _services = services.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync()
        {
            if (_services is not null)
            {
                await _services.DisposeAsync();
            }
            await _database.DisposeAsync();
        }

        private Task<PassResult> PassAsync()
            => _services!.GetRequiredService<StageRunner<Delivery, DispatcherStage>>().RunOnceAsync("w1", Options, Ct);

        /// <summary>A delivery due now, to an external address (which any integration of the channel can reach) unless a target key is given.</summary>
        private async Task<Delivery> DeliveryAsync(string channel = Channels.Email, Guid? integration = null, string? destinationKey = null)
        {
            await using var db = _database.Context();
            var request = Seed.Request(Tenant, _seed.App, _seed.Type, Guid.NewGuid().ToString("N"));
            var notification = Seed.Notification(Tenant, request.Id);
            notification.State = NotificationState.Planned;
            notification.NextAt = null;
            var recipient = Seed.Recipient(Tenant, notification.Id);
            var webhook = channel == Channels.Webhook;
            var key = destinationKey ?? (webhook ? "ext:webhook:https://example.com/hook" : "ext:email:a@example.com");
            var delivery = new Delivery
            {
                TenantId = Tenant, Channel = channel, IntegrationId = integration ?? (webhook ? _seed.HookA : _seed.A), DestinationKey = key,
                DestinationSnapshot = webhook ? """{"address":"https://example.com/hook"}""" : """{"address":"a@example.com"}""", Locale = "en",
                RendererVersion = "test", Content = """{"subject":"s","text":"t"}""", CreatedAt = _clock.GetUtcNow(), Status = DeliveryStatus.Pending,
                NextAt = _clock.GetUtcNow(),
            };
            var item = new DeliveryItem { TenantId = Tenant, DeliveryId = delivery.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = key, Channel = channel };
            db.AddRange(request, notification, recipient, delivery, item);
            await db.SaveChangesAsync(Ct);
            return delivery;
        }

        private async Task<(Delivery Row, List<DeliveryAttempt> Attempts)> ReadAsync(Guid id)
        {
            await using var db = _database.Context();
            return (await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == id, Ct),
                await db.Attempts.AsNoTracking().Where(a => a.DeliveryId == id).OrderBy(a => a.Number).ToListAsync(Ct));
        }

        /// <summary>Runs the dispatcher until the delivery is no longer due, moving the clock to each next try.</summary>
        private async Task<(Delivery Row, List<DeliveryAttempt> Attempts)> RunAsync(Guid id)
        {
            for (var i = 0; i < 20; i++)
            {
                await PassAsync();
                var (row, _) = await ReadAsync(id);
                if (row.NextAt is not { } next)
                {
                    break;
                }
                if (next > _clock.GetUtcNow())
                {
                    _clock.Advance(next - _clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
                }
            }
            return await ReadAsync(id);
        }

        /// <summary>
        /// Phase 4 proof point 1: <c>mail-a</c> is down (a definite provider failure). The delivery moves to <c>mail-b</c> and goes out there: the same
        /// delivery id at both providers, the same content, one delivery in history with an attempt through each integration, counted as one failover.
        /// </summary>
        [Fact]
        public async Task A_provider_outage_fails_over_to_the_next_integration_with_the_same_delivery_id()
        {
            LocalStack.RequireAvailable();
            _a.Then(SendResult.Permanent(FailureCategory.Provider, "535 authentication failed"));
            var delivery = await DeliveryAsync();

            var (row, attempts) = await RunAsync(delivery.Id);

            Assert.Equal((DeliveryStatus.Accepted, _seed.B, 1, 2), (row.Status, row.IntegrationId, row.Failovers, row.Attempts));
            Assert.Equal([_seed.A, _seed.B], attempts.Select(a => a.IntegrationId));
            Assert.Equal([AttemptOutcome.PermanentFailure, AttemptOutcome.Accepted], attempts.Select(a => a.Outcome));
            Assert.Equal(delivery.Id, Assert.Single(_a.Seen).Delivery);
            Assert.Equal(delivery.Id, Assert.Single(_b.Seen).Delivery);
            await using var db = _database.Context();
            Assert.Equal(1, await db.Deliveries.CountAsync(Ct));
        }

        /// <summary>Retryable provider failures (a 4xx from the server, a refused connection) use up <c>mail-a</c>'s attempts, then <c>mail-b</c> gets a budget of its own.</summary>
        [Fact]
        public async Task Retryable_provider_failures_fail_over_once_the_attempts_run_out_and_the_next_integration_starts_afresh()
        {
            LocalStack.RequireAvailable();
            for (var i = 0; i < MaxAttempts; i++)
            {
                _a.Then(SendResult.Retry(FailureCategory.Provider, "421 service not available"));
            }
            _b.Then(SendResult.Retry(FailureCategory.Provider, "451 try again"));
            var delivery = await DeliveryAsync();

            var (row, attempts) = await RunAsync(delivery.Id);

            Assert.Equal((DeliveryStatus.Accepted, _seed.B, 1, MaxAttempts + 2), (row.Status, row.IntegrationId, row.Failovers, row.Attempts));
            Assert.Equal((MaxAttempts, 2), (_a.Sends, _b.Sends));
            Assert.Equal(MaxAttempts, attempts.Count(a => a.IntegrationId == _seed.A));
        }

        /// <summary>
        /// Phase 4 proof point 2: a destination failure (the server says the mailbox doesn't exist) fails the delivery where it is: no integration
        /// can reach an address that isn't there. Neither do content and policy failures fail over.
        /// </summary>
        [Fact]
        public async Task A_destination_failure_fails_the_delivery_without_trying_another_integration()
        {
            LocalStack.RequireAvailable();
            _a.Then(SendResult.Permanent(FailureCategory.Destination, "550 5.1.1 no such user"),
                SendResult.Permanent(FailureCategory.Content, "552 message too large"),
                SendResult.Permanent(FailureCategory.Policy, "blocked by the destination policy"));
            List<Guid> ids = [];
            for (var i = 0; i < 3; i++)
            {
                ids.Add((await DeliveryAsync()).Id);
                await RunAsync(ids[^1]);
            }

            foreach (var (id, category) in ids.Zip([FailureCategory.Destination, FailureCategory.Content, FailureCategory.Policy]))
            {
                var (row, attempts) = await ReadAsync(id);
                Assert.Equal((DeliveryStatus.Failed, category, _seed.A, 0), (row.Status, row.FailureCategory, row.IntegrationId, row.Failovers));
                Assert.Single(attempts);
            }
            Assert.Equal(0, _b.Sends);
        }

        /// <summary>
        /// No failover after an unknown outcome (the first provider may have sent it; the second can't deduplicate on its key), nor for rate limits,
        /// which wait on the same integration until the attempts run out.
        /// </summary>
        [Fact]
        public async Task An_unknown_outcome_or_a_rate_limit_never_fails_over()
        {
            LocalStack.RequireAvailable();
            _a.Then(SendResult.Unknown("connection lost after DATA"));
            var unknown = await DeliveryAsync();
            await RunAsync(unknown.Id);
            for (var i = 0; i < MaxAttempts; i++)
            {
                _a.Then(SendResult.Retry(FailureCategory.RateLimited, "429"));
            }
            var limited = await DeliveryAsync();
            await RunAsync(limited.Id);

            var (u, _) = await ReadAsync(unknown.Id);
            Assert.Equal((DeliveryStatus.Failed, _seed.A, 0), (u.Status, u.IntegrationId, u.Failovers));
            Assert.StartsWith("unknown_outcome", u.LastError);
            var (l, _) = await ReadAsync(limited.Id);
            Assert.Equal((DeliveryStatus.Failed, FailureCategory.RateLimited, _seed.A, 0), (l.Status, l.FailureCategory, l.IntegrationId, l.Failovers));
            Assert.Equal(0, _b.Sends);
        }

        /// <summary>A disabled integration moves its deliveries to the next one without a send; a shared target (its address is the integration's) never moves.</summary>
        [Fact]
        public async Task A_disabled_integration_fails_over_but_a_shared_target_stays_with_its_integration()
        {
            LocalStack.RequireAvailable();
            await using (var db = _database.Context())
            {
                await db.Integrations.Where(i => i.Id == _seed.A || i.Id == _seed.HookA).ExecuteUpdateAsync(u => u.SetProperty(i => i.Enabled, false), Ct);
            }
            var mail = await DeliveryAsync();
            var target = await DeliveryAsync(Channels.Webhook, destinationKey: $"it:{Guid.NewGuid()}");

            var (m, mailAttempts) = await RunAsync(mail.Id);
            var (t, _) = await RunAsync(target.Id);

            Assert.Equal((DeliveryStatus.Accepted, _seed.B, 1), (m.Status, m.IntegrationId, m.Failovers));
            Assert.Equal(_seed.B, Assert.Single(mailAttempts).IntegrationId);
            Assert.Equal((DeliveryStatus.Failed, FailureCategory.Provider, _seed.HookA, 0), (t.Status, t.FailureCategory, t.IntegrationId, t.Failovers));
            Assert.Equal((0, 1, 0), (_a.Sends, _b.Sends, _hook.Sends));
        }

        /// <summary>The last integration failing too fails the delivery: failover goes down the order once and never comes back round to the first.</summary>
        [Fact]
        public async Task When_the_last_integration_fails_too_the_delivery_fails_without_coming_back_round()
        {
            LocalStack.RequireAvailable();
            _a.Then(SendResult.Permanent(FailureCategory.Provider, "a is down"));
            _b.Then(SendResult.Permanent(FailureCategory.Provider, "b is down"));
            var delivery = await DeliveryAsync();

            var (row, attempts) = await RunAsync(delivery.Id);

            Assert.Equal((DeliveryStatus.Failed, FailureCategory.Provider, _seed.B, 1, "b is down"),
                (row.Status, row.FailureCategory, row.IntegrationId, row.Failovers, row.LastError));
            Assert.Equal(2, attempts.Count);
            Assert.Equal((1, 1), (_a.Sends, _b.Sends));
        }

        /// <summary>
        /// A stale worker's failure through the old integration, recorded after the failover, doesn't answer the new integration: it is numbered past
        /// and the new one still sends (and no second failover follows from it).
        /// </summary>
        [Fact]
        public async Task A_stale_failure_through_the_old_integration_doesnt_answer_the_new_one()
        {
            LocalStack.RequireAvailable();
            _a.Then(SendResult.Permanent(FailureCategory.Provider, "a is down"));
            var delivery = await DeliveryAsync();
            await PassAsync(); // fails over to mail-b, due now
            await using (var db = _database.Context())
            {
                db.Attempts.Add(new DeliveryAttempt
                {
                    TenantId = Tenant, DeliveryId = delivery.Id, Number = 2, IntegrationId = _seed.A, At = _clock.GetUtcNow(),
                    Outcome = AttemptOutcome.PermanentFailure, Category = FailureCategory.Provider, Error = "a stale worker's late failure",
                });
                await db.SaveChangesAsync(Ct);
            }

            var (row, attempts) = await RunAsync(delivery.Id);

            Assert.Equal((DeliveryStatus.Accepted, _seed.B, 1, 3), (row.Status, row.IntegrationId, row.Failovers, row.Attempts));
            Assert.Equal(_seed.B, attempts[^1].IntegrationId);
            Assert.Equal(1, _b.Sends);
        }
    }
}
