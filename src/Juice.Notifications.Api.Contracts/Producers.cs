using System.Text.Json;

namespace Juice.Notifications.Api.Contracts
{
    /// <summary>
    /// <c>PUT /api/v1/applications/{app}/types</c> (design §5.1). <c>templates</c> maps a channel to a template (the <c>en</c> locale) or to an object
    /// of locale → template; <c>"passthrough"</c> sends the data as is on channels that allow it (webhook).
    /// </summary>
    public sealed record DeclareTypeBody(
        string? Key, string? Name, string? Category, string? DisplayGroup, string? Priority, bool? Mandatory, bool? ExternalRecipients,
        JsonElement? Schema, string? Correlation, List<string>? Redact, JsonElement? Workflow, JsonElement? Templates);

    public sealed record TypeVersionResponse(string Key, int Version, string Name, string SchemaHash, DateTimeOffset CreatedAt);

    public sealed record TypeVersionDetailResponse(
        string Key, int Version, string Name, string Category, string DisplayGroup, NotificationPriority Priority, bool Mandatory, bool ExternalRecipients,
        string Correlation, IReadOnlyList<string> Redact, JsonElement Schema, JsonElement Workflow,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Templates, string SchemaHash, DateTimeOffset CreatedAt);

    /// <summary><c>{ "kind": "route" }</c>, or <c>{ "kind": "address", "channel": "email", "address": "…" }</c> for a type that allows it.</summary>
    public sealed record RecipientBody(string? Kind, string? Channel = null, string? Address = null);

    public sealed record ActorBody(string? Kind, string? Id);

    /// <summary><c>POST /api/v1/tenants/{tenantId}/notifications</c> (design §5.2).</summary>
    public sealed record SendNotificationBody(
        string? Type, int? TypeVersion, string? IdempotencyKey, string? CorrelationKey, List<RecipientBody>? Recipients, ActorBody? Actor,
        string? Priority, Dictionary<string, string>? Labels, JsonElement? Data, Dictionary<string, string>? Links, DateTimeOffset? ExpiresAt);

    /// <summary><c>202</c> for a new request; <c>200</c> with the original for a repeated idempotency key.</summary>
    public sealed record AcceptedResponse(Guid Id, string Status, string Type, int TypeVersion, DateTimeOffset AcceptedAt);

    public sealed record NotificationResponse(
        Guid Id, string Type, int TypeVersion, string IdempotencyKey, string? CorrelationKey, NotificationPriority Priority,
        IReadOnlyDictionary<string, string> Labels, IReadOnlyDictionary<string, string> Links, string? Actor, DateTimeOffset AcceptedAt,
        DateTimeOffset? ExpiresAt, NotificationState State, string? Error, IReadOnlyList<DeliveryResponse> Deliveries);

    public sealed record DeliveryResponse(Guid Id, string Channel, string DestinationKey, DeliveryStatus Status, int Attempts, FailureCategory? FailureCategory,
        DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

    /// <summary>One problem with one field, in a problem's <c>errors</c>: a JSON pointer into what was sent, and a sentence.</summary>
    public sealed record FieldErrorResponse(string Pointer, string Message);
}
