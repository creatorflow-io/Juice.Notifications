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
    }
}
