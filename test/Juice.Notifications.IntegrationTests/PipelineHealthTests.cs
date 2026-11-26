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

        /// <summary>
        /// Each read is a limit over its due index, in next_at order, with no sort (review of #4): planned with sequential scans allowed, over a few
        /// thousand rows (most finished, the rest due later), analysed, as the planner would see a real backlog.
        /// </summary>
        [Theory]
        [InlineData("deliveries", "ix_deliveries_due")]
        [InlineData("notifications", "ix_notifications_due")]
        public async Task Each_read_is_a_limit_over_its_due_index_without_a_sort(string table, string index)
        {
            LocalStack.RequireAvailable();
            var now = _clock.GetUtcNow();
            await using var db = await _database.MigratedAsync();
            var app = Seed.Application("plans");
            var type = Seed.Type(app.Id, "plans.alert");
            var integration = Seed.Integration(Tenant);
            db.AddRange(app, type, integration, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now });
            for (var i = 0; i < 3000; i++)
            {
                // A tenth waiting (due later), the rest finished: the partial index holds only the waiting ones.
                DateTimeOffset? nextAt = i % 10 == 0 ? now.AddMinutes(1 + i) : null;
                if (table == "deliveries")
                {
                    var d = Seed.Delivery(Tenant, integration.Id);
                    (d.NextAt, d.Status) = (nextAt, nextAt is null ? DeliveryStatus.Delivered : DeliveryStatus.Pending);
                    db.Deliveries.Add(d);
                }
                else
                {
                    var request = Seed.Request(Tenant, app.Id, type.Id, $"k{i}");
                    var notification = Seed.Notification(Tenant, request.Id);
                    notification.NextAt = nextAt;
                    db.AddRange(request, notification);
                }
            }
            await db.SaveChangesAsync(Ct);
            await db.Database.ExecuteSqlRawAsync(table == "deliveries" ? "ANALYZE notifications.deliveries" : "ANALYZE notifications.notifications", Ct);

            // The query the health read sends, as EF writes it; its parameters are declared on the comment lines before it.
            var sql = table == "deliveries"
                ? db.Deliveries.Where(r => r.NextAt <= now && (r.LeaseUntil == null || r.LeaseUntil < now)).OrderBy(r => r.NextAt).Select(r => r.NextAt).Take(1).ToQueryString()
                : db.Notifications.Where(r => r.NextAt <= now && (r.LeaseUntil == null || r.LeaseUntil < now)).OrderBy(r => r.NextAt).Select(r => r.NextAt).Take(1).ToQueryString();
            var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
            await connection.OpenAsync(Ct);
            var lines = sql.Split('\n');
            await using var explain = new Npgsql.NpgsqlCommand("EXPLAIN (FORMAT JSON) " + string.Join('\n', lines.Where(l => !l.StartsWith("--", StringComparison.Ordinal))), connection);
            foreach (var declared in lines.Where(l => l.StartsWith("-- @", StringComparison.Ordinal)))
            {
                var name = declared[4..declared.IndexOf('=')];
                explain.Parameters.AddWithValue(name, declared.Contains("DateTime", StringComparison.Ordinal) || declared.Contains("timestamp", StringComparison.Ordinal) ? now : 1);
            }
            using var plan = System.Text.Json.JsonDocument.Parse((string)(await explain.ExecuteScalarAsync(Ct))!);
            var top = plan.RootElement[0].GetProperty("Plan");
            Assert.Equal("Limit", top.GetProperty("Node Type").GetString());
            var scan = Assert.Single(top.GetProperty("Plans").EnumerateArray());
            Assert.Equal(("Index Scan", index), (scan.GetProperty("Node Type").GetString(), scan.GetProperty("Index Name").GetString()));
            Assert.DoesNotContain("\"Sort\"", plan.RootElement.GetRawText());
        }
    }
}
