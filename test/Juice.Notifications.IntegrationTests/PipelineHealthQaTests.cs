using System.Diagnostics;
using System.Net;
using Juice.Notifications.Data;
using Juice.Notifications.Stages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// QA (M49-0, waves §4 and §6.3): the StageFailingIndexes migration over existing rows, down and up; the health read in the service; its cost
    /// over a backlog of tens of thousands of rows; and that no HTTP route hands it to anyone.
    /// </summary>
    public sealed class PipelineHealthQaTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private const string Previous = "20261126025035_HoldsAndDryRun";
        private static readonly string[] Tables = ["notifications", "notification_recipients", "escalations", "digests", "deliveries"];
        private readonly ScratchDatabase _database = new();

        public ValueTask InitializeAsync() => _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        private async Task<(Guid Integration, Guid Delivery)> SeedAsync()
        {
            await using var db = await _database.MigratedAsync();
            var app = Seed.Application("qa-health");
            var type = Seed.Type(app.Id, "qa-health.thing");
            var integration = Seed.Integration(Tenant);
            var delivery = Seed.Delivery(Tenant, integration.Id);
            (delivery.NextAt, delivery.StepFailures, delivery.Status) = (DateTimeOffset.UtcNow.AddMinutes(-3), 2, DeliveryStatus.Pending);
            var stopped = Seed.Delivery(Tenant, integration.Id);
            (stopped.NextAt, stopped.StepFailures, stopped.Status) = (null, 4, DeliveryStatus.Cancelled);
            db.AddRange(app, type, integration, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now }, delivery, stopped);
            await db.SaveChangesAsync(Ct);
            return (integration.Id, delivery.Id);
        }

        private async Task<PipelineHealth> ReadAsync()
        {
            await using var db = _database.Context();
            return await new NotificationsPipelineHealth(db, new StagePasses(), new StagesRunning(true), TimeProvider.System).ReadAsync(Ct);
        }

        // ─── Migration ────────

        [Fact]
        public async Task The_failing_indexes_apply_over_existing_rows_and_go_down_and_up_again()
        {
            LocalStack.RequireAvailable();
            await SeedAsync();
            for (var round = 0; round < 2; round++)
            {
                await using (var db = _database.Context())
                {
                    await db.GetService<IMigrator>().MigrateAsync(Previous, cancellationToken: Ct);
                }
                foreach (var table in Tables)
                {
                    Assert.Null(await IndexAsync(table));
                }
                // The rows stay through the down: the health read counts the same rows without the index.
                Assert.Equal(1, (await ReadAsync()).Stages["dispatcher"].Failing);

                await using (var db = _database.Context())
                {
                    await db.Database.MigrateAsync(Ct);
                    Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
                    Assert.False(db.Database.HasPendingModelChanges());
                }
                foreach (var table in Tables)
                {
                    var (definition, valid) = (await IndexAsync(table))!.Value;
                    Assert.True(valid, $"ix_{table}_failing isn't valid");
                    Assert.Contains("(step_failures)", definition);
                    Assert.Contains("WHERE ((step_failures > 0) AND (next_at IS NOT NULL))", definition);
                }
                var health = await ReadAsync();
                Assert.Equal(1, health.Stages["dispatcher"].Failing);
                Assert.InRange(health.Stages["dispatcher"].OldestDue!.Value, TimeSpan.FromMinutes(2.9), TimeSpan.FromMinutes(10));
            }
        }

        private async Task<(string Definition, bool Valid)?> IndexAsync(string table)
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand($"""
                SELECT pg_get_indexdef(i.indexrelid), i.indisvalid FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'notifications' AND c.relname = 'ix_{table}_failing'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            return await reader.ReadAsync(Ct) ? (reader.GetString(0), reader.GetBoolean(1)) : null;
        }

        // ─── Cost over a backlog ────────

        /// <summary>
        /// 60,000 due deliveries (500 of them backing off after failed steps, as failing rows are few), 40,000 delivered ones, and 30,000 due notifications: the read is
        /// milliseconds, through the due and failing indexes, never a sequential scan.
        /// </summary>
        [Fact]
        public async Task The_health_read_over_a_backlog_of_tens_of_thousands_takes_milliseconds()
        {
            LocalStack.RequireAvailable();
            var (_, template) = await SeedAsync();
            await CloneAsync("deliveries", template, 59_500, "now() - (g || ' seconds')::interval", "0");
            await CloneAsync("deliveries", template, 500, "now() - (g || ' seconds')::interval", "3");
            await CloneAsync("deliveries", template, 40_000, "NULL", "0");
            await using (var db = _database.Context())
            {
                var app = await db.Applications.SingleAsync(a => a.Key == "qa-health", Ct);
                var type = await db.Types.SingleAsync(t => t.ApplicationId == app.Id, Ct);
                var request = Seed.Request(Tenant, app.Id, type.Id, "qa-health-template");
                var notification = Seed.Notification(Tenant, request.Id);
                db.AddRange(request, notification);
                await db.SaveChangesAsync(Ct);
                // Notifications are one per request: clone the request with each.
                await ExecuteAsync($"""
                    WITH r AS (
                        INSERT INTO notifications.requests ({await ColumnsAsync("requests")})
                        SELECT {(await ColumnsAsync("requests")).Replace("\"id\"", "gen_random_uuid()").Replace("\"idempotency_key\"", "'qa-' || g")}
                        FROM notifications.requests, generate_series(1, 30000) g WHERE id = '{request.Id}' RETURNING id)
                    INSERT INTO notifications.notifications ({await ColumnsAsync("notifications")})
                    SELECT {(await ColumnsAsync("notifications")).Replace("\"id\"", "gen_random_uuid()").Replace("\"request_id\"", "r.id").Replace("\"next_at\"", "now() - interval '5 minutes'")}
                    FROM notifications.notifications, r WHERE notifications.id = '{notification.Id}'
                    """);
            }
            await ExecuteAsync("ANALYZE notifications.deliveries; ANALYZE notifications.notifications;");

            await ReadAsync(); // warm: the model, the pool, the plans
            var times = new List<double>();
            PipelineHealth health = null!;
            for (var i = 0; i < 7; i++)
            {
                var watch = Stopwatch.StartNew();
                health = await ReadAsync();
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            var median = times[times.Count / 2];
            Report($"health read over the backlog: median {median:F1} ms, max {times[^1]:F1} ms");
            Assert.Equal(501, health.Stages["dispatcher"].Failing); // the clones and the template
            Assert.True(health.Stages["dispatcher"].OldestDue > TimeSpan.FromHours(10), "the oldest due delivery is the earliest clone");
            Assert.True(health.Stages["planner"].OldestDue >= TimeSpan.FromMinutes(4.9));
            Assert.True(median < 50, $"median {median:F1} ms");

            foreach (var (table, index) in new[] { ("deliveries", "ix_deliveries_due"), ("notifications", "ix_notifications_due") })
            {
                var due = await PlanAsync($"""
                    SELECT next_at FROM notifications.{table} WHERE next_at <= now() AND (lease_until IS NULL OR lease_until < now()) ORDER BY next_at LIMIT 1
                    """);
                Report($"{table} oldest due:\n{due}");
                Assert.Contains(index, due);
                Assert.DoesNotContain("Seq Scan", due);
                Assert.DoesNotContain("Sort", due);
            }
            var failing = await PlanAsync("SELECT count(*) FROM (SELECT 1 FROM notifications.deliveries WHERE step_failures > 0 AND next_at IS NOT NULL LIMIT 1000) f");
            Report($"deliveries failing:\n{failing}");
            Assert.Contains("ix_deliveries_failing", failing);
            Assert.DoesNotContain("Seq Scan", failing);
        }

        /// <summary>
        /// A stage failing on a mass scale (20,000 rows backing off): the count stops at its cap and stays cheap. With so many, PostgreSQL may
        /// read the table instead of the partial index; the LIMIT keeps it short either way.
        /// </summary>
        [Fact]
        public async Task Mass_failing_is_capped_and_stays_cheap()
        {
            LocalStack.RequireAvailable();
            var (_, template) = await SeedAsync();
            await CloneAsync("deliveries", template, 20_000, "now() - (g || ' seconds')::interval", "3");
            await CloneAsync("deliveries", template, 40_000, "NULL", "0");
            await ExecuteAsync("ANALYZE notifications.deliveries;");
            await ReadAsync();
            var watch = Stopwatch.StartNew();
            var health = await ReadAsync();
            var ms = watch.Elapsed.TotalMilliseconds;
            Report($"health read with 20,000 failing deliveries: {ms:F1} ms");
            Assert.Equal(StageHealth.MaxFailing, health.Stages["dispatcher"].Failing);
            Assert.True(ms < 500, $"{ms:F1} ms");
        }

        /// <summary>
        /// The worst case of the oldest-due read: every due row leased (many replicas mid-pass). The read walks the due index past every leased
        /// row before it finds none free; measured, not asserted tight.
        /// </summary>
        [Fact]
        public async Task The_oldest_due_read_when_every_due_row_is_leased()
        {
            LocalStack.RequireAvailable();
            var (_, template) = await SeedAsync();
            await CloneAsync("deliveries", template, 50_000, "now() - (g || ' seconds')::interval", "0", lease: "now() + interval '1 minute'");
            await ExecuteAsync("UPDATE notifications.deliveries SET lease_until = now() + interval '1 minute' WHERE id = '" + template + "'");
            await ExecuteAsync("ANALYZE notifications.deliveries;");
            await ReadAsync();
            var watch = Stopwatch.StartNew();
            var health = await ReadAsync();
            var ms = watch.Elapsed.TotalMilliseconds;
            Report($"health read with 50,000 due rows all leased: {ms:F1} ms");
            Assert.Null(health.Stages["dispatcher"].OldestDue);
            Assert.True(ms < 1000, $"{ms:F1} ms");
        }

        private async Task CloneAsync(string table, Guid template, int count, string nextAt, string failures, string lease = "NULL")
        {
            var columns = await ColumnsAsync(table);
            var select = columns.Replace("\"id\"", "gen_random_uuid()").Replace("\"next_at\"", nextAt).Replace("\"step_failures\"", failures)
                .Replace("\"lease_until\"", lease).Replace("\"status\"", nextAt == "NULL" ? "'Delivered'" : "'Pending'");
            await ExecuteAsync($"INSERT INTO notifications.{table} ({columns}) SELECT {select} FROM notifications.{table}, generate_series(1, {count}) g WHERE id = '{template}'");
        }

        private async Task<string> ColumnsAsync(string table)
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand($"""
                SELECT string_agg(quote_ident(column_name), ', ' ORDER BY ordinal_position) FROM information_schema.columns
                WHERE table_schema = 'notifications' AND table_name = '{table}' AND is_generated = 'NEVER'
                """, connection);
            var columns = (string)(await command.ExecuteScalarAsync(Ct))!;
            // quote_ident leaves plain names bare: quote them all so the replacements match whole names only.
            return string.Join(", ", columns.Split(", ").Select(c => c.StartsWith('"') ? c : $"\"{c}\""));
        }

        private async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(Ct);
        }

        private async Task<string> PlanAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var lines = new List<string>();
            while (await reader.ReadAsync(Ct))
            {
                lines.Add(reader.GetString(0));
            }
            return string.Join('\n', lines);
        }

        private static void Report(string line)
            => File.AppendAllText(Path.Combine(Path.GetTempPath(), "qa-m49-health.txt"), $"{DateTime.UtcNow:O} {line}{Environment.NewLine}");
    }

    /// <summary>QA (M49-0): the health in the service host, and nothing on HTTP hands it out.</summary>
    public sealed class PipelineHealthServiceQaTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly NotificationsApp _app = new();

        public ValueTask InitializeAsync() => _app.InitializeAsync();

        public ValueTask DisposeAsync() => _app.DisposeAsync();

        [Fact]
        public async Task The_service_reads_every_stages_health_with_its_passes()
        {
            LocalStack.RequireAvailable();
            PipelineHealth health;
            var until = DateTimeOffset.UtcNow.AddSeconds(20);
            do
            {
                await using var scope = _app.Services.CreateAsyncScope();
                health = await scope.ServiceProvider.GetRequiredService<INotificationsPipelineHealth>().ReadAsync(Ct);
                if (health.Stages.Values.All(s => s.LastPassEndedAt is not null))
                {
                    break;
                }
                await Task.Delay(250, Ct);
            }
            while (DateTimeOffset.UtcNow < until);

            Assert.True(health.Running);
            Assert.Equal(["delivery-planner", "digest", "dispatcher", "escalation", "planner"], health.Stages.Keys.Order());
            Assert.All(health.Stages, s => Assert.NotNull(s.Value.LastPassEndedAt));
            Assert.All(health.Stages, s => Assert.True(s.Value.LastPassEndedAt > DateTimeOffset.UtcNow.AddMinutes(-1), s.Key));
            // Quiet: nothing due, nothing failing; no step yet, so no progress.
            Assert.All(health.Stages, s => Assert.Equal((null, 0), (s.Value.OldestDue, s.Value.Failing)));
            Assert.Null(health.OldestDueAny);

            // Passes move on: a later read has later pass ends.
            var first = health.Stages["dispatcher"].LastPassEndedAt;
            await Task.Delay(1500, Ct);
            await using (var scope = _app.Services.CreateAsyncScope())
            {
                var later = await scope.ServiceProvider.GetRequiredService<INotificationsPipelineHealth>().ReadAsync(Ct);
                Assert.True(later.Stages["dispatcher"].LastPassEndedAt > first);
            }
        }

        [Fact]
        public async Task No_route_serves_the_pipelines_health_and_the_health_checks_say_nothing_of_it()
        {
            LocalStack.RequireAvailable();
            var routes = _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Select(e => e.RoutePattern.RawText ?? "")
                // The Console's fallback, which names health only to exclude it (phase-4c-plan I49).
                .Where(r => !r.StartsWith("{*path:nonfile:regex(^(?!(api|hubs|health)", StringComparison.Ordinal)).ToList();
            var health = routes.Where(r => r.Contains("health", StringComparison.OrdinalIgnoreCase) || r.Contains("pipeline", StringComparison.OrdinalIgnoreCase)
                || r.Contains("stage", StringComparison.OrdinalIgnoreCase)).Order().ToList();
            Assert.Equal(["/health/live", "/health/ready"], health);

            foreach (var token in new[] { null, await LocalStack.TenantAdminTokenAsync(), await LocalStack.ProducerTokenAsync() })
            {
                using var client = _app.Client(token);
                foreach (var path in new[] { "/health/live", "/health/ready" })
                {
                    using var response = await client.GetAsync(path, Ct);
                    var body = await response.Content.ReadAsStringAsync(Ct);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    foreach (var word in new[] { "oldestDue", "failing", "dispatcher", "planner", "lastPass", "lastProgress" })
                    {
                        Assert.DoesNotContain(word, body, StringComparison.OrdinalIgnoreCase);
                    }
                }
                foreach (var path in new[] { "/api/v1/health", "/api/v1/pipeline/health", $"/api/v1/tenants/{ProducerApp.Tenant}/health", "/health/pipeline" })
                {
                    using var response = await client.GetAsync(path, Ct);
                    Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.MethodNotAllowed,
                        $"{path} ({(token is null ? "anonymous" : "token")}): {response.StatusCode}");
                }
            }
        }
    }
}
