using Juice.Notifications.Correlations;
using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Inbox;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;
using Juice.Notifications.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Escalation (phase-3-plan M22, design §7.1), driven by hand on a manual clock: phase 3 proof point 2 (acknowledging stops an escalation
    /// whose policy stops on acknowledgement, and not one whose policy doesn't), a step's people are added once and sent at once, and resolving
    /// stops what is left.
    /// </summary>
    public sealed class EscalationTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 10, Lease = TimeSpan.FromSeconds(30) };
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        private readonly FakePeopleResolver _staff = new();
        private ServiceProvider? _services;
        private (Guid App, NotificationType Type) _seed;

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = await _database.MigratedAsync();
            var app = Seed.Application("esc");
            var type = new NotificationType
            {
                ApplicationId = app.Id, Key = "esc.incident", Version = 1, Name = "Incident", Category = "incidents", DisplayGroup = DisplayGroups.General,
                Priority = NotificationPriority.High, Schema = "{}", SchemaHash = "h",
                Workflow = """[{"step":"send"},{"policyFrom":"labels.policy","step":"escalate"}]""", DeclarationHash = "d", CreatedAt = Seed.Now,
            };
            db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now });
            db.Resolvers.Add(new RecipientResolverConfig
            {
                TenantId = Tenant, Key = "staff", Kind = "fake", Handles = ["group", "user"], Settings = "{}", DefinitionHash = "h", UpdatedAt = Seed.Now,
            });
            const string steps = """[{"afterMinutes":5,"recipients":[{"id":"lead","kind":"group","resolver":"staff"}]}]""";
            db.EscalationPolicies.AddRange(
                new EscalationPolicy { TenantId = Tenant, Key = "stops", Steps = steps, StopOnAcknowledge = true, DefinitionHash = "h", UpdatedAt = Seed.Now },
                new EscalationPolicy { TenantId = Tenant, Key = "keeps", Steps = steps, StopOnAcknowledge = false, DefinitionHash = "h", UpdatedAt = Seed.Now });
            await db.SaveChangesAsync();
            _seed = (app.Id, type);
            _staff.Groups["crew"] = [new ResolvedPerson(FakePeopleResolver.Source, "ann"), new ResolvedPerson(FakePeopleResolver.Source, "ben")];
            // The lead is ann (already reached: not added twice) and lee (new).
            _staff.Groups["lead"] = [new ResolvedPerson(FakePeopleResolver.Source, "ann"), new ResolvedPerson(FakePeopleResolver.Source, "lee")];

            var services = new ServiceCollection().AddLogging(l => l.AddProvider(new ErrorLogProvider()));
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddPostgresClaimStore();
            services.AddPlanning(Options, runWorkers: false);
            services.AddSingleton<IRecipientResolver>(_staff);
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

        private async Task DrainAsync<T, TStage>(string stage) where T : Claimable, ITenantOwned where TStage : class, IStage<T>
        {
            var runner = _services!.GetRequiredService<StageRunner<T, TStage>>();
            PassResult pass;
            do
            {
                pass = await runner.RunOnceAsync(stage, Options, Ct);
                Assert.Equal(0, pass.Failed);
            }
            while (pass.Claimed > 0);
        }

        /// <summary>A notification to the crew on a new correlation, escalating by the policy named; its id and correlation key.</summary>
        private async Task<(Guid Notification, string Key)> NotificationAsync(string policy)
        {
            await using var db = _database.Context();
            var key = "incident:" + Guid.NewGuid().ToString("N");
            var now = _clock.GetUtcNow();
            var correlation = new Correlation { TenantId = Tenant, ApplicationId = _seed.App, Key = key, CreatedAt = now, LastRequestAt = now, UpdatedAt = now };
            db.Correlations.Add(correlation);
            var request = Seed.Request(Tenant, _seed.App, _seed.Type.Id, Guid.NewGuid().ToString("N"));
            db.Entry(request).Property(r => r.TypeKey).CurrentValue = _seed.Type.Key;
            db.Entry(request).Property(r => r.CorrelationKey).CurrentValue = key;
            db.Entry(request).Property(r => r.CorrelationId).CurrentValue = correlation.Id;
            db.Entry(request).Property(r => r.Labels).CurrentValue = new Dictionary<string, string> { ["policy"] = policy };
            db.Entry(request).Property(r => r.Recipients).CurrentValue = """[{"kind":"group","id":"crew","resolver":"staff"}]""";
            var notification = Seed.Notification(Tenant, request.Id);
            notification.NextAt = now;
            db.AddRange(request, notification);
            await db.SaveChangesAsync(Ct);
            return (notification.Id, key);
        }

        private async Task<Escalation> EscalationAsync(Guid notification)
        {
            await using var db = _database.Context();
            return await db.Escalations.AsNoTracking().SingleAsync(e => e.NotificationId == notification, Ct);
        }

        private async Task<List<(string Person, int Step, RecipientOutcome Outcome)>> PeopleAsync(Guid notification)
        {
            await using var db = _database.Context();
            return (await (from r in db.Recipients
                           join s in db.Subscribers on new { r.TenantId, Id = r.SubscriberId!.Value } equals new { s.TenantId, s.Id }
                           where r.NotificationId == notification
                           select new { s.ExternalId, r.EscalationStep, r.Outcome }).AsNoTracking().ToListAsync(Ct))
                .Select(x => (x.ExternalId, x.EscalationStep, x.Outcome)).OrderBy(x => x.ExternalId).ToList();
        }

        private async Task AcknowledgeAsync(string key)
        {
            await using var db = _database.Context();
            var lifecycle = new CorrelationLifecycle(db, _clock, new NoInboxEvents(), _services!.GetRequiredService<Events.ProducerEvents>(), NullLogger<CorrelationLifecycle>.Instance);
            Assert.Equal(OutcomeKind.Ok, (await lifecycle.AcknowledgeAsync(Tenant, _seed.App, key, "eve", Ct)).Kind);
        }

        [Fact]
        public async Task Acknowledging_stops_an_escalation_that_stops_on_acknowledgement_and_not_one_that_doesnt()
        {
            LocalStack.RequireAvailable();
            var (stops, stopsKey) = await NotificationAsync("stops");
            var (keeps, keepsKey) = await NotificationAsync("keeps");
            await DrainAsync<Notification, PlannerStage>("planner");
            var start = _clock.GetUtcNow();
            foreach (var id in new[] { stops, keeps })
            {
                var escalation = await EscalationAsync(id);
                Assert.Equal((EscalationState.Running, 0, start.AddMinutes(5)), (escalation.State, escalation.NextStep, escalation.NextAt!.Value));
                Assert.Equal([("ann", 0, RecipientOutcome.Planned), ("ben", 0, RecipientOutcome.Planned)], await PeopleAsync(id));
            }

            await AcknowledgeAsync(stopsKey);
            await AcknowledgeAsync(keepsKey);
            var stopped = await EscalationAsync(stops);
            Assert.Equal((EscalationState.Stopped, "acknowledged", (DateTimeOffset?)null), (stopped.State, stopped.StopReason, stopped.NextAt));
            Assert.Equal(EscalationState.Running, (await EscalationAsync(keeps)).State);

            _clock.Advance(TimeSpan.FromMinutes(5));
            await DrainAsync<Escalation, EscalationStage>("escalation");
            await DrainAsync<Notification, PlannerStage>("planner");

            // The policy that keeps going adds the lead: lee, at step 1 (ann was reached already and isn't added again).
            Assert.Equal([("ann", 0, RecipientOutcome.Planned), ("ben", 0, RecipientOutcome.Planned), ("lee", 1, RecipientOutcome.Planned)], await PeopleAsync(keeps));
            Assert.Equal(EscalationState.Exhausted, (await EscalationAsync(keeps)).State);
            // The one that stopped adds nobody.
            Assert.Equal(2, (await PeopleAsync(stops)).Count);
            await using var db = _database.Context();
            Assert.Equal(NotificationState.Planned, (await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == keeps, Ct)).State);
        }

        /// <summary>A step parked by a hold (p5-d3) is no claim for any worker, however late: the claim store skips a row with nothing due.</summary>
        [Fact]
        public async Task A_parked_escalation_is_never_claimed()
        {
            LocalStack.RequireAvailable();
            var (id, key) = await NotificationAsync("keeps");
            await DrainAsync<Notification, PlannerStage>("planner");
            await using (var db = _database.Context())
            {
                var lifecycle = new CorrelationLifecycle(db, _clock, new NoInboxEvents(), _services!.GetRequiredService<Events.ProducerEvents>(), NullLogger<CorrelationLifecycle>.Instance);
                Assert.Equal(OutcomeKind.Ok, (await lifecycle.HoldAsync(Tenant, _seed.App, key, "silence:1", null, Ct)).Kind);
            }
            _clock.Advance(TimeSpan.FromMinutes(5));
            await DrainAsync<Escalation, EscalationStage>("escalation");
            Assert.NotNull((await EscalationAsync(id)).HeldAt);

            await using var scope = _services!.CreateAsyncScope();
            var claims = await scope.ServiceProvider.GetRequiredService<IClaimStore>()
                .ClaimAsync<Escalation>("anyone", 10, TimeSpan.FromSeconds(30), _clock.GetUtcNow().AddDays(30), Ct);
            Assert.Empty(claims);
        }

        [Fact]
        public async Task Resolving_stops_an_escalation_before_its_step()
        {
            LocalStack.RequireAvailable();
            var (id, key) = await NotificationAsync("keeps");
            await DrainAsync<Notification, PlannerStage>("planner");
            await using (var db = _database.Context())
            {
                var lifecycle = new CorrelationLifecycle(db, _clock, new NoInboxEvents(), _services!.GetRequiredService<Events.ProducerEvents>(), NullLogger<CorrelationLifecycle>.Instance);
                Assert.Equal(OutcomeKind.Ok, (await lifecycle.ResolveAsync(Tenant, _seed.App, key, "fixed", "eve", Ct)).Kind);
            }
            var escalation = await EscalationAsync(id);
            Assert.Equal((EscalationState.Stopped, "resolved"), (escalation.State, escalation.StopReason));
            _clock.Advance(TimeSpan.FromMinutes(5));
            await DrainAsync<Escalation, EscalationStage>("escalation");
            Assert.Equal(2, (await PeopleAsync(id)).Count);
        }
    }
}
