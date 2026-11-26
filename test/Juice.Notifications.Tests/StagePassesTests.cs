using Juice.Notifications.Data;
using Juice.Notifications.Stages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// A stage's last pass on this replica (phase-5 M49-0): recorded at the end of each pass that claimed, empty or not, and never by one whose
    /// claim failed, which a dead-man check must see as a stall.
    /// </summary>
    public sealed class StagePassesTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;

            public override DateTimeOffset GetUtcNow() => Now;
        }

        private sealed class Claims(bool fail) : IClaimStore
        {
            public Task<IReadOnlyList<Claim>> ClaimAsync<T>(string worker, int batch, TimeSpan lease, DateTimeOffset now, CancellationToken ct)
                where T : Claimable, ITenantOwned
                => fail ? throw new InvalidOperationException("the database is down") : Task.FromResult<IReadOnlyList<Claim>>([]);

            public Task<bool> RenewAsync<T>(Claim claim, TimeSpan lease, CancellationToken ct) where T : Claimable, ITenantOwned => Task.FromResult(false);

            public Task<Released?> ReleaseAsync<T>(Claim claim, DateTimeOffset? nextAt, FailureBackoff? failure, CancellationToken ct)
                where T : Claimable, ITenantOwned => Task.FromResult<Released?>(null);
        }

        private sealed class Dispatcher : IStage<Delivery>
        {
            public string Name => "dispatcher";

            public Task ExecuteAsync(StageContext context, Delivery row, CancellationToken ct) => Task.CompletedTask;

            public void GiveUp(StageContext context, Delivery row, string error) => context.Done(row, null);
        }

        private static (StageRunner<Delivery, Dispatcher> Runner, StagePasses Passes, FixedClock Clock) Runner(bool claimFails)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IClaimStore>(new Claims(claimFails));
            services.AddScoped<Dispatcher>();
            var provider = services.BuildServiceProvider();
            var clock = new FixedClock(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
            var passes = new StagePasses();
            var runner = new StageRunner<Delivery, Dispatcher>(provider.GetRequiredService<IServiceScopeFactory>(), new StageNudges(), clock,
                NullLogger<StageRunner<Delivery, Dispatcher>>.Instance, passes: passes);
            return (runner, passes, clock);
        }

        [Fact]
        public async Task A_pass_records_when_it_ended_even_with_nothing_due()
        {
            var (runner, passes, clock) = Runner(claimFails: false);
            Assert.Null(passes.LastPassAt("dispatcher"));

            await runner.RunOnceAsync("w1", new StageOptions(), Ct);
            Assert.Equal(clock.Now, passes.LastPassAt("dispatcher"));

            clock.Now = clock.Now.AddSeconds(5);
            await runner.RunOnceAsync("w1", new StageOptions(), Ct);
            Assert.Equal(clock.Now, passes.LastPassAt("dispatcher"));
            Assert.Null(passes.LastPassAt("planner"));
        }

        [Fact]
        public async Task A_pass_whose_claim_failed_records_nothing()
        {
            var (runner, passes, _) = Runner(claimFails: true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunOnceAsync("w1", new StageOptions(), Ct));
            Assert.Null(passes.LastPassAt("dispatcher"));
        }

        /// <summary>
        /// The last pass recorded is the one read, even when the clock went back (review of #4): keeping the largest time would show a dispatcher
        /// that stopped after the jump as fresh until the clock caught up.
        /// </summary>
        [Fact]
        public void The_last_pass_is_the_one_recorded_even_after_the_clock_went_back()
        {
            var passes = new StagePasses();
            var at = new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
            passes.Record("dispatcher", at);
            passes.Record("dispatcher", at.AddHours(-1));
            Assert.Equal(at.AddHours(-1), passes.LastPassAt("dispatcher"));
        }
    }
}
