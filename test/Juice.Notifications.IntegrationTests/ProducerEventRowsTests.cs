using Juice.Notifications.Data;
using Juice.Notifications.Events;
using Microsoft.EntityFrameworkCore;
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
    }
}
