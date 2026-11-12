using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>A clock tests move by hand, so a row is due exactly when a test says. (Leases run on the database's clock.)</summary>
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

        public void GiveUp(StageContext context, Notification row, string error) => context.Done(row, null);
    }

    /// <summary>Writes a child row with a unique key (as the planner writes recipients), then finishes its row.</summary>
    public sealed class ChildStage : IStage<Notification>
    {
        public string Name => "child";

        public Task ExecuteAsync(StageContext context, Notification row, CancellationToken ct)
        {
            context.Db.Recipients.Add(Child(row, "child"));
            row.WorkflowStep++;
            row.Error = row.ClaimedBy;
            context.Done(row, null);
            return Task.CompletedTask;
        }

        public void GiveUp(StageContext context, Notification row, string error) => context.Done(row, null);

        internal static NotificationRecipient Child(Notification row, string namedAs) => new()
        {
            TenantId = row.TenantId, NotificationId = row.Id, Kind = RecipientKind.Route, Resolver = "test", NamedAs = namedAs, Outcome = RecipientOutcome.Planned,
        };
    }

    /// <summary>Writes a child row named after the worker and forgets to finish its row: the runner must still fence the write.</summary>
    public sealed class ForgetfulStage : IStage<Notification>
    {
        public string Name => "forgetful";

        public Task ExecuteAsync(StageContext context, Notification row, CancellationToken ct)
        {
            context.Db.Recipients.Add(ChildStage.Child(row, row.ClaimedBy!));
            return Task.CompletedTask;
        }

        public void GiveUp(StageContext context, Notification row, string error) => context.Done(row, null);
    }

    /// <summary>Always throws, as a step on a poison row does; gives up by marking the notification failed.</summary>
    public sealed class FailingStage : IStage<Notification>
    {
        public string Name => "failing";

        public Task ExecuteAsync(StageContext context, Notification row, CancellationToken ct) => throw new InvalidOperationException("poison row");

        public void GiveUp(StageContext context, Notification row, string error)
        {
            row.State = NotificationState.Failed;
            row.Error = error;
            context.Done(row, null);
        }
    }

    /// <summary>Faults for named workers: a pause before persisting, or something to do right after a claim.</summary>
    public sealed class ScriptedFaults : IStageFaults
    {
        public string? PauseWorker { get; set; }
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<string, Claim>? OnClaim { get; set; }

        public Task AfterClaimAsync(string stage, string worker, Claim claim, CancellationToken ct)
        {
            OnClaim?.Invoke(worker, claim);
            return Task.CompletedTask;
        }

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
    /// a stale worker writes nothing (its child rows included), a crashed worker's row is due again after its lease, a poison row is given up,
    /// and a stopping worker hands back what it didn't start. Phase 4 runs the same suite on SQL Server.
    /// </summary>
    public sealed class ClaimConcurrencyTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 20, Lease = TimeSpan.FromSeconds(30) };
        // Leases run on the database's clock, so a test that outlives one waits for real: a short lease keeps that short.
        private static readonly StageOptions ShortLease = new() { Batch = 20, Lease = TimeSpan.FromSeconds(1) };
        private static readonly TimeSpan PastShortLease = TimeSpan.FromMilliseconds(1500);
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
            services.AddStage<Notification, ChildStage>(Options, runWorkers: false);
            services.AddStage<Notification, ForgetfulStage>(Options, runWorkers: false);
            services.AddStage<Notification, FailingStage>(Options, runWorkers: false);
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

        private StageRunner<Notification, TStage> Runner<TStage>() where TStage : IStage<Notification>
            => _services!.GetRequiredService<StageRunner<Notification, TStage>>();

        private StageRunner<Notification, MarkStage> Runner() => Runner<MarkStage>();

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

        private async Task<List<string>> ChildrenAsync(Guid notification)
        {
            await using var db = _database.Context();
            return await db.Recipients.AsNoTracking().Where(r => r.NotificationId == notification).Select(r => r.NamedAs).ToListAsync(Ct);
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
                    var pass = await Runner().RunOnceAsync($"worker-{w}", Options, Ct);
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

            var slow = Runner().RunOnceAsync("slow", ShortLease, Ct);
            await _faults.Paused.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(0, (await Runner().RunOnceAsync("fast", ShortLease, Ct)).Claimed); // the lease still holds
            await Task.Delay(PastShortLease, Ct);
            var fast = await Runner().RunOnceAsync("fast", ShortLease, Ct);
            _faults.Resume.SetResult();
            var stale = await slow;

            Assert.Equal((1, 1), (fast.Claimed, fast.Completed));
            Assert.Equal((1, 0, 1), (stale.Claimed, stale.Completed, stale.LeaseLost));
            var row = Assert.Single(await RowsAsync(ids));
            Assert.Equal((1, "fast", (DateTimeOffset?)null), (row.WorkflowStep, row.Error, row.NextAt));
        }

        /// <summary>The stale worker's child row hits the unique key the newer worker's row already took: lease_lost, and one child.</summary>
        [Fact]
        public async Task A_stale_worker_that_trips_a_unique_key_loses_its_lease_and_writes_no_child()
        {
            LocalStack.RequireAvailable();
            var ids = await DueAsync(1);
            _faults.PauseWorker = "slow";

            var slow = Runner<ChildStage>().RunOnceAsync("slow", ShortLease, Ct);
            await _faults.Paused.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await Task.Delay(PastShortLease, Ct);
            var fast = await Runner<ChildStage>().RunOnceAsync("fast", ShortLease, Ct);
            _faults.Resume.SetResult();
            var stale = await slow;

            Assert.Equal(1, fast.Completed);
            Assert.Equal((0, 1, 0), (stale.Completed, stale.LeaseLost, stale.Failed));
            Assert.Equal(["child"], await ChildrenAsync(ids[0]));
            Assert.Equal((1, "fast"), (Assert.Single(await RowsAsync(ids)).WorkflowStep, Assert.Single(await RowsAsync(ids)).Error));
        }

        /// <summary>A step that writes only other rows and never finishes its own is still fenced: the stale worker's child row isn't written.</summary>
        [Fact]
        public async Task A_stale_worker_whose_step_forgets_its_row_still_writes_nothing()
        {
            LocalStack.RequireAvailable();
            var ids = await DueAsync(1);
            _faults.PauseWorker = "slow";

            var slow = Runner<ForgetfulStage>().RunOnceAsync("slow", ShortLease, Ct);
            await _faults.Paused.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await Task.Delay(PastShortLease, Ct);
            var fast = await Runner<ForgetfulStage>().RunOnceAsync("fast", ShortLease, Ct);
            _faults.Resume.SetResult();
            var stale = await slow;

            Assert.Equal(1, fast.Completed);
            Assert.Equal(1, stale.LeaseLost);
            Assert.Equal(["fast"], await ChildrenAsync(ids[0]));
        }

        /// <summary>The worker dies right after claiming (nothing hands its rows back): they are due again once the lease ends.</summary>
        [Fact]
        public async Task Rows_of_a_worker_that_dies_after_claiming_are_due_again_after_the_lease()
        {
            LocalStack.RequireAvailable();
            var ids = await DueAsync(3);
            await using (var scope = _services!.CreateAsyncScope())
            {
                var claimed = await scope.ServiceProvider.GetRequiredService<IClaimStore>()
                    .ClaimAsync<Notification>("doomed", 20, ShortLease.Lease, _clock.GetUtcNow(), Ct);
                Assert.Equal(3, claimed.Count);
            }

            Assert.Equal(0, (await Runner().RunOnceAsync("rescuer", ShortLease, Ct)).Claimed); // still leased
            await Task.Delay(PastShortLease, Ct);
            var rescued = await Runner().RunOnceAsync("rescuer", ShortLease, Ct);

            Assert.Equal((3, 3), (rescued.Claimed, rescued.Completed));
            Assert.All(await RowsAsync(ids), r => Assert.Equal((1, "rescuer"), (r.WorkflowStep, r.Error)));
        }

        /// <summary>A step that always throws backs off (counted, not due at once) and is given up at the limit, visible as failed.</summary>
        [Fact]
        public async Task A_poison_row_backs_off_and_is_given_up_at_the_limit()
        {
            LocalStack.RequireAvailable();
            var ids = await DueAsync(1);
            var options = new StageOptions { Batch = 20, Lease = TimeSpan.FromSeconds(30), MaxStepFailures = 3, FailureBackoff = TimeSpan.FromSeconds(10) };
            var runner = Runner<FailingStage>();

            Assert.Equal(1, (await runner.RunOnceAsync("w", options, Ct)).Failed);
            Assert.Equal(0, (await runner.RunOnceAsync("w", options, Ct)).Claimed); // backing off, not due at once
            var row = Assert.Single(await RowsAsync(ids));
            Assert.Equal((1, (string?)null, (DateTimeOffset?)_clock.GetUtcNow().AddSeconds(10)), (row.StepFailures, row.ClaimedBy, row.NextAt));

            _clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(1, (await runner.RunOnceAsync("w", options, Ct)).Failed);
            Assert.Equal(0, (await runner.RunOnceAsync("w", options, Ct)).Claimed);
            _clock.Advance(TimeSpan.FromSeconds(20)); // the backoff doubled
            Assert.Equal(1, (await runner.RunOnceAsync("w", options, Ct)).Failed);

            row = Assert.Single(await RowsAsync(ids));
            Assert.Equal((NotificationState.Failed, (DateTimeOffset?)null, (string?)null), (row.State, row.NextAt, row.ClaimedBy));
            Assert.Equal("poison row", row.Error);
            _clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(0, (await runner.RunOnceAsync("w", options, Ct)).Claimed);
        }

        /// <summary>A worker stopped after its first step hands the rest of its batch back at once instead of holding them for a lease.</summary>
        [Fact]
        public async Task A_stopping_worker_hands_back_the_rows_it_did_not_start()
        {
            LocalStack.RequireAvailable();
            var ids = await DueAsync(3);
            using var stop = new CancellationTokenSource();
            _faults.OnClaim = (worker, _) => stop.Cancel();

            var pass = await Runner().RunOnceAsync("stopping", Options, stop.Token);
            _faults.OnClaim = null;

            Assert.Equal(3, pass.Claimed);
            var rows = await RowsAsync(ids);
            // The step that was running keeps its lease; the two never started are free for another worker now.
            Assert.Equal(2, rows.Count(r => r.ClaimedBy is null && r.LeaseUntil is null));
            Assert.Equal(2, (await Runner().RunOnceAsync("other", Options, Ct)).Completed);
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

            Assert.Equal(1, (await Runner().RunOnceAsync("w", Options, Ct)).Completed);
            _clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(1, (await Runner().RunOnceAsync("w", Options, Ct)).Completed);
            Assert.All(await RowsAsync(ids), r => Assert.Equal(1, r.WorkflowStep));
        }
    }
}
