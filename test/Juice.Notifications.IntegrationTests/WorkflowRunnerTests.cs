using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The workflow runner in the planner stage (phase-3-plan M19, I27–I30), driven by hand on a manual clock: a delay sends after it and not
    /// before, a failed condition sends nothing, a throttle lets its limit through per window and the rest wait (none lost, however many
    /// workers), and a type whose workflow is just <c>send</c> takes the path it always did.
    /// </summary>
    public sealed class WorkflowRunnerTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 10, Lease = TimeSpan.FromSeconds(30) };
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private readonly ScratchDatabase _database = new();
        // On a window boundary, so a throttle's window starts with the test.
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        private readonly FakePeopleResolver _staff = new();
        private ServiceProvider? _services;
        private Guid _app;

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = await _database.MigratedAsync();
            var app = Seed.Application("flow");
            db.AddRange(app, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now });
            db.Resolvers.Add(new RecipientResolverConfig
            {
                TenantId = Tenant, Key = "staff", Kind = "fake", Handles = ["group", "user"], Settings = "{}", DefinitionHash = "h", UpdatedAt = Seed.Now,
            });
            await db.SaveChangesAsync();
            _app = app.Id;

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

        private StageRunner<Notification, PlannerStage> Runner => _services!.GetRequiredService<StageRunner<Notification, PlannerStage>>();

        /// <summary>A type of this test with the given workflow, as a declaration would store it.</summary>
        private async Task<NotificationType> TypeAsync(string workflow)
        {
            await using var db = _database.Context();
            var key = "flow.t" + Guid.NewGuid().ToString("N")[..10];
            var type = new NotificationType
            {
                ApplicationId = _app, Key = key, Version = 1, Name = key, Category = "test", DisplayGroup = DisplayGroups.General,
                Priority = NotificationPriority.Normal, Schema = "{}", SchemaHash = "h", Workflow = workflow, DeclarationHash = "d", CreatedAt = Seed.Now,
            };
            db.Types.Add(type);
            await db.SaveChangesAsync(Ct);
            return type;
        }

        /// <summary>A notification of the type for the people of a new group, due now; its id and the people's ids.</summary>
        private async Task<Guid> NotificationAsync(NotificationType type, int people = 2, Dictionary<string, string>? labels = null)
        {
            var group = "g" + Guid.NewGuid().ToString("N")[..8];
            _staff.Groups[group] = Enumerable.Range(0, people).Select(i => new ResolvedPerson(FakePeopleResolver.Source, $"{group}-{i}")).ToArray();
            await using var db = _database.Context();
            var request = Seed.Request(Tenant, _app, type.Id, Guid.NewGuid().ToString("N"));
            db.Entry(request).Property(r => r.TypeKey).CurrentValue = type.Key;
            db.Entry(request).Property(r => r.Recipients).CurrentValue = $$"""[{"kind":"group","id":"{{group}}","resolver":"staff"}]""";
            if (labels is not null)
            {
                db.Entry(request).Property(r => r.Labels).CurrentValue = labels;
            }
            var notification = Seed.Notification(Tenant, request.Id);
            notification.NextAt = _clock.GetUtcNow();
            db.AddRange(request, notification);
            await db.SaveChangesAsync(Ct);
            return notification.Id;
        }

        private async Task DrainAsync(StageRunner<Notification, PlannerStage>? runner = null)
        {
            PassResult pass;
            do
            {
                pass = await (runner ?? Runner).RunOnceAsync("planner", Options, Ct);
                Assert.Equal(0, pass.Failed);
            }
            while (pass.Claimed > 0);
        }

        private async Task<(Notification Row, List<NotificationRecipient> Recipients)> ReadAsync(Guid id)
        {
            await using var db = _database.Context();
            return (await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == id, Ct),
                await db.Recipients.AsNoTracking().Where(r => r.NotificationId == id).ToListAsync(Ct));
        }

        [Fact]
        public async Task A_send_only_workflow_plans_and_sends_in_one_step()
        {
            LocalStack.RequireAvailable();
            var id = await NotificationAsync(await TypeAsync("""[{"step":"send"}]"""));
            var pass = await Runner.RunOnceAsync("planner", Options, Ct);
            Assert.Equal(1, pass.Completed);
            var (row, recipients) = await ReadAsync(id);
            Assert.Equal((NotificationState.Planned, (DateTimeOffset?)null), (row.State, row.NextAt));
            Assert.All(recipients, r => Assert.Equal((RecipientOutcome.Planned, _clock.GetUtcNow()), (r.Outcome, r.NextAt!.Value)));
        }

        [Fact]
        public async Task A_delay_holds_the_people_until_it_has_passed()
        {
            LocalStack.RequireAvailable();
            var start = _clock.GetUtcNow();
            var id = await NotificationAsync(await TypeAsync("""[{"step":"delay","seconds":60},{"step":"send"}]"""));
            await DrainAsync();

            var (row, recipients) = await ReadAsync(id);
            Assert.Equal((NotificationState.Running, 0, start.AddSeconds(60)), (row.State, row.WorkflowStep, row.NextAt!.Value));
            Assert.Equal(2, recipients.Count);
            Assert.All(recipients, r => Assert.Equal((RecipientOutcome.Pending, (DateTimeOffset?)null), (r.Outcome, r.NextAt)));

            _clock.Advance(TimeSpan.FromSeconds(59));
            await DrainAsync();
            Assert.All((await ReadAsync(id)).Recipients, r => Assert.Equal(RecipientOutcome.Pending, r.Outcome));

            _clock.Advance(TimeSpan.FromSeconds(1));
            await DrainAsync();
            (row, recipients) = await ReadAsync(id);
            Assert.Equal((NotificationState.Planned, 2, (DateTimeOffset?)null, (DateTimeOffset?)null), (row.State, row.WorkflowStep, row.NextAt, row.StepEnteredAt));
            Assert.All(recipients, r => Assert.Equal((RecipientOutcome.Planned, start.AddSeconds(60)), (r.Outcome, r.NextAt!.Value)));
        }

        [Fact]
        public async Task A_condition_that_doesnt_hold_ends_the_notification_and_sends_nothing()
        {
            LocalStack.RequireAvailable();
            var type = await TypeAsync("""[{"step":"condition","when":{"labels":{"env":"prod"}}},{"step":"send"}]""");
            var skipped = await NotificationAsync(type, labels: new() { ["env"] = "dev" });
            var sent = await NotificationAsync(type, labels: new() { ["env"] = "prod" });
            await DrainAsync();

            var (row, recipients) = await ReadAsync(skipped);
            Assert.Equal(NotificationState.ConditionNotMet, row.State);
            Assert.All(recipients, r => Assert.Equal((RecipientOutcome.Cancelled, (DateTimeOffset?)null), (r.Outcome, r.NextAt)));
            (row, recipients) = await ReadAsync(sent);
            Assert.Equal(NotificationState.Planned, row.State);
            Assert.All(recipients, r => Assert.Equal(RecipientOutcome.Planned, r.Outcome));
        }

        /// <summary>Two workers at once, five notifications, two per minute: two go now, two the next minute, one the minute after.</summary>
        [Fact]
        public async Task A_throttle_lets_its_limit_through_per_window_and_the_rest_wait_whatever_the_workers()
        {
            LocalStack.RequireAvailable();
            var start = _clock.GetUtcNow();
            var type = await TypeAsync("""[{"step":"throttle","limit":2,"windowSeconds":60,"by":"labels.service"},{"step":"send"}]""");
            var ids = new List<Guid>();
            for (var i = 0; i < 5; i++)
            {
                ids.Add(await NotificationAsync(type, people: 1, labels: new() { ["service"] = "billing" }));
            }
            // Another key has its own window.
            var other = await NotificationAsync(type, people: 1, labels: new() { ["service"] = "shop" });

            // Two runners, each its own scope and connection, claiming in parallel.
            await using var second = _services!.CreateAsyncScope();
            await Task.WhenAll(DrainAsync(), DrainAsync(second.ServiceProvider.GetRequiredService<StageRunner<Notification, PlannerStage>>()));

            async Task<int> SentAsync() => (await Task.WhenAll(ids.Select(ReadAsync))).Count(n => n.Row.State == NotificationState.Planned);
            Assert.Equal(2, await SentAsync());
            Assert.Equal(NotificationState.Planned, (await ReadAsync(other)).Row.State);
            Assert.All((await Task.WhenAll(ids.Select(ReadAsync))).Where(n => n.Row.State != NotificationState.Planned),
                n => Assert.Equal((NotificationState.Running, start.AddMinutes(1)), (n.Row.State, n.Row.NextAt!.Value)));

            _clock.Advance(TimeSpan.FromMinutes(1));
            await DrainAsync();
            Assert.Equal(4, await SentAsync());
            _clock.Advance(TimeSpan.FromMinutes(1));
            await DrainAsync();
            Assert.Equal(5, await SentAsync());

            // Each notification counted once, in the window it went in; the first window's entries were cleared on the way (a window old).
            await using var db = _database.Context();
            var entries = await db.ThrottleEntries.AsNoTracking().Where(e => e.TypeKey == type.Key && e.Key == "billing").ToListAsync(Ct);
            Assert.Equal(entries.Count, entries.Select(e => e.NotificationId).Distinct().Count());
            Assert.Equal([(start.AddMinutes(1), 2), (start.AddMinutes(2), 1)],
                entries.GroupBy(e => e.WindowStart).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())));
        }

        /// <summary>A condition reads the correlation as it is when the step runs: resolved meanwhile, the reminder isn't sent.</summary>
        [Fact]
        public async Task A_condition_reads_the_correlations_state_when_its_step_runs()
        {
            LocalStack.RequireAvailable();
            var type = await TypeAsync("""[{"step":"delay","seconds":60},{"step":"condition","when":{"correlation":["open","acknowledged"]}},{"step":"send"}]""");
            var (open, resolved) = (await NotificationAsync(type), await NotificationAsync(type));
            await using (var db = _database.Context())
            {
                foreach (var (id, state) in new[] { (open, CorrelationState.Open), (resolved, CorrelationState.Open) })
                {
                    var correlation = new Correlation
                    {
                        TenantId = Tenant, ApplicationId = _app, Key = "c" + Guid.NewGuid().ToString("N"), State = state, CreatedAt = Seed.Now,
                        LastRequestAt = Seed.Now, UpdatedAt = Seed.Now,
                    };
                    db.Correlations.Add(correlation);
                    var request = await db.Requests.SingleAsync(r => db.Notifications.Any(n => n.Id == id && n.RequestId == r.Id), Ct);
                    db.Entry(request).Property(r => r.CorrelationId).CurrentValue = correlation.Id;
                }
                await db.SaveChangesAsync(Ct);
            }
            await DrainAsync();
            await using (var db = _database.Context())
            {
                // Resolved during the delay.
                var request = await db.Requests.AsNoTracking().SingleAsync(r => db.Notifications.Any(n => n.Id == resolved && n.RequestId == r.Id), Ct);
                await db.Correlations.Where(c => c.Id == request.CorrelationId).ExecuteUpdateAsync(c => c.SetProperty(x => x.State, CorrelationState.Resolved), Ct);
            }
            _clock.Advance(TimeSpan.FromSeconds(60));
            await DrainAsync();
            Assert.Equal(NotificationState.Planned, (await ReadAsync(open)).Row.State);
            var (row, recipients) = await ReadAsync(resolved);
            Assert.Equal(NotificationState.ConditionNotMet, row.State);
            Assert.All(recipients, r => Assert.Equal(RecipientOutcome.Cancelled, r.Outcome));
        }

        [Fact]
        public async Task A_request_that_expires_while_its_workflow_waits_sends_nothing()
        {
            LocalStack.RequireAvailable();
            var id = await NotificationAsync(await TypeAsync("""[{"step":"delay","seconds":600},{"step":"send"}]"""));
            await using (var db = _database.Context())
            {
                var request = await db.Requests.SingleAsync(r => db.Notifications.Any(n => n.Id == id && n.RequestId == r.Id), Ct);
                db.Entry(request).Property(r => r.ExpiresAt).CurrentValue = _clock.GetUtcNow().AddMinutes(5);
                await db.SaveChangesAsync(Ct);
            }
            await DrainAsync();
            _clock.Advance(TimeSpan.FromMinutes(10));
            await DrainAsync();
            var (row, recipients) = await ReadAsync(id);
            Assert.Equal(NotificationState.Expired, row.State);
            Assert.All(recipients, r => Assert.Equal(RecipientOutcome.Cancelled, r.Outcome));
        }
    }
}
