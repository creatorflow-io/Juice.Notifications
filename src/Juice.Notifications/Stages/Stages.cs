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
    }

    /// <summary>Per stage: how many rows a claim takes, how long its lease is, how often the worker polls when nothing wakes it.</summary>
    public sealed class StageOptions
    {
        public int Batch { get; set; } = 20;
        public TimeSpan Lease { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(1);
        public int Workers { get; set; } = 1;
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

        /// <summary>The step is finished with the row: clear the lease and set when it's next due (null: never).</summary>
        public void Done(Claimable row, DateTimeOffset? nextAt)
        {
            row.ClaimedBy = null;
            row.LeaseUntil = null;
            row.NextAt = nextAt;
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
    /// Runs a stage: claims a batch of due rows, and runs each in its own scope and transaction (design §11). A save that finds a newer version
    /// lost its lease to another worker: its whole step is rolled back and logged as <c>lease_lost</c>. A step that throws is rolled back too; its
    /// row stays claimed and is due again when the lease ends.
    /// </summary>
    public sealed class StageRunner<T, TStage>(IServiceScopeFactory scopes, StageNudges nudges, TimeProvider clock, ILogger<StageRunner<T, TStage>> logger,
        IStageFaults? faults = null)
        where T : Claimable, ITenantOwned
        where TStage : IStage<T>
    {
        private readonly IStageFaults _faults = faults ?? new NoFaults();

        public async Task<PassResult> RunOnceAsync(string worker, StageOptions options, CancellationToken ct)
        {
            IReadOnlyList<Claim> claims;
            string stageName;
            await using (var scope = scopes.CreateAsyncScope())
            {
                stageName = scope.ServiceProvider.GetRequiredService<TStage>().Name;
                claims = await scope.ServiceProvider.GetRequiredService<IClaimStore>().ClaimAsync<T>(worker, options.Batch, options.Lease, clock.UtcNow(), ct);
            }
            int completed = 0, lost = 0, failed = 0;
            foreach (var claim in claims)
            {
                switch (await StepAsync(stageName, worker, claim, ct))
                {
                    case StepResult.Completed: completed++; break;
                    case StepResult.LeaseLost: lost++; break;
                    default: failed++; break;
                }
            }
            return new PassResult(claims.Count, completed, lost, failed);
        }

        private enum StepResult { Completed, LeaseLost, Failed }

        private async Task<StepResult> StepAsync(string stageName, string worker, Claim claim, CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var stage = scope.ServiceProvider.GetRequiredService<TStage>();
            try
            {
                await _faults.AfterClaimAsync(stageName, worker, claim, ct);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var row = await db.Set<T>().SingleOrDefaultAsync(r => r.TenantId == claim.TenantId && Microsoft.EntityFrameworkCore.EF.Property<Guid>(r, "Id") == claim.Id
                    && r.Version == claim.Version, ct);
                if (row is null)
                {
                    // Claimed again by someone else after this lease ended: their claim moved the version.
                    logger.LogInformation("{Stage} {Worker}: lease_lost before the step on {Id}", stageName, worker, claim.Id);
                    return StepResult.LeaseLost;
                }
                var context = new StageContext(db, claim, clock.UtcNow(), scope.ServiceProvider);
                await stage.ExecuteAsync(context, row, ct);
                await _faults.BeforePersistAsync(stageName, worker, claim, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                foreach (var next in context.Wakes)
                {
                    nudges.Wake(next);
                }
                return StepResult.Completed;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A worker whose lease ended can fail on the version check, or first on a unique key the newer worker's rows already took.
                // Either way the row's version moved: the result is discarded and that is a lost lease, not a failure. A save that fails while
                // the row is still at the claimed version is a real failure.
                if (DbConflicts.Classify(ex) is not null && await VersionMovedAsync(claim, ct))
                {
                    logger.LogInformation("{Stage} {Worker}: lease_lost on {Id}; its result was discarded", stageName, worker, claim.Id);
                    return StepResult.LeaseLost;
                }
                logger.LogError(ex, "{Stage} {Worker}: the step on {Id} failed; it is due again when its lease ends", stageName, worker, claim.Id);
                return StepResult.Failed;
            }
        }

        private async Task<bool> VersionMovedAsync(Claim claim, CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var version = await db.Set<T>().AsNoTracking()
                .Where(r => r.TenantId == claim.TenantId && Microsoft.EntityFrameworkCore.EF.Property<Guid>(r, "Id") == claim.Id)
                .Select(r => (int?)r.Version).SingleOrDefaultAsync(ct);
            return version != claim.Version;
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
                name = scope.ServiceProvider.GetRequiredService<TStage>().Name;
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
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "{Stage} {Worker}: claiming failed; trying again at the next poll", stage, worker);
                }
                await nudges.WaitAsync(stage, options.Poll, ct);
            }
        }
    }
}
