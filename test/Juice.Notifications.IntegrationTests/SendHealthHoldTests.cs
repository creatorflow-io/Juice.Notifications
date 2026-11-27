using Juice.Notifications.Data;
using Juice.Notifications.History;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The send health leaves out deliveries a tenant's gate holds on purpose (M52, #55), until their hold lapses; every writer that moves a
    /// delivery on clears the flag.
    /// </summary>
    public sealed class SendHealthHoldTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));
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
            _app = Seed.Application("held");
            _type = Seed.Type(_app.Id, "held.alert");
            _integration = Seed.Integration(Tenant);
            db.AddRange(_app, _type, _integration, new ApplicationTenant { ApplicationId = _app.Id, TenantId = Tenant, LinkedAt = Seed.Now });
            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        private async Task<TimeSpan?> OldestUndeliveredAsync()
        {
            await using var db = _database.Context();
            var options = Options.Create(new Providers.DeliveryOptions());
            return (await new NotificationsPipelineHealth(db, new StagePasses(), new StagesRunning(true), _clock, options).ReadAsync(Ct)).Sends.OldestUndelivered;
        }

        private async Task<Delivery> SeedAsync(TimeSpan age, DeliveryStatus status, bool held, TimeSpan? dueIn = null, string content = "{}")
        {
            var d = new Delivery
            {
                TenantId = Tenant, Channel = Channels.Webhook, IntegrationId = _integration.Id, DestinationKey = "ext:webhook:https://example.com/hook",
                DestinationSnapshot = "https://example.com/hook", Locale = "en", RendererVersion = "test", Content = content, CreatedAt = _clock.GetUtcNow() - age,
            };
            (d.Status, d.TenantHeld, d.NextAt) = (status, held, _clock.GetUtcNow() + (dueIn ?? TimeSpan.Zero));
            await using var db = _database.Context();
            db.Deliveries.Add(d);
            await db.SaveChangesAsync(Ct);
            return d;
        }

        [Fact]
        public async Task A_held_delivery_isnt_undelivered_but_a_stuck_one_of_an_active_tenant_still_counts_beside_it()
        {
            LocalStack.RequireAvailable();
            await SeedAsync(TimeSpan.FromMinutes(30), DeliveryStatus.Pending, held: true, dueIn: TimeSpan.FromMinutes(5));
            Assert.Null(await OldestUndeliveredAsync());

            await SeedAsync(TimeSpan.FromMinutes(10), DeliveryStatus.Retrying, held: false, dueIn: TimeSpan.FromMinutes(1));
            Assert.Equal(TimeSpan.FromMinutes(10), await OldestUndeliveredAsync());
        }

        [Fact]
        public async Task A_hold_nobody_rechecks_counts_again_after_TenantHold()
        {
            LocalStack.RequireAvailable();
            // Held, due again in a minute; nobody (no dispatcher) looks at it again.
            await SeedAsync(TimeSpan.FromMinutes(30), DeliveryStatus.Pending, held: true, dueIn: TimeSpan.FromMinutes(1));
            Assert.Null(await OldestUndeliveredAsync());
            _clock.Advance(TimeSpan.FromMinutes(5)); // due 4 minutes ago: inside the 5-minute TenantHold
            Assert.Null(await OldestUndeliveredAsync());
            _clock.Advance(TimeSpan.FromMinutes(1)); // next_at + TenantHold reached
            Assert.Equal(TimeSpan.FromMinutes(36), await OldestUndeliveredAsync());
        }

        [Fact]
        public async Task Upgrade_rows_from_before_the_migration_count_until_re_held()
        {
            LocalStack.RequireAvailable();
            var d = await SeedAsync(TimeSpan.FromMinutes(20), DeliveryStatus.Pending, held: false);
            await using (var db = _database.Context())
            {
                // As a row from before the column: the default.
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notifications.deliveries SET tenant_held = DEFAULT WHERE id = {d.Id}", Ct);
            }
            Assert.Equal(TimeSpan.FromMinutes(20), await OldestUndeliveredAsync());
            await using (var db = _database.Context())
            {
                await db.Deliveries.Where(x => x.Id == d.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.TenantHeld, true), Ct);
            }
            Assert.Null(await OldestUndeliveredAsync());
        }

        [Fact]
        public async Task Retry_replay_and_correlation_cancel_clear_the_flag()
        {
            LocalStack.RequireAvailable();
            var retried = await SeedAsync(TimeSpan.FromMinutes(5), DeliveryStatus.Failed, held: true, content: """{"a":1}""");
            var replayed = await SeedAsync(TimeSpan.FromMinutes(5), DeliveryStatus.Failed, held: true, content: """{"a":1}""");
            await using (var db = _database.Context())
            {
                var outcome = await new RetryDeliveryHandler(db, _clock).Handle(new RetryDelivery(Tenant, retried.Id), Ct);
                Assert.Equal(OutcomeKind.Ok, outcome.Kind);
                await db.SaveChangesAsync(Ct);
            }
            await using (var db = _database.Context())
            {
                var outcome = await new ReplayDeliveriesHandler(db, _clock).Handle(
                    new ReplayDeliveries(Tenant, new ReplayFilter(null, null, null, null, null, null, null)), Ct);
                Assert.Equal(1, outcome.Value!.Replayed); // the retried one is Pending now
            }
            await using (var db = _database.Context())
            {
                Assert.All(await db.Deliveries.AsNoTracking().Where(x => x.Id == retried.Id || x.Id == replayed.Id).ToListAsync(Ct),
                    x => Assert.False(x.TenantHeld));
            }

            // The correlation cancel.
            var now = _clock.GetUtcNow();
            var correlation = new Correlation { TenantId = Tenant, ApplicationId = _app.Id, Key = "held:1", CreatedAt = now, LastRequestAt = now, UpdatedAt = now,
                State = CorrelationState.Resolved };
            var request = Seed.Request(Tenant, _app.Id, _type.Id, "held-cancel");
            var notification = Seed.Notification(Tenant, request.Id);
            var recipient = Seed.Recipient(Tenant, notification.Id);
            var held = await SeedAsync(TimeSpan.FromMinutes(5), DeliveryStatus.Pending, held: true);
            await using (var db = _database.Context())
            {
                db.Entry(request).Property(r => r.CorrelationId).CurrentValue = correlation.Id;
                db.Entry(request).Property(r => r.CorrelationKey).CurrentValue = correlation.Key;
                db.AddRange(correlation, request, notification, recipient, Seed.Item(Tenant, held.Id, recipient.Id, request.Id));
                await db.SaveChangesAsync(Ct);
                var (_, _, cancelled) = await Correlations.CorrelationLifecycle.StopAsync(db, Tenant, correlation.Id, now, Ct);
                Assert.Equal(1, cancelled);
            }
            await using var check = _database.Context();
            var row = await check.Deliveries.AsNoTracking().SingleAsync(x => x.Id == held.Id, Ct);
            Assert.Equal((DeliveryStatus.Cancelled, false), (row.Status, row.TenantHeld));
        }

        [Fact]
        public async Task Two_dispatchers_racing_a_held_row_leave_one_consistent_flag()
        {
            LocalStack.RequireAvailable();
            var d = await SeedAsync(TimeSpan.FromMinutes(5), DeliveryStatus.Pending, held: false);
            await using var first = _database.Context();
            await using var second = _database.Context();
            var a = await first.Deliveries.SingleAsync(x => x.Id == d.Id, Ct);
            var b = await second.Deliveries.SingleAsync(x => x.Id == d.Id, Ct);
            (a.TenantHeld, a.Version) = (true, a.Version + 1);   // the one that held it
            (b.TenantHeld, b.Version) = (false, b.Version + 1);  // the one that found the tenant active
            await first.SaveChangesAsync(Ct);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));
            await using var check = _database.Context();
            Assert.True((await check.Deliveries.AsNoTracking().SingleAsync(x => x.Id == d.Id, Ct)).TenantHeld);
        }
    }
}
