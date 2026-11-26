using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The pipeline's health per stage (phase-5 M49-0, waves §4 and §6.3): how long the oldest due, unleased row has waited and how many rows
    /// are backing off after a failed step, across replicas (database); when this replica last ended a pass and last finished a step (memory).
    /// </summary>
    public sealed class PipelineHealthTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private static readonly string[] StageNames = ["planner", "delivery-planner", "digest", "escalation", "dispatcher"];
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
        private readonly StagePasses _passes = new();
        private Application _app = null!;
        private NotificationType _type = null!;
        private Integration _integration = null!;

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = await _database.MigratedAsync();
            _app = Seed.Application("health");
            _type = Seed.Type(_app.Id, "health.alert");
            _integration = Seed.Integration(Tenant);
            db.AddRange(_app, _type, _integration, new ApplicationTenant { ApplicationId = _app.Id, TenantId = Tenant, LinkedAt = Seed.Now });
            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        private Task<PipelineHealth> ReadAsync(bool running = true) => ReadAsync(_database, running);

        private async Task<PipelineHealth> ReadAsync(ScratchDatabase database, bool running)
        {
            await using var db = database.Context();
            return await new NotificationsPipelineHealth(db, _passes, new StagesRunning(running), _clock).ReadAsync(Ct);
        }

        private Delivery Delivery(DateTimeOffset? nextAt, DateTimeOffset? leaseUntil = null, int failures = 0)
        {
            var d = Seed.Delivery(Tenant, _integration.Id);
            (d.NextAt, d.LeaseUntil, d.StepFailures, d.Status) = (nextAt, leaseUntil, failures, nextAt is null ? DeliveryStatus.Delivered : DeliveryStatus.Pending);
            return d;
        }

        /// <summary>A planned notification of its own request, with recipients due as given.</summary>
        private async Task RecipientsAsync(params DateTimeOffset?[] due)
        {
            await using var db = _database.Context();
            var request = Seed.Request(Tenant, _app.Id, _type.Id, Guid.NewGuid().ToString("N"));
            var notification = Seed.Notification(Tenant, request.Id);
            (notification.State, notification.NextAt) = (NotificationState.Planned, null);
            db.AddRange(request, notification);
            for (var i = 0; i < due.Length; i++)
            {
                var r = Seed.Recipient(Tenant, notification.Id);
                db.Entry(r).Property(x => x.NamedAs).CurrentValue = $"address:webhook:https://example.com/{i}";
                (r.Outcome, r.NextAt) = (RecipientOutcome.Planned, due[i]);
                db.Recipients.Add(r);
            }
            await db.SaveChangesAsync(Ct);
        }

        [Fact]
        public async Task Nothing_due_or_failing_is_every_stage_quiet()
        {
            LocalStack.RequireAvailable();
            var health = await ReadAsync(running: false);
            Assert.False(health.Running);
            Assert.Null(health.OldestDueAny);
            Assert.Equal(StageNames.Order(), health.Stages.Keys.Order());
            Assert.All(health.Stages.Values, s => Assert.Equal(new StageHealth(), s));
        }

        [Fact]
        public async Task The_oldest_due_free_rows_are_measured_and_the_rest_left_out()
        {
            LocalStack.RequireAvailable();
            var now = _clock.GetUtcNow();
            await using (var db = _database.Context())
            {
                db.Deliveries.AddRange(
                    Delivery(now.AddMinutes(-3)),                                  // due 3 minutes: the oldest that counts
                    Delivery(now.AddMinutes(-1)),
                    Delivery(now.AddMinutes(-10), leaseUntil: now.AddSeconds(30)), // older, but a worker has it
                    Delivery(now.AddMinutes(5)),                                   // not due yet
                    Delivery(null));                                               // finished
                foreach (var (nextAt, lease) in new (DateTimeOffset?, DateTimeOffset?)[] { (now.AddSeconds(-40), null), (now.AddMinutes(-7), now.AddMinutes(1)), (null, null) })
                {
                    var request = Seed.Request(Tenant, _app.Id, _type.Id, Guid.NewGuid().ToString("N"));
                    var notification = Seed.Notification(Tenant, request.Id);
                    (notification.NextAt, notification.LeaseUntil) = (nextAt, lease);
                    db.AddRange(request, notification);
                }
                await db.SaveChangesAsync(Ct);
            }
            _passes.Record("dispatcher", now.AddSeconds(-2));
            _passes.Progress("dispatcher", now.AddSeconds(-3));

            var health = await ReadAsync();

            Assert.Equal(new StageHealth { LastPassEndedAt = now.AddSeconds(-2), LastProgressAt = now.AddSeconds(-3), OldestDue = TimeSpan.FromMinutes(3) },
                health.Stages["dispatcher"]);
            Assert.Equal(new StageHealth { OldestDue = TimeSpan.FromSeconds(40) }, health.Stages["planner"]);
            Assert.Equal(TimeSpan.FromMinutes(3), health.OldestDueAny);
        }

        /// <summary>A delivery planner that throws on every step, as one whose every write is rejected.</summary>
        private sealed class Broken : IStage<NotificationRecipient>
        {
            public string Name => "delivery-planner";

            public Task ExecuteAsync(StageContext context, NotificationRecipient row, CancellationToken ct) => throw new InvalidOperationException("broken");

            public void GiveUp(StageContext context, NotificationRecipient row, string error) => context.Done(row, null);
        }

        /// <summary>A delivery planner whose steps take 15 seconds each; it notes the progress this replica shows as each step starts.</summary>
        private sealed class Slow(ManualClock clock, StagePasses passes, List<DateTimeOffset?> seen) : IStage<NotificationRecipient>
        {
            public string Name => "delivery-planner";

            public Task ExecuteAsync(StageContext context, NotificationRecipient row, CancellationToken ct)
            {
                seen.Add(passes.LastProgressAt(Name));
                clock.Advance(TimeSpan.FromSeconds(15));
                context.Done(row, null);
                return Task.CompletedTask;
            }

            public void GiveUp(StageContext context, NotificationRecipient row, string error) => context.Done(row, null);
        }

        private StageRunner<NotificationRecipient, TStage> Runner<TStage>(Func<IServiceProvider, TStage> stage) where TStage : class, IStage<NotificationRecipient>
        {
            var services = new ServiceCollection().AddLogging();
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton(_passes);
            services.AddPostgresClaimStore();
            services.AddScoped(stage);
            services.AddSingleton<StageNudges>();
            services.AddSingleton<StageRunner<NotificationRecipient, TStage>>();
            return services.BuildServiceProvider().GetRequiredService<StageRunner<NotificationRecipient, TStage>>();
        }

        [Fact]
        public async Task A_delivery_planner_failing_every_step_shows_failing_while_the_dispatcher_is_idle_and_healthy()
        {
            LocalStack.RequireAvailable();
            var now = _clock.GetUtcNow();
            await RecipientsAsync(now, now, now);

            var pass = await Runner(_ => new Broken()).RunOnceAsync("w1", new StageOptions { Batch = 10 }, Ct);
            Assert.Equal(3, pass.Failed);

            var health = await ReadAsync();
            // Backing off, so not due now: only Failing tells.
            Assert.Equal((3, (TimeSpan?)null), (health.Stages["delivery-planner"].Failing, health.Stages["delivery-planner"].OldestDue));
            Assert.Equal(now, health.Stages["delivery-planner"].LastProgressAt);
            Assert.Equal(new StageHealth(), health.Stages["dispatcher"]);
        }

        [Fact]
        public async Task A_slow_pass_shows_progress_after_every_step()
        {
            LocalStack.RequireAvailable();
            var start = _clock.GetUtcNow();
            await RecipientsAsync(start, start, start);
            var seen = new List<DateTimeOffset?>();

            await Runner(_ => new Slow(_clock, _passes, seen)).RunOnceAsync("w1", new StageOptions { Batch = 10, Lease = TimeSpan.FromMinutes(5) }, Ct);

            Assert.Equal([null, start.AddSeconds(15), start.AddSeconds(30)], seen);
            Assert.Equal((start.AddSeconds(45), start.AddSeconds(45)), (_passes.LastProgressAt("delivery-planner"), _passes.LastPassAt("delivery-planner")));
        }

        [Fact]
        public async Task A_stopped_stages_oldest_due_grows()
        {
            LocalStack.RequireAvailable();
            await using (var db = _database.Context())
            {
                db.Deliveries.Add(Delivery(_clock.GetUtcNow().AddMinutes(-1)));
                await db.SaveChangesAsync(Ct);
            }
            Assert.Equal(TimeSpan.FromMinutes(1), (await ReadAsync()).Stages["dispatcher"].OldestDue);
            _clock.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(TimeSpan.FromMinutes(6), (await ReadAsync()).Stages["dispatcher"].OldestDue);
        }

        [Fact]
        public async Task A_held_escalation_is_neither_due_nor_failing()
        {
            LocalStack.RequireAvailable();
            await using (var db = _database.Context())
            {
                var now = _clock.GetUtcNow();
                var request = Seed.Request(Tenant, _app.Id, _type.Id, "held");
                var notification = Seed.Notification(Tenant, request.Id);
                notification.NextAt = null;
                var correlation = new Correlation { TenantId = Tenant, ApplicationId = _app.Id, Key = "alert:1", CreatedAt = now, LastRequestAt = now, UpdatedAt = now };
                var policy = new EscalationPolicy { TenantId = Tenant, Key = "paging", Steps = "[]", DefinitionHash = "h", UpdatedAt = now };
                db.AddRange(request, notification, correlation, policy);
                db.Escalations.Add(new Escalation
                {
                    TenantId = Tenant, NotificationId = notification.Id, CorrelationId = correlation.Id, PolicyId = policy.Id, State = EscalationState.Running,
                    NextAt = null, HeldAt = now.AddMinutes(-30), CreatedAt = now.AddHours(-1), UpdatedAt = now,
                });
                await db.SaveChangesAsync(Ct);
            }
            Assert.Equal(new StageHealth(), (await ReadAsync()).Stages["escalation"]);
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
            var integration = Seed.Integration(Tenant, "plans-hook");
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

        /// <summary>
        /// The failing count reads only the rows backing off, through the new partial index (M49-0 migration), with sequential scans allowed over a
        /// few thousand analysed rows of which a handful failed their last step.
        /// </summary>
        [Fact]
        public async Task The_failing_count_reads_its_partial_index()
        {
            LocalStack.RequireAvailable();
            var now = _clock.GetUtcNow();
            await using var db = _database.Context();
            for (var i = 0; i < 3000; i++)
            {
                db.Deliveries.Add(Delivery(i % 10 == 0 ? now.AddMinutes(1 + i) : null, failures: i % 500 == 0 ? 2 : 0));
            }
            await db.SaveChangesAsync(Ct);
            await db.Database.ExecuteSqlRawAsync("ANALYZE notifications.deliveries", Ct);
            var sql = db.Deliveries.Where(r => r.StepFailures > 0).Take(StageHealth.MaxFailing).ToQueryString();
            var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
            await connection.OpenAsync(Ct);
            var lines = sql.Split('\n');
            await using var explain = new Npgsql.NpgsqlCommand(
                "EXPLAIN (FORMAT JSON) SELECT count(*) FROM (" + string.Join('\n', lines.Where(l => !l.StartsWith("--", StringComparison.Ordinal))) + ") AS f", connection);
            foreach (var declared in lines.Where(l => l.StartsWith("-- @", StringComparison.Ordinal)))
            {
                explain.Parameters.AddWithValue(declared[4..declared.IndexOf('=')], StageHealth.MaxFailing);
            }
            var plan = (string)(await explain.ExecuteScalarAsync(Ct))!;
            Assert.Contains("\"Index Name\": \"ix_deliveries_failing\"", plan);
            Assert.DoesNotContain("\"Seq Scan\"", plan);
        }
    }
}
