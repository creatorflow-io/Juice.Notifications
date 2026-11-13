using Juice.EventBus.RabbitMQ;
using Juice.Notifications.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace Juice.Notifications.Tests
{
    /// <summary>A broker outage is reported, but never as unhealthy: HTTP intake stays in rotation.</summary>
    public sealed class BrokerHealthCheckTests
    {
        private sealed class Connection(bool connected) : IRabbitMQPersistentConnection
        {
            public string Name => "test";
            public bool IsConnected => connected;
            public int Connects { get; private set; }
            public ValueTask<bool> TryConnectAsync(CancellationToken cancellationToken = default) { Connects++; return ValueTask.FromResult(false); }
            public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
            public ValueTask<IChannel?> CreateChannelAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IChannel?>(null);
            public ValueTask<IChannel?> CreateChannelAsync(CreateChannelOptions? options, CancellationToken cancellationToken = default) => ValueTask.FromResult<IChannel?>(null);
            public void Dispose() { }
        }

        [Theory]
        [InlineData(true, HealthStatus.Healthy)]
        [InlineData(false, HealthStatus.Degraded)]
        public async Task The_broker_is_healthy_when_connected_and_degraded_otherwise(bool connected, HealthStatus expected)
        {
            var connection = new Connection(connected);
            var result = await new BrokerHealthCheck(connection).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
            Assert.Equal(expected, result.Status);
            Assert.Equal(0, connection.Connects); // a probe doesn't reconnect, so it never waits on the broker
        }
    }
}
