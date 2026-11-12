using System.Collections.Concurrent;
using Juice.MediatR;
using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Delivering;
using Juice.Notifications.History;
using Juice.Notifications.Providers;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// A provider that answers from a script (then <c>Accepted</c>) and remembers what it was asked to send. <see cref="OnSend"/> runs inside the
    /// send, so a test can block it, move the clock or stop the worker while the send is in flight.
    /// </summary>
    public sealed class ScriptedProvider(string channel, string kind = "fake") : INotificationProvider
    {
        private readonly ConcurrentQueue<SendResult> _script = new();

        public ConcurrentQueue<(Guid Delivery, int Attempt, CancellationToken Token)> Seen { get; } = new();
        public int Sends => Seen.Count;
        public Func<Task>? OnSend { get; set; }

        public string Channel => channel;
        public string Kind => kind;
        public bool Idempotent => false;

        public string? CheckSettings(string settings) => null;

        public void Then(params SendResult[] results)
        {
            foreach (var result in results)
            {
                _script.Enqueue(result);
            }
        }

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            Seen.Enqueue((delivery.DeliveryId, delivery.Attempt, ct));
            if (OnSend is { } onSend)
            {
                await onSend();
            }
            return _script.TryDequeue(out var result) ? result : SendResult.Accepted();
        }
    }

    /// <summary>A worker that dies between the send and the save: <see cref="BeforePersistAsync"/> throws while <see cref="Kills"/> is above zero.</summary>
    public sealed class DyingFaults : IStageFaults
    {
        private int _kills;

        public int Kills { get => _kills; set => _kills = value; }

        public Task BeforePersistAsync(string stage, string worker, Claim claim, CancellationToken ct)
            => Interlocked.Decrement(ref _kills) >= 0 ? throw new InvalidOperationException("the worker died after the send") : Task.CompletedTask;
    }

    /// <summary>
    /// The dispatcher (design §10, phase-1-plan M7) on the real PostgreSQL, through the real <see cref="StageRunner{T, TStage}"/> and claim store,
    /// with a scripted provider and a clock moved by hand: claim order, backoff, the unknown-outcome rules, a worker that dies or stops around a
    /// send, a dead-letter retry over an attempt never applied, and snapshots or secrets it can't send to.
    /// </summary>
    public sealed class DispatcherTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private static readonly TimeSpan RetryBase = TimeSpan.FromSeconds(1);
        private static readonly StageOptions Options = new() { Batch = 1, Lease = TimeSpan.FromSeconds(60) };
        private const string Content = """{"subject":"s","text":"t"}""";
        private const string Mail = """{"address":"a@example.com"}""";
        private const string Hook = """{"address":"https://example.com/hook"}""";

        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        private readonly DyingFaults _faults = new();
        private readonly ScriptedProvider _email = new(Channels.Email);
        private readonly ScriptedProvider _webhook = new(Channels.Webhook);
        private ServiceProvider? _services;
        private (Guid App, Guid Type, Guid Email, Guid Webhook) _seed;

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = await _database.MigratedAsync();
            var app = Seed.Application("dispatch");
            var type = Seed.Type(app.Id, "dispatch.thing");
            var email = new Integration
            {
                TenantId = Tenant, Key = "fake-mail", Channel = Channels.Email, Kind = "fake", Settings = "{}", DefinitionHash = "d", UpdatedAt = Seed.Now,
            };
            var webhook = new Integration
            {
                TenantId = Tenant, Key = "fake-hook", Channel = Channels.Webhook, Kind = "fake", Settings = "{}", DefinitionHash = "d", UpdatedAt = Seed.Now,
            };
            db.AddRange(app, type, email, webhook, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now });
            await db.SaveChangesAsync();
            _seed = (app.Id, type.Id, email.Id, webhook.Id);

            var services = new ServiceCollection().AddLogging();
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton<IStageFaults>(_faults);
            services.AddPostgresClaimStore();
            services.Configure<DeliveryOptions>(o =>
            {
                o.MaxAttempts = 8;
                o.RetryBase = RetryBase;
                o.RetryMax = TimeSpan.FromMinutes(10);
            });
            services.AddSingleton<INotificationProvider>(_email);
            services.AddSingleton<INotificationProvider>(_webhook);
            services.AddStage<Delivery, DispatcherStage>(Options, runWorkers: false);
            // The real handler, without the API's transaction behavior: the test saves what it changed, as that behavior would.
            services.AddMediatR(mediator => mediator.RegisterServicesFromAssemblyContaining<RetryDelivery>(includeNonPublicTypes: true));
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

        private StageRunner<Delivery, DispatcherStage> Runner => _services!.GetRequiredService<StageRunner<Delivery, DispatcherStage>>();

        private Task<PassResult> PassAsync(string worker = "w1", StageOptions? options = null, CancellationToken? ct = null)
            => Runner.RunOnceAsync(worker, options ?? Options, ct ?? Ct);

        /// <summary>A delivery due now (unless <paramref name="nextAt"/> says otherwise), with the request, notification and recipient it carries.</summary>
        private async Task<Delivery> DeliveryAsync(string channel = Channels.Email, NotificationPriority priority = NotificationPriority.Normal, bool mandatory = false,
            string? snapshot = null, DeliveryStatus status = DeliveryStatus.Pending, int attempts = 0, DateTimeOffset? nextAt = null, bool due = true)
        {
            await using var db = _database.Context();
            var request = Seed.Request(Tenant, _seed.App, _seed.Type, Guid.NewGuid().ToString("N"));
            var notification = Seed.Notification(Tenant, request.Id);
            notification.State = NotificationState.Planned;
            notification.NextAt = null;
            var recipient = Seed.Recipient(Tenant, notification.Id);
            var destination = channel == Channels.Webhook ? "ext:webhook:https://example.com/hook" : "ext:email:a@example.com";
            var delivery = new Delivery
            {
                TenantId = Tenant, Channel = channel, IntegrationId = channel == Channels.Webhook ? _seed.Webhook : _seed.Email, DestinationKey = destination,
                DestinationSnapshot = snapshot ?? (channel == Channels.Webhook ? Hook : Mail), Locale = "en", RendererVersion = "test", Content = Content,
                CreatedAt = _clock.GetUtcNow(), Priority = priority, Mandatory = mandatory, Status = status, Attempts = attempts,
                NextAt = due ? nextAt ?? _clock.GetUtcNow() : null,
            };
            var item = new DeliveryItem { TenantId = Tenant, DeliveryId = delivery.Id, RecipientId = recipient.Id, DestinationKey = destination, Channel = channel };
            db.AddRange(request, notification, recipient, delivery, item);
            await db.SaveChangesAsync(Ct);
            return delivery;
        }

        private async Task<(Delivery Row, List<DeliveryAttempt> Attempts)> ReadAsync(Guid id)
        {
            await using var db = _database.Context();
            var row = await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
            var attempts = await db.Attempts.AsNoTracking().Where(a => a.DeliveryId == id).OrderBy(a => a.Number).ToListAsync(Ct);
            return (row, attempts);
        }

        /// <summary>Moves the clock to just past when the row is next due.</summary>
        private void AdvanceTo(DateTimeOffset? at)
        {
            Assert.NotNull(at);
            var by = at.Value - _clock.GetUtcNow() + TimeSpan.FromMilliseconds(1);
            if (by > TimeSpan.Zero)
            {
                _clock.Advance(by);
            }
        }

        /// <summary>The n-th backoff: RetryBase × 2^(n−1) within ±20 % (a millisecond either side for the database's rounding).</summary>
        private static void AssertBackoff(DateTimeOffset from, DateTimeOffset? next, int n)
        {
            Assert.NotNull(next);
            var nominal = RetryBase.TotalSeconds * Math.Pow(2, n - 1);
            Assert.InRange((next.Value - from).TotalSeconds, nominal * 0.8 - 0.001, nominal * 1.2 + 0.001);
        }

        /// <summary>
        /// Deliveries are claimed most urgent first (Critical, High, Normal, Low), then by when they fell due: a Low one due longest ago goes
        /// last, and the priority isn't ranked as text (which would put Low before Normal).
        /// </summary>
        [Fact]
        public async Task Deliveries_are_claimed_by_priority_then_by_due_time()
        {
            LocalStack.RequireAvailable();
            var now = _clock.GetUtcNow();
            var low = await DeliveryAsync(priority: NotificationPriority.Low, nextAt: now.AddSeconds(-30));
            var normalLate = await DeliveryAsync(priority: NotificationPriority.Normal, nextAt: now.AddSeconds(-5));
            var normalEarly = await DeliveryAsync(priority: NotificationPriority.Normal, nextAt: now.AddSeconds(-20));
            var high = await DeliveryAsync(priority: NotificationPriority.High, nextAt: now.AddSeconds(-3));
            var critical = await DeliveryAsync(priority: NotificationPriority.Critical, nextAt: now.AddSeconds(-1));

            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(1, (await PassAsync()).Completed);
            }

            Assert.Equal([critical.Id, high.Id, normalEarly.Id, normalLate.Id, low.Id], _email.Seen.Select(s => s.Delivery));
        }

        /// <summary>
        /// Three retryable failures: each attempt's <c>At</c> is when its send ended (the provider takes a second, on the clock and for real), and
        /// each <c>NextAt</c> is that plus RetryBase × 2^(n−1) within ±20 %. From the send's start, the first backoff would be due a second early.
        /// </summary>
        [Fact]
        public async Task Retryable_failures_back_off_exponentially_from_when_each_send_ended()
        {
            LocalStack.RequireAvailable();
            var send = TimeSpan.FromSeconds(1);
            var ends = new List<DateTimeOffset>();
            // The dispatcher times the send with a stopwatch, so the send takes real time as well as moving the test clock.
            _email.OnSend = async () =>
            {
                await Task.Delay(send, Ct);
                _clock.Advance(send);
                ends.Add(_clock.GetUtcNow());
            };
            _email.Then(SendResult.Retry(FailureCategory.Provider, "503 one", 503), SendResult.Retry(FailureCategory.Provider, "503 two", 503),
                SendResult.Retry(FailureCategory.Provider, "503 three", 503));
            var delivery = await DeliveryAsync();

            for (var n = 1; n <= 3; n++)
            {
                var start = _clock.GetUtcNow();
                Assert.Equal(1, (await PassAsync()).Completed);
                var (row, attempts) = await ReadAsync(delivery.Id);
                var attempt = attempts[^1];
                Assert.Equal((n, AttemptOutcome.RetryableFailure, DeliveryStatus.Retrying, n), (attempt.Number, attempt.Outcome, row.Status, row.Attempts));
                Assert.InRange(attempt.At, ends[n - 1].AddMilliseconds(-50), ends[n - 1].AddMilliseconds(500));
                AssertBackoff(attempt.At, row.NextAt, n);
                Assert.True(row.NextAt > start + send + RetryBase * Math.Pow(2, n - 1) * 0.8 - TimeSpan.FromMilliseconds(60),
                    $"attempt {n} is due at {row.NextAt:O}, timed from before its send ({start:O})");
                AdvanceTo(row.NextAt);
            }
            Assert.Equal(3, _email.Sends);
        }

        /// <summary>
        /// A webhook send whose outcome is unknown is retried (receivers deduplicate on <c>webhook-id</c>, the delivery id), and the provider
        /// gets the same delivery id on the repeat (proof 5).
        /// </summary>
        [Fact]
        public async Task A_webhook_unknown_outcome_is_retried_with_the_same_delivery_id()
        {
            LocalStack.RequireAvailable();
            _webhook.Then(SendResult.Unknown("connection reset after the request was written"), SendResult.Delivered(200));
            var delivery = await DeliveryAsync(Channels.Webhook);

            await PassAsync();
            var (afterUnknown, _) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Retrying, FailureCategory.Provider, 1), (afterUnknown.Status, afterUnknown.FailureCategory, afterUnknown.Attempts));
            AdvanceTo(afterUnknown.NextAt);
            await PassAsync();

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal(DeliveryStatus.Delivered, row.Status);
            Assert.Equal([(delivery.Id, 1), (delivery.Id, 2)], _webhook.Seen.Select(s => (s.Delivery, s.Attempt)));
            Assert.Equal([AttemptOutcome.Unknown, AttemptOutcome.Delivered], attempts.Select(a => a.Outcome));
        }

        /// <summary>
        /// Through a provider that isn't idempotent, a normal type's unknown outcome isn't repeated (it may have gone out): <c>Failed {Provider}</c>
        /// with <c>unknown_outcome</c>, not due again, and no second send.
        /// </summary>
        [Fact]
        public async Task A_non_idempotent_unknown_outcome_fails_as_unknown_outcome_without_a_second_send()
        {
            LocalStack.RequireAvailable();
            _email.Then(SendResult.Unknown("dropped after DATA"));
            var delivery = await DeliveryAsync();

            await PassAsync();
            _clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(0, (await PassAsync()).Claimed);

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Failed, FailureCategory.Provider, (DateTimeOffset?)null), (row.Status, row.FailureCategory, row.NextAt));
            Assert.StartsWith("unknown_outcome", row.LastError);
            Assert.Equal(1, _email.Sends);
            Assert.Equal(AttemptOutcome.Unknown, Assert.Single(attempts).Outcome);
        }

        /// <summary>
        /// A mandatory type is repeated once after an unknown outcome (a duplicate is better than silence), whichever attempt the unknown was: a
        /// retryable failure, an unknown, the one repeat, accepted.
        /// </summary>
        [Fact]
        public async Task A_mandatory_unknown_outcome_is_repeated_exactly_once()
        {
            LocalStack.RequireAvailable();
            _email.Then(SendResult.Retry(FailureCategory.Provider, "connect refused"), SendResult.Unknown("dropped after DATA"), SendResult.Accepted("m-1"));
            var delivery = await DeliveryAsync(mandatory: true);

            for (var pass = 0; pass < 3; pass++)
            {
                await PassAsync();
                var (between, _) = await ReadAsync(delivery.Id);
                if (between.NextAt is { } next)
                {
                    AdvanceTo(next);
                }
            }

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Accepted, "m-1"), (row.Status, row.ProviderMessageId));
            Assert.Equal([AttemptOutcome.RetryableFailure, AttemptOutcome.Unknown, AttemptOutcome.Accepted], attempts.Select(a => a.Outcome));
            Assert.Equal(3, _email.Sends);
        }

        /// <summary>A mandatory type's second unknown outcome, after its one repeat, isn't repeated again: <c>Failed {Provider}</c>, <c>unknown_outcome</c>.</summary>
        [Fact]
        public async Task A_mandatory_second_unknown_outcome_fails_as_unknown_outcome()
        {
            LocalStack.RequireAvailable();
            _email.Then(SendResult.Retry(FailureCategory.Provider, "connect refused"), SendResult.Unknown("dropped after DATA"), SendResult.Unknown("dropped again"));
            var delivery = await DeliveryAsync(mandatory: true);

            for (var pass = 0; pass < 4; pass++)
            {
                await PassAsync();
                var (between, _) = await ReadAsync(delivery.Id);
                if (between.NextAt is { } next)
                {
                    AdvanceTo(next);
                }
            }

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Failed, FailureCategory.Provider), (row.Status, row.FailureCategory));
            Assert.StartsWith("unknown_outcome", row.LastError);
            Assert.Equal([AttemptOutcome.RetryableFailure, AttemptOutcome.Unknown, AttemptOutcome.Unknown], attempts.Select(a => a.Outcome));
            Assert.Equal(3, _email.Sends);
        }

        /// <summary>A permanent failure (a 4xx: the destination refuses it) fails the delivery after one attempt, with its category, and isn't retried.</summary>
        [Fact]
        public async Task A_permanent_failure_fails_after_one_attempt_without_retry()
        {
            LocalStack.RequireAvailable();
            _webhook.Then(SendResult.Permanent(FailureCategory.Destination, "410 Gone", 410));
            var delivery = await DeliveryAsync(Channels.Webhook);

            await PassAsync();
            _clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(0, (await PassAsync()).Claimed);

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Failed, FailureCategory.Destination, "410 Gone", 1, (DateTimeOffset?)null),
                (row.Status, row.FailureCategory, row.LastError, row.Attempts, row.NextAt));
            var attempt = Assert.Single(attempts);
            Assert.Equal((AttemptOutcome.PermanentFailure, (int?)410), (attempt.Outcome, attempt.StatusCode));
            Assert.Equal(1, _webhook.Sends);
        }

        /// <summary>
        /// A stale worker's unknown outcome doesn't outweigh a definite refusal: when both are unapplied, the 404 decides (no extra send, and the
        /// category is the receiver's), as §10.3 and proof 5 want.
        /// </summary>
        [Fact]
        public async Task A_definite_refusal_outweighs_a_stale_workers_unknown_outcome()
        {
            LocalStack.RequireAvailable();
            var delivery = await DeliveryAsync(Channels.Webhook);
            _webhook.OnSend = async () =>
            {
                // While this send is in flight, a worker whose lease ended records its own send of the same delivery, outcome unknown.
                await using var db = _database.Context();
                db.Attempts.Add(new DeliveryAttempt
                {
                    TenantId = Tenant, DeliveryId = delivery.Id, Number = 1, IntegrationId = _seed.Webhook, At = _clock.GetUtcNow(),
                    Outcome = AttemptOutcome.Unknown, Category = FailureCategory.Provider, Error = "stale: no answer",
                });
                await db.SaveChangesAsync(Ct);
            };
            _webhook.Then(SendResult.Permanent(FailureCategory.Destination, "404 Not Found", 404));

            await PassAsync();

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Failed, FailureCategory.Destination, 2), (row.Status, row.FailureCategory, row.Attempts));
            Assert.Equal([AttemptOutcome.Unknown, AttemptOutcome.PermanentFailure], attempts.Select(a => a.Outcome));
            Assert.Equal(1, _webhook.Sends);
        }

        /// <summary>
        /// The reverse: a stale worker's refusal (say a 401 signed with a secret since rotated) is older than the current owner's send, whose
        /// outcome is unknown. The newer send may have gone out, so the unknown-outcome policy decides: a webhook is repeated, not failed.
        /// </summary>
        [Fact]
        public async Task An_older_stale_refusal_doesnt_outweigh_a_newer_unknown_outcome()
        {
            LocalStack.RequireAvailable();
            var delivery = await DeliveryAsync(Channels.Webhook);
            _webhook.OnSend = async () =>
            {
                await using var db = _database.Context();
                db.Attempts.Add(new DeliveryAttempt
                {
                    TenantId = Tenant, DeliveryId = delivery.Id, Number = 1, IntegrationId = _seed.Webhook, At = _clock.GetUtcNow().AddSeconds(-30),
                    Outcome = AttemptOutcome.PermanentFailure, Category = FailureCategory.Content, StatusCode = 401, Error = "stale: 401",
                });
                await db.SaveChangesAsync(Ct);
            };
            _webhook.Then(SendResult.Unknown("no answer"));

            await PassAsync();

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal(DeliveryStatus.Retrying, row.Status);
            Assert.Equal([AttemptOutcome.PermanentFailure, AttemptOutcome.Unknown], attempts.Select(a => a.Outcome));
        }

        /// <summary>
        /// Proof 4: the worker dies after the send, before its step is saved. The attempt is recorded anyway; once the row is due again another
        /// worker applies it instead of sending blindly, and a webhook unknown is then re-sent under the same delivery id.
        /// </summary>
        [Fact]
        public async Task Dispatcher_killed_after_send_resends_after_lease_with_the_same_id()
        {
            LocalStack.RequireAvailable();
            _faults.Kills = 1;
            _webhook.Then(SendResult.Unknown("no response"), SendResult.Delivered(200));
            var delivery = await DeliveryAsync(Channels.Webhook);

            Assert.Equal(0, (await PassAsync("dies")).Completed);
            var (dead, recorded) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Pending, 0), (dead.Status, dead.Attempts)); // the step's save was lost
            Assert.Equal((1, AttemptOutcome.Unknown), (Assert.Single(recorded).Number, recorded[0].Outcome));

            _clock.Advance(Options.Lease + TimeSpan.FromSeconds(1));
            await PassAsync("w2"); // applies the recorded attempt: a retry, no send
            Assert.Equal(1, _webhook.Sends);
            var (applied, _) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Retrying, 1), (applied.Status, applied.Attempts));
            AdvanceTo(applied.NextAt);
            await PassAsync("w2");

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal(DeliveryStatus.Delivered, row.Status);
            Assert.Equal([(delivery.Id, 1), (delivery.Id, 2)], _webhook.Seen.Select(s => (s.Delivery, s.Attempt)));
            Assert.Equal([AttemptOutcome.Unknown, AttemptOutcome.Delivered], attempts.Select(a => a.Outcome));
        }

        /// <summary>Proof 4, accepted: the worker dies after a send that was accepted; the next worker takes the recorded attempt in and doesn't re-send.</summary>
        [Fact]
        public async Task Dispatcher_killed_after_an_accepted_send_applies_it_without_resending()
        {
            LocalStack.RequireAvailable();
            _faults.Kills = 1;
            _webhook.Then(SendResult.Accepted("msg-1"));
            var delivery = await DeliveryAsync(Channels.Webhook);

            await PassAsync("dies");
            _clock.Advance(Options.Lease + TimeSpan.FromSeconds(1));
            Assert.Equal(1, (await PassAsync("w2")).Completed);
            _clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(0, (await PassAsync("w2")).Claimed);

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Accepted, "msg-1", 1, (DateTimeOffset?)null), (row.Status, row.ProviderMessageId, row.Attempts, row.NextAt));
            Assert.Single(attempts);
            Assert.Equal(1, _webhook.Sends);
        }

        /// <summary>
        /// The worker stops while the provider is sending: the provider doesn't get the worker's token, so the send finishes, its attempt is
        /// recorded and applied, and the delivery isn't sent again.
        /// </summary>
        [Fact]
        public async Task A_worker_stopped_mid_send_records_the_attempt_and_does_not_send_twice()
        {
            LocalStack.RequireAvailable();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _email.OnSend = async () =>
            {
                entered.TrySetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);
            };
            var delivery = await DeliveryAsync();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

            var pass = PassAsync(ct: stopping.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await stopping.CancelAsync();
            var result = await pass;
            _email.OnSend = null;
            _clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(0, (await PassAsync()).Claimed);

            Assert.Equal(1, result.Completed);
            Assert.False(_email.Seen.Single().Token.CanBeCanceled);
            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Accepted, 1, (string?)null), (row.Status, row.Attempts, row.ClaimedBy));
            Assert.Equal(AttemptOutcome.Accepted, Assert.Single(attempts).Outcome);
            Assert.Equal(1, _email.Sends);
        }

        /// <summary>
        /// A dead-letter retry of a failed delivery with an attempt recorded but never taken in (#2, a stale worker's): the real
        /// <see cref="RetryDelivery"/> command moves past it, so the next pass sends attempt #3 instead of re-applying #2.
        /// </summary>
        [Fact]
        public async Task A_dead_letter_retry_over_an_unapplied_attempt_sends_again()
        {
            LocalStack.RequireAvailable();
            var delivery = await DeliveryAsync(status: DeliveryStatus.Failed, attempts: 1, due: false);
            await using (var db = _database.Context())
            {
                db.Attempts.AddRange(
                    new DeliveryAttempt { TenantId = Tenant, DeliveryId = delivery.Id, Number = 1, IntegrationId = _seed.Email, At = _clock.GetUtcNow(),
                        Outcome = AttemptOutcome.PermanentFailure, Category = FailureCategory.Content, Error = "first" },
                    new DeliveryAttempt { TenantId = Tenant, DeliveryId = delivery.Id, Number = 2, IntegrationId = _seed.Email, At = _clock.GetUtcNow(),
                        Outcome = AttemptOutcome.PermanentFailure, Category = FailureCategory.Content, Error = "stale" });
                await db.SaveChangesAsync(Ct);
            }

            await using (var scope = _services!.CreateAsyncScope())
            {
                using var context = NotificationsMessageContext.Begin("test");
                var retried = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new RetryDelivery(Tenant, delivery.Id), Ct);
                await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().SaveChangesAsync(Ct);
                Assert.Equal((DeliveryStatus.Pending, 2, 1), (retried.Value!.Status, retried.Value.Attempts, retried.Value.Replays));
            }
            await PassAsync();

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Accepted, 3, 2), (row.Status, row.Attempts, row.AttemptsAtReplay));
            Assert.Equal([(delivery.Id, 3)], _email.Seen.Select(s => (s.Delivery, s.Attempt)));
            Assert.Equal([1, 2, 3], attempts.Select(a => a.Number));
        }

        /// <summary>
        /// A stale worker's failure, recorded just after a dead-letter retry (its send began before the retry), doesn't answer the retry: it is
        /// numbered past, and the retry still sends.
        /// </summary>
        [Fact]
        public async Task A_stale_failure_recorded_after_a_retry_does_not_use_up_the_retry()
        {
            LocalStack.RequireAvailable();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = 1;
            _email.OnSend = async () =>
            {
                if (Interlocked.Exchange(ref first, 0) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            };
            // w2's send is refused; w1's (stale) send, still in flight, is refused too; the retry's send goes through.
            _email.Then(SendResult.Permanent(FailureCategory.Destination, "w2: 550"), SendResult.Permanent(FailureCategory.Destination, "w1 stale: 550"));
            var delivery = await DeliveryAsync();

            var stale = Task.Run(() => PassAsync("w1"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await using (var expire = _database.Context())
            {
                // w1's lease ends while it sends (leases are on the database clock).
                await expire.Deliveries.Where(d => d.Id == delivery.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
            }
            await PassAsync("w2");
            Assert.Equal(DeliveryStatus.Failed, (await ReadAsync(delivery.Id)).Row.Status);

            _clock.Advance(TimeSpan.FromSeconds(1)); // the retry comes after w1's send began
            await using (var scope = _services!.CreateAsyncScope())
            {
                using var context = NotificationsMessageContext.Begin("test");
                await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new RetryDelivery(Tenant, delivery.Id), Ct);
                await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().SaveChangesAsync(Ct);
            }
            release.SetResult(); // w1's send ends now and records its attempt
            await stale;
            var sendsBefore = _email.Sends;
            await PassAsync("w3");

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal(DeliveryStatus.Accepted, row.Status);
            Assert.Equal(sendsBefore + 1, _email.Sends);
            Assert.Equal([1, 2, 3], attempts.Select(a => a.Number));
        }

        /// <summary>
        /// A stale unknown outcome recorded after a retry doesn't count against the retry's fresh budget: a mandatory type still gets its one
        /// repeat after the retry's own unknown outcome.
        /// </summary>
        [Fact]
        public async Task A_stale_unknown_after_a_retry_doesnt_take_the_mandatory_repeat()
        {
            LocalStack.RequireAvailable();
            var delivery = await DeliveryAsync(mandatory: true, status: DeliveryStatus.Failed, attempts: 1, due: false);
            await using (var db = _database.Context())
            {
                db.Attempts.Add(new DeliveryAttempt { TenantId = Tenant, DeliveryId = delivery.Id, Number = 1, IntegrationId = _seed.Email,
                    At = _clock.GetUtcNow(), Outcome = AttemptOutcome.RetryableFailure, Category = FailureCategory.Provider, Error = "first" });
                await db.SaveChangesAsync(Ct);
            }
            _clock.Advance(TimeSpan.FromSeconds(10));
            await using (var scope = _services!.CreateAsyncScope())
            {
                using var context = NotificationsMessageContext.Begin("test");
                await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new RetryDelivery(Tenant, delivery.Id), Ct);
                await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().SaveChangesAsync(Ct);
            }
            await using (var db = _database.Context())
            {
                // A stale worker's send, begun before the retry, recorded after it: unknown.
                db.Attempts.Add(new DeliveryAttempt { TenantId = Tenant, DeliveryId = delivery.Id, Number = 2, IntegrationId = _seed.Email,
                    At = _clock.GetUtcNow().AddSeconds(-5), DurationMs = 1000, Outcome = AttemptOutcome.Unknown, Category = FailureCategory.Provider, Error = "stale" });
                await db.SaveChangesAsync(Ct);
            }
            _email.Then(SendResult.Unknown("dropped after DATA"));

            await PassAsync();

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal(DeliveryStatus.Retrying, row.Status);
            Assert.Equal([1, 2, 3], attempts.Select(a => a.Number));
            Assert.Equal(1, _email.Sends);
        }

        /// <summary>A snapshot with no address, or one that isn't JSON, is the delivery's own data: <c>Failed {Destination}</c> at once, nothing sent.</summary>
        [Theory]
        [InlineData("""{"name":"alice"}""")]
        [InlineData("""{"address":""}""")]
        [InlineData("not json")]
        [InlineData("""["a@example.com"]""")]
        public async Task A_snapshot_without_an_address_fails_as_destination_without_a_send(string snapshot)
        {
            LocalStack.RequireAvailable();
            var delivery = await DeliveryAsync(snapshot: snapshot);

            Assert.Equal(1, (await PassAsync()).Completed);

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Failed, FailureCategory.Destination, (DateTimeOffset?)null), (row.Status, row.FailureCategory, row.NextAt));
            Assert.Empty(attempts);
            Assert.Equal(0, _email.Sends);
        }

        /// <summary>
        /// An address kept as a secret reference to an environment variable that isn't set is a deployment problem, not an attempt: the step
        /// fails (StepFailures goes up, nothing is recorded or sent), and at the step-failure limit the delivery is given up as failed.
        /// </summary>
        [Fact]
        public async Task An_unset_secret_fails_the_step_without_recording_an_attempt()
        {
            LocalStack.RequireAvailable();
            var variable = "NOTIFICATIONS_SECRET_UNSET_" + Guid.NewGuid().ToString("N");
            var delivery = await DeliveryAsync(snapshot: $$"""{"addressRef":"env:{{variable}}"}""");
            var options = new StageOptions { Batch = 1, Lease = TimeSpan.FromSeconds(60), MaxStepFailures = 2, FailureBackoff = TimeSpan.FromSeconds(5) };

            Assert.Equal(1, (await PassAsync(options: options)).Failed);
            var (failing, none) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Pending, 1, 0), (failing.Status, failing.StepFailures, failing.Attempts));
            Assert.Empty(none);

            AdvanceTo(failing.NextAt);
            Assert.Equal(1, (await PassAsync(options: options)).Failed);

            var (row, attempts) = await ReadAsync(delivery.Id);
            Assert.Equal((DeliveryStatus.Failed, (DateTimeOffset?)null), (row.Status, row.NextAt));
            Assert.StartsWith("The dispatcher failed repeatedly", row.LastError);
            Assert.Contains(variable, row.LastError);
            Assert.Empty(attempts);
            Assert.Equal(0, _email.Sends);
        }
    }
}
