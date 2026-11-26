using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Delivering;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The <c>RecipientDryStamp</c> migration (M44-2c) over rows already there: the request's own recipients of a request accepted dry take its
    /// flag, so they stay dry however late they are planned; those of a real request, and every escalation step's, are real.
    /// </summary>
    public sealed class RecipientDryStampMigrationTests : IAsyncLifetime
    {
        private const string Previous = "20261126090432_RequestDryStamp";
        private static readonly Guid Tenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000000c2");
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly ScratchDatabase _database = new();

        public ValueTask InitializeAsync() => _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        [Fact]
        public async Task Existing_own_recipients_of_a_dry_request_become_dry_and_the_rest_stay_real()
        {
            LocalStack.RequireAvailable();
            var ids = new Dictionary<string, Guid>();
            await using (var db = await _database.MigratedAsync())
            {
                var app = Seed.Application("dry-stamp-migration");
                var type = Seed.Type(app.Id, "dry-stamp-migration.thing");
                db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now });
                foreach (var (name, dry, step) in new[] { ("dry-own", true, 0), ("dry-step", true, 1), ("real-own", false, 0) })
                {
                    var request = Seed.Request(Tenant, app.Id, type.Id, name);
                    db.Entry(request).Property(r => r.DryRun).CurrentValue = dry;
                    var notification = Seed.Notification(Tenant, request.Id);
                    var recipient = Seed.Recipient(Tenant, notification.Id);
                    recipient.EscalationStep = step;
                    db.AddRange(request, notification, recipient);
                    ids[name] = recipient.Id;
                }
                await db.SaveChangesAsync(Ct);
                await db.GetService<IMigrator>().MigrateAsync(Previous, cancellationToken: Ct);
            }

            await using (var db = _database.Context())
            {
                await db.Database.MigrateAsync(Ct);
                var flags = await db.Recipients.AsNoTracking().Where(r => r.TenantId == Tenant).ToDictionaryAsync(r => r.Id, r => r.DryRun, Ct);
                Assert.Equal((true, false, false), (flags[ids["dry-own"]], flags[ids["dry-step"]], flags[ids["real-own"]]));
            }
        }

        /// <summary>
        /// QA (M44-2c): an escalation step's recipient already there and not yet planned (deferred by quiet hours, a throttle, a busy
        /// notification) when the migration runs. Before it, M44-2b planned it in the mode at planning; the backfill makes it real, so a host
        /// still in shadow when it is deployed would page for real beside legacy, which pages in shadow. Planned by a host in dry run, it must be
        /// dry, whatever the backfill does for the steps (unknown at migration: a null left to the planner's mode, or a backfill from the host's
        /// own mode at start, both meet this).
        /// </summary>
        [Fact]
        public async Task An_escalation_step_recipient_waiting_at_the_migration_is_planned_dry_by_a_host_in_dry_run()
        {
            LocalStack.RequireAvailable();
            Guid recipientId;
            await using (var db = await _database.MigratedAsync())
            {
                var app = Seed.Application("dry-stamp-pending");
                var type = Seed.Type(app.Id, "dry-stamp-pending.thing");
                var integration = Seed.Integration(Tenant);
                var request = Seed.Request(Tenant, app.Id, type.Id, "pending-step");
                db.Entry(request).Property(r => r.DryRun).CurrentValue = true;
                var notification = Seed.Notification(Tenant, request.Id);
                (notification.State, notification.NextAt) = (NotificationState.Planned, null);
                var recipient = Seed.Recipient(Tenant, notification.Id);
                (recipient.EscalationStep, recipient.Outcome, recipient.NextAt) = (1, RecipientOutcome.Planned, Seed.Now);
                db.AddRange(app, type, integration, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now }, request, notification, recipient);
                db.Templates.Add(new Template { TypeId = type.Id, Channel = Channels.Webhook, Locale = "en", Version = 1, Engine = "scriban", Body = """{"x":1}""", CreatedAt = Seed.Now });
                await db.SaveChangesAsync(Ct);
                recipientId = recipient.Id;
                await db.GetService<IMigrator>().MigrateAsync(Previous, cancellationToken: Ct);
            }
            await using (var db = _database.Context())
            {
                await db.Database.MigrateAsync(Ct);
            }

            var stage = new StageOptions { Batch = 10, Lease = TimeSpan.FromSeconds(30) };
            var services = new ServiceCollection().AddLogging();
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton(TimeProvider.System);
            services.AddPostgresClaimStore();
            services.Configure<Providers.DeliveryOptions>(o => o.DryRun = true);
            services.AddPlanning(stage, runWorkers: false);
            services.AddDeliveryPlanning(stage, runWorkers: false);
            await using var provider = services.BuildServiceProvider();
            var runner = provider.GetRequiredService<StageRunner<NotificationRecipient, DeliveryPlannerStage>>();
            Assert.Equal(1, (await runner.RunOnceAsync("delivery-planner", stage, Ct)).Completed);

            await using var check = _database.Context();
            var delivery = await check.Deliveries.AsNoTracking().SingleAsync(d => check.DeliveryItems.Any(i => i.DeliveryId == d.Id && i.RecipientId == recipientId), Ct);
            Assert.True(delivery.DryRun, "a step recipient waiting at the migration was planned for real by a host in dry run");
        }
    }
}
