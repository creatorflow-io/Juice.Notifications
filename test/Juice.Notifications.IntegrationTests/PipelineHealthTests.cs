using Juice.Notifications.Data;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// How long the oldest due work has waited (phase-5 M49-0): the deliveries and notifications due now and not leased, as the claim takes them;
    /// a row due later, leased by a worker, or finished doesn't count; each read goes through its stage's due index.
    /// </summary>
    public sealed class PipelineHealthTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));

        public ValueTask InitializeAsync() => _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        private NotificationsPipelineHealth Health(NotificationsDbContext db, StagePasses? passes = null, bool running = true)
            => new(db, passes ?? new StagePasses(), new StagesRunning(running), _clock);

        [Fact]
        public async Task Nothing_due_is_no_age()
        {
            LocalStack.RequireAvailable();
            await using var db = await _database.MigratedAsync();
            var health = await Health(db, running: false).ReadAsync(Ct);
            Assert.Equal(new PipelineHealth(null, null, null, false), health);
        }

        [Fact]
        public async Task The_oldest_due_free_delivery_and_notification_are_measured_and_the_rest_left_out()
        {
            LocalStack.RequireAvailable();
            var now = _clock.GetUtcNow();
            await using (var db = await _database.MigratedAsync())
            {
                var app = Seed.Application("health");
                var type = Seed.Type(app.Id, "health.alert");
                var integration = Seed.Integration(Tenant);
                db.AddRange(app, type, integration, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now });
                Delivery D(DateTimeOffset? nextAt, DateTimeOffset? leaseUntil = null, DeliveryStatus status = DeliveryStatus.Pending)
                {
                    var d = Seed.Delivery(Tenant, integration.Id);
                    (d.NextAt, d.LeaseUntil, d.Status) = (nextAt, leaseUntil, status);
                    return d;
                }
                db.Deliveries.AddRange(
                    D(now.AddMinutes(-3)),                                  // due 3 minutes: the oldest that counts
                    D(now.AddMinutes(-1)),                                  // due 1 minute
                    D(now.AddMinutes(-10), leaseUntil: now.AddSeconds(30)), // older, but a worker has it
                    D(now.AddMinutes(5)),                                   // not due yet
                    D(null, status: DeliveryStatus.Delivered));             // finished
                foreach (var (n, nextAt, lease) in new (int, DateTimeOffset?, DateTimeOffset?)[]
                {
                    (1, now.AddSeconds(-40), null), (2, now.AddMinutes(-7), now.AddMinutes(1)), (3, now.AddMinutes(2), null), (4, null, null),
                })
                {
                    var request = Seed.Request(Tenant, app.Id, type.Id, $"k{n}");
                    var notification = Seed.Notification(Tenant, request.Id);
                    (notification.NextAt, notification.LeaseUntil) = (nextAt, lease);
                    db.AddRange(request, notification);
                }
                await db.SaveChangesAsync(Ct);
            }
            var passes = new StagePasses();
            passes.Record("dispatcher", now.AddSeconds(-2));

            await using var read = _database.Context();
            var health = await Health(read, passes).ReadAsync(Ct);

            Assert.Equal(new PipelineHealth(now.AddSeconds(-2), TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(40), true), health);
        }

        [Theory]
        [InlineData("deliveries", "ix_deliveries_due")]
        [InlineData("notifications", "ix_notifications_due")]
        public async Task Each_read_goes_through_its_due_index(string table, string index)
        {
            LocalStack.RequireAvailable();
            await using var db = await _database.MigratedAsync();
            await using var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
            await connection.OpenAsync(Ct);
            await using (var off = new Npgsql.NpgsqlCommand("SET enable_seqscan = off", connection))
            {
                await off.ExecuteNonQueryAsync(Ct);
            }
            // The query the health read sends, as EF writes it.
            var now = _clock.GetUtcNow();
            var sql = table == "deliveries"
                ? db.Deliveries.Where(r => r.NextAt <= now && (r.LeaseUntil == null || r.LeaseUntil < now)).OrderBy(r => r.NextAt).Select(r => r.NextAt).Take(1).ToQueryString()
                : db.Notifications.Where(r => r.NextAt <= now && (r.LeaseUntil == null || r.LeaseUntil < now)).OrderBy(r => r.NextAt).Select(r => r.NextAt).Take(1).ToQueryString();
            // ToQueryString declares each parameter on a comment line (-- @name='value' …) before the statement: the time is now, a count is 1.
            var lines = sql.Split('\n');
            await using var explain = new Npgsql.NpgsqlCommand("EXPLAIN " + string.Join('\n', lines.Where(l => !l.StartsWith("--", StringComparison.Ordinal))), connection);
            foreach (var declared in lines.Where(l => l.StartsWith("-- @", StringComparison.Ordinal)))
            {
                var name = declared[4..declared.IndexOf('=')];
                explain.Parameters.AddWithValue(name, declared.Contains("DateTime", StringComparison.Ordinal) || declared.Contains("timestamp", StringComparison.Ordinal) ? now : 1);
            }
            var plan = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync(Ct))
            {
                while (await reader.ReadAsync(Ct))
                {
                    plan.Add(reader.GetString(0));
                }
            }
            Assert.Contains(plan, line => line.Contains(index, StringComparison.Ordinal));
        }
    }
}
