using Juice.Notifications.Correlations;
using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Delivering;
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
        private NotificationType? _delayed;

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
            // The same, with a 2-minute delay before the escalate step (M45-n).
            var delayed = new NotificationType
            {
                ApplicationId = app.Id, Key = "esc.delayed", Version = 1, Name = "Delayed incident", Category = "incidents", DisplayGroup = DisplayGroups.General,
                Priority = NotificationPriority.High, Schema = "{}", SchemaHash = "h",
                Workflow = """[{"step":"send"},{"step":"delay","seconds":120},{"policyFrom":"labels.policy","step":"escalate"}]""", DeclarationHash = "d",
                CreatedAt = Seed.Now,
            };
            _delayed = delayed;
            db.Add(delayed);
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
            services.AddDeliveryPlanning(Options, runWorkers: false);
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
        private async Task<(Guid Notification, string Key)> NotificationAsync(string policy, DateTimeOffset? occurredAt = null, bool delayed = false,
            DateTimeOffset? acceptedAt = null)
        {
            await using var db = _database.Context();
            var key = "incident:" + Guid.NewGuid().ToString("N");
            var now = _clock.GetUtcNow();
            var correlation = new Correlation { TenantId = Tenant, ApplicationId = _seed.App, Key = key, CreatedAt = now, LastRequestAt = now, UpdatedAt = now };
            db.Correlations.Add(correlation);
            var type = delayed ? _delayed! : _seed.Type;
            var request = Seed.Request(Tenant, _seed.App, type.Id, Guid.NewGuid().ToString("N"));
            db.Entry(request).Property(r => r.TypeKey).CurrentValue = type.Key;
            db.Entry(request).Property(r => r.CorrelationKey).CurrentValue = key;
            db.Entry(request).Property(r => r.CorrelationId).CurrentValue = correlation.Id;
            db.Entry(request).Property(r => r.Labels).CurrentValue = new Dictionary<string, string> { ["policy"] = policy };
            db.Entry(request).Property(r => r.Recipients).CurrentValue = """[{"kind":"group","id":"crew","resolver":"staff"}]""";
            db.Entry(request).Property(r => r.OccurredAt).CurrentValue = occurredAt;
            db.Entry(request).Property(r => r.AcceptedAt).CurrentValue = acceptedAt ?? now;
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

        /// <summary>
        /// The first step's time (phase 5, M45-n; the architect's decision): with the request's occurrence time, its wait after the event and
        /// never before now, even after a workflow delay; a request sent late (held under a silence) gets the step at once. Without one (the
        /// producer didn't say, or a request from before the column), its wait from when the escalation starts, as before. Later steps stay
        /// relative to the step before.
        /// </summary>
        [Theory]
        [InlineData(null, null, false, 5)] // no occurrence: from the escalation's start
        [InlineData(null, -60, false, 5)] // accepted an hour ago, before the column: still from the start, not from acceptance
        [InlineData(-60, null, false, 0)] // an hour-old event: due at once
        [InlineData(-2, null, false, 3)] // a recent event: what is left of the wait
        [InlineData(null, null, true, 7)] // no occurrence after a 2-minute delay: 2 + 5
        [InlineData(0, null, true, 5)] // an occurrence after a 2-minute delay: from the event
        public async Task The_first_step_is_due_its_wait_after_the_event_when_there_is_one_and_from_its_start_otherwise(int? occurredMinutesAgo,
            int? acceptedMinutesAgo, bool delayed, int dueInMinutes)
        {
            LocalStack.RequireAvailable();
            var now = _clock.GetUtcNow();
            var (id, _) = await NotificationAsync("keeps", occurredMinutesAgo is { } ago ? now.AddMinutes(ago) : null, delayed,
                acceptedMinutesAgo is { } accepted ? now.AddMinutes(accepted) : null);
            await DrainAsync<Notification, PlannerStage>("planner");
            if (delayed)
            {
                await using (var db = _database.Context())
                {
                    Assert.False(await db.Escalations.AnyAsync(e => e.NotificationId == id, Ct), "the escalate step waits for the delay");
                }
                _clock.Advance(TimeSpan.FromMinutes(2));
                await DrainAsync<Notification, PlannerStage>("planner");
            }
            Assert.Equal(now.AddMinutes(dueInMinutes), (await EscalationAsync(id)).NextAt!.Value);
            if (dueInMinutes == 0)
            {
                await DrainAsync<Escalation, EscalationStage>("escalation");
                await DrainAsync<Notification, PlannerStage>("planner");
                Assert.Contains(("lee", 1, RecipientOutcome.Planned), await PeopleAsync(id));
            }
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

        /// <summary>
        /// A step naming a route by its key (phase-5 decision G5) reaches that route whatever its match, as a legacy webhook step reached its
        /// endpoints whatever events they took; a disabled or deleted route reaches nobody, and the escalation goes on.
        /// </summary>
        [Fact]
        public async Task A_step_naming_a_route_by_key_reaches_it_whatever_its_match()
        {
            LocalStack.RequireAvailable();
            await using (var db = _database.Context())
            {
                db.Routes.AddRange(
                    new Route { TenantId = Tenant, Key = "pager", Match = """{"type":"some.other.type"}""", Targets = [], DefinitionHash = "h", UpdatedAt = Seed.Now },
                    new Route { TenantId = Tenant, Key = "off", Match = "{}", Targets = [], Enabled = false, DefinitionHash = "h", UpdatedAt = Seed.Now });
                db.EscalationPolicies.Add(new EscalationPolicy
                {
                    TenantId = Tenant, Key = "by-route", DefinitionHash = "h", UpdatedAt = Seed.Now,
                    Steps = """[{"afterMinutes":5,"recipients":[{"kind":"route","key":"pager"},{"kind":"route","key":"off"},{"kind":"route","key":"gone"}]},{"afterMinutes":5,"recipients":[{"kind":"route","key":"gone"}]}]""",
                });
                await db.SaveChangesAsync(Ct);
            }
            var (id, _) = await NotificationAsync("by-route");
            await DrainAsync<Notification, PlannerStage>("planner");

            _clock.Advance(TimeSpan.FromMinutes(5));
            await DrainAsync<Escalation, EscalationStage>("escalation");
            await DrainAsync<Notification, PlannerStage>("planner");

            await using (var db = _database.Context())
            {
                var routes = await db.Recipients.AsNoTracking().Where(r => r.NotificationId == id && r.Kind == RecipientKind.Route)
                    .Select(r => new { r.NamedAs, r.EscalationStep }).ToListAsync(Ct);
                Assert.Equal([("route:pager", 1)], routes.Select(r => (r.NamedAs, r.EscalationStep)));
            }
            // The second step reaches nobody (its route is gone); the escalation still runs it, and ends.
            _clock.Advance(TimeSpan.FromMinutes(5));
            await DrainAsync<Escalation, EscalationStage>("escalation");
            await DrainAsync<Notification, PlannerStage>("planner");
            Assert.Equal(EscalationState.Exhausted, (await EscalationAsync(id)).State);
            // The routes that reached nobody are recorded with the step that named them, as a name that resolved to no one is.
            await using (var db = _database.Context())
            {
                var unreached = await db.Recipients.AsNoTracking().Where(r => r.NotificationId == id && r.Kind == RecipientKind.Name)
                    .OrderBy(r => r.EscalationStep).ThenBy(r => r.NamedAs).Select(r => new { r.NamedAs, r.EscalationStep, r.Outcome }).ToListAsync(Ct);
                Assert.Equal([("route:gone", 1, RecipientOutcome.Failed), ("route:off", 1, RecipientOutcome.Failed), ("route:gone", 2, RecipientOutcome.Failed)],
                    unreached.Select(r => (r.NamedAs, r.EscalationStep, r.Outcome)));
            }
        }

        /// <summary>
        /// A route reached when the notification was sent, and named by key in every step, gets each step's send (review of #3, decision G5): as a
        /// legacy step sent alert.escalated to every endpoint it named, whatever they had been sent. A step planned again adds nothing more.
        /// </summary>
        [Fact]
        public async Task A_route_named_by_key_is_sent_each_step_even_when_reached_before()
        {
            LocalStack.RequireAvailable();
            Guid integrationId;
            await using (var db = _database.Context())
            {
                var integration = Seed.Integration(Tenant, "pager-hooks");
                var target = new IntegrationTarget
                {
                    TenantId = Tenant, IntegrationId = integration.Id, Key = "pager-1", Address = "https://hooks.example.com/pager", DefinitionHash = "h", UpdatedAt = Seed.Now,
                };
                var route = new Route { TenantId = Tenant, Key = "pager", Match = "{}", Targets = ["pager-1"], DefinitionHash = "h", UpdatedAt = Seed.Now };
                db.AddRange(integration, target, route, new RouteTarget { TenantId = Tenant, RouteId = route.Id, TargetId = target.Id, Position = 0 });
                db.EscalationPolicies.Add(new EscalationPolicy
                {
                    TenantId = Tenant, Key = "re-page", DefinitionHash = "h", UpdatedAt = Seed.Now,
                    Steps = """[{"afterMinutes":5,"recipients":[{"kind":"route","key":"pager"}]},{"afterMinutes":5,"recipients":[{"kind":"route","key":"pager"}]}]""",
                });
                await db.SaveChangesAsync(Ct);
                integrationId = integration.Id;
            }
            var (id, _) = await NotificationAsync("re-page");
            await using (var db = _database.Context())
            {
                // Sent to the routes it matches, as an alert's firing is.
                await db.Requests.Where(r => r.Id == db.Notifications.Where(n => n.Id == id).Select(n => n.RequestId).Single())
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Recipients, """[{"kind":"route"}]"""), Ct);
            }
            async Task RunAsync()
            {
                await DrainAsync<Notification, PlannerStage>("planner");
                await DrainAsync<NotificationRecipient, DeliveryPlannerStage>("delivery-planner");
            }
            async Task<List<(string NamedAs, int Step, int Deliveries)>> PagerAsync()
            {
                await using var db = _database.Context();
                return (await db.Recipients.AsNoTracking().Where(r => r.NotificationId == id && r.NamedAs == "route:pager").OrderBy(r => r.EscalationStep)
                    .Select(r => new { r.NamedAs, r.EscalationStep, Deliveries = db.DeliveryItems.Count(i => i.RecipientId == r.Id) }).ToListAsync(Ct))
                    .Select(r => (r.NamedAs, r.EscalationStep, r.Deliveries)).ToList();
            }

            await RunAsync();
            _clock.Advance(TimeSpan.FromMinutes(5));
            await DrainAsync<Escalation, EscalationStage>("escalation");
            await RunAsync();
            Assert.Equal([("route:pager", 0, 1), ("route:pager", 1, 1)], await PagerAsync());

            // Step 1 planned again (a replanned step): nothing more.
            await using (var db = _database.Context())
            {
                await db.Notifications.Where(n => n.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.State, NotificationState.Planning).SetProperty(n => n.NextAt, _clock.GetUtcNow()), Ct);
            }
            await RunAsync();
            Assert.Equal([("route:pager", 0, 1), ("route:pager", 1, 1)], await PagerAsync());

            _clock.Advance(TimeSpan.FromMinutes(5));
            await DrainAsync<Escalation, EscalationStage>("escalation");
            await RunAsync();
            Assert.Equal([("route:pager", 0, 1), ("route:pager", 1, 1), ("route:pager", 2, 1)], await PagerAsync());
            await using (var check = _database.Context())
            {
                Assert.Equal(3, await check.Deliveries.CountAsync(d => d.IntegrationId == integrationId, Ct));
            }
        }

        /// <summary>
        /// QA (M46-0): a step that names a missing route by key, planned again (a replanned step), records the unreached route once and fails
        /// nothing: the step's own record is the only one, as for a route it reaches.
        /// </summary>
        [Fact]
        public async Task A_replanned_step_naming_a_missing_route_records_it_once()
        {
            LocalStack.RequireAvailable();
            await using (var db = _database.Context())
            {
                db.EscalationPolicies.Add(new EscalationPolicy
                {
                    TenantId = Tenant, Key = "to-gone", DefinitionHash = "h", UpdatedAt = Seed.Now,
                    Steps = """[{"afterMinutes":5,"recipients":[{"kind":"route","key":"gone"},{"id":"lead","kind":"group","resolver":"staff"}]}]""",
                });
                await db.SaveChangesAsync(Ct);
            }
            var (id, _) = await NotificationAsync("to-gone");
            await DrainAsync<Notification, PlannerStage>("planner");
            _clock.Advance(TimeSpan.FromMinutes(5));
            await DrainAsync<Escalation, EscalationStage>("escalation");
            await DrainAsync<Notification, PlannerStage>("planner");

            await using (var db = _database.Context())
            {
                await db.Notifications.Where(n => n.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.State, NotificationState.Planning).SetProperty(n => n.NextAt, _clock.GetUtcNow()), Ct);
            }
            await DrainAsync<Notification, PlannerStage>("planner");

            await using var check = _database.Context();
            var named = await check.Recipients.AsNoTracking().Where(r => r.NotificationId == id && r.EscalationStep == 1)
                .Select(r => new { r.NamedAs, r.Outcome }).ToListAsync(Ct);
            Assert.Single(named, r => r.NamedAs == "route:gone" && r.Outcome == RecipientOutcome.Failed);
            Assert.Equal(named.Count, named.Select(r => r.NamedAs).Distinct().Count());
            Assert.Contains(named, r => r.NamedAs.StartsWith("subscriber:"));
        }
    }
}
