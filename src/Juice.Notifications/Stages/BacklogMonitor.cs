using System.Diagnostics.Metrics;
using Juice.Notifications.Data;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Stages
{
    /// <summary>
    /// <c>notifications.due_backlog</c> by stage (design §14): the rows due now and not leased, counted every <see cref="Interval"/> and
    /// reported from the last count, so an export never waits on the database. A backlog that grows means the stage's workers can't keep up.
    /// </summary>
    public sealed class BacklogMonitor(IServiceScopeFactory scopes, TimeProvider clock, ILogger<BacklogMonitor> logger) : BackgroundService
    {
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

        private volatile IReadOnlyList<Measurement<long>> _latest = [];

        public IReadOnlyList<Measurement<long>> Latest => _latest;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Telemetry.RegisterBacklog(() => _latest);
            using var timer = new PeriodicTimer(Interval, clock);
            do
            {
                try
                {
                    await CountAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Counting the due backlog failed; the gauge keeps its last values");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        public async Task CountAsync(CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var now = clock.UtcNow();
            _latest =
            [
                Measure(PlannerStage.StageName, await Due(db.Notifications, now).CountAsync(ct)),
                Measure(DeliveryStageNames.DeliveryPlanner, await Due(db.Recipients, now).CountAsync(ct)),
                Measure(DeliveryStageNames.Dispatcher, await Due(db.Deliveries, now).CountAsync(ct)),
            ];
        }

        // The claim's own condition (PostgresClaimStore), but on the application clock: close enough for a gauge.
        private static IQueryable<T> Due<T>(IQueryable<T> rows, DateTimeOffset now) where T : Claimable
            => rows.Where(r => r.NextAt <= now && (r.LeaseUntil == null || r.LeaseUntil < now));

        private static Measurement<long> Measure(string stage, int count) => new(count, new KeyValuePair<string, object?>("stage", stage));
    }
}
