using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Proof 3 (design §16): planning is safe under retries and with several workers at once. The planner runs through the stage runner against
    /// the real PostgreSQL, with workers racing, a worker paused past its lease, and a worker crashing after its claim.
    /// </summary>
    public sealed class PlannerConcurrencyTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 10, Lease = TimeSpan.FromSeconds(30) };
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
        private readonly ScriptedFaults _faults = new();
        private ServiceProvider? _services;
        private (Guid App, Guid Type) _seed;

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = await _database.MigratedAsync();
            var app = Seed.Application("plan");
            var type = Seed.Type(app.Id, "plan.thing");
            db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now });
            foreach (var key in new[] { "a", "b", "c" })
            {
                db.Routes.Add(new Route { TenantId = Tenant, Key = "route-" + key, Match = """{ "type": "plan.*" }""", Targets = ["t"], DefinitionHash = "h", UpdatedAt = Seed.Now });
            }
            await db.SaveChangesAsync();
            _seed = (app.Id, type.Id);

            var services = new ServiceCollection().AddLogging();
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton<IStageFaults>(_faults);
            services.AddPostgresClaimStore();
            services.AddPlanning(Options, runWorkers: false);
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

        /// <summary>Notifications due for planning, each naming the routes and one address.</summary>
        private async Task<List<Guid>> PlanningAsync(int count)
        {
            await using var db = _database.Context();
            var ids = new List<Guid>();
            for (var i = 0; i < count; i++)
            {
                var request = Seed.Request(Tenant, _seed.App, _seed.Type, Guid.NewGuid().ToString("N"));
                request.Labels["n"] = i.ToString();
                db.Entry(request).Property(r => r.TypeKey).CurrentValue = "plan.thing";
                db.Entry(request).Property(r => r.Recipients).CurrentValue =
                    """[{"kind":"route"},{"kind":"address","channel":"email","address":"x@example.com"}]""";
                var notification = Seed.Notification(Tenant, request.Id);
                notification.NextAt = _clock.GetUtcNow();
                db.AddRange(request, notification);
                ids.Add(notification.Id);
            }
            await db.SaveChangesAsync(Ct);
            return ids;
        }

        private async Task AssertOneSetEachAsync(List<Guid> ids)
        {
            await using var db = _database.Context();
            var recipients = await db.Recipients.Where(r => ids.Contains(r.NotificationId)).ToListAsync(Ct);
            foreach (var id in ids)
            {
                var named = recipients.Where(r => r.NotificationId == id).Select(r => r.NamedAs).Order().ToList();
                Assert.Equal(["address:email:x@example.com", "route:route-a", "route:route-b", "route:route-c"], named);
            }
            Assert.All(await db.Notifications.Where(n => ids.Contains(n.Id)).ToListAsync(Ct),
                n => Assert.Equal((NotificationState.Planned, (DateTimeOffset?)null), (n.State, n.NextAt)));
            // Each recipient is due for the delivery planner.
            Assert.All(recipients, r => Assert.Equal((RecipientOutcome.Planned, _clock.GetUtcNow()), (r.Outcome, r.NextAt!.Value)));
        }

        [Fact]
        public async Task Planning_the_same_notification_concurrently_or_after_a_fault_yields_one_set_of_recipients()
        {
            LocalStack.RequireAvailable();

            // Several workers at once.
            var racing = await PlanningAsync(60);
            await Task.WhenAll(Enumerable.Range(1, 6).Select(async w =>
            {
                while ((await Runner.RunOnceAsync($"planner-{w}", Options, Ct)).Claimed > 0)
                {
                }
            }));
            await AssertOneSetEachAsync(racing);

            // A worker paused past its lease, after it resolved: another plans the row, and the paused worker's set is discarded.
            var paused = await PlanningAsync(1);
            _faults.PauseWorker = "slow";
            var slow = Runner.RunOnceAsync("slow", Options, Ct);
            await _faults.Paused.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            _clock.Advance(Options.Lease + TimeSpan.FromSeconds(1));
            Assert.Equal(1, (await Runner.RunOnceAsync("fast", Options, Ct)).Completed);
            _faults.Resume.SetResult();
            Assert.Equal(1, (await slow).LeaseLost);
            await AssertOneSetEachAsync(paused);

            // A worker that crashed after claiming: planned once its lease ends.
            var crashed = await PlanningAsync(2);
            _faults.PauseWorker = null;
            _faults.CrashWorker = "doomed";
            Assert.Equal(2, (await Runner.RunOnceAsync("doomed", Options, Ct)).Failed);
            _clock.Advance(Options.Lease + TimeSpan.FromSeconds(1));
            Assert.Equal(2, (await Runner.RunOnceAsync("rescuer", Options, Ct)).Completed);
            await AssertOneSetEachAsync(crashed);
        }

        [Fact]
        public async Task A_request_past_its_expiry_is_expired_with_no_recipients()
        {
            LocalStack.RequireAvailable();
            var ids = await PlanningAsync(1);
            await using (var db = _database.Context())
            {
                var notification = await db.Notifications.SingleAsync(n => n.Id == ids[0], Ct);
                var request = await db.Requests.SingleAsync(r => r.Id == notification.RequestId, Ct);
                db.Entry(request).Property(r => r.ExpiresAt).CurrentValue = _clock.GetUtcNow().AddSeconds(1);
                await db.SaveChangesAsync(Ct);
            }
            _clock.Advance(TimeSpan.FromSeconds(2));

            Assert.Equal(1, (await Runner.RunOnceAsync("w", Options, Ct)).Completed);

            await using var check = _database.Context();
            Assert.Equal(NotificationState.Expired, (await check.Notifications.SingleAsync(n => n.Id == ids[0], Ct)).State);
            Assert.False(await check.Recipients.AnyAsync(r => r.NotificationId == ids[0], Ct));
        }
    }
}
