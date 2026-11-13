namespace Juice.Notifications.Api.Contracts
{
    /// <summary>
    /// <c>POST /api/v1/applications</c>. <see cref="BrokerUser"/> is the RabbitMQ user the application publishes <c>NotificationRequested</c> as;
    /// without one it sends over HTTP only.
    /// </summary>
    public sealed record CreateApplicationBody(string Key, string Name, string ClientId, string? BrokerUser = null);

    /// <summary><c>PUT /api/v1/applications/{key}</c>: only the fields given change. <see cref="BrokerUser"/> <c>""</c> removes it.</summary>
    public sealed record UpdateApplicationBody(string? Name, string? ClientId, bool? Enabled, string? BrokerUser = null);

    public sealed record ApplicationResponse(Guid Id, string Key, string Name, string ClientId, string? BrokerUser, bool Enabled, IReadOnlyList<Guid> Tenants,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
}
