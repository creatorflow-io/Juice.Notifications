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

    /// <summary>
    /// <c>GET …/providers</c>: a channel and provider kind this deployment can send through. <c>Signs</c>: an integration of it must name a
    /// secret. <c>RegistersCallbacks</c>: <c>POST …/integrations/{key}/callbacks</c> tells the provider where its callbacks go.
    /// </summary>
    public sealed record ProviderResponse(string Channel, string Kind, bool Signs, bool RegistersCallbacks);

    /// <summary>
    /// <c>GET …/resolver-kinds</c>: a resolver kind this deployment has, which a tenant's resolver can be (<c>PUT …/resolvers/{key}</c>), and
    /// the recipient kinds it can handle.
    /// </summary>
    public sealed record ResolverKindResponse(string Kind, IReadOnlyList<string> Handles);

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

    /// <summary>
    /// <c>PUT …/escalation-policies/{key}</c> (phase-3-plan I34): steps <c>[{ "afterMinutes": 15, "recipients": [ … ] }]</c> (recipients as a request
    /// names them: a route, or people through the tenant's resolvers), and whether acknowledging stops it (default true). Left out: as stored.
    /// </summary>
    public sealed record PutEscalationPolicyBody(JsonElement? Steps, bool? StopOnAcknowledge);

    public sealed record EscalationPolicyResponse(string Key, JsonElement Steps, bool StopOnAcknowledge, string ManagedBy, DateTimeOffset UpdatedAt);

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

    /// <summary>A type the tenant receives, at its latest version, and per channel whether its application and the tenant have a template.</summary>
    public sealed record TenantTypeResponse(string Key, string Name, int Version, string Application, IReadOnlyList<TenantTypeChannelResponse> Channels);

    public sealed record TenantTypeChannelResponse(string Channel, bool Declared, bool Overridden);

    /// <summary>
    /// <c>GET …/types/{key}</c>: the type's latest version as declared, its versions, where its inbox items go in this tenant (<c>group</c>;
    /// <c>groupChosen</c> when the tenant chose it, otherwise the type's own <c>displayGroup</c>) and its channels' templates by locale.
    /// <c>serviceType</c>: the service's own type, whose templates can't be changed.
    /// </summary>
    public sealed record TenantTypeDetailResponse(
        string Key, string Application, int Version, string Name, string Category, string DisplayGroup, string Group, bool GroupChosen,
        NotificationPriority Priority, bool Mandatory, bool ExternalRecipients, string Correlation, JsonElement Schema, bool ServiceType,
        IReadOnlyList<TypeVersionResponse> Versions, IReadOnlyList<TenantTypeChannelDetailResponse> Channels);

    public sealed record TenantTypeChannelDetailResponse(string Channel, bool Declared, bool Overridden, IReadOnlyList<TenantTypeLocaleResponse> Locales);

    public sealed record TenantTypeLocaleResponse(string Locale, bool Declared, bool Overridden);

    /// <summary>A channel's templates for a type version: the application's and the tenant's own (null when it has none).</summary>
    public sealed record TenantTemplateResponse(string Type, int TypeVersion, string Channel, string Locale, string? Application, string? Tenant, int? TenantVersion,
        DateTimeOffset? UpdatedAt);

    /// <summary>A tenant's template (phase-4-plan I45): Scriban text, or <c>passthrough</c> where the channel allows it.</summary>
    public sealed record PutTemplateBody(string? Body, string? Locale);

    /// <summary>
    /// Renders <c>body</c> (or the template in effect in <c>locale</c>, the default one without it, when there is no body) with <c>data</c> (or a
    /// sample from the type's schema); nothing is stored.
    /// </summary>
    public sealed record PreviewBody(string? Type, string? Channel, string? Body, JsonElement? Data, string? Locale = null);

    public sealed record PreviewResponse(string Channel, JsonElement? Content, string? Error, JsonElement Data);

    /// <summary>A tenant's settings: a null field takes the deployment's default.</summary>
    public sealed record TenantSettingsBody(int? StormThreshold, int? RequestRetentionDays = null, int? ContentRetentionDays = null);

    public sealed record TenantSettingsResponse(int? StormThreshold, int DefaultStormThreshold, int? RequestRetentionDays, int DefaultRequestRetentionDays,
        int? ContentRetentionDays, int DefaultContentRetentionDays, DateTimeOffset? UpdatedAt);

    /// <summary>A storm on a route (phase-4-plan I43): going while <c>endedAt</c> is null.</summary>
    public sealed record StormResponse(Guid Id, string Route, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int Peak, int Digested);

    /// <summary>Asks for a code to link a chat account (phase-4-plan I41): <c>slack</c>, <c>telegram</c>.</summary>
    public sealed record LinkCodeBody(string? Channel);

    /// <summary>The code to send the tenant's bot from the account to link, until <see cref="ExpiresAt"/>; once used, the account is a contact point.</summary>
    public sealed record LinkCodeResponse(string Channel, string Code, DateTimeOffset ExpiresAt);

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
