using System.Collections.Concurrent;
using System.Threading.Channels;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Stages
{
    /// <summary>
    /// One processing stage (design §4.1, phase-1-plan I3): the rows it works on, and the step it runs on one claimed row. The runner loads the
    /// row at its claimed version; the step changes it and whatever else it writes, and says when the row is next due (<see cref="StageContext.Done"/>).
    /// The runner saves it all in one transaction, conditional on the claimed version.
    /// </summary>
    public interface IStage<T> where T : Claimable, ITenantOwned
    {
        /// <summary>The stage's name, for workers, logs and nudges: <c>planner</c>, <c>delivery-planner</c>, <c>dispatcher</c>.</summary>
        string Name { get; }

        /// <summary>Runs one step on <paramref name="row"/>, which the runner loaded for the claim.</summary>
        Task ExecuteAsync(StageContext context, T row, CancellationToken ct);

        /// <summary>
        /// The step on <paramref name="row"/> threw <see cref="StageOptions.MaxStepFailures"/> times in a row: record it as failed, in the row's own
        /// terms, so it shows in history instead of being retried forever. The runner saves it; it must call <see cref="StageContext.Done"/>.
        /// </summary>
        void GiveUp(StageContext context, T row, string error);
    }

    /// <summary>Per stage: how many rows a claim takes, how long its lease is, how often the worker polls when nothing wakes it.</summary>
    public sealed class StageOptions
    {
        public int Batch { get; set; } = 20;
        /// <summary>How long one step may take: each row's lease is renewed for this long just before its step.</summary>
        public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(1);
        public int Workers { get; set; } = 1;
        /// <summary>
        /// Steps that throw on one row in a row before the stage gives the row up. A deadlock, a command timeout or a row that won't load counts
        /// (with its backoff, so one that recurs can't spin); only a failed database connection doesn't.
        /// </summary>
        public int MaxStepFailures { get; set; } = 10;
        /// <summary>The wait after a step throws, doubling with each counted failure up to <see cref="FailureBackoffMax"/>.</summary>
        public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan FailureBackoffMax { get; set; } = TimeSpan.FromMinutes(10);
    }

    /// <summary>What a step works with: its scope's DbContext, the claim it runs under, and the clock.</summary>
    public sealed class StageContext(NotificationsDbContext db, Claim claim, DateTimeOffset now, IServiceProvider services)
    {
        public NotificationsDbContext Db { get; } = db;
        public Claim Claim { get; } = claim;
        public DateTimeOffset Now { get; } = now;
        public IServiceProvider Services { get; } = services;

        /// <summary>The stages to wake once this step commits.</summary>
        public HashSet<string> Wakes { get; } = new(StringComparer.Ordinal);

        /// <summary>What to do once this step commits, and only then (metrics): a step whose save is lost runs again and would count twice.</summary>
        public List<Action> AfterCommit { get; } = [];

        /// <summary>The step is finished with the row: clear the lease and set when it's next due (null: never).</summary>
        public void Done(Claimable row, DateTimeOffset? nextAt)
        {
            row.ClaimedBy = null;
            row.LeaseUntil = null;
            row.NextAt = nextAt;
            row.StepFailures = 0;
            // The write is conditional on the claimed version (a concurrency token) and moves it on, so no older claim can write after this.
            row.Version = Claim.Version + 1;
        }
    }

    /// <summary>Lets a step wake another stage now instead of at its next poll. Lost wake-ups only cost latency: workers poll anyway.</summary>
    public sealed class StageNudges
    {
        private readonly ConcurrentDictionary<string, Channel<bool>> _channels = new(StringComparer.Ordinal);

        private Channel<bool> For(string stage) => _channels.GetOrAdd(stage, _ => Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        }));

        public void Wake(string stage) => For(stage).Writer.TryWrite(true);

        /// <summary>Waits for a wake-up or the poll interval, whichever comes first.</summary>
        public async Task WaitAsync(string stage, TimeSpan poll, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(poll);
            try
            {
                await For(stage).Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    /// <summary>A seam for tests (phase-1-plan M3): pause or fail a step between claim, execute and persist. The default does nothing.</summary>
    public interface IStageFaults
    {
        Task AfterClaimAsync(string stage, string worker, Claim claim, CancellationToken ct) => Task.CompletedTask;

        Task BeforePersistAsync(string stage, string worker, Claim claim, CancellationToken ct) => Task.CompletedTask;
    }

    internal sealed class NoFaults : IStageFaults;

    /// <summary>What one pass did, for tests and metrics.</summary>
    public sealed record PassResult(int Claimed, int Completed, int LeaseLost, int Failed);

    /// <summary>
    /// Runs a stage (design §11): claims a batch of due rows, then runs each in its own scope. Before each step the row's lease is renewed, so a row
    /// late in the batch still has a whole lease; a row claimed again meanwhile is skipped as <c>lease_lost</c> without running.
    /// <list type="bullet">
    /// <item>The step's writes are one <c>SaveChanges</c>, always including the claimed row conditional on its claimed version: a worker whose lease
    /// ended writes nothing, child rows included, and that is <c>lease_lost</c>. No transaction is held open while the step runs (a send can take
    /// seconds).</item>
    /// <item>A step that throws is handed back with a backoff and counted; at <see cref="StageOptions.MaxStepFailures"/> the stage gives the row up
    /// (<see cref="IStage{T}.GiveUp"/>). A failed database connection, which isn't the row's doing, waits the base backoff, uncounted.</item>
    /// <item>When the worker stops, rows claimed but not started are handed back at once rather than when their lease ends.</item>
    /// </list>
    /// </summary>
    public sealed class StageRunner<T, TStage>(IServiceScopeFactory scopes, StageNudges nudges, TimeProvider clock, ILogger<StageRunner<T, TStage>> logger,
        IStageFaults? faults = null)
        where T : Claimable, ITenantOwned
        where TStage : IStage<T>
    {
        private readonly IStageFaults _faults = faults ?? new NoFaults();
        private string? _name;

        /// <summary>The stage's name, read once.</summary>
        public string StageName(IServiceProvider services) => _name ??= services.GetRequiredService<TStage>().Name;

        public async Task<PassResult> RunOnceAsync(string worker, StageOptions options, CancellationToken ct)
        {
            IReadOnlyList<Claim> claims;
            string stageName;
            await using (var scope = scopes.CreateAsyncScope())
            {
                stageName = StageName(scope.ServiceProvider);
                claims = await scope.ServiceProvider.GetRequiredService<IClaimStore>().ClaimAsync<T>(worker, options.Batch, options.Lease, clock.UtcNow(), ct);
            }
            int completed = 0, lost = 0, failed = 0;
            for (var i = 0; i < claims.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    await HandBackAsync(stageName, worker, claims.Skip(i).ToList());
                    break;
                }
                switch (await StepAsync(stageName, worker, claims[i], options, ct))
                {
                    case StepResult.Completed: completed++; break;
                    case StepResult.LeaseLost: lost++; Telemetry.LeaseLost.Add(1, new KeyValuePair<string, object?>("stage", stageName)); break;
                    default: failed++; break;
                }
            }
            return new PassResult(claims.Count, completed, lost, failed);
        }

        private enum StepResult { Completed, LeaseLost, Failed }

        private async Task<StepResult> StepAsync(string stageName, string worker, Claim claim, StageOptions options, CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<IClaimStore>();
            var stage = scope.ServiceProvider.GetRequiredService<TStage>();
            var persisting = false;
            try
            {
                await _faults.AfterClaimAsync(stageName, worker, claim, ct);
                if (!await store.RenewAsync<T>(claim, options.Lease, ct))
                {
                    // This lease ended before the step and someone claimed the row: their claim moved the version.
                    logger.LogInformation("{Stage} {Worker}: lease_lost before the step on {Id}", stageName, worker, claim.Id);
                    return StepResult.LeaseLost;
                }
                var row = await LoadAsync(db, claim, ct);
                if (row is null)
                {
                    logger.LogInformation("{Stage} {Worker}: lease_lost before the step on {Id}", stageName, worker, claim.Id);
                    return StepResult.LeaseLost;
                }
                var context = new StageContext(db, claim, clock.UtcNow(), scope.ServiceProvider);
                await stage.ExecuteAsync(context, row, ct);
                await _faults.BeforePersistAsync(stageName, worker, claim, ct);
                // Not stopped by the worker stopping: the step happened (a send may have gone out), so its result is recorded. Bounded, so a
                // database that hangs can't hold shutdown.
                persisting = true;
                using (var persist = new CancellationTokenSource(PersistLimit))
                {
                    await PersistAsync(stageName, worker, context, row, persist.Token);
                }
                return StepResult.Completed;
            }
            catch (Exception ex) when (ct.IsCancellationRequested && !persisting)
            {
                // Stopping: whatever the step threw on the way out (a cancellation, a disposed or torn-down connection) isn't the row's fault.
                // The row keeps its lease and is due again when it ends. A save that fails while stopping is still a failure: it runs on its
                // own token, so it is handled below like any other.
                logger.LogDebug(ex, "{Stage} {Worker}: stopped during the step on {Id}", stageName, worker, claim.Id);
                return StepResult.Failed;
            }
            catch (Exception ex)
            {
                // Everything counts toward giving the row up (a deadlock, a timeout, a row that won't load) except the connection itself
                // failing, which says nothing about the row.
                return await FailedAsync(stageName, worker, claim, options, ex, counts: !DbConflicts.IsConnectionFailure(ex));
            }
        }

        private static readonly TimeSpan PersistLimit = TimeSpan.FromSeconds(30);

        private static Task<T?> LoadAsync(NotificationsDbContext db, Claim claim, CancellationToken ct)
            => db.Set<T>().SingleOrDefaultAsync(r => r.TenantId == claim.TenantId && r.Id == claim.Id && r.Version == claim.Version, ct);

        /// <summary>
        /// Saves a step's writes, always with the claimed row conditional on its claimed version: a stale worker's other writes would otherwise
        /// land unfenced. A step that didn't finish its row leaves it claimed, and it runs again when the lease ends. Then wakes what the step asked.
        /// </summary>
        private async Task PersistAsync(string stageName, string worker, StageContext context, T row, CancellationToken ct)
        {
            if (row.Version == context.Claim.Version)
            {
                logger.LogWarning("{Stage} {Worker}: the step on {Id} didn't finish its row; it runs again when the lease ends", stageName, worker, row.Id);
                row.Version = context.Claim.Version + 1;
            }
            await context.Db.SaveChangesAsync(ct);
            foreach (var next in context.Wakes)
            {
                nudges.Wake(next);
            }
            RunAfterCommit(context);
        }

        private void RunAfterCommit(StageContext context)
        {
            foreach (var action in context.AfterCommit)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "An after-commit action failed");
                }
            }
        }

        private async Task<StepResult> FailedAsync(string stageName, string worker, Claim claim, StageOptions options, Exception ex, bool counts)
        {
            Released? released;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                // Counted: due after the backoff the new count earns. Not counted: due after the base backoff, so it can't spin.
                released = await scope.ServiceProvider.GetRequiredService<IClaimStore>().ReleaseAsync<T>(claim,
                    counts ? null : clock.UtcNow() + options.FailureBackoff,
                    counts ? new FailureBackoff(clock.UtcNow(), options.FailureBackoff, options.FailureBackoffMax) : null, CancellationToken.None);
            }
            catch (Exception handing)
            {
                // Handing the row back failed too (the database is down): its lease ends on its own, and it is due again then.
                logger.LogError(new AggregateException(ex, handing), "{Stage} {Worker}: the step on {Id} failed, and so did handing it back; it is due again when its lease ends",
                    stageName, worker, claim.Id);
                return StepResult.Failed;
            }
            if (released is null)
            {
                // A worker whose lease ended fails on the version check, or first on a unique key the newer worker's rows already took. The
                // row's version moved either way: the result is discarded, and that is a lost lease, not a failure.
                logger.LogInformation(ex, "{Stage} {Worker}: lease_lost on {Id}; its result was discarded", stageName, worker, claim.Id);
                return StepResult.LeaseLost;
            }
            if (counts && released.StepFailures >= options.MaxStepFailures)
            {
                logger.LogError(ex, "{Stage} {Worker}: the step on {Id} failed {Failures} times in a row; giving the row up", stageName, worker, claim.Id, released.StepFailures);
                await GiveUpAsync(stageName, worker, claim with { Version = released.Version }, ex);
                return StepResult.Failed;
            }
            logger.LogError(ex, counts
                ? "{Stage} {Worker}: the step on {Id} failed ({Failures} in a row); it runs again at {NextAt}"
                : "{Stage} {Worker}: the step on {Id} failed because the database connection did ({Failures} counted); it runs again at {NextAt}",
                stageName, worker, claim.Id, released.StepFailures, released.NextAt);
            return StepResult.Failed;
        }

        /// <summary>Lets the stage record the row as failed, written under the version the hand-back gave it, like any step.</summary>
        private async Task GiveUpAsync(string stageName, string worker, Claim claim, Exception ex)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var row = await LoadAsync(db, claim, CancellationToken.None);
                if (row is null)
                {
                    return; // claimed again meanwhile: that worker owns it now
                }
                var context = new StageContext(db, claim, clock.UtcNow(), scope.ServiceProvider);
                // The root cause: an EF save failure's own message only says to look inside.
                var error = ex.GetBaseException().Message;
                scope.ServiceProvider.GetRequiredService<TStage>().GiveUp(context, row, error is { Length: > 2000 } ? error[..2000] : error);
                await PersistAsync(stageName, worker, context, row, CancellationToken.None);
            }
            catch (Exception giving)
            {
                // The row is handed back with its backoff, so it runs (and fails, and is given up) again then.
                logger.LogError(giving, "{Stage} {Worker}: giving up the row {Id} failed; it runs again after its backoff", stageName, worker, claim.Id);
            }
        }

        /// <summary>Hands back claims a stopping worker won't run, so they are due now rather than when their lease ends.</summary>
        private async Task HandBackAsync(string stageName, string worker, IReadOnlyList<Claim> claims)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IClaimStore>();
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var claim in claims)
                {
                    await store.ReleaseAsync<T>(claim, null, null, limit.Token);
                }
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex, "{Stage} {Worker}: stopping; {Count} claimed rows are due again when their leases end", stageName, worker, claims.Count);
            }
        }
    }

    /// <summary>The background workers of one stage: each claims and runs passes until there is nothing due, then waits for a nudge or the poll.</summary>
    public sealed class StageWorker<T, TStage>(StageRunner<T, TStage> runner, StageNudges nudges, IServiceScopeFactory scopes, StageOptions options,
        ILogger<StageWorker<T, TStage>> logger) : BackgroundService
        where T : Claimable, ITenantOwned
        where TStage : IStage<T>
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            string name;
            await using (var scope = scopes.CreateAsyncScope())
            {
                name = runner.StageName(scope.ServiceProvider);
            }
            var workers = Enumerable.Range(1, Math.Max(1, options.Workers))
                .Select(i => RunAsync(name, $"{Environment.MachineName}/{Environment.ProcessId}/{name}/{i}", stoppingToken));
            await Task.WhenAll(workers);
        }

        private async Task RunAsync(string stage, string worker, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var pass = await runner.RunOnceAsync(worker, options, ct);
                    if (pass.Claimed == options.Batch)
                    {
                        continue; // a full batch: there may be more due right now
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    return; // stopping: whatever failed on the way out isn't a failure of the stage
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "{Stage} {Worker}: claiming failed; trying again at the next poll", stage, worker);
                }
                await nudges.WaitAsync(stage, options.Poll, ct);
            }
        }
    }
}
