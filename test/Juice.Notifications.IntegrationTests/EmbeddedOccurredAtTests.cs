using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Embedded;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;
using Juice.Notifications.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static Juice.Notifications.IntegrationTests.EmbeddedHostTests;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// QA (phase 5, M45-n) in an embedded host, as Observability sends: a request's <c>occurredAt</c> through the client (range, offsets), an
    /// alert's firing held past its first step and released (the step runs at the release), and a send racing an acknowledge or a hold of its
    /// correlation in another host transaction (#53).
    /// </summary>
    public sealed class EmbeddedOccurredAtTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000e0b01");
        private const string Thing = "host.thing.happened";
        private const string Alert = "host.alert.firing";
        private static readonly StageOptions Options = new() { Batch = 10, Lease = TimeSpan.FromSeconds(30) };
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = new HostDbContext(new DbContextOptionsBuilder<HostDbContext>().UseNpgsql(_database.ConnectionString).Options);
            await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA host; CREATE TABLE host.things (id uuid PRIMARY KEY, name text NOT NULL);");
        }

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        private Task<EmbeddedHost> HostAsync()
            => EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false, services: s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock)));

        private static NotificationToSend Send(string key, DateTimeOffset? occurredAt = null, string type = Thing, string? correlation = null,
            IReadOnlyDictionary<string, string>? labels = null)
            => new(type, key) { Data = JsonSerializer.SerializeToElement(new { n = 1 }), OccurredAt = occurredAt, CorrelationKey = correlation, Labels = labels };

        /// <summary>
        /// The client takes any occurrence (decision of 26 Nov): one out of range is kept as sent (in UTC), and
        /// only the escalate step clamps it to [now − 7 days, now + 5 minutes] (the next test). A time with an offset other than
        /// UTC (a host that sends local times) names the same instant and is kept as it, in UTC; so is an expiry.
        /// </summary>
        [Fact]
        public async Task The_client_takes_any_occurrence_as_sent_in_any_offset()
        {
            LocalStack.RequireAvailable();
            await using var host = await HostAsync();
            var now = _clock.GetUtcNow();
            var local = now.AddMinutes(-3).ToOffset(TimeSpan.FromHours(7));
            foreach (var (key, at) in new (string, DateTimeOffset)[]
            {
                ("ahead", now.AddHours(1)), ("old", now.AddDays(-30)), ("edge-ahead", now.AddMinutes(5)), ("edge-old", now.AddDays(-7)), ("local", local),
            })
            {
                await host.InTransactionAsync(async (_, client) => (await client.SendAsync(Send(key, at), Ct)).Id);
            }
            var expires = now.AddHours(2).ToOffset(TimeSpan.FromHours(7));
            await host.InTransactionAsync(async (_, client) => (await client.SendAsync(Send("expires-local") with { ExpiresAt = expires }, Ct)).Id);

            await using var db = _database.Context();
            var stored = await db.Requests.AsNoTracking().Where(r => r.TenantId == Tenant)
                .ToDictionaryAsync(r => r.IdempotencyKey, r => (r.OccurredAt, r.ExpiresAt), Ct);
            long Ms(DateTimeOffset? at) => at!.Value.ToUnixTimeMilliseconds();
            Assert.Equal(Ms(now.AddHours(1)), Ms(stored["ahead"].OccurredAt)); // stored as sent; the escalate step clamps
            Assert.Equal(Ms(now.AddDays(-30)), Ms(stored["old"].OccurredAt));
            Assert.Equal(Ms(now.AddMinutes(5)), Ms(stored["edge-ahead"].OccurredAt));
            Assert.Equal(Ms(now.AddDays(-7)), Ms(stored["edge-old"].OccurredAt));
            Assert.Equal(Ms(local), Ms(stored["local"].OccurredAt));
            Assert.Equal(Ms(expires), Ms(stored["expires-local"].ExpiresAt));
        }

        /// <summary>
        /// Clamped events time the first step (5 minutes): 30 days old is due at once; an hour ahead is due 5 + 5 minutes on; 2 minutes old is
        /// in range (due in 3). Each clamp is counted once, by direction, on <c>notifications.requests_occurred_at_clamped</c>, after the step
        /// commits; the in-range one isn't, and a second pass over the same notification counts nothing more. Only this test's measurements are
        /// read (an async-local mark), since the meter is process-wide and other classes clamp beside it.
        /// </summary>
        [Theory]
        [InlineData(-30 * 24 * 60, 0, "past")]
        [InlineData(60, 10, "future")]
        [InlineData(-2, 3, null)]
        public async Task A_clamped_occurrence_times_the_first_step_from_its_clamp_and_is_counted_once(int occurredInMinutes, int dueInMinutes, string? direction)
        {
            LocalStack.RequireAvailable();
            var counted = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var listener = new System.Diagnostics.Metrics.MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == Telemetry.Name && instrument.Name == "notifications.requests_occurred_at_clamped")
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                },
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                if (Mine.Value)
                {
                    foreach (var tag in tags)
                    {
                        if (tag.Key == "direction")
                        {
                            for (var i = 0; i < value; i++)
                            {
                                counted.Enqueue((string)tag.Value!);
                            }
                        }
                    }
                }
            });
            listener.Start();
            Mine.Value = true;

            await using var host = await HostAsync();
            await AlertSetupAsync();
            var key = $"alert:clamped{occurredInMinutes}";
            var now = _clock.GetUtcNow();
            await host.InTransactionAsync(async (_, client) =>
                (await client.SendAsync(Send(key, now.AddMinutes(occurredInMinutes), Alert, key, new Dictionary<string, string> { ["policy"] = "page" }), Ct)).Id);
            await DrainAsync<Notification, PlannerStage>(host, "planner");
            Assert.Equal(now.AddMinutes(dueInMinutes), (await EscalationAsync(key)).NextAt);
            await DrainAsync<Notification, PlannerStage>(host, "planner");
            Assert.Equal(direction is null ? [] : [direction], counted.ToArray());
            // Kept as sent: only the timing is clamped.
            await using var db = _database.Context();
            Assert.Equal(now.AddMinutes(occurredInMinutes), (await db.Requests.AsNoTracking().SingleAsync(r => r.IdempotencyKey == key, Ct)).OccurredAt);
        }

        private static readonly AsyncLocal<bool> Mine = new();

        /// <summary>
        /// Observability's flow: an alert fires, its correlation is held (a silence) at once, its first step's wait (5 minutes from the firing)
        /// passes under the hold and the step parks; the release, 10 minutes after the firing, runs the step then, not 5 minutes later.
        /// </summary>
        [Fact]
        public async Task A_firing_held_past_its_first_step_gets_the_step_at_the_release()
        {
            LocalStack.RequireAvailable();
            await using var host = await HostAsync();
            await AlertSetupAsync();
            const string key = "alert:held-firing";
            var fired = _clock.GetUtcNow();
            await host.InTransactionAsync(async (_, client) =>
            {
                await client.SendAsync(Send("held-1", fired, Alert, key, new Dictionary<string, string> { ["policy"] = "page" }), Ct);
                return (await client.HoldAsync(key, "silence:9", "obs", Ct)).Changed;
            });
            await DrainAsync<Notification, PlannerStage>(host, "planner");
            Assert.Equal(fired.AddMinutes(5), (await EscalationAsync(key)).NextAt);

            _clock.Advance(TimeSpan.FromMinutes(10));
            await DrainAsync<Escalation, EscalationStage>(host, "escalation");
            var parked = await EscalationAsync(key);
            Assert.Equal(((DateTimeOffset?)null, true), (parked.NextAt, parked.HeldAt is not null));
            Assert.Empty(await StepOneAsync(key));

            var released = _clock.GetUtcNow();
            Assert.Equal(1, (await host.InTransactionAsync(async (_, client) => await client.ReleaseAsync(key, "obs", Ct))).Resumed);
            await DrainAsync<Escalation, EscalationStage>(host, "escalation");
            await DrainAsync<Notification, PlannerStage>(host, "planner");
            var step = Assert.Single(await StepOneAsync(key));
            Assert.Equal(("route:all", released), (step.NamedAs, step.CreatedAt));
        }

        /// <summary>
        /// The same alert sent late (Observability held the firing itself under a silence and sends it at the release): with its firing time,
        /// the first step, whose wait has passed, runs at once.
        /// </summary>
        [Fact]
        public async Task A_firing_sent_late_with_its_time_gets_the_first_step_at_once()
        {
            LocalStack.RequireAvailable();
            await using var host = await HostAsync();
            await AlertSetupAsync();
            const string key = "alert:late-firing";
            var fired = _clock.GetUtcNow().AddMinutes(-10);
            await host.InTransactionAsync(async (_, client) =>
                (await client.SendAsync(Send("late-1", fired, Alert, key, new Dictionary<string, string> { ["policy"] = "page" }), Ct)).Id);
            await DrainAsync<Notification, PlannerStage>(host, "planner");
            Assert.Equal(_clock.GetUtcNow(), (await EscalationAsync(key)).NextAt);
            await DrainAsync<Escalation, EscalationStage>(host, "escalation");
            await DrainAsync<Notification, PlannerStage>(host, "planner");
            Assert.Equal("route:all", Assert.Single(await StepOneAsync(key)).NamedAs);
        }

        /// <summary>
        /// #53 with the real calls, when the other host transaction already holds the correlation: a hold or an acknowledge written there and not
        /// yet committed makes the send wait (its upsert of the correlation waits on the row), then read what committed and be accepted on it: no
        /// lost change and no refusal. The hold stands; the acknowledgement stands (an acknowledged correlation is open). Rolled back instead, the send
        /// goes on as if nothing happened. (The <c>concurrent_change</c> window, a change committed between the send's read and its save, is the
        /// PR's interceptor test.)
        /// </summary>
        [Theory]
        [InlineData("hold", true)]
        [InlineData("acknowledge", true)]
        [InlineData("hold", false)]
        public async Task A_send_waiting_on_a_hold_or_acknowledge_in_another_host_transaction_is_accepted_on_what_committed(string change, bool commit)
        {
            LocalStack.RequireAvailable();
            await using var host = await HostAsync();
            var key = $"alert:wait-{change}-{commit}";
            await host.InTransactionAsync(async (_, client) => (await client.SendAsync(Send(key + "-1", correlation: key), Ct)).Id);

            await using var other = host.Services.CreateAsyncScope();
            var otherDb = other.ServiceProvider.GetRequiredService<HostDbContext>();
            await using var otherTransaction = await otherDb.Database.BeginTransactionAsync(Ct);
            var otherClient = other.ServiceProvider.GetRequiredService<INotificationClient>();
            if (change == "hold")
            {
                await otherClient.HoldAsync(key, "silence:1", "obs", Ct);
            }
            else
            {
                await otherClient.AcknowledgeAsync(key, "obs", Ct);
            }

            var accepted = host.InTransactionAsync(async (_, client) => await client.SendAsync(Send(key + "-2", correlation: key), Ct));
            await LockWaitAsync();
            if (commit)
            {
                await otherTransaction.CommitAsync(Ct);
            }
            else
            {
                await otherTransaction.RollbackAsync(Ct);
            }
            Assert.False((await accepted).Duplicate);

            await using var db = _database.Context();
            Assert.Equal(2, await db.Requests.CountAsync(r => r.CorrelationKey == key, Ct));
            var correlation = await db.Correlations.AsNoTracking().SingleAsync(c => c.Key == key, Ct);
            Assert.Equal(change == "hold" && commit, correlation.HeldAt is not null);
            // An acknowledged correlation is still open: the request keeps it acknowledged (it reopens only a closed one).
            Assert.Equal(change == "acknowledge" && commit ? CorrelationState.Acknowledged : CorrelationState.Open, correlation.State);
        }

        /// <summary>
        /// QA (M44-2b), Observability's cutover end to end with a real receiver: an alert fires in shadow (an embedded host in dry run), the host
        /// restarts out of it, and the alert's escalation step falls due after the switch. The firing stays <c>WouldSend</c> (legacy sent it); the
        /// step, planned after the switch, pages for real (legacy's escalation stopped at the switch): the receiver hears exactly that one.
        /// </summary>
        [Fact]
        public async Task An_alert_fired_in_shadow_escalates_for_real_after_the_switch()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await DeliveryTests.Receiver.StartAsync();
            const string key = "alert:cutover";
            Guid firing;
            await using (var shadow = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false, receiver, services: s =>
            {
                s.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
                s.PostConfigure<Providers.DeliveryOptions>(o => o.DryRun = true);
            }))
            {
                await AlertSetupAsync();
                await using (var db = _database.Context())
                {
                    var type = await db.Types.AsNoTracking().SingleAsync(t => t.Key == Alert, Ct);
                    db.Templates.Add(new Template { TypeId = type.Id, Channel = Channels.Webhook, Locale = "en", Version = 1, Engine = "scriban", Body = """{"alert":"firing"}""", CreatedAt = Seed.Now });
                    await db.SaveChangesAsync(Ct);
                }
                firing = await shadow.InTransactionAsync(async (_, client) =>
                    (await client.SendAsync(Send("cutover-1", _clock.GetUtcNow(), Alert, key, new Dictionary<string, string> { ["policy"] = "page" }), Ct)).Id);
                await DrainAsync<Notification, PlannerStage>(shadow, "planner");
                await DrainAsync<NotificationRecipient, Delivering.DeliveryPlannerStage>(shadow, "delivery-planner");
                await DrainAsync<Delivery, Delivering.DispatcherStage>(shadow, "dispatcher");
            }
            Assert.Empty(receiver.Requests);

            await using (var live = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false, receiver,
                services: s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock))))
            {
                _clock.Advance(TimeSpan.FromMinutes(5));
                await DrainAsync<Escalation, EscalationStage>(live, "escalation");
                await DrainAsync<Notification, PlannerStage>(live, "planner");
                await DrainAsync<NotificationRecipient, Delivering.DeliveryPlannerStage>(live, "delivery-planner");
                await DrainAsync<Delivery, Delivering.DispatcherStage>(live, "dispatcher");
            }
            Assert.Single(receiver.Requests);
            await using var check = _database.Context();
            var sent = await (from d in check.Deliveries.AsNoTracking()
                              join i in check.DeliveryItems on new { d.TenantId, DeliveryId = d.Id } equals new { i.TenantId, i.DeliveryId }
                              join r in check.Recipients on new { i.TenantId, Id = i.RecipientId } equals new { r.TenantId, r.Id }
                              where i.RequestId == firing
                              orderby r.EscalationStep
                              select new { r.EscalationStep, d.Status, d.DryRun }).ToListAsync(Ct);
            Assert.Equal([(0, DeliveryStatus.WouldSend, true), (1, DeliveryStatus.Delivered, false)], sent.Select(x => (x.EscalationStep, x.Status, x.DryRun)));
        }

        /// <summary>A type whose workflow escalates, and a policy whose one step, 5 minutes on, pages the provisioned route <c>all</c> by key.</summary>
        private async Task AlertSetupAsync()
        {
            await using var db = _database.Context();
            var app = await db.Applications.AsNoTracking().SingleAsync(a => a.Key == "host", Ct);
            db.Types.Add(new NotificationType
            {
                ApplicationId = app.Id, Key = Alert, Version = 1, Name = "Alert firing", Category = "alerts", DisplayGroup = DisplayGroups.General,
                Priority = NotificationPriority.High, Schema = "{}", SchemaHash = "h",
                Workflow = """[{"step":"send"},{"policyFrom":"labels.policy","step":"escalate"}]""", DeclarationHash = "d", CreatedAt = Seed.Now,
            });
            db.EscalationPolicies.Add(new EscalationPolicy
            {
                TenantId = Tenant, Key = "page", Steps = """[{"afterMinutes":5,"recipients":[{"kind":"route","key":"all"}]}]""", DefinitionHash = "h",
                UpdatedAt = Seed.Now,
            });
            await db.SaveChangesAsync(Ct);
        }

        private static async Task DrainAsync<T, TStage>(EmbeddedHost host, string stage) where T : Claimable, ITenantOwned where TStage : class, IStage<T>
        {
            var runner = host.Services.GetRequiredService<StageRunner<T, TStage>>();
            PassResult pass;
            do
            {
                pass = await runner.RunOnceAsync(stage, Options, Ct);
                Assert.Equal(0, pass.Failed);
            }
            while (pass.Claimed > 0);
        }

        private async Task<Escalation> EscalationAsync(string key)
        {
            await using var db = _database.Context();
            var correlation = await db.Correlations.AsNoTracking().SingleAsync(c => c.Key == key, Ct);
            return await db.Escalations.AsNoTracking().SingleAsync(e => e.CorrelationId == correlation.Id, Ct);
        }

        private async Task<List<NotificationRecipient>> StepOneAsync(string key)
        {
            await using var db = _database.Context();
            var correlation = await db.Correlations.AsNoTracking().SingleAsync(c => c.Key == key, Ct);
            return await (from r in db.Recipients.AsNoTracking()
                          join e in db.Escalations on new { r.TenantId, r.NotificationId } equals new { e.TenantId, e.NotificationId }
                          where e.CorrelationId == correlation.Id && r.EscalationStep == 1
                          select r).ToListAsync(Ct);
        }

        private async Task LockWaitAsync()
        {
            await using var db = _database.Context();
            for (var i = 0; i < 100; i++)
            {
                if (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'")
                    .SingleAsync(Ct) > 0)
                {
                    return;
                }
                await Task.Delay(50, Ct);
            }
            Assert.Fail("The send never waited on the other transaction.");
        }
    }
}
