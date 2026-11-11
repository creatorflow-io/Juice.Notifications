using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>A clock tests move by hand, so a lease ends exactly when a test says.</summary>
    public sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>
    /// A stage that only marks its row done: one more workflow step, the worker's name in <c>Error</c>, nothing due after. A row completed twice
    /// would show two steps.
    /// </summary>
    public sealed class MarkStage : IStage<Notification>
    {
        public string Name => "mark";

        public Task ExecuteAsync(StageContext context, Notification row, CancellationToken ct)
        {
            row.WorkflowStep++;
            row.State = NotificationState.Planned;
            row.Error = row.ClaimedBy;
            row.UpdatedAt = context.Now;
            context.Done(row, null);
            return Task.CompletedTask;
        }
    }

    /// <summary>Faults for named workers: a pause before persisting, or a crash right after claiming.</summary>
    public sealed class ScriptedFaults : IStageFaults
    {
        public string? PauseWorker { get; set; }
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CrashWorker { get; set; }

        public Task AfterClaimAsync(string stage, string worker, Claim claim, CancellationToken ct)
            => worker == CrashWorker ? throw new InvalidOperationException("crashed after claiming") : Task.CompletedTask;

        public async Task BeforePersistAsync(string stage, string worker, Claim claim, CancellationToken ct)
        {
            if (worker == PauseWorker)
            {
                Paused.TrySetResult();
                await Resume.Task.WaitAsync(ct);
            }
        }
    }

    /// <summary>
    /// The claim store's concurrency suite (design §11, phase-1-plan M3), on the real PostgreSQL: rows complete exactly once under many workers,
    /// a stale worker's write affects nothing, and a crashed worker's row is due again after its lease. Phase 4 runs the same suite on SQL Server.
    /// </summary>
    public sealed class ClaimConcurrencyTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 20, Lease = TimeSpan.FromSeconds(30) };
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
            var app = Seed.Application("claims");
            var type = Seed.Type(app.Id, "claims.thing");
            db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = TwoTenantDatabase.TenantA, LinkedAt = Seed.Now });
            await db.SaveChangesAsync();
            _seed = (app.Id, type.Id);

            var services = new ServiceCollection().AddLogging();
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton<IStageFaults>(_faults);
            services.AddPostgresClaimStore();
            services.AddStage<Notification, MarkStage>(Options, runWorkers: false);
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

        private StageRunner<Notification, MarkStage> Runner => _services!.GetRequiredService<StageRunner<Notification, MarkStage>>();

        /// <summary>Due notifications, each with its request.</summary>
        private async Task<List<Guid>> DueAsync(int count)
        {
            await using var db = _database.Context();
            var ids = new List<Guid>();
            for (var i = 0; i < count; i++)
            {
                var request = Seed.Request(TwoTenantDatabase.TenantA, _seed.App, _seed.Type, Guid.NewGuid().ToString("N"));
                var notification = Seed.Notification(TwoTenantDatabase.TenantA, request.Id);
                notification.NextAt = _clock.GetUtcNow();
                db.AddRange(request, notification);
                ids.Add(notification.Id);
            }
            await db.SaveChangesAsync(Ct);
            return ids;
        }

        private async Task<List<Notification>> RowsAsync(List<Guid> ids)
        {
            await using var db = _database.Context();
            return await db.Notifications.AsNoTracking().Where(n => ids.Contains(n.Id)).ToListAsync(Ct);
        }

        [Fact]
        public async Task Eight_workers_complete_each_of_1000_due_rows_exactly_once()
        {
            LocalStack.RequireAvailable();
            var ids = await DueAsync(1000);

            var passes = await Task.WhenAll(Enumerable.Range(1, 8).Select(async w =>
            {
                var results = new List<PassResult>();
                while (true)
                {
                    var pass = await Runner.RunOnceAsync($"worker-{w}", Options, Ct);
                    results.Add(pass);
                    if (pass.Claimed == 0)
                    {
                        return results;
                    }
                }
            }));

            var all = passes.SelectMany(p => p).ToList();
            Assert.Equal(1000, all.Sum(p => p.Completed));
            Assert.Equal(0, all.Sum(p => p.LeaseLost + p.Failed));
            var rows = await RowsAsync(ids);
            Assert.Equal(1000, rows.Count);
            Assert.All(rows, r => Assert.Equal((1, null, null, NotificationState.Planned), (r.WorkflowStep, r.NextAt, r.ClaimedBy, r.State)));
            // Work spread over several workers.
            Assert.True(rows.Select(r => r.Error).Distinct().Count() > 1);
        }

        [Fact]
        public async Task A_worker_paused_past_its_lease_writes_nothing_and_another_completes_the_row()
        {
            LocalStack.RequireAvailable();
            var ids = await DueAsync(1);
            _faults.PauseWorker = "slow";

            var slow = Runner.RunOnceAsync("slow", Options, Ct);
            await _faults.Paused.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(0, (await Runner.RunOnceAsync("fast", Options, Ct)).Claimed); // the lease still holds
            _clock.Advance(Options.Lease + TimeSpan.FromSeconds(1));
            var fast = await Runner.RunOnceAsync("fast", Options, Ct);
            _faults.Resume.SetResult();
            var stale = await slow;

            Assert.Equal((1, 1), (fast.Claimed, fast.Completed));
            Assert.Equal((1, 0, 1), (stale.Claimed, stale.Completed, stale.LeaseLost));
            var row = Assert.Single(await RowsAsync(ids));
            Assert.Equal((1, "fast", (DateTimeOffset?)null), (row.WorkflowStep, row.Error, row.NextAt));
        }

        [Fact]
        public async Task A_worker_that_crashes_after_claiming_leaves_its_row_due_again_after_the_lease()
        {
            LocalStack.RequireAvailable();
            var ids = await DueAsync(3);
            _faults.CrashWorker = "doomed";

            var crashed = await Runner.RunOnceAsync("doomed", Options, Ct);
            Assert.Equal((3, 3), (crashed.Claimed, crashed.Failed));
            Assert.Equal(0, (await Runner.RunOnceAsync("rescuer", Options, Ct)).Claimed); // still leased
            _clock.Advance(Options.Lease + TimeSpan.FromSeconds(1));
            var rescued = await Runner.RunOnceAsync("rescuer", Options, Ct);

            Assert.Equal((3, 3), (rescued.Claimed, rescued.Completed));
            Assert.All(await RowsAsync(ids), r => Assert.Equal((1, "rescuer"), (r.WorkflowStep, r.Error)));
        }

        [Fact]
        public async Task Rows_not_yet_due_are_not_claimed()
        {
            LocalStack.RequireAvailable();
            var ids = await DueAsync(2);
            await using (var db = _database.Context())
            {
                var later = await db.Notifications.SingleAsync(n => n.Id == ids[1], Ct);
                later.NextAt = _clock.GetUtcNow().AddMinutes(5);
                await db.SaveChangesAsync(Ct);
            }

            Assert.Equal(1, (await Runner.RunOnceAsync("w", Options, Ct)).Completed);
            _clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(1, (await Runner.RunOnceAsync("w", Options, Ct)).Completed);
            Assert.All(await RowsAsync(ids), r => Assert.Equal(1, r.WorkflowStep));
        }
    }
}
