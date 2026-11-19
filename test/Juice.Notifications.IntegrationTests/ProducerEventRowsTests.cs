using Juice.Notifications.Correlations;
using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Delivering;
using Juice.Notifications.Events;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Events are raised as rows saved with the change (phase-3-plan I37): only when events are on, and only for an application with a broker
    /// user (design §19 decision 12). Publishing them over RabbitMQ is the relay's and the outbox's (tested with the stack's broker).
    /// </summary>
    public sealed class ProducerEventRowsTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private readonly ScratchDatabase _database = new();
        private (Guid WithBroker, Guid Without) _apps;

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = await _database.MigratedAsync();
            var withBroker = Seed.Application("brokered");
            withBroker.BrokerUser = "brokered-user";
            var without = Seed.Application("plain");
            db.AddRange(withBroker, without);
            await db.SaveChangesAsync();
            _apps = (withBroker.Id, without.Id);
        }

        public async ValueTask DisposeAsync() => await _database.DisposeAsync();

        private async Task<int> RaiseAsync(bool enabled, Guid application)
        {
            await using var db = _database.Context();
            var events = new ProducerEvents(Options.Create(new EventsOptions { Enabled = enabled }));
            await events.RaiseAsync(db, Tenant, application, app => new NotificationEvent
            {
                Event = ProducerEventNames.Interaction, Application = app, NotificationTenantId = Tenant, OccurredAt = DateTimeOffset.UtcNow,
            }, Ct);
            await db.SaveChangesAsync(Ct);
            return await db.ProducerEvents.CountAsync(Ct);
        }

        [Fact]
        public async Task An_event_is_a_row_only_when_events_are_on_and_the_application_has_a_broker_user()
        {
            LocalStack.RequireAvailable();
            Assert.Equal(0, await RaiseAsync(enabled: false, _apps.WithBroker));
            Assert.Equal(0, await RaiseAsync(enabled: true, _apps.Without));
            Assert.Equal(1, await RaiseAsync(enabled: true, _apps.WithBroker));
            await using var db = _database.Context();
            var row = await db.ProducerEvents.AsNoTracking().SingleAsync(Ct);
            var evt = ProducerEvents.Read(row.Payload);
            Assert.Equal((row.Id, "brokered.notification.interaction"), (evt.MessageId, evt.EventName));
        }

        /// <summary>The delivery.failed worker over this database, events on.</summary>
        private Delivering.FailureEvents Failures()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.AddDbContext<NotificationsDbContext>(services,
                options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
            return new Delivering.FailureEvents(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(provider),
                new ProducerEvents(Options.Create(new EventsOptions { Enabled = true })), TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Delivering.FailureEvents>.Instance);
        }

        /// <summary>
        /// delivery.failed (phase-3-plan I37, phases 3–4 review): raised once every one of a recipient's deliveries has ended without going out,
        /// whatever ended the last one (here a provider's bounce, not the dispatcher), and only once; a recipient one of whose deliveries went
        /// out hears nothing.
        /// </summary>
        [Fact]
        public async Task Delivery_failed_is_raised_once_when_the_last_destination_ends_however_it_ends()
        {
            LocalStack.RequireAvailable();
            await using var db = _database.Context();
            var type = Seed.Type(_apps.WithBroker, "brokered.alert");
            var integration = Seed.Integration(Tenant);
            db.AddRange(type, integration, new ApplicationTenant { ApplicationId = _apps.WithBroker, TenantId = Tenant, LinkedAt = Seed.Now });
            Delivery Make(string channel, DeliveryStatus status, bool due) => new()
            {
                TenantId = Tenant, Channel = channel, IntegrationId = integration.Id, DestinationKey = $"ext:{channel}:{Guid.NewGuid()}", DestinationSnapshot = "{}",
                Locale = "en", RendererVersion = "test", Content = "{}", CreatedAt = Seed.Now, Status = status, FailureCheckDue = due,
            };
            (Delivery Email, Delivery Sms) Recipient(DeliveryStatus email, DeliveryStatus sms)
            {
                var request = Seed.Request(Tenant, _apps.WithBroker, type.Id, Guid.NewGuid().ToString("N"));
                var notification = Seed.Notification(Tenant, request.Id);
                var recipient = Seed.Recipient(Tenant, notification.Id);
                recipient.Outcome = RecipientOutcome.Planned; // planned and through: nothing more to plan for them
                var (e, s) = (Make("email", email, email == DeliveryStatus.Failed), Make("sms", sms, false));
                db.AddRange(request, notification, recipient, e, s,
                    new DeliveryItem { TenantId = Tenant, DeliveryId = e.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = e.DestinationKey, Channel = "email" },
                    new DeliveryItem { TenantId = Tenant, DeliveryId = s.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = s.DestinationKey, Channel = "sms" });
                return (e, s);
            }
            var stillGoing = Recipient(DeliveryStatus.Failed, DeliveryStatus.Retrying);
            var wentOut = Recipient(DeliveryStatus.Failed, DeliveryStatus.Delivered);
            await db.SaveChangesAsync(Ct);
            var failures = Failures();

            await failures.RunOnceAsync(Ct);
            Assert.Equal(0, await db.ProducerEvents.CountAsync(Ct));
            Assert.False(await db.Deliveries.AnyAsync(d => d.FailureCheckDue, Ct));

            // The SMS bounces (as a provider callback ends it): now every destination has ended.
            await db.Deliveries.Where(d => d.Id == stillGoing.Sms.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatus.Bounced)
                .SetProperty(d => d.FailureCheckDue, true), Ct);
            await failures.RunOnceAsync(Ct);
            // Both marked again (a duplicate mark, a second worker): still once.
            await db.Deliveries.Where(d => d.Id == stillGoing.Sms.Id || d.Id == stillGoing.Email.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.FailureCheckDue, true), Ct);
            await failures.RunOnceAsync(Ct);

            var row = await db.ProducerEvents.AsNoTracking().SingleAsync(Ct);
            Assert.Equal("brokered.notification.delivery.failed", ProducerEvents.Read(row.Payload).EventName);
            Assert.Contains("\"email\"", row.Payload);
            Assert.Contains("\"sms\"", row.Payload);
        }

        /// <summary>One person's recipient with one failed inbox delivery, marked, in the given outcome.</summary>
        private async Task<(NotificationRecipient Recipient, Delivery Inbox)> FailedInboxAsync(RecipientOutcome outcome, DateTimeOffset? nextAt,
            Correlation? correlation = null, DateTimeOffset? expiresAt = null)
        {
            await using var db = _database.Context();
            var type = Seed.Type(_apps.WithBroker, "brokered.held." + Guid.NewGuid().ToString("N")[..6]);
            var integration = Seed.Integration(Tenant, "held-" + Guid.NewGuid().ToString("N")[..6]);
            var request = Seed.Request(Tenant, _apps.WithBroker, type.Id, Guid.NewGuid().ToString("N"));
            var notification = Seed.Notification(Tenant, request.Id);
            var recipient = Seed.Recipient(Tenant, notification.Id);
            db.Entry(request).Property(r => r.TypeKey).CurrentValue = type.Key;
            db.Entry(request).Property(r => r.ExpiresAt).CurrentValue = expiresAt;
            if (correlation is not null)
            {
                db.Add(correlation);
                db.Entry(request).Property(r => r.CorrelationId).CurrentValue = correlation.Id;
                db.Entry(request).Property(r => r.CorrelationKey).CurrentValue = correlation.Key;
            }
            notification.State = NotificationState.Planned;
            notification.NextAt = null;
            (recipient.Outcome, recipient.NextAt) = (outcome, nextAt);
            var inbox = new Delivery
            {
                TenantId = Tenant, Channel = Channels.Inbox, IntegrationId = integration.Id, DestinationKey = $"inbox:{Guid.NewGuid()}", DestinationSnapshot = "{}",
                Locale = "en", RendererVersion = "test", Content = "{}", CreatedAt = Seed.Now, Status = DeliveryStatus.Failed, FailureCheckDue = true,
            };
            db.AddRange(type, integration, request, notification, recipient, inbox,
                new DeliveryItem { TenantId = Tenant, DeliveryId = inbox.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = inbox.DestinationKey, Channel = Channels.Inbox });
            if (!await db.ApplicationTenants.AnyAsync(t => t.ApplicationId == _apps.WithBroker && t.TenantId == Tenant, Ct))
            {
                db.Add(new ApplicationTenant { ApplicationId = _apps.WithBroker, TenantId = Tenant, LinkedAt = Seed.Now });
            }
            await db.SaveChangesAsync(Ct);
            return (recipient, inbox);
        }

        /// <summary>The delivery planner over this database (no workers), to run its stage by hand.</summary>
        private ServiceProvider PlannerServices()
        {
            var services = new ServiceCollection().AddLogging();
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton(TimeProvider.System);
            services.AddPostgresClaimStore();
            services.AddDeliveryPlanning(PlannerOptions, runWorkers: false);
            return services.BuildServiceProvider();
        }

        private static readonly StageOptions PlannerOptions = new() { Batch = 20, Lease = TimeSpan.FromSeconds(30) };

        /// <summary>
        /// A person in quiet hours has only their inbox delivery so far: its failure raises nothing while more can be planned for them, and the
        /// mark moves to them. The delivery planner settling them (here: the request expired before the hours ended, so only the inbox stands)
        /// does nothing for the event, which is raised then (phases 3–4 review, rounds 3 to 5).
        /// </summary>
        [Fact]
        public async Task Delivery_failed_is_raised_once_the_delivery_planner_settles_a_deferred_person()
        {
            LocalStack.RequireAvailable();
            var (recipient, inbox) = await FailedInboxAsync(RecipientOutcome.Deferred, DateTimeOffset.UtcNow.AddSeconds(-1), expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));
            var failures = Failures();

            Assert.Equal(0, await failures.RunOnceAsync(Ct));
            await using var db = _database.Context();
            Assert.False(await db.ProducerEvents.AnyAsync(Ct));
            Assert.False((await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == inbox.Id, Ct)).FailureCheckDue);
            Assert.True((await db.Recipients.AsNoTracking().SingleAsync(r => r.Id == recipient.Id, Ct)).FailureCheckDue);

            await using (var planning = PlannerServices())
            {
                var runner = planning.GetRequiredService<StageRunner<NotificationRecipient, DeliveryPlannerStage>>();
                var pass = await runner.RunOnceAsync(DeliveryStageNames.DeliveryPlanner, PlannerOptions, Ct);
                Assert.Equal(1, pass.Completed);
            }
            var settled = await db.Recipients.AsNoTracking().SingleAsync(r => r.Id == recipient.Id, Ct);
            Assert.Equal((RecipientOutcome.Planned, (DateTimeOffset?)null), (settled.Outcome, settled.NextAt));

            Assert.Equal(1, await failures.RunOnceAsync(Ct));
            Assert.Equal("brokered.notification.delivery.failed", ProducerEvents.Read((await db.ProducerEvents.AsNoTracking().SingleAsync(Ct)).Payload).EventName);
            Assert.False((await db.Recipients.AsNoTracking().SingleAsync(r => r.Id == recipient.Id, Ct)).FailureCheckDue);
        }

        /// <summary>The same for a deferred person whose correlation is resolved: the close cancels them, and the event is raised then.</summary>
        [Fact]
        public async Task Delivery_failed_is_raised_once_a_closed_correlation_settles_a_deferred_person()
        {
            LocalStack.RequireAvailable();
            var correlation = new Correlation
            {
                TenantId = Tenant, ApplicationId = _apps.WithBroker, Key = "c-" + Guid.NewGuid().ToString("N")[..6], State = CorrelationState.Open,
                CreatedAt = Seed.Now, LastRequestAt = Seed.Now,
            };
            var (recipient, _) = await FailedInboxAsync(RecipientOutcome.Deferred, DateTimeOffset.UtcNow.AddHours(8), correlation);
            var failures = Failures();
            Assert.Equal(0, await failures.RunOnceAsync(Ct));

            await using var db = _database.Context();
            await db.Correlations.Where(c => c.Id == correlation.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.State, CorrelationState.Resolved), Ct);
            await CorrelationLifecycle.StopAsync(db, Tenant, correlation.Id, DateTimeOffset.UtcNow, Ct);
            Assert.Equal(RecipientOutcome.Cancelled, (await db.Recipients.AsNoTracking().SingleAsync(r => r.Id == recipient.Id, Ct)).Outcome);

            Assert.Equal(1, await failures.RunOnceAsync(Ct));
            Assert.Single(await db.ProducerEvents.AsNoTracking().ToListAsync(Ct));
        }

        /// <summary>
        /// A recipient another transaction holds (a retry, another instance's worker) is passed over, not waited for: its delivery keeps the
        /// mark and is checked on a later pass, so two workers can't deadlock over it.
        /// </summary>
        [Fact]
        public async Task A_held_recipient_is_passed_over_and_its_delivery_keeps_the_mark()
        {
            LocalStack.RequireAvailable();
            var (recipient, inbox) = await FailedInboxAsync(RecipientOutcome.Planned, null);
            var failures = Failures();
            await using (var other = _database.Context())
            {
                await using var transaction = await other.Database.BeginTransactionAsync(Ct);
                await other.Database.ExecuteSqlAsync($"SELECT 1 FROM notifications.notification_recipients WHERE id = {recipient.Id} FOR UPDATE", Ct);
                Assert.Equal(0, await failures.RunOnceAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
                await transaction.RollbackAsync(Ct);
            }
            await using var db = _database.Context();
            Assert.True((await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == inbox.Id, Ct)).FailureCheckDue);
            Assert.False(await db.ProducerEvents.AnyAsync(Ct));

            Assert.Equal(1, await failures.RunOnceAsync(Ct));
            Assert.Single(await db.ProducerEvents.AsNoTracking().ToListAsync(Ct));
        }
    }
}
