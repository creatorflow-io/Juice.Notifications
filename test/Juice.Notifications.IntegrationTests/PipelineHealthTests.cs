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

        /// <summary>
        /// A row that failed a step and was then stopped by a resolve (next_at null, its failure count kept) isn't failing any more (review of #4,
        /// round 2): otherwise each one would count until retention removed it, and enough of them would hold the dead-man's alarm on.
        /// </summary>
        [Fact]
        public async Task Rows_that_failed_then_were_stopped_by_a_resolve_arent_failing()
        {
            LocalStack.RequireAvailable();
            var now = _clock.GetUtcNow();
            Guid correlationId;
            await using (var db = _database.Context())
            {
                var correlation = new Correlation { TenantId = Tenant, ApplicationId = _app.Id, Key = "alert:42", CreatedAt = now, LastRequestAt = now, UpdatedAt = now };
                var request = Seed.Request(Tenant, _app.Id, _type.Id, "failing");
                db.Entry(request).Property(r => r.CorrelationId).CurrentValue = correlation.Id;
                db.Entry(request).Property(r => r.CorrelationKey).CurrentValue = correlation.Key;
                var notification = Seed.Notification(Tenant, request.Id);
                (notification.State, notification.NextAt, notification.StepFailures) = (NotificationState.Running, now.AddMinutes(2), 1);
                var recipient = Seed.Recipient(Tenant, notification.Id);
                (recipient.Outcome, recipient.NextAt, recipient.StepFailures) = (RecipientOutcome.Planned, now.AddMinutes(2), 2);
                var delivery = Delivery(now.AddMinutes(2), failures: 3);
                delivery.Status = DeliveryStatus.Retrying;
                db.AddRange(correlation, request, notification, recipient, delivery, Seed.Item(Tenant, delivery.Id, recipient.Id, request.Id));
                await db.SaveChangesAsync(Ct);
                correlationId = correlation.Id;
            }
            var before = await ReadAsync();
            Assert.Equal((1, 1, 1), (before.Stages["planner"].Failing, before.Stages["delivery-planner"].Failing, before.Stages["dispatcher"].Failing));

            await using (var db = _database.Context())
            {
                await db.Correlations.Where(c => c.Id == correlationId).ExecuteUpdateAsync(s => s.SetProperty(c => c.State, CorrelationState.Resolved), Ct);
                await Correlations.CorrelationLifecycle.StopAsync(db, Tenant, correlationId, now, Ct);
            }

            var after = await ReadAsync();
            Assert.All(after.Stages.Values, s => Assert.Equal(0, s.Failing));
            await using var check = _database.Context();
            Assert.True(await check.Deliveries.AnyAsync(d => d.StepFailures == 3 && d.NextAt == null, Ct)); // the count is kept; it isn't failing
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

        // ─── Sends (M49-0b: "not getting out") ───────────────────────────────

        /// <summary>A delivery made <paramref name="age"/> ago, in <paramref name="status"/>, backing off (due later) when in flight.</summary>
        private async Task<Delivery> SendingAsync(TimeSpan age, DeliveryStatus status = DeliveryStatus.Retrying, params (TimeSpan Ago, AttemptOutcome Outcome)[] attempts)
        {
            await using var db = _database.Context();
            var d = Seed.Delivery(Tenant, _integration.Id);
            var inFlight = status is DeliveryStatus.Pending or DeliveryStatus.Sending or DeliveryStatus.Retrying;
            (d.Status, d.NextAt) = (status, inFlight ? _clock.GetUtcNow().AddMinutes(5) : null);
            db.Deliveries.Add(d);
            db.Entry(d).Property(x => x.CreatedAt).CurrentValue = _clock.GetUtcNow() - age;
            for (var i = 0; i < attempts.Length; i++)
            {
                db.Attempts.Add(new DeliveryAttempt
                {
                    TenantId = Tenant, DeliveryId = d.Id, Number = i + 1, IntegrationId = _integration.Id, At = _clock.GetUtcNow() - attempts[i].Ago,
                    Outcome = attempts[i].Outcome,
                });
            }
            await db.SaveChangesAsync(Ct);
            return d;
        }

        [Fact]
        public async Task A_provider_refusing_every_send_shows_no_success_and_a_growing_oldest_undelivered_while_no_stage_fails()
        {
            LocalStack.RequireAvailable();
            var failures = Enumerable.Range(1, 6).Select(i => (TimeSpan.FromMinutes(i), AttemptOutcome.RetryableFailure)).ToArray();
            await SendingAsync(TimeSpan.FromMinutes(8), DeliveryStatus.Retrying, failures);
            await SendingAsync(TimeSpan.FromMinutes(2), DeliveryStatus.Pending);

            var health = await ReadAsync();
            Assert.Equal((SendHealth.DefaultWindow, 0, 6, (TimeSpan?)TimeSpan.FromMinutes(8)),
                (health.Sends.Window, health.Sends.Succeeded, health.Sends.Failed, health.Sends.OldestUndelivered));
            // Backing off without failing a step: no stage shows it.
            Assert.Equal((0, (TimeSpan?)null), (health.Stages["dispatcher"].Failing, health.Stages["dispatcher"].OldestDue));

            _clock.Advance(TimeSpan.FromMinutes(20));
            health = await ReadAsync();
            Assert.Equal(TimeSpan.FromMinutes(28), health.Sends.OldestUndelivered);
            // The failures have left the window.
            Assert.Equal((0, 0), (health.Sends.Succeeded, health.Sends.Failed));
        }

        [Fact]
        public async Task One_success_in_the_window_is_counted_and_every_failed_outcome_is()
        {
            LocalStack.RequireAvailable();
            await SendingAsync(TimeSpan.FromMinutes(30), DeliveryStatus.Failed,
                (TimeSpan.FromMinutes(3), AttemptOutcome.RetryableFailure), (TimeSpan.FromMinutes(2), AttemptOutcome.PermanentFailure),
                (TimeSpan.FromMinutes(1), AttemptOutcome.Unknown));
            await SendingAsync(TimeSpan.FromMinutes(5), DeliveryStatus.Accepted, (TimeSpan.FromMinutes(4), AttemptOutcome.Accepted));
            await SendingAsync(TimeSpan.FromMinutes(5), DeliveryStatus.Delivered, (TimeSpan.FromMinutes(16), AttemptOutcome.Delivered));

            var sends = (await ReadAsync()).Sends;
            // The Delivered attempt is older than the window; nothing is in flight.
            Assert.Equal((1, 3, (TimeSpan?)null), (sends.Succeeded, sends.Failed, sends.OldestUndelivered));
        }

        [Fact]
        public async Task A_dry_runs_deliveries_count_in_neither_signal()
        {
            LocalStack.RequireAvailable();
            await SendingAsync(TimeSpan.FromHours(1), DeliveryStatus.WouldSend);
            var sends = (await ReadAsync()).Sends;
            Assert.Equal((0, 0, (TimeSpan?)null), (sends.Succeeded, sends.Failed, sends.OldestUndelivered));
        }

        /// <summary>The send reads use their indexes (M49-0b migration) over a few thousand analysed rows, most of them finished and old.</summary>
        [Fact]
        public async Task The_send_reads_use_their_indexes()
        {
            LocalStack.RequireAvailable();
            var now = _clock.GetUtcNow();
            await using (var db = _database.Context())
            {
                for (var i = 0; i < 3000; i++)
                {
                    var d = Seed.Delivery(Tenant, _integration.Id);
                    (d.Status, d.NextAt) = i % 100 == 0 ? (DeliveryStatus.Retrying, now.AddMinutes(1)) : (DeliveryStatus.Delivered, (DateTimeOffset?)null);
                    d.TenantHeld = i % 100 == 50; // a few held rows, due and past their hold: the lapsed-hold read has rows to choose from
                    if (d.TenantHeld)
                    {
                        (d.Status, d.NextAt) = (DeliveryStatus.Pending, now.AddMinutes(-30));
                    }
                    db.Deliveries.Add(d);
                    db.Attempts.Add(new DeliveryAttempt
                    {
                        TenantId = Tenant, DeliveryId = d.Id, Number = 1, IntegrationId = _integration.Id, At = now.AddHours(-1 - i), Outcome = AttemptOutcome.Delivered,
                    });
                }
                await db.SaveChangesAsync(Ct);
                await db.Database.ExecuteSqlRawAsync("ANALYZE notifications.deliveries; ANALYZE notifications.delivery_attempts", Ct);
            }
            var since = now - SendHealth.DefaultWindow;
            Assert.Contains("\"Index Name\": \"ix_delivery_attempts_at\"",
                await PlanAsync(db => db.Attempts.Where(a => a.At >= since && a.Outcome == AttemptOutcome.Delivered).Take(SendHealth.MaxCount), now, count: true));
            // The health read's own query: its statuses are literals, the index's filter, so no plan (generic or custom) has a parameter to doubt.
            await using (var context = _database.Context())
            {
                var sql = Juice.Notifications.Stages.NotificationsPipelineHealth.OldestInFlight(context.Deliveries).Take(1).ToQueryString();
                Assert.DoesNotContain("ANY (", sql);
                Assert.Contains("IN ('Pending', 'Sending', 'Retrying')", sql);
                Assert.Contains("NOT (d.tenant_held)", sql);
                var held = Juice.Notifications.Stages.NotificationsPipelineHealth.OldestLapsedHold(context.Deliveries, now).Take(1).ToQueryString();
                Assert.DoesNotContain("ANY (", held);
                Assert.Contains("IN ('Pending', 'Sending', 'Retrying')", held);
                Assert.Contains("d.tenant_held", held);
            }
            Assert.Contains("\"Index Name\": \"ix_deliveries_in_flight\"",
                await PlanAsync(db => Juice.Notifications.Stages.NotificationsPipelineHealth.OldestInFlight(db.Deliveries).Take(1), now, count: false));
            // The lapsed-hold read: through its own partial index, in a custom and in a generic plan.
            Assert.Contains("\"Index Name\": \"ix_deliveries_tenant_held\"",
                await PlanAsync(db => Juice.Notifications.Stages.NotificationsPipelineHealth.OldestLapsedHold(db.Deliveries, now - TimeSpan.FromMinutes(5)).Take(1), now, count: false));
            Assert.Contains("\"Index Name\": \"ix_deliveries_tenant_held\"",
                await GenericPlanAsync(db => Juice.Notifications.Stages.NotificationsPipelineHealth.OldestLapsedHold(db.Deliveries, now - TimeSpan.FromMinutes(5)).Take(1)));
            // A generic plan (what a prepared or auto-prepared statement settles on) still uses the index for the health read's query...
            Assert.Contains("\"Index Name\": \"ix_deliveries_in_flight\"",
                await GenericPlanAsync(db => Juice.Notifications.Stages.NotificationsPipelineHealth.OldestInFlight(db.Deliveries).Take(1)));
            // ...and can't for the same read with its statuses as a parameter (the control: this check would catch the parameter coming back).
            DeliveryStatus[] parameter = [DeliveryStatus.Pending, DeliveryStatus.Sending, DeliveryStatus.Retrying];
            Assert.DoesNotContain("\"Index Name\": \"ix_deliveries_in_flight\"",
                await GenericPlanAsync(db => db.Deliveries.Where(d => parameter.Contains(d.Status)).OrderBy(d => d.CreatedAt).Select(d => (DateTimeOffset?)d.CreatedAt).Take(1)));
        }

        /// <summary>
        /// The generic plan of a query (<c>PREPARE</c>, <c>plan_cache_mode = force_generic_plan</c>): the plan that doesn't know its parameters'
        /// values, so a partial index is used only when its filter holds for any of them.
        /// </summary>
        private async Task<string> GenericPlanAsync<T>(Func<NotificationsDbContext, IQueryable<T>> query)
        {
            await using var db = _database.Context();
            var lines = query(db).ToQueryString().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var body = string.Join('\n', lines.Where(l => !l.StartsWith("--", StringComparison.Ordinal)));
            var types = new List<string>();
            var values = new List<string>();
            foreach (var declared in lines.Where(l => l.StartsWith("-- @", StringComparison.Ordinal)))
            {
                var name = declared[4..declared.IndexOf('=')];
                var array = declared.Contains('{', StringComparison.Ordinal);
                var timestamp = declared.Contains("DateTime", StringComparison.Ordinal);
                types.Add(array ? "text[]" : timestamp ? "timestamptz" : "integer");
                values.Add(array ? "'{Pending,Sending,Retrying}'" : timestamp ? "'2026-11-26T08:55:00Z'" : "1");
                body = System.Text.RegularExpressions.Regex.Replace(body, $@"@{name}\b", $"${types.Count}");
            }
            var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
            await connection.OpenAsync(Ct);
            var signature = types.Count == 0 ? "" : $"({string.Join(", ", types)})";
            var arguments = values.Count == 0 ? "" : $"({string.Join(", ", values)})";
            await using (var prepare = new Npgsql.NpgsqlCommand($"SET plan_cache_mode = force_generic_plan; PREPARE generic{signature} AS {body}", connection))
            {
                await prepare.ExecuteNonQueryAsync(Ct);
            }
            await using var explain = new Npgsql.NpgsqlCommand($"EXPLAIN (FORMAT JSON) EXECUTE generic{arguments}", connection);
            return (string)(await explain.ExecuteScalarAsync(Ct))!;
        }

        private async Task<string> PlanAsync<T>(Func<NotificationsDbContext, IQueryable<T>> query, DateTimeOffset now, bool count)
        {
            await using var db = _database.Context();
            var sql = query(db).ToQueryString();
            var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
            await connection.OpenAsync(Ct);
            var lines = sql.Split('\n');
            var body = string.Join('\n', lines.Where(l => !l.StartsWith("--", StringComparison.Ordinal)));
            await using var explain = new Npgsql.NpgsqlCommand("EXPLAIN (FORMAT JSON) " + (count ? $"SELECT count(*) FROM ({body}) AS c" : body), connection);
            foreach (var declared in lines.Where(l => l.StartsWith("-- @", StringComparison.Ordinal)))
            {
                var name = declared[4..declared.IndexOf('=')];
                explain.Parameters.AddWithValue(name, declared.Contains("timestamp", StringComparison.Ordinal) || declared.Contains("DateTime", StringComparison.Ordinal)
                    ? now - SendHealth.DefaultWindow
                    : declared.Contains('{', StringComparison.Ordinal) ? new[] { "Pending", "Sending", "Retrying" }
                    : (object)SendHealth.MaxCount);
            }
            return (string)(await explain.ExecuteScalarAsync(Ct))!;
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
            var sql = db.Deliveries.Where(r => r.StepFailures > 0 && r.NextAt != null).Take(StageHealth.MaxFailing).ToQueryString();
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

        // ─── QA (M49-0b) ───────────────────────────────────────────────────

        /// <summary>
        /// Through the real dispatcher and a provider that refuses every send: each pass adds a failed attempt, nothing succeeds, the oldest
        /// undelivered grows with the clock, and no stage shows it (the delivery backs off without failing a step). One success then counts in
        /// the window, and the delivery is no longer in flight.
        /// </summary>
        [Fact]
        public async Task An_always_failing_provider_through_the_real_dispatcher_and_one_success_after()
        {
            LocalStack.RequireAvailable();
            var provider = new ScriptedProvider(Channels.Webhook, "fake");
            var options = new StageOptions { Batch = 10, Lease = TimeSpan.FromSeconds(60) };
            var services = new ServiceCollection().AddLogging();
            services.AddDbContext<NotificationsDbContext>(o => NotificationsDbContext.Configure(o, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddPostgresClaimStore();
            services.Configure<Providers.DeliveryOptions>(o => (o.MaxAttempts, o.RetryBase, o.RetryMax) = (50, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)));
            services.AddSingleton<INotificationProvider>(provider);
            services.AddBuiltInChannels();
            People.ContactEncryptionRegistration.AddContactEncryption(services);
            services.AddStage<Delivery, Delivering.DispatcherStage>(options, runWorkers: false);
            await using var sp = services.BuildServiceProvider();
            var runner = sp.GetRequiredService<StageRunner<Delivery, Delivering.DispatcherStage>>();

            var start = _clock.GetUtcNow();
            await using (var db = _database.Context())
            {
                var fake = new Integration { TenantId = Tenant, Key = "fake-hook", Channel = Channels.Webhook, Kind = "fake", Settings = "{}", DefinitionHash = "d", UpdatedAt = Seed.Now };
                // A delivery as the planner leaves one: its request, notification and recipient, and the item tying them.
                var request = Seed.Request(Tenant, _app.Id, _type.Id, Guid.NewGuid().ToString("N"));
                var notification = Seed.Notification(Tenant, request.Id);
                (notification.State, notification.NextAt) = (NotificationState.Planned, null);
                var recipient = Seed.Recipient(Tenant, notification.Id);
                var d = new Delivery
                {
                    TenantId = Tenant, Channel = Channels.Webhook, IntegrationId = fake.Id, DestinationKey = "ext:webhook:https://example.com/hook",
                    DestinationSnapshot = """{"address":"https://example.com/hook"}""", Locale = "en", RendererVersion = "test",
                    Content = """{"subject":"s","text":"t"}""", CreatedAt = start, Status = DeliveryStatus.Pending, NextAt = start,
                };
                db.AddRange(fake, request, notification, recipient, d, new DeliveryItem
                {
                    TenantId = Tenant, DeliveryId = d.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = d.DestinationKey, Channel = d.Channel,
                });
                db.Entry(d).Property(x => x.CreatedAt).CurrentValue = start;
                await db.SaveChangesAsync(Ct);
            }
            for (var pass = 1; pass <= 4; pass++)
            {
                provider.Then(SendResult.Retry(FailureCategory.Provider, "503 from the provider", 503));
                Assert.Equal(1, (await runner.RunOnceAsync("dispatcher", options, Ct)).Claimed);
                var health = await ReadAsync();
                Assert.Equal((0, pass, (TimeSpan?)(_clock.GetUtcNow() - start)), (health.Sends.Succeeded, health.Sends.Failed, health.Sends.OldestUndelivered));
                Assert.Equal(0, health.Stages["dispatcher"].Failing);
                // To just past its backoff (jittered), so the next pass takes it.
                await using var db = _database.Context();
                var next = (await db.Deliveries.AsNoTracking().SingleAsync(x => x.TenantId == Tenant && x.IntegrationId != _integration.Id, Ct)).NextAt!.Value;
                _clock.Advance(next - _clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
            }

            provider.Then(SendResult.Delivered(200));
            Assert.Equal(1, (await runner.RunOnceAsync("dispatcher", options, Ct)).Claimed);
            var after = (await ReadAsync()).Sends;
            Assert.Equal((1, 4, (TimeSpan?)null), (after.Succeeded, after.Failed, after.OldestUndelivered));
        }

        /// <summary>The window's edge: an attempt exactly 15 minutes old counts; one a microsecond older doesn't.</summary>
        [Fact]
        public async Task An_attempt_exactly_at_the_window_edge_counts_and_one_just_before_it_does_not()
        {
            LocalStack.RequireAvailable();
            var edge = SendHealth.DefaultWindow;
            await SendingAsync(TimeSpan.FromMinutes(30), DeliveryStatus.Failed,
                (edge, AttemptOutcome.RetryableFailure), (edge + TimeSpan.FromTicks(10), AttemptOutcome.PermanentFailure));
            await SendingAsync(TimeSpan.FromMinutes(30), DeliveryStatus.Delivered,
                (edge, AttemptOutcome.Delivered), (edge + TimeSpan.FromTicks(10), AttemptOutcome.Accepted));
            var sends = (await ReadAsync()).Sends;
            Assert.Equal((1, 1), (sends.Succeeded, sends.Failed));
        }

        /// <summary>Each count stops at <see cref="SendHealth.MaxCount"/> (10,000), however many attempts the window holds.</summary>
        [Fact]
        public async Task The_counts_stop_at_ten_thousand()
        {
            LocalStack.RequireAvailable();
            var d = await SendingAsync(TimeSpan.FromMinutes(30), DeliveryStatus.Failed);
            await using (var db = _database.Context())
            {
                // 10,050 of each in the window (numbered apart), straight in SQL.
                var since = _clock.GetUtcNow().AddMinutes(-5);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO notifications.delivery_attempts (tenant_id, id, delivery_id, number, integration_id, at, outcome, duration_ms)
                    SELECT {Tenant}, gen_random_uuid(), {d.Id}, n, {_integration.Id}, {since}, CASE WHEN n % 2 = 0 THEN 'Delivered' ELSE 'RetryableFailure' END, 0
                    FROM generate_series(1, 20100) AS n
                    """, Ct);
            }
            var sends = (await ReadAsync()).Sends;
            Assert.Equal((SendHealth.MaxCount, SendHealth.MaxCount), (sends.Succeeded, sends.Failed));
        }

        /// <summary>
        /// QA (M49-0b) cost, run on demand (NOTIFICATIONS_QA_COST=1; seeding takes a minute): 2,000,000 deliveries (1 % in flight) and 3,000,000
        /// attempts over 30 days, 60,000 of them in the last 15 minutes. The whole read, stages included, stays well under a second, and the
        /// send reads' plans (EXPLAIN ANALYZE, written beside the tests as send-health-plans.txt) use their indexes.
        /// </summary>
        [Fact]
        public async Task The_send_reads_stay_cheap_on_large_tables()
        {
            LocalStack.RequireAvailable();
            Assert.SkipUnless(Environment.GetEnvironmentVariable("NOTIFICATIONS_QA_COST") == "1", "On demand: NOTIFICATIONS_QA_COST=1.");
            const string Empty = "{}";
            var now = _clock.GetUtcNow();
            await using (var db = _database.Context())
            {
                db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO notifications.deliveries (tenant_id, id, channel, integration_id, destination_key, destination_snapshot, template_version, locale,
                        renderer_version, content, priority, mandatory, status, attempts, created_at, version, next_at)
                    SELECT {Tenant}, gen_random_uuid(), 'webhook', {_integration.Id}, 'ext:webhook:https://example.com/' || n, {Empty}, 0, 'en', 'test', {Empty}, 1, false,
                        CASE WHEN n % 100 = 0 THEN 'Retrying' ELSE 'Delivered' END, 1, {now} - (n * interval '1 second'), 0,
                        CASE WHEN n % 100 = 0 THEN {now} + interval '5 minutes' END
                    FROM generate_series(1, 2000000) AS n
                    """, Ct);
                var sample = await db.Deliveries.AsNoTracking().Select(d => d.Id).FirstAsync(Ct);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO notifications.delivery_attempts (tenant_id, id, delivery_id, number, integration_id, at, outcome, duration_ms)
                    SELECT {Tenant}, gen_random_uuid(), {sample}, n, {_integration.Id},
                        CASE WHEN n <= 60000 THEN {now} - (n * interval '14 milliseconds') ELSE {now} - interval '20 minutes' - (n * interval '860 milliseconds') END,
                        CASE WHEN n % 3 = 0 THEN 'RetryableFailure' ELSE 'Delivered' END, 0
                    FROM generate_series(1, 3000000) AS n
                    """, Ct);
                await db.Database.ExecuteSqlRawAsync("ANALYZE notifications.deliveries; ANALYZE notifications.delivery_attempts", Ct);
            }

            await ReadAsync(); // warm
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var health = await ReadAsync();
            var elapsed = watch.Elapsed;
            Assert.Equal((SendHealth.MaxCount, SendHealth.MaxCount), (health.Sends.Succeeded, health.Sends.Failed));
            Assert.Equal(TimeSpan.FromSeconds(2000000 - 2000000 % 100), health.Sends.OldestUndelivered);

            var since = now - SendHealth.DefaultWindow;
            AttemptOutcome[] succeeded = [AttemptOutcome.Delivered, AttemptOutcome.Accepted];
            DeliveryStatus[] inFlight = [DeliveryStatus.Pending, DeliveryStatus.Sending, DeliveryStatus.Retrying];
            var plans = new System.Text.StringBuilder($"read: {elapsed.TotalMilliseconds:0} ms\n\n");
            foreach (var (name, sql) in new[]
            {
                ("succeeded", $"SELECT count(*) FROM (SELECT 1 FROM notifications.delivery_attempts d WHERE d.at >= '{since:O}' AND d.outcome IN ('Delivered','Accepted') LIMIT 10000) c"),
                ("in flight", "SELECT d.created_at FROM notifications.deliveries d WHERE d.status IN ('Pending','Sending','Retrying') ORDER BY d.created_at LIMIT 1"),
                ("in flight (as a parameter array)", "PREPARE f(text[]) AS SELECT d.created_at FROM notifications.deliveries d WHERE d.status = ANY($1) ORDER BY d.created_at LIMIT 1"),
            })
            {
                await using var db = _database.Context();
                var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
                await connection.OpenAsync(Ct);
                string text;
                if (sql.StartsWith("PREPARE", StringComparison.Ordinal))
                {
                    await using (var prepare = new Npgsql.NpgsqlCommand(sql, connection)) { await prepare.ExecuteNonQueryAsync(Ct); }
                    await using (var generic = new Npgsql.NpgsqlCommand("SET plan_cache_mode = force_generic_plan", connection)) { await generic.ExecuteNonQueryAsync(Ct); }
                    await using var explain = new Npgsql.NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) EXECUTE f('{Pending,Sending,Retrying}')", connection);
                    text = await ReadPlanAsync(explain);
                }
                else
                {
                    await using var explain = new Npgsql.NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, connection);
                    text = await ReadPlanAsync(explain);
                }
                plans.Append($"== {name}\n{text}\n");
            }
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "send-health-plans.txt"), plans.ToString(), Ct);
            Assert.True(elapsed < TimeSpan.FromSeconds(1), $"the read took {elapsed.TotalMilliseconds:0} ms");

            static async Task<string> ReadPlanAsync(Npgsql.NpgsqlCommand command)
            {
                var lines = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(Ct);
                while (await reader.ReadAsync(Ct))
                {
                    lines.Add(reader.GetString(0));
                }
                return string.Join('\n', lines);
            }
        }
    }
}
