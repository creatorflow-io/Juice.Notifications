using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using static Juice.Notifications.IntegrationTests.DeliveryTests;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Events to producers over the broker (phase-3-plan M23, I37; design §19 decision 12): an application's broker user binds a queue of its own
    /// to <c>{app}.#</c> on the events exchange and hears what happened to its notifications; binding to another application's events, or to all
    /// of them, is refused by the broker's topic permissions.
    /// </summary>
    public sealed class ProducerEventBrokerTests(RabbitMQIntakeApp broker) : IClassFixture<RabbitMQIntakeApp>
    {
        private const string Exchange = "juice.notifications.events";
        private static readonly string Tenant = $"/api/v1/tenants/{RabbitMQIntakeApp.Tenant}";

        private static async Task<(IConnection Connection, IChannel Channel, string Queue)> BindAsync(Task<IConnection> connect, string routingKey)
        {
            var connection = await connect;
            var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
            var queue = (await channel.QueueDeclareAsync("", durable: false, exclusive: true, autoDelete: true, cancellationToken: Ct)).QueueName;
            await channel.QueueBindAsync(queue, Exchange, routingKey, cancellationToken: Ct);
            return (connection, channel, queue);
        }

        [Fact]
        public async Task An_application_hears_its_own_events_and_the_broker_refuses_binding_to_anothers()
        {
            LocalStack.RequireAvailable();
            var (connection, channel, queue) = await BindAsync(broker.ConnectAsync(broker.Producer), $"{RabbitMQIntakeApp.Key}.#");
            await using var _ = connection;
            await using var __ = channel;

            // Another application's broker user can't bind to this one's events, and this one can't bind to everything.
            foreach (var (user, key) in new[] { (broker.Stranger, $"{RabbitMQIntakeApp.Key}.#"), (broker.Producer, "#") })
            {
                var refused = await Assert.ThrowsAnyAsync<OperationInterruptedException>(async () =>
                {
                    var (c, ch, _) = await BindAsync(broker.ConnectAsync(user), key);
                    await ch.DisposeAsync();
                    await c.DisposeAsync();
                });
                Assert.Equal((ushort)403, refused.ShutdownReason!.ReplyCode);
            }

            // A correlated notification, resolved by the producer: the application hears correlation.resolved.
            using var producer = broker.App.Client(await LocalStack.ProducerTokenAsync());
            var type = $"{RabbitMQIntakeApp.Key}.correlated";
            await Ok(producer.PutAsJsonAsync($"/api/v1/applications/{RabbitMQIntakeApp.Key}/types", new DeclareTypeBody(type, "Correlated", "alerts", null, null,
                null, null, DeliveryTests.Json("""{ "type": "object" }"""), "optional", null, null, DeliveryTests.Json("""{ "webhook": "passthrough" }""")), Ct));
            var correlation = "c" + Guid.NewGuid().ToString("N");
            await Ok(producer.PostAsJsonAsync($"{Tenant}/notifications", new SendNotificationBody(type, null, Guid.NewGuid().ToString("N"), correlation, null,
                null, null, null, DeliveryTests.Json("{}"), null, null), Ct));
            await Ok(producer.PostAsJsonAsync($"{Tenant}/correlations/{correlation}/resolve", new ResolveCorrelationBody("done", "test"), Ct));

            for (var i = 0; i < 150; i++)
            {
                var message = await channel.BasicGetAsync(queue, autoAck: true, Ct);
                if (message is null)
                {
                    await Task.Delay(100, Ct);
                    continue;
                }
                using var body = JsonDocument.Parse(Encoding.UTF8.GetString(message.Body.Span));
                if (body.RootElement.GetProperty("CorrelationKey").GetString() != correlation)
                {
                    continue; // another test's event
                }
                Assert.Equal($"{RabbitMQIntakeApp.Key}.notification.correlation.resolved", message.RoutingKey);
                Assert.Equal("correlation.resolved", body.RootElement.GetProperty("Event").GetString());
                Assert.Equal(RabbitMQIntakeApp.Key, body.RootElement.GetProperty("Application").GetString());
                return;
            }
            Assert.Fail("No correlation.resolved event reached the application's queue.");
        }
    }
}
