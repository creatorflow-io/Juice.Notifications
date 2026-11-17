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

    /// <summary><c>PUT …/local-users/{id}</c>: a person kept in this service for the <c>local</c> resolver. A field left out keeps its value.</summary>
    public sealed record PutLocalUserBody(string? Name, string? Locale, string? TimeZone);

    /// <summary><c>PUT …/groups/{key}</c>: a local group; its members are added one by one (<c>PUT …/groups/{key}/members/{subscriberId}</c>).</summary>
    public sealed record PutGroupBody(string? Name);

    public sealed record GroupResponse(string Key, string Name, int Members, DateTimeOffset UpdatedAt);

    /// <summary><c>PUT …/topics/{key}</c>: a topic; <c>selfService</c> (default true) lets people subscribe themselves.</summary>
    public sealed record PutTopicBody(string? Name, bool? SelfService);

    public sealed record TopicResponse(string Key, string Name, bool SelfService, int Subscribers, DateTimeOffset UpdatedAt);

    /// <summary>A topic as the signed-in person sees it: whether they follow it. Subscriber counts aren't shown.</summary>
    public sealed record MyTopicResponse(string Key, string Name, bool SelfService, bool Subscribed);

    /// <summary>A contact point the person adds: a channel that reaches people (<c>email</c>) and the address. It starts unverified.</summary>
    public sealed record AddContactPointBody(string? Channel, string? Address);

    /// <summary>The six-digit code sent to the address.</summary>
    public sealed record VerifyContactPointBody(string? Code);

    /// <summary>
    /// One of the person's contact points. Only a verified one that hasn't failed is ever used; <c>verifiedBy</c> is <c>self</c> (a code) or the
    /// source that vouched for it.
    /// </summary>
    public sealed record ContactPointResponse(Guid Id, string Channel, string Address, bool Verified, DateTimeOffset? VerifiedAt, string? VerifiedBy,
        ContactHealth Health, string? HealthReason, DateTimeOffset CreatedAt);

    /// <summary>A code is on its way; it works until <c>expiresAt</c>, for a few tries.</summary>
    public sealed record VerificationSentResponse(DateTimeOffset ExpiresAt);

    /// <summary>A Web Push subscription as the browser's <c>PushSubscription.toJSON()</c> gives it, keys flattened.</summary>
    public sealed record WebPushSubscriptionBody(string? Endpoint, string? P256dh, string? Auth);

    /// <summary>
    /// Registers the app on this device for push (design §9.2): <c>fcm</c> or <c>apns</c> with a token, or <c>webpush</c> with a subscription. The
    /// same token again refreshes the registration, and moves it to the caller.
    /// </summary>
    public sealed record RegisterDeviceBody(string? Platform, string? AppId, string? Token, WebPushSubscriptionBody? Subscription, string? Locale);

    /// <summary>A registration; its token is never returned.</summary>
    public sealed record DeviceResponse(Guid Id, string Platform, string AppId, string? Locale, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt);

    /// <summary>
    /// How the person hears about a category (a type's, or <c>*</c>) on a channel (<c>inbox</c>, <c>email</c>…, or <c>*</c>): <c>on</c>, <c>off</c>
    /// or <c>digest</c> (on until digests arrive). The most specific choice wins: the category's over <c>*</c>. A category off on <c>*</c> opts
    /// out of it; a mandatory type still reaches the person, and its inbox can't be turned off (design §6.4).
    /// </summary>
    public sealed record PreferenceBody(string? Category, string? Channel, string? Mode);

    /// <summary><c>PUT …/me/preferences</c>: replaces all of the person's preferences (an empty list: everything on).</summary>
    public sealed record PutPreferencesBody(List<PreferenceBody>? Preferences);

    public sealed record PreferenceResponse(string Category, string Channel, string Mode, DateTimeOffset UpdatedAt);

    /// <summary>
    /// <c>PUT …/me/quiet-hours</c>: a quiet period starting on each of <c>days</c> (<c>mon</c> … <c>sun</c>; left out: every day) at <c>start</c>
    /// and ending at <c>end</c> (<c>HH:mm</c>, local; an end before the start is the next day), in <c>timeZone</c> (an IANA id; left out: the
    /// person's, else UTC). Personal channels wait until it ends; the inbox doesn't, and a mandatory type doesn't wait.
    /// </summary>
    public sealed record QuietHoursBody(List<string>? Days, string? Start, string? End, string? TimeZone);

    public sealed record QuietHoursResponse(IReadOnlyList<string>? Days, string Start, string End, string? TimeZone);

    /// <summary>The signed-in person as the service knows them. Name, locale and time zone come from their identity's source.</summary>
    public sealed record MeResponse(Guid Id, string Source, string ExternalId, string? Name, string? Locale, string? TimeZone, QuietHoursResponse? QuietHours);

    /// <summary>
    /// <c>POST …/mutes</c> (the tenant's) or <c>…/me/mutes</c> (the person's own; a snooze is one with no match and an end): silences what
    /// <c>match</c> matches (a route's match: type patterns, labels, minPriority; left out: everything) from <c>startsAt</c> (left out: now) until
    /// <c>endsAt</c> (left out: until cancelled).
    /// </summary>
    public sealed record AddMuteBody(JsonElement? Match, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Reason);

    /// <summary>
    /// A mute. <c>appliesToMandatory</c>: a tenant mute that names a mandatory type exactly, so it silences that type too; a wildcard never does,
    /// and a personal mute never does (design §6.4).
    /// </summary>
    public sealed record MuteResponse(Guid Id, JsonElement Match, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, string? Reason, string? CreatedBy,
        DateTimeOffset CreatedAt, DateTimeOffset? CancelledAt, bool AppliesToMandatory);
}
