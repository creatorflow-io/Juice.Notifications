using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Configuration;
using Juice.Notifications.Embedded;
using Juice.Notifications.Types;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Juice.Notifications.IntegrationTests.DeliveryTests;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Embedded hosting (phase-5-plan M43, I57–I63, I74): the pipeline in a minimal host of its own, with its own <see cref="DbContext"/> on the
    /// same database, as Observability will run it. A request is written in the host's transaction, so it commits or rolls back with the host's
    /// change; a committed one is delivered even when the host stops before planning it; the same key twice is one request; and the container
    /// holds nothing of the service's.
    /// </summary>
    public sealed class EmbeddedHostTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000e0b01");
        private const string Type = "host.thing.happened";
        private readonly ScratchDatabase _database = new();

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            // The host's own schema and table, beside the pipeline's (I60: neither references the other).
            await using var db = new HostDbContext(new DbContextOptionsBuilder<HostDbContext>().UseNpgsql(_database.ConnectionString).Options);
            await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA host; CREATE TABLE host.things (id uuid PRIMARY KEY, name text NOT NULL);");
        }

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        [Fact]
        public async Task A_rolled_back_host_transaction_leaves_no_request()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);

            await host.InTransactionAsync(async (db, client) =>
            {
                db.Things.Add(new Thing { Id = Guid.CreateVersion7(), Name = "rolled back" });
                await db.SaveChangesAsync(Ct);
                var accepted = await client.SendAsync(Notification("rollback-1"), Ct);
                Assert.False(accepted.Duplicate);
                return false; // roll back
            });

            await using var notifications = _database.Context();
            Assert.Empty(await notifications.Requests.ToListAsync(Ct));
            Assert.Empty(await notifications.Notifications.ToListAsync(Ct));
            await using var check = host.HostContext();
            Assert.Empty(await check.Things.ToListAsync(Ct));
        }

        [Fact]
        public async Task A_committed_request_is_delivered_after_the_host_stops_before_planning_it()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            Guid id;
            await using (var first = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false, receiver))
            {
                id = await first.InTransactionAsync(async (db, client) =>
                {
                    db.Things.Add(new Thing { Id = Guid.CreateVersion7(), Name = "committed" });
                    await db.SaveChangesAsync(Ct);
                    return (await client.SendAsync(Notification("commit-1"), Ct)).Id;
                });
                // Told to wake the planner once committed (its stages are off here).
                Assert.Equal(1, first.CommittedActions);
            }
            await using (var db = _database.Context())
            {
                Assert.True(await db.Requests.AnyAsync(r => r.Id == id, Ct), "the request committed with the host's change");
                Assert.Empty(await db.Deliveries.ToListAsync(Ct));
            }
            Assert.Empty(receiver.Requests);

            await using var second = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: true, receiver);
            for (var i = 0; i < 300 && !receiver.Requests.Any(); i++)
            {
                await Task.Delay(100, Ct);
            }
            if (receiver.Requests.IsEmpty)
            {
                // Where it stopped, for the failure's message.
                await using var db = _database.Context();
                var notification = await db.Notifications.AsNoTracking().SingleAsync(n => n.RequestId == id, Ct);
                var recipients = await db.Recipients.AsNoTracking().Where(r => r.NotificationId == notification.Id).Select(r => r.Outcome).ToListAsync(Ct);
                var deliveries = await db.Deliveries.AsNoTracking().Select(d => d.Status).ToListAsync(Ct);
                var allowed = await db.Allowlist.AsNoTracking().Select(a => a.Destination).ToListAsync(Ct);
                var attempts = await db.Attempts.AsNoTracking().Select(a => a.Outcome + " " + a.StatusCode + " " + a.Error).ToListAsync(Ct);
                Assert.Fail($"Not delivered: notification {notification.State}, recipients [{string.Join(", ", recipients)}], deliveries [{string.Join(", ", deliveries)}], attempts [{string.Join("; ", attempts)}], allowlist [{string.Join(", ", allowed)}], receiver {receiver.Url}");
            }
            var received = Assert.Single(receiver.Requests);
            Assert.Contains("\"n\":1", received.Body);
        }

        [Fact]
        public async Task The_same_key_twice_is_one_request_and_no_new_work()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            var first = await host.InTransactionAsync(async (_, client) => await client.SendAsync(Notification("dup-1"), Ct));
            var again = await host.InTransactionAsync(async (_, client) => await client.SendAsync(Notification("dup-1") with { Data = Data(2) }, Ct));

            Assert.False(first.Duplicate);
            Assert.True(again.Duplicate);
            Assert.Equal(first.Id, again.Id);
            await using var db = _database.Context();
            Assert.Single(await db.Requests.ToListAsync(Ct));
            Assert.Single(await db.Notifications.ToListAsync(Ct));
            Assert.Equal(1, host.CommittedActions); // the duplicate wakes nothing
        }

        [Fact]
        public async Task Without_a_transaction_a_send_is_refused_and_people_are_no_recipients_here()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var client = scope.ServiceProvider.GetRequiredService<INotificationClient>();
                var refused = await Assert.ThrowsAsync<NotificationRefusedException>(() => client.SendAsync(Notification("no-tx"), Ct));
                Assert.Equal(ErrorCodes.NoTransaction, refused.Code);
            }

            // Embedded has no people (design §4.2): a user is refused at intake as the HTTP API refuses it, and nothing is written.
            var person = await Assert.ThrowsAsync<NotificationRefusedException>(() => host.InTransactionAsync(async (_, client) =>
                await client.SendAsync(Notification("person") with { Recipients = [new NotificationRecipientSpec(RecipientKinds.User, Id: "ann")] }, Ct)));
            Assert.Contains(person.Errors, e => e.Pointer.StartsWith("/recipients/0", StringComparison.Ordinal));
            await using var db = _database.Context();
            Assert.Empty(await db.Requests.ToListAsync(Ct));
        }

        [Fact]
        public async Task A_correlation_is_resolved_in_the_host_transaction_and_its_rollback_keeps_it_open()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            await host.InTransactionAsync(async (_, client) => await client.SendAsync(Notification("corr-1") with { CorrelationKey = "alert:1" }, Ct));

            // Resolved inside the host's transaction, which then rolls back: still open.
            await host.InTransactionAsync(async (_, client) =>
            {
                Assert.Equal(CorrelationState.Resolved, await client.ResolveAsync("alert:1", "fixed", "host", Ct));
                return false;
            });
            Assert.Equal(CorrelationState.Open, await StateAsync());

            var acknowledged = await host.InTransactionAsync(async (_, client) => await client.AcknowledgeAsync("alert:1", "ann", Ct));
            Assert.Equal(CorrelationState.Acknowledged, acknowledged);
            Assert.Equal(CorrelationState.Resolved, await host.InTransactionAsync(async (_, client) => await client.ResolveAsync("alert:1", "fixed", "host", Ct)));
            Assert.Equal(CorrelationState.Resolved, await StateAsync());

            async Task<CorrelationState> StateAsync()
            {
                await using var db = _database.Context();
                return (await db.Correlations.SingleAsync(c => c.Key == "alert:1", Ct)).State;
            }
        }

        /// <summary>
        /// A correlation of the host's with an escalation parked by a hold (p5-d3), as the escalation stage leaves one: Running, not due, held since
        /// now. The stages are off in these hosts, so the row is written as the stage would write it.
        /// </summary>
        private async Task<Guid> ParkedAsync(EmbeddedHost host, string key)
        {
            var request = await host.InTransactionAsync(async (_, client) => (await client.SendAsync(Notification(key) with { CorrelationKey = key }, Ct)).Id);
            Assert.True((await host.InTransactionAsync(async (_, client) => await client.HoldAsync(key, "silence:1", "host", Ct))).Changed);
            await using var db = _database.Context();
            var notification = await db.Notifications.AsNoTracking().SingleAsync(n => n.RequestId == request, Ct);
            var correlation = await db.Correlations.AsNoTracking().SingleAsync(c => c.Key == key, Ct);
            var escalation = new Escalation
            {
                TenantId = Tenant, NotificationId = notification.Id, CorrelationId = correlation.Id, PolicyId = Guid.CreateVersion7(), State = EscalationState.Running,
                HeldAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Escalations.Add(escalation);
            await db.SaveChangesAsync(Ct);
            return escalation.Id;
        }

        private async Task<(Correlation Correlation, Escalation? Escalation)> HoldStateAsync(string key)
        {
            await using var db = _database.Context();
            var correlation = await db.Correlations.AsNoTracking().SingleAsync(c => c.Key == key, Ct);
            return (correlation, await db.Escalations.AsNoTracking().SingleOrDefaultAsync(e => e.CorrelationId == correlation.Id, Ct));
        }

        [Fact]
        public async Task A_hold_in_a_rolled_back_host_transaction_never_happened()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            await host.InTransactionAsync(async (_, client) => await client.SendAsync(Notification("hold-rb") with { CorrelationKey = "alert:rb" }, Ct));

            await host.InTransactionAsync(async (_, client) =>
            {
                var hold = await client.HoldAsync("alert:rb", "silence:1", "host", Ct);
                Assert.Equal((true, true, "silence:1", "host", CorrelationState.Open), (hold.Held, hold.Changed, hold.Reason, hold.HeldBy, hold.State));
                return false;
            });
            Assert.Null((await HoldStateAsync("alert:rb")).Correlation.HeldAt);
        }

        [Fact]
        public async Task A_committed_release_resumes_the_parked_escalation_and_wakes_its_stage_only_once_the_host_commits()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            await ParkedAsync(host, "alert:rel");

            // Rolled back: still held, still parked; the wake asked for never runs.
            var before = host.CommittedActions;
            await host.InTransactionAsync(async (_, client) =>
            {
                var released = await client.ReleaseAsync("alert:rel", "host", Ct);
                Assert.Equal((false, true, 1), (released.Held, released.Changed, released.Resumed));
                return false;
            });
            var (held, parked) = await HoldStateAsync("alert:rel");
            Assert.NotNull(held.HeldAt);
            Assert.Equal(((DateTimeOffset?)null, true), (parked!.NextAt, parked.HeldAt is not null));

            // Committed: due now, and the escalation stage woken by the commit action, which runs only after the commit.
            await host.InTransactionAsync(async (_, client) =>
            {
                Assert.Equal(1, (await client.ReleaseAsync("alert:rel", "host", Ct)).Resumed);
                Assert.Equal(before + 2, host.CommittedActions);
                Assert.False(await WokenAsync(host), "the stage isn't woken before the host commits");
                return true;
            });
            Assert.True(await WokenAsync(host));
            var (open, resumed) = await HoldStateAsync("alert:rel");
            Assert.Null(open.HeldAt);
            Assert.Equal((true, (DateTimeOffset?)null), (resumed!.NextAt is not null, resumed.HeldAt));

            // Again: nothing held, nothing to wake.
            var again = await host.InTransactionAsync(async (_, client) => await client.ReleaseAsync("alert:rel", null, Ct));
            Assert.Equal((false, 0), (again.Changed, again.Resumed));
            Assert.Equal(before + 2, host.CommittedActions);

            // Whether the escalation stage has a wake-up waiting (the stages are off here, so nothing else takes it): it answers at once if so,
            // else at the end of its wait. Taking it reads it, so each wake is seen once.
            static async Task<bool> WokenAsync(EmbeddedHost host)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await host.Services.GetRequiredService<Stages.StageNudges>().WaitAsync(Workflows.EscalationStage.StageName, TimeSpan.FromSeconds(1), Ct);
                return watch.Elapsed < TimeSpan.FromMilliseconds(800);
            }
        }

        /// <summary>
        /// QA (M44-1): a hold rolled back with the host's transaction leaves the escalation as it was (its fence rolled back too, so a stage that
        /// claimed it keeps its save); a committed hold fences it, leaves its time alone (the stage parks it when it falls due) and wakes nothing.
        /// </summary>
        [Fact]
        public async Task A_rolled_back_hold_leaves_the_escalation_unfenced_and_a_committed_one_fences_it_and_wakes_nothing()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            const string key = "alert:fence";
            var request = await host.InTransactionAsync(async (_, client) => (await client.SendAsync(Notification("fence") with { CorrelationKey = key }, Ct)).Id);
            var due = DateTimeOffset.UtcNow.AddMinutes(5);
            due = due.AddTicks(-(due.Ticks % 10));
            await using (var db = _database.Context())
            {
                var notification = await db.Notifications.AsNoTracking().SingleAsync(n => n.RequestId == request, Ct);
                var correlation = await db.Correlations.AsNoTracking().SingleAsync(c => c.Key == key, Ct);
                db.Escalations.Add(new Escalation
                {
                    TenantId = Tenant, NotificationId = notification.Id, CorrelationId = correlation.Id, PolicyId = Guid.CreateVersion7(),
                    State = EscalationState.Running, NextAt = due, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(Ct);
            }
            var version = (await HoldStateAsync(key)).Escalation!.Version;
            await DrainAsync(host);

            await host.InTransactionAsync(async (_, client) =>
            {
                Assert.True((await client.HoldAsync(key, "silence:1", "host", Ct)).Changed);
                return false;
            });
            var (unheld, untouched) = await HoldStateAsync(key);
            Assert.Null(unheld.HeldAt);
            Assert.Equal((version, (DateTimeOffset?)due, (DateTimeOffset?)null), (untouched!.Version, untouched.NextAt, untouched.HeldAt));

            var hold = await host.InTransactionAsync(async (_, client) => await client.HoldAsync(key, "silence:1", "host", Ct));
            Assert.Equal((true, true, 0), (hold.Held, hold.Changed, hold.Resumed));
            Assert.False(await WokenAsync(host), "a hold wakes no stage");
            var (held, fenced) = await HoldStateAsync(key);
            Assert.NotNull(held.HeldAt);
            Assert.Equal((version + 1, (DateTimeOffset?)due, (DateTimeOffset?)null, (string?)null), (fenced!.Version, fenced.NextAt, fenced.HeldAt, fenced.ClaimedBy));

            // Takes any wake-up left by the send, so the check below sees only what the hold asks for.
            static Task DrainAsync(EmbeddedHost host)
                => host.Services.GetRequiredService<Stages.StageNudges>().WaitAsync(Workflows.EscalationStage.StageName, TimeSpan.FromMilliseconds(50), Ct);

            static async Task<bool> WokenAsync(EmbeddedHost host)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await host.Services.GetRequiredService<Stages.StageNudges>().WaitAsync(Workflows.EscalationStage.StageName, TimeSpan.FromSeconds(1), Ct);
                return watch.Elapsed < TimeSpan.FromMilliseconds(800);
            }
        }

        [Fact]
        public async Task Without_a_transaction_a_hold_or_release_is_refused()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            await using var scope = host.Services.CreateAsyncScope();
            var client = scope.ServiceProvider.GetRequiredService<INotificationClient>();
            Assert.Equal(ErrorCodes.NoTransaction, (await Assert.ThrowsAsync<NotificationRefusedException>(() => client.HoldAsync("alert:x", "silence:1", null, Ct))).Code);
            Assert.Equal(ErrorCodes.NoTransaction, (await Assert.ThrowsAsync<NotificationRefusedException>(() => client.ReleaseAsync("alert:x", null, Ct))).Code);
            var unknown = await Assert.ThrowsAsync<NotificationRefusedException>(() => host.InTransactionAsync(async (_, c) => await c.HoldAsync("alert:none", "silence:1", null, Ct)));
            Assert.Equal(ErrorCodes.NotFound, unknown.Code);
        }

        /// <summary>
        /// A release that meets another change of the correlation (here an update held by another connection, committed while the release waits)
        /// is refused as <c>concurrent_change</c>, and the host's transaction is still usable: asking again in it answers on the new state.
        /// </summary>
        [Fact]
        public async Task A_conflicting_release_is_refused_as_a_concurrent_change_and_the_host_transaction_goes_on()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            await ParkedAsync(host, "alert:race");

            await host.InTransactionAsync(async (hostDb, client) =>
            {
                hostDb.Things.Add(new Thing { Id = Guid.CreateVersion7(), Name = "kept" });
                await hostDb.SaveChangesAsync(Ct);
                await using (var other = _database.Context())
                {
                    await using var otherTransaction = await other.Database.BeginTransactionAsync(Ct);
                    await other.Database.ExecuteSqlAsync($"UPDATE notifications.correlations SET updated_at = now() WHERE key = 'alert:race'", Ct);
                    var release = client.ReleaseAsync("alert:race", "host", Ct);
                    await LockWaitAsync();
                    await otherTransaction.CommitAsync(Ct);
                    var refused = await Assert.ThrowsAsync<NotificationRefusedException>(() => release);
                    Assert.Equal(ErrorCodes.ConcurrentChange, refused.Code);
                }
                Assert.Equal(1, (await client.ReleaseAsync("alert:race", "host", Ct)).Resumed);
                return true;
            });
            Assert.Null((await HoldStateAsync("alert:race")).Correlation.HeldAt);
            await using var check = host.HostContext();
            Assert.Single(await check.Things.Where(t => t.Name == "kept").ToListAsync(Ct));
        }

        /// <summary>Waits until a statement of this database waits on a row lock.</summary>
        private async Task LockWaitAsync()
        {
            await using var db = _database.Context();
            for (var i = 0; i < 100; i++)
            {
                if (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'")
                    .SingleAsync(Ct) > 0)
                {
                    return;
                }
                await Task.Delay(50, Ct);
            }
            Assert.Fail("The release never waited on the other transaction.");
        }

        [Fact]
        public async Task The_setup_is_provisioned_and_each_change_reaches_the_host_audit_sink()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            await using var db = _database.Context();
            var app = await db.Applications.SingleAsync(a => a.Key == "host", Ct);
            Assert.Equal(EmbeddedApplicationProvisioner.ClientId("host"), app.ClientId);
            Assert.True(await db.ApplicationTenants.AnyAsync(t => t.ApplicationId == app.Id && t.TenantId == Tenant, Ct));
            Assert.True(await db.Types.AnyAsync(t => t.ApplicationId == app.Id && t.Key == Type, Ct));
            // The provisioning file's rows (the receiver's integration, target, route and allowlist entry), each heard by the host's sink.
            Assert.Contains(host.Audit.Changes, c => c.Table == "integrations" && c.Change == "Inserted" && c.TenantId == Tenant.ToString()
                && c.User == Configuration.ConfigurationProvisioner.Actor);
            Assert.Contains(host.Audit.Changes, c => c.Table == "routes");
            // Once each: the save dispatches a change's event, and nothing dispatches it again.
            Assert.Single(host.Audit.Changes, c => c.Table == "integrations");
            // Not the host's own rows: its context raises its changes through the same mediator.
            await host.InTransactionAsync(async (hostDb, _) =>
            {
                hostDb.Things.Add(new Thing { Id = Guid.CreateVersion7(), Name = "not audited here" });
                await hostDb.SaveChangesAsync(Ct);
                return true;
            });
            Assert.DoesNotContain(host.Audit.Changes, c => c.Table == "things");

            // Started again, the application renamed: one application, with the new name; its link and its unchanged type stay as they are.
            await using var again = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false, applicationName: "The host, renamed");
            Assert.Equal("The host, renamed", (await db.Applications.AsNoTracking().SingleAsync(a => a.Key == "host", Ct)).Name);
            Assert.Single(await db.Types.Where(t => t.Key == Type).ToListAsync(Ct));
        }

        /// <summary>
        /// The host's dead-man check reads the pipeline's health (phase-5 M49-0): with the stages running, the dispatcher's passes show; without
        /// them, it says so and has no pass.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_host_reads_the_pipelines_health(bool runStages)
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages);
            PipelineHealth health;
            var until = DateTimeOffset.UtcNow.AddSeconds(15);
            do
            {
                await using var scope = host.Services.CreateAsyncScope();
                health = await scope.ServiceProvider.GetRequiredService<INotificationsPipelineHealth>().ReadAsync(Ct);
                if (!runStages || health.Stages["dispatcher"].LastPassEndedAt is not null)
                {
                    break;
                }
                await Task.Delay(200, Ct);
            }
            while (DateTimeOffset.UtcNow < until);

            Assert.Equal(runStages, health.Running);
            Assert.Equal(runStages, health.Stages["dispatcher"].LastPassEndedAt is not null);
            Assert.Equal(["delivery-planner", "digest", "dispatcher", "escalation", "planner"], health.Stages.Keys.Order());
            Assert.Null(health.OldestDueAny);
        }

        private static PutIntegration Hooks(string key, Guid? id = null, string secretRef = TestHooks.SecretRef)
            => new(Tenant, key, "webhook", "webhook", JsonSerializer.SerializeToElement(new { }), secretRef, 0, true) { Id = id };

        /// <summary>
        /// A host that writes its own row and the pipeline's setup together (M46-2, the webhooks alias): one transaction, so its commit keeps
        /// both and its rollback leaves neither.
        /// </summary>
        [Fact]
        public async Task The_host_scope_commits_and_rolls_back_with_the_host()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            foreach (var (name, commit) in new[] { ("committed", true), ("rolled-back", false) })
            {
                await host.InHostScopeAsync(async (hostDb, scope) =>
                {
                    hostDb.Things.Add(new Thing { Id = Guid.CreateVersion7(), Name = name });
                    await hostDb.SaveChangesAsync(Ct);
                    var outcome = await scope.RunAsync(work => work.SendAsync(Hooks($"hooks-{name}"), Ct), Ct);
                    Assert.Equal(OutcomeKind.Ok, outcome.Kind);
                    return commit;
                });
            }
            await using var db = _database.Context();
            await using var check = host.HostContext();
            Assert.Equal(["committed"], await check.Things.Where(t => t.Name == "committed" || t.Name == "rolled-back").Select(t => t.Name).ToListAsync(Ct));
            Assert.Equal(["hooks-committed"], await db.Integrations.Where(i => i.Key.StartsWith("hooks-")).Select(i => i.Key).ToListAsync(Ct));
        }

        /// <summary>A command the pipeline refuses, or one that fails, takes the host's own row with it when the host rolls back on it (M46-2).</summary>
        [Fact]
        public async Task A_refused_or_failed_pipeline_command_rolls_the_host_row_back()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            // Refused: a secret the API may not name. The host rolls back on the refusal.
            await host.InHostScopeAsync(async (hostDb, scope) =>
            {
                hostDb.Things.Add(new Thing { Id = Guid.CreateVersion7(), Name = "refused" });
                await hostDb.SaveChangesAsync(Ct);
                var outcome = await scope.RunAsync(work => work.SendAsync(Hooks("hooks-refused", secretRef: "env:OTHER_SECRET"), Ct), Ct);
                return outcome.Kind == OutcomeKind.Ok;
            });
            // Failed: the second command breaks the unique key the first one took (an id another integration has), in the database itself.
            await Assert.ThrowsAnyAsync<Exception>(() => host.InHostScopeAsync(async (hostDb, scope) =>
            {
                hostDb.Things.Add(new Thing { Id = Guid.CreateVersion7(), Name = "failed" });
                await hostDb.SaveChangesAsync(Ct);
                return await scope.RunAsync(async work =>
                {
                    Assert.Equal(OutcomeKind.Ok, (await work.SendAsync(Hooks("hooks-failed"), Ct)).Kind);
                    work.Db.Integrations.Add(new Integration
                    {
                        TenantId = Tenant, Key = "hooks-failed", Channel = "webhook", Kind = "webhook", Settings = "{}", DefinitionHash = "h",
                    });
                    await work.Db.SaveChangesAsync(Ct);
                    return true;
                }, Ct);
            }));
            await using var db = _database.Context();
            await using var check = host.HostContext();
            Assert.False(await check.Things.AnyAsync(t => t.Name == "refused" || t.Name == "failed", Ct));
            Assert.False(await db.Integrations.AnyAsync(i => i.Key == "hooks-refused" || i.Key == "hooks-failed", Ct));
        }

        [Fact]
        public async Task Without_a_host_transaction_the_host_scope_is_refused()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            await using var scope = host.Services.CreateAsyncScope();
            var refused = await Assert.ThrowsAsync<NotificationRefusedException>(() =>
                scope.ServiceProvider.GetRequiredService<INotificationsHostScope>().RunAsync(work => work.SendAsync(Hooks("hooks-none"), Ct), Ct));
            Assert.Equal(ErrorCodes.NoTransaction, refused.Code);
        }

        /// <summary>
        /// Two host transactions create the same integration at once (the same key, or the same caller's id): the second waits on the first's
        /// row, and when the first commits its save loses. It is undone to its savepoint and answered as a conflict, and the second host's
        /// transaction goes on: its next command is saved and it commits (QA M46-2, round 1).
        /// </summary>
        [Theory]
        [InlineData(false, ErrorCodes.ConcurrentChange)]
        [InlineData(true, ErrorCodes.IdInUse)]
        public async Task A_create_racing_another_host_transaction_is_a_conflict_and_the_host_transaction_goes_on(bool sameId, string code)
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            var suffix = Guid.NewGuid().ToString("N")[..6];
            var id = Guid.CreateVersion7();
            var (first, second) = sameId ? ($"race-a-{suffix}", $"race-b-{suffix}") : ($"race-{suffix}", $"race-{suffix}");
            Task<(Outcome<IntegrationView> Raced, Outcome<AllowlistView> Next)>? racing = null;
            await host.InHostScopeAsync((_, scope) => scope.RunAsync(async work =>
            {
                Assert.Equal(OutcomeKind.Ok, (await work.SendAsync(Hooks(first, sameId ? id : null), Ct)).Kind);
                // The other host transaction, while this one is open: its insert waits on this one's row.
                racing = host.InHostScopeAsync((_, other) => other.RunAsync(async w =>
                    (await w.SendAsync(Hooks(second, sameId ? id : null), Ct), await w.SendAsync(new PutAllowlistEntry(Tenant, $"race-{suffix}.example.com", [8443]), Ct)), Ct));
                await LockWaitAsync();
                return true;
            }, Ct));
            var (raced, next) = await racing!;
            Assert.Equal((OutcomeKind.Conflict, code), (raced.Kind, raced.Code));
            Assert.Equal(OutcomeKind.Ok, next.Kind);

            await using var db = _database.Context();
            Assert.Equal(sameId ? [first] : [first], await db.Integrations.Where(i => i.Key == first || i.Key == second).Select(i => i.Key).Distinct().ToListAsync(Ct));
            Assert.True(await db.Allowlist.AnyAsync(a => a.Destination == $"race-{suffix}.example.com", Ct), "the second host transaction committed its next command");
        }

        [Fact]
        public async Task A_command_for_another_tenant_or_an_empty_id_is_refused()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            var (other, empty) = await host.InHostScopeAsync((_, scope) => scope.RunAsync(async work =>
                (await work.SendAsync(new PutAllowlistEntry(Guid.CreateVersion7(), "elsewhere.example.com", null), Ct),
                 await work.SendAsync(Hooks("hooks-empty", Guid.Empty), Ct)), Ct));
            Assert.Equal((OutcomeKind.Forbidden, ErrorCodes.TenantForbidden), (other.Kind, other.Code));
            Assert.Equal((OutcomeKind.Invalid, ErrorCodes.InvalidRequest, "/id"), (empty.Kind, empty.Code, Assert.Single(empty.Errors!).Pointer));
            await using var db = _database.Context();
            Assert.False(await db.Allowlist.AnyAsync(a => a.Destination == "elsewhere.example.com", Ct));
        }

        /// <summary>Two commands in one host scope: each change is heard by the host's audit sink once, and the outbox holds what it should, once.</summary>
        [Fact]
        public async Task Two_commands_in_one_host_scope_are_audited_once_each_and_add_nothing_twice_to_the_outbox()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            await using var db = _database.Context();
            var outboxBefore = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM notifications.outbox_events").SingleAsync(Ct);
            var before = host.Audit.Changes.Count;
            await host.InHostScopeAsync((_, scope) => scope.RunAsync(async work =>
            {
                Assert.Equal(OutcomeKind.Ok, (await work.SendAsync(Hooks("hooks-once"), Ct)).Kind);
                Assert.Equal(OutcomeKind.Ok, (await work.SendAsync(new PutAllowlistEntry(Tenant, "once.example.com", [8443]), Ct)).Kind);
                return true;
            }, Ct));
            var heard = host.Audit.Changes.Skip(before).ToList();
            Assert.Single(heard, c => c.Table == "integrations");
            Assert.Single(heard, c => c.Table == "destination_allowlist");
            Assert.Equal(2, heard.Count);
            // Setup changes raise no outbox message (they are audited through the host's sink, in its transaction).
            Assert.Equal(outboxBefore, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM notifications.outbox_events").SingleAsync(Ct));
        }

        /// <summary>
        /// An in-process caller's integration id (M46-2): a create takes it; another integration's is refused, and so is a different one for an
        /// existing integration, whose id never changes.
        /// </summary>
        [Fact]
        public async Task An_integration_takes_the_callers_id_once_and_refuses_one_taken()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false);
            var id = Guid.CreateVersion7();
            var outcomes = await host.InHostScopeAsync((_, scope) => scope.RunAsync(async work => new[]
            {
                await work.SendAsync(Hooks("hooks-kept", id), Ct),
                await work.SendAsync(Hooks("hooks-kept", id), Ct),                 // the same: unchanged
                await work.SendAsync(Hooks("hooks-other", id), Ct),                // another integration's id
                await work.SendAsync(Hooks("hooks-kept", Guid.CreateVersion7()), Ct), // a different id for this one
            }, Ct));
            Assert.Equal([(OutcomeKind.Ok, null), (OutcomeKind.Ok, null), (OutcomeKind.Conflict, ErrorCodes.IdInUse), (OutcomeKind.Conflict, ErrorCodes.IdInUse)],
                outcomes.Select(o => (o.Kind, o.Code)));
            await using var db = _database.Context();
            Assert.Equal(id, (await db.Integrations.SingleAsync(i => i.Key == "hooks-kept", Ct)).Id);
            Assert.False(await db.Integrations.AnyAsync(i => i.Key == "hooks-other", Ct));
        }

        [Fact]
        public void The_container_holds_nothing_of_the_service()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddJuiceNotificationsEmbedded(new ConfigurationBuilder().Build(), EmbeddedHost.Configure("Host=localhost;Database=none"));
            var named = services.SelectMany(d => new[] { d.ServiceType, d.ImplementationType, d.ImplementationInstance?.GetType() })
                .Where(t => t is not null).Select(t => t!.FullName ?? t.Name).Distinct().ToList();
            string[] forbidden = ["Microsoft.AspNetCore.Authentication", "Microsoft.AspNetCore.SignalR", "RabbitMQ", "Juice.EventBus.RabbitMQ", "Juice.Audit",
                "Juice.MultiTenant", "Juice.Notifications.Api", "Juice.Notifications.Messaging", "Juice.Notifications.People.IdentityRecipientResolver",
                "Juice.Notifications.People.ServiceTypesProvisioner"];
            Assert.DoesNotContain(named, n => forbidden.Any(f => n.StartsWith(f, StringComparison.Ordinal)));
            // And as the container answers (a factory registration names no type above): none of the service's parts resolve.
            using (var provider = services.BuildServiceProvider())
            {
                Assert.Null(provider.GetService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>());
                Assert.Null(provider.GetService<Microsoft.AspNetCore.SignalR.IHubContext<Api.InboxHub>>());
                Assert.Null(provider.GetService<Api.Producers>());
                Assert.Null(provider.GetService<Juice.MultiTenant.AspNetCore.ITenantAccess>());
                Assert.Null(provider.GetService<Planning.IIdentityDirectory>());
                Assert.DoesNotContain(provider.GetServices<Planning.IRecipientResolver>(), r => r is Planning.IdentityRecipientResolver);
            }
            // The pipeline is there: intake's client and the stages.
            Assert.Contains(services, d => d.ServiceType == typeof(INotificationClient));
            Assert.Contains(services, d => d.ImplementationType == typeof(Delivering.DispatcherStage) || d.ServiceType == typeof(Delivering.DispatcherStage));
        }

        private static NotificationToSend Notification(string key) => new(Type, key) { Data = Data(1) };

        private static JsonElement Data(int n) => JsonSerializer.SerializeToElement(new { n });

        // The host's side: its own context, its table, and its transaction as the pipeline sees it.

        internal sealed class Thing
        {
            public Guid Id { get; set; }
            public required string Name { get; set; }
        }

        internal sealed class HostDbContext(DbContextOptions<HostDbContext> options) : DbContext(options)
        {
            public DbSet<Thing> Things => Set<Thing>();

            protected override void OnModelCreating(ModelBuilder model)
                => model.Entity<Thing>(e => { e.ToTable("things", "host"); e.Property(t => t.Id).HasColumnName("id"); e.Property(t => t.Name).HasColumnName("name"); });
        }

        /// <summary>The host's transaction (I59), as Observability's adapter gives <c>PlatformDbContext</c>'s; commit actions run once it commits.</summary>
        internal sealed class HostTransaction(HostDbContext db, EmbeddedHost.Committed committed) : INotificationTransaction
        {
            public System.Data.Common.DbConnection? Connection => db.Database.GetDbConnection();

            public System.Data.Common.DbTransaction? Transaction => db.Database.CurrentTransaction?.GetDbTransaction();

            public List<Action> OnCommit { get; } = [];

            public bool OnCommitted(Action action)
            {
                OnCommit.Add(action);
                committed.Count++;
                return true;
            }
        }

        internal sealed class RecordingSink : INotificationsAuditSink
        {
            public System.Collections.Concurrent.ConcurrentQueue<NotificationsDataChange> Changes { get; } = new();

            public ValueTask RecordAsync(NotificationsDataChange change, CancellationToken cancellationToken)
            {
                Changes.Enqueue(change);
                return ValueTask.CompletedTask;
            }
        }

        internal sealed class EmbeddedHost : IAsyncDisposable
        {
            public sealed class Committed
            {
                public int Count { get; set; }
            }

            private readonly IHost _host;
            private readonly string _connectionString;

            private EmbeddedHost(IHost host, string connectionString)
            {
                _host = host;
                _connectionString = connectionString;
            }

            public IServiceProvider Services => _host.Services;

            public RecordingSink Audit => _host.Services.GetRequiredService<RecordingSink>();

            /// <summary>How many commit actions the pipeline asked for (one per new request).</summary>
            public int CommittedActions => _host.Services.GetRequiredService<Committed>().Count;

            public static Action<NotificationsEmbeddedOptions> Configure(string connectionString, string applicationName = "The host") => o =>
            {
                o.ConnectionString = connectionString;
                o.TenantId = Tenant;
                o.ApplicationKey = "host";
                o.ApplicationName = applicationName;
                o.Types.Add(new TypeDeclaration(Type, "A thing happened", "things", null, null, null, null,
                    JsonSerializer.SerializeToElement(new { type = "object" }), CorrelationRules.Optional, null, null,
                    JsonSerializer.SerializeToElement(new { webhook = "passthrough" })));
            };

            public static async Task<EmbeddedHost> StartAsync(string connectionString, bool runStages, Receiver? receiver = null,
                string applicationName = "The host")
            {
                var builder = Host.CreateApplicationBuilder();
                var settings = new Dictionary<string, string?>
                {
                    ["Notifications:Database:Migrate"] = "true",
                    ["Notifications:Stages:Run"] = runStages ? "true" : "false",
                    ["Notifications:Stages:Planner:Poll"] = "00:00:00.2",
                    ["Notifications:Stages:DeliveryPlanner:Poll"] = "00:00:00.2",
                    ["Notifications:Stages:Dispatcher:Poll"] = "00:00:00.2",
                    // The receiver is plain http on this machine's private address, allowlisted (as the local stack allows).
                    ["Notifications:Delivery:AllowHttpToAllowlisted"] = "true",
                };
                if (receiver is not null)
                {
                    var path = Path.Combine(Path.GetTempPath(), $"embedded-provisioning-{Guid.NewGuid():N}.json");
                    await File.WriteAllTextAsync(path, $$"""
                        { "tenants": { "{{Tenant}}": {
                            "integrations": { "hooks": { "channel": "webhook", "kind": "webhook", "secretRef": "{{TestHooks.SecretRef}}",
                                                         "targets": { "receiver": { "address": "{{receiver.Url}}" } } } },
                            "routes": { "all": { "match": { "type": "host.*" }, "targets": ["receiver"] } },
                            "allowlist": [ { "destination": "{{receiver.Address}}", "ports": [{{receiver.Port}}] } ]
                        } } }
                        """);
                    settings["Notifications:Provisioning:Path"] = path;
                }
                else
                {
                    // A route and an integration all the same, so the audit sink has setup rows to hear.
                    var path = Path.Combine(Path.GetTempPath(), $"embedded-provisioning-{Guid.NewGuid():N}.json");
                    await File.WriteAllTextAsync(path, $$"""
                        { "tenants": { "{{Tenant}}": {
                            "integrations": { "hooks": { "channel": "webhook", "kind": "webhook", "secretRef": "{{TestHooks.SecretRef}}",
                                                         "targets": { "receiver": { "address": "https://hooks.example.com/in" } } } },
                            "routes": { "all": { "match": { "type": "host.*" }, "targets": ["receiver"] } }
                        } } }
                        """);
                    settings["Notifications:Provisioning:Path"] = path;
                }
                builder.Configuration.AddInMemoryCollection(settings);
                builder.Services.AddDbContext<HostDbContext>(o => o.UseNpgsql(connectionString));
                builder.Services.AddSingleton<Committed>();
                builder.Services.AddScoped<HostTransaction>();
                builder.Services.AddScoped<INotificationTransaction>(sp => sp.GetRequiredService<HostTransaction>());
                builder.Services.AddSingleton<RecordingSink>();
                builder.Services.AddSingleton<INotificationsAuditSink>(sp => sp.GetRequiredService<RecordingSink>());
                builder.Services.AddJuiceNotificationsEmbedded(builder.Configuration.GetSection("Notifications"), Configure(connectionString, applicationName));
                var host = builder.Build();
                await host.StartAsync(Ct);
                return new EmbeddedHost(host, connectionString);
            }

            public HostDbContext HostContext() => new(new DbContextOptionsBuilder<HostDbContext>().UseNpgsql(_connectionString).Options);

            /// <summary>
            /// <paramref name="work"/> in one host transaction, committed when it returns true (or a non-boolean result) and rolled back when it
            /// returns false or throws; the commit actions run after a commit, as the host would run them.
            /// </summary>
            public async Task<T> InTransactionAsync<T>(Func<HostDbContext, INotificationClient, Task<T>> work)
            {
                await using var scope = _host.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HostDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                var result = await work(db, scope.ServiceProvider.GetRequiredService<INotificationClient>());
                if (result is false)
                {
                    await transaction.RollbackAsync(Ct);
                    return result;
                }
                await transaction.CommitAsync(Ct);
                foreach (var action in scope.ServiceProvider.GetRequiredService<HostTransaction>().OnCommit)
                {
                    action();
                }
                return result;
            }

            /// <summary>Host work in its transaction through the host scope (M46-2): committed unless it returns false; an exception rolls it back.</summary>
            public async Task<T> InHostScopeAsync<T>(Func<HostDbContext, INotificationsHostScope, Task<T>> work)
            {
                await using var scope = _host.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HostDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                var result = await work(db, scope.ServiceProvider.GetRequiredService<INotificationsHostScope>());
                if (result is false)
                {
                    await transaction.RollbackAsync(Ct);
                    return result;
                }
                await transaction.CommitAsync(Ct);
                return result;
            }

            public async ValueTask DisposeAsync()
            {
                await _host.StopAsync(CancellationToken.None);
                _host.Dispose();
            }
        }
    }
}
