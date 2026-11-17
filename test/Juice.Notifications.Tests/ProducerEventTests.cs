using Juice.Notifications.Events;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Tests
{
    /// <summary>Events to producers as they are named and stored (phase-3-plan I37).</summary>
    public sealed class ProducerEventTests
    {
        [Fact]
        public void An_event_is_routed_by_its_application_and_name()
        {
            var evt = new NotificationEvent { Event = ProducerEventNames.Interaction, Application = "observability" };
            Assert.Equal("observability.notification.interaction", evt.EventName);
            Assert.Equal("observability.notification.delivery.failed", (evt with { Event = ProducerEventNames.DeliveryFailed }).EventName);
        }

        [Fact]
        public void An_event_reads_back_as_it_was_raised_with_its_id()
        {
            var id = Guid.CreateVersion7();
            var evt = new NotificationEvent
            {
                MessageId = id, Event = ProducerEventNames.Interaction, Application = "app", NotificationTenantId = Guid.NewGuid(), CorrelationKey = "k",
                Kind = "action", ActionId = "approve", Channels = ["email"], Names = ["group:sre"], OccurredAt = DateTimeOffset.UnixEpoch,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(evt, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            var read = ProducerEvents.Read(json);
            Assert.Equal((id, "action", "approve", "app.notification.interaction"), (read.MessageId, read.Kind, read.ActionId, read.EventName));
            Assert.Equal(["email"], read.Channels!);
        }

        [Fact]
        public void Events_are_off_unless_configured()
            => Assert.False(new ProducerEvents(Options.Create(new EventsOptions())).Enabled);
    }
}
