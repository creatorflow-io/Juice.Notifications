using System.Text.Json;

namespace Juice.Notifications.Api.Contracts
{
    /// <summary><c>PUT /api/v1/tenants/{tenantId}/integrations/{key}</c>: a provider connection for one channel. Its channel is fixed once created.</summary>
    public sealed record PutIntegrationBody(string? Channel, string? Kind, JsonElement? Settings, string? SecretRef, int? Priority, bool? Enabled);

    /// <summary><c>PUT …/integrations/{key}/targets/{targetKey}</c>: a shared destination, by address or by a secret reference to one.</summary>
    public sealed record PutTargetBody(string? Address, string? AddressRef, bool? Enabled);

    /// <summary><c>PUT …/routes/{key}</c>: which notifications reach which targets (design §6.3).</summary>
    public sealed record PutRouteBody(JsonElement? Match, List<string>? Targets, bool? Enabled);

    /// <summary><c>PUT …/allowlist</c>: a private host or network outbound HTTP may reach, with its ports (design §13.1).</summary>
    public sealed record PutAllowlistBody(string? Destination, List<int>? Ports);

    public sealed record IntegrationResponse(string Key, string Channel, string Kind, JsonElement Settings, string? SecretRef, int Priority, bool Enabled,
        string ManagedBy, IReadOnlyList<TargetResponse> Targets, DateTimeOffset UpdatedAt);

    public sealed record TargetResponse(string Key, string? Address, string? AddressRef, bool Enabled, string ManagedBy, DateTimeOffset UpdatedAt);

    public sealed record RouteResponse(string Key, JsonElement Match, IReadOnlyList<string> Targets, bool Enabled, string ManagedBy, DateTimeOffset UpdatedAt);

    public sealed record AllowlistResponse(string Destination, IReadOnlyList<int> Ports, string ManagedBy, DateTimeOffset UpdatedAt);

    /// <summary>
    /// <c>PUT …/resolvers/{key}</c>: a resolver for the people kinds (design §6.1): its implementation (<c>kind</c>, fixed once created), the
    /// recipient kinds it handles here (all it can, when left out on creation), and its settings.
    /// </summary>
    public sealed record PutResolverBody(string? Kind, List<string>? Handles, JsonElement? Settings, string? SecretRef, bool? Enabled);

    public sealed record ResolverResponse(string Key, string Kind, IReadOnlyList<string> Handles, JsonElement Settings, string? SecretRef, bool Enabled,
        string ManagedBy, DateTimeOffset UpdatedAt);

    /// <summary><c>POST …/subscribers/{id}/aliases</c>: links another source's identity to the subscriber (design D23).</summary>
    public sealed record LinkAliasBody(string? Source, string? ExternalId);

    public sealed record AliasResponse(string Source, string ExternalId, string? LinkedBy, DateTimeOffset LinkedAt);

    public sealed record SubscriberResponse(Guid Id, string Source, string ExternalId, string? Name, string? Locale, string? TimeZone, Guid? MergedInto,
        IReadOnlyList<AliasResponse> Aliases, DateTimeOffset CreatedAt);
}
