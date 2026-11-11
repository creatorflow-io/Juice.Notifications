namespace Juice.Notifications.Api.Contracts
{
    /// <summary><c>POST /api/v1/applications</c>.</summary>
    public sealed record CreateApplicationBody(string Key, string Name, string ClientId);

    /// <summary><c>PUT /api/v1/applications/{key}</c>: only the fields given change.</summary>
    public sealed record UpdateApplicationBody(string? Name, string? ClientId, bool? Enabled);

    public sealed record ApplicationResponse(Guid Id, string Key, string Name, string ClientId, bool Enabled, IReadOnlyList<Guid> Tenants,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
}
