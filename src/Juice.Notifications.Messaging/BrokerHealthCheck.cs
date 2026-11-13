using Juice.EventBus.RabbitMQ;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Juice.Notifications.Messaging
{
    /// <summary>
    /// The intake's broker connection, for <c>/health/ready</c>: Healthy when connected, Degraded when not, never Unhealthy. HTTP is the main way in,
    /// so a broker outage is reported without taking the service out of rotation (<c>/health/ready</c> stays 200 and says Degraded). It only
    /// reads the connection's state: the RabbitMQ client recovers the connection by itself, and a probe that reconnected would wait on it.
    /// </summary>
    internal sealed class BrokerHealthCheck(IRabbitMQPersistentConnection connection) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(connection.IsConnected
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded("The RabbitMQ intake isn't connected to its broker; NotificationRequested messages wait on the queue."));
    }
}
