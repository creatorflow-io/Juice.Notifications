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
    /// Correlation holds (phase-5-plan p5-d3, phase 5 proof point 3, the notifications half), driven by hand on a manual clock: a step that falls
    /// due under a hold is parked, not run and not dropped; the release runs it if the correlation is still open, and the next step is spaced
    /// from the release; a hold stands through resolve and reopen; and the fence (m44-architecture §6.3) makes a step that raced a hold or a
    /// release lose its save rather than run, or park, against it.
    /// </summary>
    public sealed class CorrelationHoldTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 10, Lease = TimeSpan.FromSeconds(30) };
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private static readonly Guid OtherTenant = TwoTenantDatabase.TenantB;
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
        private readonly FakePeopleResolver _staff = new();
        private readonly HookFaults _faults = new();
        private ServiceProvider? _services;
        private (Guid App, Guid OtherApp, NotificationType Type) _seed;

        /// <summary>Runs an action once, between a stage step and its save: a hold or release that commits while the step is in flight.</summary>
        private sealed class HookFaults : IStageFaults
        {
            public async Task BeforePersistAsync(string stage, string worker, Claim claim, CancellationToken ct)
            {
                if (stage == EscalationStage.StageName && Interlocked.Exchange(ref _once, null) is { } hook)
                {
                    await hook();
                }
            }

            private Func<Task>? _once;

            public void Once(Func<Task> hook) => _once = hook;
        }

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = await _database.MigratedAsync();
            var app = Seed.Application("hold");
            var other = Seed.Application("hold-other");
            var type = new NotificationType
            {
                ApplicationId = app.Id, Key = "hold.incident", Version = 1, Name = "Incident", Category = "incidents", DisplayGroup = DisplayGroups.General,
                Priority = NotificationPriority.High, Schema = "{}", SchemaHash = "h",
                Workflow = """[{"step":"send"},{"policyFrom":"labels.policy","step":"escalate"}]""", DeclarationHash = "d", CreatedAt = Seed.Now,
            };
            db.AddRange(app, other, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now },
                new ApplicationTenant { ApplicationId = app.Id, TenantId = OtherTenant, LinkedAt = Seed.Now },
                new ApplicationTenant { ApplicationId = other.Id, TenantId = Tenant, LinkedAt = Seed.Now });
            db.Resolvers.Add(new RecipientResolverConfig
            {
                TenantId = Tenant, Key = "staff", Kind = "fake", Handles = ["group", "user"], Settings = "{}", DefinitionHash = "h", UpdatedAt = Seed.Now,
            });
            // Two steps: the lead 5 minutes after the first send, the boss 10 minutes after the lead.
            const string steps = """
                [{"afterMinutes":5,"recipients":[{"id":"lead","kind":"group","resolver":"staff"}]},
                 {"afterMinutes":10,"recipients":[{"id":"boss","kind":"group","resolver":"staff"}]}]
                """;
            db.EscalationPolicies.AddRange(
                new EscalationPolicy { TenantId = Tenant, Key = "stops", Steps = steps, StopOnAcknowledge = true, DefinitionHash = "h", UpdatedAt = Seed.Now },
                new EscalationPolicy { TenantId = Tenant, Key = "keeps", Steps = steps, StopOnAcknowledge = false, DefinitionHash = "h", UpdatedAt = Seed.Now });
            await db.SaveChangesAsync();
            _seed = (app.Id, other.Id, type);
            _staff.Groups["crew"] = [new ResolvedPerson(FakePeopleResolver.Source, "ann"), new ResolvedPerson(FakePeopleResolver.Source, "ben")];
            _staff.Groups["lead"] = [new ResolvedPerson(FakePeopleResolver.Source, "lee")];
            _staff.Groups["boss"] = [new ResolvedPerson(FakePeopleResolver.Source, "bob")];

            var services = new ServiceCollection().AddLogging(l => l.AddProvider(new ErrorLogProvider()));
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton<IStageFaults>(_faults);
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

        private async Task<PassResult> PassAsync<T, TStage>(string stage) where T : Claimable, ITenantOwned where TStage : class, IStage<T>
            => await _services!.GetRequiredService<StageRunner<T, TStage>>().RunOnceAsync(stage, Options, Ct);

        private async Task DrainAsync<T, TStage>(string stage) where T : Claimable, ITenantOwned where TStage : class, IStage<T>
        {
            PassResult pass;
            do
            {
                pass = await PassAsync<T, TStage>(stage);
                Assert.Equal(0, pass.Failed);
            }
            while (pass.Claimed > 0);
        }

        /// <summary>Planner, escalation and planner again: whatever is due now runs to its end.</summary>
        private async Task RunAsync()
        {
            await DrainAsync<Notification, PlannerStage>("planner");
            await DrainAsync<Escalation, EscalationStage>("escalation");
            await DrainAsync<Notification, PlannerStage>("planner");
        }

        /// <summary>A notification to the crew on the correlation <paramref name="key"/> (a new one, or the one there, reopened as a request does).</summary>
        private async Task<(Guid Notification, string Key)> NotificationAsync(string policy, string? key = null)
        {
            await using var db = _database.Context();
            key ??= "incident:" + Guid.NewGuid().ToString("N");
            var correlation = await CorrelationBook.ForRequestAsync(db, Tenant, _seed.App, key, _clock.GetUtcNow(), Ct);
            var request = Seed.Request(Tenant, _seed.App, _seed.Type.Id, Guid.NewGuid().ToString("N"));
            db.Entry(request).Property(r => r.TypeKey).CurrentValue = _seed.Type.Key;
            db.Entry(request).Property(r => r.CorrelationKey).CurrentValue = key;
            db.Entry(request).Property(r => r.CorrelationId).CurrentValue = correlation.Id;
            db.Entry(request).Property(r => r.Labels).CurrentValue = new Dictionary<string, string> { ["policy"] = policy };
            db.Entry(request).Property(r => r.Recipients).CurrentValue = """[{"kind":"group","id":"crew","resolver":"staff"}]""";
            var notification = Seed.Notification(Tenant, request.Id);
            notification.NextAt = _clock.GetUtcNow();
            db.AddRange(request, notification);
            await db.SaveChangesAsync(Ct);
            return (notification.Id, key);
        }

        private CorrelationLifecycle Lifecycle(NotificationsDbContext db)
            => new(db, _clock, new NoInboxEvents(), _services!.GetRequiredService<Events.ProducerEvents>(), NullLogger<CorrelationLifecycle>.Instance);

        private async Task<Outcome<CorrelationHoldResult>> HoldAsync(string key, string? reason = "silence:7", Guid? tenant = null, Guid? app = null)
        {
            await using var db = _database.Context();
            return await Lifecycle(db).HoldAsync(tenant ?? Tenant, app ?? _seed.App, key, reason, "obs", Ct);
        }

        private async Task<CorrelationHoldResult> HeldAsync(string key, string reason = "silence:7")
        {
            var outcome = await HoldAsync(key, reason);
            Assert.Equal(OutcomeKind.Ok, outcome.Kind);
            return outcome.Value!;
        }

        private async Task<CorrelationHoldResult> ReleasedAsync(string key)
        {
            await using var db = _database.Context();
            var outcome = await Lifecycle(db).ReleaseAsync(Tenant, _seed.App, key, "obs", Ct);
            Assert.Equal(OutcomeKind.Ok, outcome.Kind);
            return outcome.Value!;
        }

        private async Task ResolveAsync(string key)
        {
            await using var db = _database.Context();
            Assert.Equal(OutcomeKind.Ok, (await Lifecycle(db).ResolveAsync(Tenant, _seed.App, key, "fixed", "eve", Ct)).Kind);
        }

        private async Task AcknowledgeAsync(string key)
        {
            await using var db = _database.Context();
            Assert.Equal(OutcomeKind.Ok, (await Lifecycle(db).AcknowledgeAsync(Tenant, _seed.App, key, "eve", Ct)).Kind);
        }

        private async Task<Escalation> EscalationAsync(Guid notification)
        {
            await using var db = _database.Context();
            return await db.Escalations.AsNoTracking().SingleAsync(e => e.NotificationId == notification, Ct);
        }

        private async Task<Correlation> CorrelationAsync(string key, Guid? tenant = null, Guid? app = null)
        {
            await using var db = _database.Context();
            return await db.Correlations.AsNoTracking().SingleAsync(c => c.TenantId == (tenant ?? Tenant) && c.ApplicationId == (app ?? _seed.App) && c.Key == key, Ct);
        }

        /// <summary>Who the notification reached, at which escalation step (0: the request's own plan).</summary>
        private async Task<List<(string Person, int Step)>> PeopleAsync(Guid notification)
        {
            await using var db = _database.Context();
            return (await (from r in db.Recipients
                           join s in db.Subscribers on new { r.TenantId, Id = r.SubscriberId!.Value } equals new { s.TenantId, s.Id }
                           where r.NotificationId == notification
                           select new { s.ExternalId, r.EscalationStep }).AsNoTracking().ToListAsync(Ct))
                .Select(x => (x.ExternalId, x.EscalationStep)).OrderBy(x => x.EscalationStep).ThenBy(x => x.ExternalId).ToList();
        }

        /// <summary>Waits until a statement of this database waits on a row lock: the second transaction has read and is blocked on its update.</summary>
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
            Assert.Fail("The second transaction never waited on the first.");
        }

        private static readonly List<(string, int)> Crew = [("ann", 0), ("ben", 0)];

        /// <summary>Holds, waits through the step's time (parked), then releases while open: the step runs once and the next is spaced from it.</summary>
        [Fact]
        public async Task A_step_due_under_a_hold_waits_for_the_release_then_runs_once_and_the_next_step_is_spaced_from_it()
        {
            LocalStack.RequireAvailable();
            var (id, key) = await NotificationAsync("keeps");
            await RunAsync();
            Assert.Equal(Crew, await PeopleAsync(id));

            var hold = await HeldAsync(key);
            Assert.True(hold.Changed);
            Assert.Equal((_clock.GetUtcNow(), "silence:7", "obs"), (hold.View.HeldAt!.Value, hold.View.HoldReason, hold.View.HeldBy));
            _clock.Advance(TimeSpan.FromMinutes(5));
            await RunAsync();
            var parked = await EscalationAsync(id);
            Assert.Equal((EscalationState.Running, 0, (DateTimeOffset?)null, _clock.GetUtcNow()), (parked.State, parked.NextStep, parked.NextAt, parked.HeldAt!.Value));
            Assert.Equal(Crew, await PeopleAsync(id));

            // However long the hold, nothing more is claimed.
            _clock.Advance(TimeSpan.FromHours(2));
            Assert.Equal(0, (await PassAsync<Escalation, EscalationStage>("escalation")).Claimed);

            var released = await ReleasedAsync(key);
            Assert.Equal((true, 1), (released.Changed, released.Resumed));
            Assert.Null(released.View.HeldAt);
            var releasedAt = _clock.GetUtcNow();
            Assert.Equal((releasedAt, (DateTimeOffset?)null), ((await EscalationAsync(id)).NextAt!.Value, (await EscalationAsync(id)).HeldAt));
            await RunAsync();
            Assert.Equal([.. Crew, ("lee", 1)], await PeopleAsync(id));
            var next = await EscalationAsync(id);
            Assert.Equal((1, releasedAt.AddMinutes(10)), (next.NextStep, next.NextAt!.Value));

            // Spaced, not bunched: the boss waits the policy's 10 minutes from the release.
            _clock.Advance(TimeSpan.FromMinutes(9));
            await RunAsync();
            Assert.Equal([.. Crew, ("lee", 1)], await PeopleAsync(id));
            _clock.Advance(TimeSpan.FromMinutes(1));
            await RunAsync();
            Assert.Equal([.. Crew, ("lee", 1), ("bob", 2)], await PeopleAsync(id));
            Assert.Equal(EscalationState.Exhausted, (await EscalationAsync(id)).State);

            // A hold after the last step has nothing to park, and its release nothing to resume.
            await HeldAsync(key);
            Assert.Equal(0, (await ReleasedAsync(key)).Resumed);
        }

        [Fact]
        public async Task Hold_resolve_release_plans_nothing_and_the_escalation_stops_as_resolved()
        {
            LocalStack.RequireAvailable();
            var (id, key) = await NotificationAsync("keeps");
            await RunAsync();
            await HeldAsync(key);
            _clock.Advance(TimeSpan.FromMinutes(5));
            await RunAsync();
            Assert.NotNull((await EscalationAsync(id)).HeldAt);

            await ResolveAsync(key);
            var stopped = await EscalationAsync(id);
            Assert.Equal((EscalationState.Stopped, "resolved", (DateTimeOffset?)null), (stopped.State, stopped.StopReason, stopped.HeldAt));
            // The hold stays recorded through the resolve (m44-architecture §11 Q1).
            Assert.NotNull((await CorrelationAsync(key)).HeldAt);

            var released = await ReleasedAsync(key);
            Assert.Equal((true, 0), (released.Changed, released.Resumed));
            _clock.Advance(TimeSpan.FromMinutes(30));
            await RunAsync();
            Assert.Equal(Crew, await PeopleAsync(id));
        }

        [Theory]
        [InlineData("stops")]
        [InlineData("keeps")]
        public async Task An_acknowledgement_under_a_hold_stops_what_stops_on_it_and_the_release_runs_the_rest(string policy)
        {
            LocalStack.RequireAvailable();
            var (id, key) = await NotificationAsync(policy);
            await RunAsync();
            await HeldAsync(key);
            _clock.Advance(TimeSpan.FromMinutes(5));
            await RunAsync();
            await AcknowledgeAsync(key);

            var released = await ReleasedAsync(key);
            await RunAsync();
            if (policy == "stops")
            {
                Assert.Equal(0, released.Resumed);
                var stopped = await EscalationAsync(id);
                Assert.Equal((EscalationState.Stopped, "acknowledged", (DateTimeOffset?)null), (stopped.State, stopped.StopReason, stopped.HeldAt));
                Assert.Equal(Crew, await PeopleAsync(id));
            }
            else
            {
                Assert.Equal(1, released.Resumed);
                Assert.Equal([.. Crew, ("lee", 1)], await PeopleAsync(id));
            }
        }

        [Fact]
        public async Task A_hold_released_before_the_step_is_due_leaves_its_time_as_it_was()
        {
            LocalStack.RequireAvailable();
            var (id, key) = await NotificationAsync("keeps");
            var start = _clock.GetUtcNow();
            await RunAsync();
            _clock.Advance(TimeSpan.FromMinutes(1));
            await HeldAsync(key);
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(0, (await ReleasedAsync(key)).Resumed);
            Assert.Equal(start.AddMinutes(5), (await EscalationAsync(id)).NextAt!.Value);

            _clock.Advance(TimeSpan.FromMinutes(2));
            await RunAsync();
            Assert.Equal(Crew, await PeopleAsync(id));
            _clock.Advance(TimeSpan.FromMinutes(1));
            await RunAsync();
            Assert.Equal([.. Crew, ("lee", 1)], await PeopleAsync(id));
        }

        [Fact]
        public async Task Hold_and_release_are_idempotent_by_state()
        {
            LocalStack.RequireAvailable();
            var (_, key) = await NotificationAsync("keeps");
            await RunAsync();
            var first = await HeldAsync(key);
            var version = (await CorrelationAsync(key)).Version;
            _clock.Advance(TimeSpan.FromMinutes(1));

            var again = await HeldAsync(key);
            Assert.False(again.Changed);
            // Nothing written: a request at the same moment isn't made to retry for nothing.
            Assert.Equal(version, (await CorrelationAsync(key)).Version);

            var replaced = await HeldAsync(key, "silence:8");
            Assert.True(replaced.Changed);
            Assert.Equal((first.View.HeldAt, "silence:8", "obs"), (replaced.View.HeldAt, replaced.View.HoldReason, replaced.View.HeldBy));

            Assert.True((await ReleasedAsync(key)).Changed);
            var twice = await ReleasedAsync(key);
            Assert.Equal((false, 0), (twice.Changed, twice.Resumed));
        }

        /// <summary>The default for m44-architecture §11 Q1: a hold stands until released, through a resolve and the request that reopens it.</summary>
        [Fact]
        public async Task A_hold_survives_a_resolve_and_a_reopen_and_parks_the_reopened_escalation()
        {
            LocalStack.RequireAvailable();
            var (_, key) = await NotificationAsync("keeps");
            await RunAsync();
            await HeldAsync(key);
            await ResolveAsync(key);

            var (reopened, _) = await NotificationAsync("keeps", key);
            Assert.Equal(CorrelationState.Open, (await CorrelationAsync(key)).State);
            await RunAsync();
            _clock.Advance(TimeSpan.FromMinutes(5));
            await RunAsync();
            Assert.NotNull((await EscalationAsync(reopened)).HeldAt);
            Assert.Equal(Crew, await PeopleAsync(reopened));

            Assert.Equal(1, (await ReleasedAsync(key)).Resumed);
            await RunAsync();
            Assert.Equal([.. Crew, ("lee", 1)], await PeopleAsync(reopened));
        }

        [Fact]
        public async Task A_policy_deleted_while_parked_stops_the_escalation_at_the_release()
        {
            LocalStack.RequireAvailable();
            await using (var db = _database.Context())
            {
                db.EscalationPolicies.Add(new EscalationPolicy
                {
                    TenantId = Tenant, Key = "gone", Steps = """[{"afterMinutes":5,"recipients":[{"id":"lead","kind":"group","resolver":"staff"}]}]""",
                    DefinitionHash = "h", UpdatedAt = Seed.Now,
                });
                await db.SaveChangesAsync(Ct);
            }
            var (id, key) = await NotificationAsync("gone");
            await RunAsync();
            await HeldAsync(key);
            _clock.Advance(TimeSpan.FromMinutes(5));
            await RunAsync();
            await using (var db = _database.Context())
            {
                await db.EscalationPolicies.Where(p => p.TenantId == Tenant && p.Key == "gone").ExecuteDeleteAsync(Ct);
            }
            Assert.Equal(1, (await ReleasedAsync(key)).Resumed);
            await RunAsync();
            var stopped = await EscalationAsync(id);
            Assert.Equal((EscalationState.Stopped, "policy-deleted"), (stopped.State, stopped.StopReason));
            Assert.Equal(Crew, await PeopleAsync(id));
        }

        [Fact]
        public async Task Two_parked_escalations_of_one_correlation_are_both_resumed()
        {
            LocalStack.RequireAvailable();
            var (first, key) = await NotificationAsync("keeps");
            var (second, _) = await NotificationAsync("keeps", key);
            await RunAsync();
            await HeldAsync(key);
            _clock.Advance(TimeSpan.FromMinutes(5));
            await RunAsync();
            Assert.Equal(2, (await ReleasedAsync(key)).Resumed);
            await RunAsync();
            Assert.Equal([.. Crew, ("lee", 1)], await PeopleAsync(first));
            Assert.Equal([.. Crew, ("lee", 1)], await PeopleAsync(second));
        }

        [Fact]
        public async Task A_hold_is_the_applications_and_tenants_own_and_an_unknown_key_is_not_found()
        {
            LocalStack.RequireAvailable();
            var key = "shared:" + Guid.NewGuid().ToString("N");
            await using (var db = _database.Context())
            {
                await CorrelationBook.ForRequestAsync(db, Tenant, _seed.App, key, _clock.GetUtcNow(), Ct);
                await CorrelationBook.ForRequestAsync(db, Tenant, _seed.OtherApp, key, _clock.GetUtcNow(), Ct);
                await CorrelationBook.ForRequestAsync(db, OtherTenant, _seed.App, key, _clock.GetUtcNow(), Ct);
                await db.SaveChangesAsync(Ct);
            }
            await HeldAsync(key);
            Assert.NotNull((await CorrelationAsync(key)).HeldAt);
            Assert.Null((await CorrelationAsync(key, app: _seed.OtherApp)).HeldAt);
            Assert.Null((await CorrelationAsync(key, tenant: OtherTenant)).HeldAt);

            Assert.Equal(OutcomeKind.NotFound, (await HoldAsync("nobody:" + Guid.NewGuid().ToString("N"))).Kind);
            await using var other = _database.Context();
            Assert.Equal(OutcomeKind.NotFound, (await Lifecycle(other).ReleaseAsync(Tenant, _seed.App, "nobody", null, Ct)).Kind);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" silence:7")]
        [InlineData("silence\n7")]
        public async Task A_hold_without_a_good_reason_is_refused(string? reason)
        {
            LocalStack.RequireAvailable();
            var (_, key) = await NotificationAsync("keeps");
            var outcome = await HoldAsync(key, reason);
            Assert.Equal((OutcomeKind.Invalid, ErrorCodes.InvalidRequest), (outcome.Kind, outcome.Code));
            Assert.Equal("/reason", Assert.Single(outcome.Errors!).Pointer);
            Assert.Null((await CorrelationAsync(key)).HeldAt);
        }

        /// <summary>Race: the stage read the correlation unheld and handed the step over; the hold commits before its save, which is lost.</summary>
        [Fact]
        public async Task A_hold_committed_during_a_step_fences_it_and_the_next_pass_parks()
        {
            LocalStack.RequireAvailable();
            var (id, key) = await NotificationAsync("keeps");
            await RunAsync();
            _clock.Advance(TimeSpan.FromMinutes(5));
            _faults.Once(() => HeldAsync(key));
            var pass = await PassAsync<Escalation, EscalationStage>("escalation");
            Assert.Equal((1, 1), (pass.Claimed, pass.LeaseLost));

            await RunAsync();
            var parked = await EscalationAsync(id);
            Assert.Equal((0, (DateTimeOffset?)null), (parked.NextStep, parked.NextAt));
            Assert.NotNull(parked.HeldAt);
            Assert.Equal(Crew, await PeopleAsync(id));
            await using var db = _database.Context();
            Assert.Equal(NotificationState.Planned, (await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == id, Ct)).State);
        }

        /// <summary>Race: the stage read the correlation held and is parking; the release commits before its save, which is lost (not parked for ever).</summary>
        [Fact]
        public async Task A_release_committed_during_a_park_fences_it_and_the_next_pass_runs_the_step_once()
        {
            LocalStack.RequireAvailable();
            var (id, key) = await NotificationAsync("keeps");
            await RunAsync();
            await HeldAsync(key);
            _clock.Advance(TimeSpan.FromMinutes(5));
            _faults.Once(async () => Assert.Equal(0, (await ReleasedAsync(key)).Resumed)); // not parked yet: nothing to resume, the fence still moves
            var pass = await PassAsync<Escalation, EscalationStage>("escalation");
            Assert.Equal((1, 1), (pass.Claimed, pass.LeaseLost));

            await RunAsync();
            Assert.Equal([.. Crew, ("lee", 1)], await PeopleAsync(id));
            var next = await EscalationAsync(id);
            Assert.Equal((1, (DateTimeOffset?)null), (next.NextStep, next.HeldAt));
        }

        /// <summary>Release and resolve at once, in two transactions: the correlation's row version lets one commit; the other is refused.</summary>
        [Fact]
        public async Task A_release_and_a_resolve_at_once_commit_one_and_refuse_the_other()
        {
            LocalStack.RequireAvailable();
            var (id, key) = await NotificationAsync("keeps");
            await RunAsync();
            await HeldAsync(key);
            _clock.Advance(TimeSpan.FromMinutes(5));
            await RunAsync();

            await using var releasing = _database.Context();
            await using var resolving = _database.Context();
            await using var first = await releasing.Database.BeginTransactionAsync(Ct);
            Assert.Equal(1, (await Lifecycle(releasing).ReleaseAsync(Tenant, _seed.App, key, null, Ct)).Value!.Resumed);
            // The resolve waits on the release's row lock, then finds the row changed.
            var resolve = Lifecycle(resolving).ResolveAsync(Tenant, _seed.App, key, "fixed", "eve", Ct);
            await LockWaitAsync();
            await first.CommitAsync(Ct);
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => resolve);
            Assert.Equal(ConflictKind.RowGone, DbConflicts.Classify(refused)?.Kind);

            // The release won: the correlation is open and unheld, and the resumed step runs.
            var correlation = await CorrelationAsync(key);
            Assert.Equal((CorrelationState.Open, (DateTimeOffset?)null), (correlation.State, correlation.HeldAt));
            await RunAsync();
            Assert.Equal([.. Crew, ("lee", 1)], await PeopleAsync(id));
        }

        /// <summary>Resolve first, then the release: nothing is sent.</summary>
        [Fact]
        public async Task A_resolve_that_commits_before_a_release_leaves_nothing_to_send()
        {
            LocalStack.RequireAvailable();
            var (id, key) = await NotificationAsync("keeps");
            await RunAsync();
            await HeldAsync(key);
            _clock.Advance(TimeSpan.FromMinutes(5));
            await RunAsync();

            await using var resolving = _database.Context();
            await using var releasing = _database.Context();
            await using var first = await resolving.Database.BeginTransactionAsync(Ct);
            Assert.Equal(OutcomeKind.Ok, (await Lifecycle(resolving).ResolveAsync(Tenant, _seed.App, key, "fixed", "eve", Ct)).Kind);
            var release = Lifecycle(releasing).ReleaseAsync(Tenant, _seed.App, key, null, Ct);
            await LockWaitAsync();
            await first.CommitAsync(Ct);
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => release);
            Assert.Equal(ConflictKind.RowGone, DbConflicts.Classify(refused)?.Kind);

            Assert.Equal(0, (await ReleasedAsync(key)).Resumed); // asked again: released, nothing to resume
            await RunAsync();
            Assert.Equal(Crew, await PeopleAsync(id));
        }

        [Fact]
        public async Task Two_releases_at_once_plan_the_step_once()
        {
            LocalStack.RequireAvailable();
            var (id, key) = await NotificationAsync("keeps");
            await RunAsync();
            await HeldAsync(key);
            _clock.Advance(TimeSpan.FromMinutes(5));
            await RunAsync();

            await using var one = _database.Context();
            await using var two = _database.Context();
            await using var first = await one.Database.BeginTransactionAsync(Ct);
            Assert.Equal(1, (await Lifecycle(one).ReleaseAsync(Tenant, _seed.App, key, null, Ct)).Value!.Resumed);
            var second = Lifecycle(two).ReleaseAsync(Tenant, _seed.App, key, null, Ct);
            await LockWaitAsync();
            await first.CommitAsync(Ct);
            try
            {
                var other = await second;
                Assert.Equal((false, 0), (other.Value!.Changed, other.Value.Resumed));
            }
            catch (Exception ex) when (DbConflicts.Classify(ex) is { Kind: ConflictKind.RowGone })
            {
                // Refused as concurrent_change: also correct.
            }
            await RunAsync();
            Assert.Equal([.. Crew, ("lee", 1)], await PeopleAsync(id));
        }
    }
}
