namespace Juice.Notifications.Data
{
    // The phase 1 tables (phase-1-plan §4, design §12). Tenant-owned rows are keyed (TenantId, Id), and every reference between them
    // uses both columns, so the database itself refuses a row that points at another tenant's row.

    /// <summary>A row owned by one tenant.</summary>
    public interface ITenantOwned
    {
        Guid TenantId { get; }
        Guid Id { get; }
    }

    /// <summary>
    /// A row a stage worker claims (design §11). <see cref="Version"/> is the fencing token: it moves on every claim and every write, and a write
    /// that names an older version affects nothing. <see cref="NextAt"/> is null once the row has nothing left to do.
    /// </summary>
    public abstract class Claimable
    {
        public int Version { get; set; }
        public string? ClaimedBy { get; set; }
        public DateTimeOffset? LeaseUntil { get; set; }
        public DateTimeOffset? NextAt { get; set; }
        /// <summary>Steps on this row that threw in a row; the runner backs off on each and gives the row up at the stage's limit.</summary>
        public int StepFailures { get; set; }
    }

    /// <summary>A registered producer, platform-wide. It owns the types under <c>{Key}.*</c> and sends for the tenants it's linked to.</summary>
    public sealed class Application : Juice.Domain.IAuditable
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        /// <summary>The type namespace, such as <c>observability</c>.</summary>
        public required string Key { get; init; }
        public required string Name { get; set; }
        /// <summary>The OIDC client id its tokens carry (<c>azp</c> or <c>client_id</c>).</summary>
        public required string ClientId { get; set; }
        /// <summary>
        /// The RabbitMQ user it publishes <c>NotificationRequested</c> as, which the broker vouches for (<c>user_id</c>); null when it sends
        /// over HTTP only.
        /// </summary>
        public string? BrokerUser { get; set; }
        public bool Enabled { get; set; } = true;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>A tenant an application may send for. A request references this row, so an unlinked tenant can't receive one.</summary>
    public sealed class ApplicationTenant : Juice.Domain.IAuditable
    {
        public Guid ApplicationId { get; init; }
        public Guid TenantId { get; init; }
        public DateTimeOffset LinkedAt { get; init; }
    }

    /// <summary>One version of a notification type, declared by its application. Versions are never changed or removed (design D10).</summary>
    public sealed class NotificationType
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid ApplicationId { get; init; }
        public required string Key { get; init; }
        public int Version { get; init; }
        public required string Name { get; init; }
        public required string Category { get; init; }
        /// <summary>The default display group key (design D12).</summary>
        public required string DisplayGroup { get; init; }
        public NotificationPriority Priority { get; init; }
        public bool Mandatory { get; init; }
        public bool ExternalRecipients { get; init; }
        /// <summary>Whether requests carry a correlation key: <c>none</c>, <c>optional</c> or <c>required</c> (design §5.1). Stored in phase 1, acted on in phase 3.</summary>
        public string Correlation { get; init; } = CorrelationRules.Optional;
        /// <summary>The data schema, JSON Schema 2020-12.</summary>
        public required string Schema { get; init; }
        public required string SchemaHash { get; init; }
        /// <summary>The workflow steps, as JSON. Phase 1: <c>[{"step":"send"}]</c>.</summary>
        public required string Workflow { get; init; }
        /// <summary>JSON paths masked in stored data after rendering, and in logs.</summary>
        public string[] Redact { get; init; } = [];
        /// <summary>Inbox actions, as JSON (phase 3).</summary>
        public string? Actions { get; init; }
        /// <summary>Of the whole declaration: the same declaration again returns this version.</summary>
        public required string DeclarationHash { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>A template for one type version, channel and locale; <see cref="TenantId"/> is null for the application's default.</summary>
    public sealed class Template
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid? TenantId { get; init; }
        public Guid TypeId { get; init; }
        public required string Channel { get; init; }
        public required string Locale { get; init; }
        public int Version { get; init; }
        /// <summary><c>scriban</c> (design D22).</summary>
        public required string Engine { get; init; }
        /// <summary>A WYSIWYG editor's block model, compiled to <see cref="Body"/>; null until one exists.</summary>
        public string? Source { get; init; }
        public required string Body { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>A tenant's provider connection for one channel. <see cref="SecretRef"/> names where the secret is; the secret is never stored.</summary>
    public sealed class Integration : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public required string Key { get; init; }
        public required string Channel { get; init; }
        /// <summary>The provider: <c>webhook</c>, <c>smtp</c>.</summary>
        public required string Kind { get; set; }
        /// <summary>Provider settings, as JSON.</summary>
        public required string Settings { get; set; }
        public string? SecretRef { get; set; }
        /// <summary>Failover order within a channel (phase 4).</summary>
        public int Priority { get; set; }
        public bool Enabled { get; set; } = true;
        public string ManagedBy { get; set; } = Notifications.ManagedBy.Api;
        public required string DefinitionHash { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>A shared destination reached through an integration: a webhook URL, a mailbox, a Slack channel. It belongs to no person.</summary>
    public sealed class IntegrationTarget : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid IntegrationId { get; set; }
        public required string Key { get; init; }
        /// <summary>The address, when it isn't itself a secret.</summary>
        public string? Address { get; set; }
        /// <summary>Where the address is, when it is a secret (a chat webhook URL).</summary>
        public string? AddressRef { get; set; }
        public bool Enabled { get; set; } = true;
        public string ManagedBy { get; set; } = Notifications.ManagedBy.Api;
        public required string DefinitionHash { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>Sends matching requests to shared targets. Adds recipients; never removes ones a request named.</summary>
    public sealed class Route : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public required string Key { get; init; }
        /// <summary>Type pattern, label matchers and minimum priority, as JSON.</summary>
        public required string Match { get; set; }
        /// <summary>Integration target keys.</summary>
        public string[] Targets { get; set; } = [];
        public int DigestSeconds { get; set; }
        public bool Enabled { get; set; } = true;
        public string ManagedBy { get; set; } = Notifications.ManagedBy.Api;
        public required string DefinitionHash { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>One target a route names (deferred #8): the database keeps a named target from being deleted.</summary>
    public sealed class RouteTarget
    {
        public Guid TenantId { get; init; }
        public Guid RouteId { get; init; }
        public Guid TargetId { get; init; }
        /// <summary>
        /// The target's place in the route's list. Rewritten on every Put of the route, so the Put writes each row it keeps: one deleted under it
        /// meanwhile fails the Put (a concurrency conflict) instead of leaving the route naming a target nothing holds.
        /// </summary>
        public int Position { get; set; }
    }

    /// <summary>A private host or CIDR, with ports, that outbound HTTP may reach (design §13.1).</summary>
    public sealed class AllowlistEntry : Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        /// <summary>A CIDR (<c>10.20.0.0/16</c>) or an exact host name.</summary>
        public required string Destination { get; init; }
        /// <summary>Allowed ports; empty for 443 only.</summary>
        public int[] Ports { get; set; } = [];
        public string ManagedBy { get; set; } = Notifications.ManagedBy.Api;
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>One "this happened" from a producer, exactly as accepted, pinned to the type version it was validated against.</summary>
    public sealed class NotificationRequest : ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid ApplicationId { get; init; }
        public required string IdempotencyKey { get; init; }
        public Guid TypeId { get; init; }
        public required string TypeKey { get; init; }
        public int TypeVersion { get; init; }
        public required string SchemaHash { get; init; }
        public string? CorrelationKey { get; init; }
        /// <summary>The correlation the key names, for the request's application (phase-3-plan I32).</summary>
        public Guid? CorrelationId { get; init; }
        public NotificationPriority Priority { get; init; }
        public Dictionary<string, string> Labels { get; init; } = [];
        /// <summary>The request's data, as JSON. Paths the type redacts are masked after rendering.</summary>
        public required string Data { get; set; }
        /// <summary>The recipients the request named, as JSON.</summary>
        public required string Recipients { get; init; }
        /// <summary>Who caused it, as the producer names them (a user id, a service account), shown in history. Plain text, up to 400 characters.</summary>
        public string? Actor { get; init; }
        public Dictionary<string, string> Links { get; init; } = [];
        public DateTimeOffset? ExpiresAt { get; init; }
        public DateTimeOffset AcceptedAt { get; init; }
        /// <summary>
        /// When what the request tells of happened, as the producer says (phase 5: an alert's firing time); null when it didn't say (and on
        /// requests from before it was kept). Only an escalation's first step reads it: due its wait after this, never before now, even when a
        /// workflow delay ran first; null, the wait runs from when the escalation starts, as before.
        /// </summary>
        public DateTimeOffset? OccurredAt { get; init; }
        /// <summary>
        /// Accepted in dry run (phase-5 M44-2b, <c>Delivery:DryRun</c> at acceptance): the deliveries to its own recipients are dry whatever the
        /// mode is when they are planned, so work accepted in shadow and planned after the switch (a delay, a digest) never goes out beside
        /// legacy's. An escalation step's recipients take the mode when the step is planned instead.
        /// </summary>
        public bool DryRun { get; init; }
        /// <summary>When the type's <c>redact</c> paths were masked in <see cref="Data"/> (after everything was rendered, phase-4-plan I46).</summary>
        public DateTimeOffset? RedactedAt { get; set; }
        /// <summary>The producer's W3C trace context, carried to every stage.</summary>
        public string? TraceParent { get; init; }
    }

    /// <summary>
    /// A resolver a tenant uses for the people kinds (design §6.1, D20): which implementation (<see cref="Kind"/>), which recipient kinds it
    /// handles here, and its settings. A request names it by <see cref="Key"/>, or gets the only enabled one that handles the kind.
    /// </summary>
    public sealed class RecipientResolverConfig : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public required string Key { get; init; }
        /// <summary>The implementation: <c>local</c>, <c>topic</c>, <c>identity</c>.</summary>
        public required string Kind { get; set; }
        /// <summary>The recipient kinds it handles for this tenant, a subset of what the implementation can.</summary>
        public string[] Handles { get; set; } = [];
        public required string Settings { get; set; }
        public string? SecretRef { get; set; }
        public bool Enabled { get; set; } = true;
        public string ManagedBy { get; set; } = Notifications.ManagedBy.Api;
        public required string DefinitionHash { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>
    /// A person known to the service, by the source that first produced them and its id for them (design D23). Created the first time a resolver
    /// returns them, never by matching addresses.
    /// </summary>
    public sealed class Subscriber : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        /// <summary>The resolver's source: <c>local</c>, <c>oidc</c>.</summary>
        public required string Source { get; init; }
        public required string ExternalId { get; init; }
        public string? Name { get; set; }
        public string? Locale { get; set; }
        /// <summary>An IANA time zone id.</summary>
        public string? TimeZone { get; set; }
        /// <summary>
        /// Set while this person's identity is linked as an alias of another subscriber: resolving them reaches that one. Their own rows stay as
        /// history; unlinking clears it.
        /// </summary>
        public Guid? MergedInto { get; set; }
        /// <summary>
        /// Moves on every link or unlink that relies on this subscriber (as the one linked into, or the one merged): two links that would chain
        /// subscribers can't both commit. The planner's upsert doesn't move it.
        /// </summary>
        public int Version { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        /// <summary>
        /// When the current hour of verification sends began, and how many went out in it: a person sends a few codes an hour at most, however
        /// many contact points they add and remove (phase-2-plan M13). Written by one atomic update, never through the tracked row.
        /// </summary>
        public DateTimeOffset? VerificationWindowStart { get; set; }
        public int VerificationsInWindow { get; set; }
        /// <summary>
        /// When personal channels hold their notifications (design §6.4, phase-2-plan I21), as JSON: <c>{ "days": ["mon", …], "start": "22:00",
        /// "end": "07:00", "timeZone": "Europe/Berlin" }</c>. Null when the person has none. The person sets it; no source does.
        /// </summary>
        public string? QuietHours { get; set; }
    }

    /// <summary>
    /// Where a person can be reached on a channel (design §9, §12, phase-2-plan I19): the address encrypted under the key <see cref="KeyId"/>, with
    /// an HMAC of the normalized address (<see cref="AddressHash"/>) for uniqueness. Planned only once verified, and never once failed (§6.4).
    /// </summary>
    public sealed class ContactPoint : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid SubscriberId { get; init; }
        public required string Channel { get; init; }
        /// <summary>The normalized address, AES-GCM encrypted (<c>ContactCrypto</c>), base64.</summary>
        public required string Address { get; init; }
        public required string AddressHash { get; init; }
        public required string KeyId { get; init; }
        /// <summary>The address as history shows it (<c>j…@example.com</c>), kept so planning never decrypts.</summary>
        public required string Masked { get; init; }
        public DateTimeOffset? VerifiedAt { get; set; }
        /// <summary><c>self</c> (the person entered a code), or the source that vouched for it (<c>oidc</c>).</summary>
        public string? VerifiedBy { get; set; }
        public ContactHealth Health { get; set; }
        public string? HealthReason { get; set; }
        public DateTimeOffset? HealthChangedAt { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>The code outstanding for a contact point (phase-2-plan I20): one at a time, hashed, a few tries, a short life.</summary>
    /// <summary>
    /// A one-time code a person sends a chat bot to link their account there (phase-4-plan I41): stored as a keyed hash, unique per tenant, valid
    /// for 15 minutes, used once. The account that sends it becomes the person's verified contact point on the channel.
    /// </summary>
    public sealed class LinkCode : ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid SubscriberId { get; init; }
        public required string Channel { get; init; }
        public required string CodeHash { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset? UsedAt { get; set; }
        /// <summary>The contact point the code made, once used.</summary>
        public Guid? ContactPointId { get; set; }
    }

    /// <summary>
    /// A storm on a route (design §14, phase-4-plan I43): more notifications reached it in a minute than the tenant's threshold. While it lasts the
    /// route's recipients join a one-minute digest per type; it ends once the route has stayed under half the threshold for five minutes. One
    /// open storm per route.
    /// </summary>
    public sealed class Storm : ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public required string RouteKey { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; set; }
        /// <summary>The last minute the route was at half the threshold or more: the storm ends five minutes after it.</summary>
        public DateTimeOffset LastHighAt { get; set; }
        /// <summary>The most notifications the route took in one minute during the storm.</summary>
        public int Peak { get; set; }
        /// <summary>Route recipients sent to digests during the storm.</summary>
        public int Digested { get; set; }
    }

    /// <summary>Notifications that reached a route in one minute (storm counting, I43); rows older than an hour are dropped.</summary>
    public sealed class RouteMinute
    {
        public Guid TenantId { get; init; }
        public required string RouteKey { get; init; }
        public DateTimeOffset Minute { get; init; }
        public int Count { get; set; }
    }

    /// <summary>A tenant's own settings (phase-4-plan §4): its storm threshold; retention from M33. Null fields take the deployment's defaults.</summary>
    public sealed class TenantSettings : Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public int? StormThreshold { get; set; }
        /// <summary>Days requests, deliveries and attempts are kept (design §13.2: 90 by default).</summary>
        public int? RequestRetentionDays { get; set; }
        /// <summary>Days deliveries keep their rendered content (14 by default); then it is cleared, and they can't be retried.</summary>
        public int? ContentRetentionDays { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public sealed class ContactVerification
    {
        public Guid TenantId { get; init; }
        public Guid ContactPointId { get; init; }
        public required string CodeHash { get; set; }
        public DateTimeOffset SentAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public int Tries { get; set; }
    }

    /// <summary>
    /// A device a person's app registered for push (design §9.2): the token (for Web Push, the subscription) encrypted like an address, with its
    /// hash unique per tenant, platform and app, so registering it again moves it to whoever registered it last.
    /// </summary>
    public sealed class DeviceRegistration : ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid SubscriberId { get; set; }
        public required string Platform { get; init; }
        public required string AppId { get; init; }
        public required string Token { get; set; }
        public required string TokenHash { get; init; }
        public required string KeyId { get; set; }
        public string? Locale { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset LastSeenAt { get; set; }
    }

    /// <summary>
    /// How a person hears about a category on a channel (design §6.4, §12): <c>on</c>, <c>off</c> or <c>digest</c> (on until phase 3).
    /// <see cref="Category"/> or <see cref="Channel"/> is <c>*</c> for all of them. A category off on every channel (<c>channel = *</c>) opts the
    /// person out of it; a channel off keeps the category but not that channel.
    /// </summary>
    public sealed class Preference : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid SubscriberId { get; init; }
        public required string Category { get; init; }
        public required string Channel { get; init; }
        public required string Mode { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>
    /// A mute (design §6.1 step 4, §6.4, §12): the tenant's (<see cref="SubscriberId"/> null), or one person's own, which a snooze is. It silences
    /// what <see cref="Match"/> matches between <see cref="StartsAt"/> and <see cref="EndsAt"/> (none: until cancelled). Never deleted: cancelling
    /// sets <see cref="CancelledAt"/>, so "why didn't I get it?" can still be answered.
    /// </summary>
    public sealed class Mute : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid? SubscriberId { get; init; }
        /// <summary>A route's match (type patterns, labels, minimum priority), as JSON; <c>{}</c> matches everything.</summary>
        public required string Match { get; init; }
        public DateTimeOffset StartsAt { get; init; }
        public DateTimeOffset? EndsAt { get; init; }
        public string? Reason { get; init; }
        public string? CreatedBy { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? CancelledAt { get; set; }
    }

    /// <summary>
    /// A display group in a tenant's catalog (design D12, phase-2-plan I24): where the inbox shows a type's items. <c>approval</c>, <c>system</c>
    /// and <c>general</c> are seeded on first use (<see cref="BuiltIn"/>): they can be renamed and reordered, never deleted. A group's behaviour never
    /// depends on its key or name.
    /// </summary>
    public sealed class DisplayGroup : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public required string Key { get; init; }
        public required string Name { get; set; }
        /// <summary>Order in the inbox, ascending; ties by key.</summary>
        public int Position { get; set; }
        public bool BuiltIn { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>A tenant's choice of display group for a type (every version of its key), over the group the type declares (design D12).</summary>
    public sealed class TypeDisplayGroup : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public required string TypeKey { get; init; }
        public required string GroupKey { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>
    /// One item in a person's inbox (design §7.3, §12, phase-2-plan I17): written by the inbox provider when its delivery is sent, once per
    /// delivery. Its content is the delivery's, copied so the inbox reads one table; its group is resolved when delivered and rewritten when the
    /// tenant moves the type or deletes the group (I24). Open means not archived (phase 3 adds the correlation's state).
    /// </summary>
    public sealed class InboxItem : ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid SubscriberId { get; init; }
        public Guid DeliveryId { get; init; }
        public required string TypeKey { get; init; }
        public required string GroupKey { get; set; }
        public required string Title { get; init; }
        public required string Body { get; init; }
        public string? Url { get; init; }
        /// <summary>The type's actions when delivered (id and label), as JSON; null when it has none. Acted on in phase 3.</summary>
        public string? Actions { get; init; }
        /// <summary>The type declares actions (§7.3): the "actionable and open" filter.</summary>
        public bool Actionable { get; init; }
        public NotificationPriority Priority { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ReadAt { get; set; }
        public DateTimeOffset? ArchivedAt { get; set; }
        /// <summary>The correlation of the item's request: the item shows its state and outcome (phase-3-plan I35).</summary>
        public Guid? CorrelationId { get; init; }
    }

    /// <summary>Another source's id for a subscriber, linked by a tenant admin (design D23, phase-2-plan p2-d2).</summary>
    public sealed class SubscriberAlias : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid SubscriberId { get; init; }
        public required string Source { get; init; }
        public required string ExternalId { get; init; }
        public string? LinkedBy { get; init; }
        public DateTimeOffset LinkedAt { get; init; }
    }

    /// <summary>A group kept in this service for the <c>local</c> resolver (design §6.1): any subscribers, of any source.</summary>
    public sealed class LocalGroup : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public required string Key { get; init; }
        public required string Name { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public sealed class LocalGroupMember : Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid GroupId { get; init; }
        public Guid SubscriberId { get; init; }
        public DateTimeOffset AddedAt { get; init; }
    }

    /// <summary>Something people follow (design §6.1, <c>topic</c> recipients): its subscribers are who a request naming it reaches.</summary>
    public sealed class Topic : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        /// <summary>As requests name it: <c>observability.service:billing</c>.</summary>
        public required string Key { get; init; }
        public required string Name { get; set; }
        /// <summary>Whether people may subscribe themselves (<c>me/topics</c>); otherwise only an admin adds them.</summary>
        public bool SelfService { get; set; } = true;
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public sealed class TopicSubscription : Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid TopicId { get; init; }
        public Guid SubscriberId { get; init; }
        public DateTimeOffset SubscribedAt { get; init; }
    }

    /// <summary>A request's processing state. One per request.</summary>
    public sealed class Notification : Claimable, ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid RequestId { get; init; }
        public NotificationState State { get; set; }
        public int WorkflowStep { get; set; }
        public string? Error { get; set; }
        /// <summary>
        /// When planning first found a resolver unavailable; it waits for it until the wait limit after this (phase-2-plan I22). Null when no
        /// resolver has failed for the plan under way, and cleared when it is planned.
        /// </summary>
        public DateTimeOffset? ResolverWaitSince { get; set; }
        /// <summary>When the workflow's current step began waiting (a <c>delay</c>); null when it isn't (phase-3-plan I28).</summary>
        public DateTimeOffset? StepEnteredAt { get; set; }
        /// <summary>
        /// The escalation step being planned (1 for the policy's first step), and what it names (recipient specs, JSON): the planner resolves
        /// these instead of the request's, and sends them at once (phase-3-plan I34). 0 and null for the request's own plan.
        /// </summary>
        public int EscalationStep { get; set; }
        public string? EscalationSpecs { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>
    /// Requests of one application that share a correlation key (design §5.3, §12; phase-3-plan I32). Created by the first request (an upsert,
    /// committed on its own); each accepted request touches it, so its row version stands between a request and a resolve at the same moment.
    /// </summary>
    public sealed class Correlation : ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid ApplicationId { get; init; }
        public required string Key { get; init; }
        public CorrelationState State { get; set; }
        /// <summary>Who acknowledged it, as the producer said ("adam"), and when.</summary>
        public string? AcknowledgedBy { get; set; }
        public DateTimeOffset? AcknowledgedAt { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        /// <summary>What the resolution was, shown on every recipient's item ("approved by adam"); none for a cancel.</summary>
        public string? Outcome { get; set; }
        public string? ResolvedBy { get; set; }
        /// <summary>When a request reopened it after it was resolved or cancelled.</summary>
        public DateTimeOffset? ReopenedAt { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset LastRequestAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        /// <summary>
        /// When the producer's current hold started (phase-5-plan p5-d3): a due escalation step waits for the release. Orthogonal to
        /// <see cref="State"/>, and set if and only if <see cref="HoldReason"/> is (one hold, not a stack).
        /// </summary>
        public DateTimeOffset? HeldAt { get; set; }
        /// <summary>Why it is held, as the producer said (<c>silence:7</c>).</summary>
        public string? HoldReason { get; set; }
        /// <summary>Who held it, as the producer names them.</summary>
        public string? HeldBy { get; set; }
        /// <summary>PostgreSQL's <c>xmin</c>: a concurrency token, so a request and a resolve at once can't both pass.</summary>
        public uint Version { get; set; }
    }

    /// <summary>
    /// A provider's callback event, once (phase-4-plan I40): its id is unique per tenant and provider, so a repeat is ignored. The delivery it was
    /// about, when we have it.
    /// </summary>
    public sealed class ProviderEvent : ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public required string Provider { get; init; }
        public required string ProviderEventId { get; init; }
        public Guid IntegrationId { get; init; }
        public Guid? DeliveryId { get; init; }
        /// <summary><see cref="CallbackKind"/>: <c>Delivered</c>, <c>Bounced</c>, …</summary>
        public required string Kind { get; init; }
        /// <summary>When the provider says it happened.</summary>
        public DateTimeOffset At { get; init; }
        public DateTimeOffset ReceivedAt { get; init; }
    }

    /// <summary>
    /// An event for a producer, raised and saved with the change it reports (phase-3-plan I37), until the relay moves it into the outbox.
    /// <c>Payload</c> is the event as JSON.
    /// </summary>
    public sealed class ProducerEvent : ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid ApplicationId { get; init; }
        public required string Event { get; init; }
        public required string Payload { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>
    /// What a person did with an inbox item (design §5.4, §7.3, §12; phase-3-plan I36): read it, acknowledged it, or chose one of its actions.
    /// Once each per item, kind and action, so a double click is one interaction and one event.
    /// </summary>
    public sealed class Interaction : ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid InboxItemId { get; init; }
        public Guid DeliveryId { get; init; }
        public Guid? CorrelationId { get; init; }
        public Guid SubscriberId { get; init; }
        /// <summary><c>read</c>, <c>acknowledged</c> or <c>action</c>.</summary>
        public required string Kind { get; init; }
        /// <summary>The action's id for an action; empty otherwise.</summary>
        public string ActionId { get; init; } = "";
        public DateTimeOffset At { get; init; }
    }

    /// <summary>
    /// A tenant's escalation policy (design §7.1, §12; phase-3-plan I34): steps that, while a correlation stays open, add recipients after a
    /// wait. <c>Steps</c> is JSON: <c>[{ "afterMinutes": 15, "recipients": [recipient specs] }]</c>.
    /// </summary>
    public sealed class EscalationPolicy : ITenantOwned, Juice.Domain.IAuditable
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public required string Key { get; init; }
        public required string Steps { get; set; }
        /// <summary>Whether acknowledging the correlation stops it (otherwise only resolve and cancel do).</summary>
        public bool StopOnAcknowledge { get; set; }
        public string ManagedBy { get; set; } = Notifications.ManagedBy.Api;
        public required string DefinitionHash { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>
    /// A notification's escalation (design §7.1, §12): its policy's next step and when it is due. Claimed then: while the correlation is still
    /// open (and not acknowledged, for a policy that stops on that), the step's recipients are added to the notification. One per notification.
    /// </summary>
    public sealed class Escalation : Claimable, ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid NotificationId { get; init; }
        public Guid CorrelationId { get; init; }
        public Guid PolicyId { get; init; }
        /// <summary>The policy's step to run next, from 0.</summary>
        public int NextStep { get; set; }
        public EscalationState State { get; set; }
        /// <summary>Why it stopped: <c>acknowledged</c>, <c>resolved</c>, <c>cancelled</c>, <c>policy-deleted</c>.</summary>
        public string? StopReason { get; set; }
        /// <summary>
        /// When its due step was parked by its correlation's hold (p5-d3): a Running escalation with this set isn't due (<c>NextAt</c> null) and
        /// keeps its <see cref="NextStep"/>; the release makes it due now and clears this.
        /// </summary>
        public DateTimeOffset? HeldAt { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>
    /// Recipients held together for a window (design §7.1, D19; phase-3-plan I31): those a <c>digest</c> step adds while it is open, by the
    /// type and the step's key. When it closes it is claimed, and its members get one delivery per destination with an item each. One open
    /// digest per type and key (a partial unique index); members join under an advisory lock that the close takes too, so none joins a digest
    /// already planned.
    /// </summary>
    public sealed class Digest : Claimable, ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public required string TypeKey { get; init; }
        /// <summary>What it groups by: <c>route</c>, <c>subscriber</c>, <c>correlation</c> or <c>type</c>.</summary>
        public required string By { get; init; }
        public required string Key { get; init; }
        public DigestState State { get; set; }
        public DateTimeOffset OpensAt { get; init; }
        public DateTimeOffset ClosesAt { get; init; }
        public DateTimeOffset? ClosedAt { get; set; }
        /// <summary>The members it planned when it closed.</summary>
        public int Members { get; set; }
        /// <summary>
        /// Dry (phase-5 M44-2b): it takes only dry members, and its deliveries are dry. Part of the open digest's key, so dry and real members
        /// never share one: a digest open at the switch closes dry, and members after it open a real one.
        /// </summary>
        public bool DryRun { get; init; }
    }

    /// <summary>
    /// A notification counted by a <c>throttle</c> step in a window (phase-3-plan I30): the window's count is its rows for the type and key. One
    /// per notification and step, so a step run again (a lost lease) counts once.
    /// </summary>
    public sealed class ThrottleEntry
    {
        public Guid TenantId { get; init; }
        public Guid NotificationId { get; init; }
        public int Step { get; init; }
        public required string TypeKey { get; init; }
        public required string Key { get; init; }
        public DateTimeOffset WindowStart { get; init; }
    }

    /// <summary>
    /// Someone a notification addresses: a person (a subscriber), a route match or an external address. <see cref="RouteId"/> has no foreign key, so
    /// a route can be deleted while its history stays; <see cref="RouteKey"/> keeps its name.
    /// </summary>
    public sealed class NotificationRecipient : Claimable, ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid NotificationId { get; init; }
        public RecipientKind Kind { get; init; }
        /// <summary>A person: one recipient per subscriber, however many names reached them (phase-2-plan I14).</summary>
        public Guid? SubscriberId { get; init; }
        /// <summary>What reached a person, such as <c>group:sre</c> and <c>user:adam</c>, in ordinal order; empty for other kinds.</summary>
        public string[] Via { get; init; } = [];
        public Guid? RouteId { get; init; }
        public string? RouteKey { get; init; }
        /// <summary>An external recipient's channel and normalized address.</summary>
        public string? Channel { get; init; }
        public string? Address { get; init; }
        /// <summary>The resolver that produced it.</summary>
        public required string Resolver { get; init; }
        /// <summary>
        /// What the request or step named, such as <c>route:ops</c> or <c>address:email:ops@example.com</c>; <c>subscriber:{id}</c> for a person,
        /// so a person named twice is one recipient (the unique key).
        /// </summary>
        public required string NamedAs { get; init; }
        public int EscalationStep { get; set; }
        /// <summary>The digest it waits in (phase-3-plan I31): it is sent with the digest's other members when that closes.</summary>
        public Guid? DigestId { get; set; }
        public RecipientOutcome Outcome { get; set; }
        /// <summary>
        /// The rules a mandatory type went past to reach this person (design §6.4, phase-2-plan I21): <c>tenant-mute</c> (one with a wildcard),
        /// <c>opt-out</c>, <c>personal-mute</c>, <c>quiet-hours</c> (<see cref="BypassedRules"/>). Empty for a normal type.
        /// </summary>
        public string[] Bypassed { get; set; } = [];
        /// <summary>When <c>delivery.failed</c> was raised for the recipient (every destination ended without going out): once.</summary>
        public DateTimeOffset? FailureRaisedAt { get; set; }
        /// <summary>
        /// One of the recipient's deliveries ended while more could still be planned for them (quiet hours): <c>delivery.failed</c> looks at
        /// them once they are settled (<see cref="RecipientPlanning"/>). The partial index holds only settled ones, so they enter it as they settle.
        /// </summary>
        public bool FailureCheckDue { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>
    /// Whether more can still be planned for a recipient: pending, held by quiet hours (<c>Deferred</c>: only their inbox so far), or due at a
    /// stage (<c>next_at</c> set). One definition for <c>delivery.failed</c>, a closed correlation and redaction, in C# and in SQL.
    /// </summary>
    public static class RecipientPlanning
    {
        private static readonly RecipientOutcome[] Outcomes = [RecipientOutcome.Pending, RecipientOutcome.Deferred];
        private static readonly string[] Names = Outcomes.Select(o => o.ToString()).ToArray();

        /// <summary>The outcomes that can still be planned for, whatever <c>next_at</c> says (read-only).</summary>
        public static IReadOnlyList<RecipientOutcome> StillPlannedOutcomes { get; } = Array.AsReadOnly(Outcomes);

        /// <summary>The same, as stored (read-only; a SQL parameter for <c>outcome = ANY(…)</c> takes a copy as an array).</summary>
        public static IReadOnlyList<string> StillPlannedNames { get; } = Array.AsReadOnly(Names);

        /// <summary>
        /// Settled, as a SQL predicate over <c>notification_recipients</c> with literals (no parameters), so a query that repeats it can use the
        /// partial index whose filter it is.
        /// </summary>
        public static readonly string SettledSql = $"next_at IS NULL AND outcome NOT IN ({string.Join(", ", Names.Select(n => $"'{n}'"))})";

        /// <summary>Over the stored outcome: one this build doesn't know is taken as settled, as <see cref="SettledSql"/> takes it.</summary>
        public static bool CanStillBePlanned(string outcome, DateTimeOffset? nextAt) => nextAt is not null || Names.Contains(outcome, StringComparer.Ordinal);
    }

    /// <summary>One send of one destination on one channel, through one integration. Its id never changes across retries (design D6).</summary>
    public sealed class Delivery : Claimable, ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        /// <summary>The digest it carries, one item per member (phase-3-plan I31); null for a single notification's delivery.</summary>
        public Guid? DigestId { get; init; }
        public required string Channel { get; init; }
        /// <summary>The integration it goes through; changed only by a failover (D8, phase-4-plan I38), which keeps the id and the content.</summary>
        public Guid IntegrationId { get; set; }
        /// <summary><c>cp:…</c>, <c>it:…</c>, <c>ext:…</c> or <c>inbox:…</c> (design §3).</summary>
        public required string DestinationKey { get; init; }
        /// <summary>The address when it was planned, for history. A secret address is masked.</summary>
        public required string DestinationSnapshot { get; init; }
        public int TemplateVersion { get; init; }
        /// <summary>Ended without going out, and its recipients not yet looked at for <c>delivery.failed</c> (phase-3-plan I37).</summary>
        public bool FailureCheckDue { get; set; }
        public required string Locale { get; init; }
        public required string RendererVersion { get; init; }
        /// <summary>The rendered body, stored before the first attempt so every attempt sends the same bytes.</summary>
        public required string Content { get; init; }
        public NotificationPriority Priority { get; init; }
        public bool Mandatory { get; init; }
        public DeliveryStatus Status { get; set; }
        public FailureCategory? FailureCategory { get; set; }
        public string? LastError { get; set; }
        public int Attempts { get; set; }
        public string? ProviderMessageId { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? FirstAttemptAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        /// <summary>Dead-letter retries so far (phase-1-plan M8).</summary>
        public int Replays { get; set; }
        /// <summary>The attempts made before the latest retry: its attempt budget, backoff and unknown-outcome rule count from here.</summary>
        public int AttemptsAtReplay { get; set; }
        /// <summary>When the latest retry was made: a failure from a send that started before it (a stale worker's) doesn't answer the retry.</summary>
        public DateTimeOffset? ReplayedAt { get; set; }
        /// <summary>How often it moved to the tenant's next integration for its channel (phase-4-plan I38).</summary>
        public int Failovers { get; set; }
        /// <summary>The attempts made before the latest failover: the new integration's budget and backoff count from here.</summary>
        public int AttemptsAtFailover { get; set; }
        /// <summary>
        /// Planned in dry run (phase-5-plan I67): the dispatcher takes it through every check and records <see cref="DeliveryStatus.WouldSend"/>
        /// instead of sending. Fixed at planning, never changed after.
        /// </summary>
        public bool DryRun { get; init; }
    }

    /// <summary>Links a delivery to a recipient it carries: one normally, several for a digest (design D19).</summary>
    public sealed class DeliveryItem : ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid DeliveryId { get; init; }
        public Guid RecipientId { get; init; }
        /// <summary>
        /// The request the recipient came from (its notification's), kept here so the dispatcher reaches it in one indexed step rather than
        /// through recipients and notifications (M9 load check: that join, planned while the tables had no statistics, timed out).
        /// </summary>
        public Guid RequestId { get; init; }
        public required string DestinationKey { get; init; }
        public required string Channel { get; init; }
    }

    /// <summary>One execution of a delivery through a provider. Recorded even when the worker lost its claim: the world saw it.</summary>
    public sealed class DeliveryAttempt : ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public Guid DeliveryId { get; init; }
        public int Number { get; init; }
        public Guid IntegrationId { get; init; }
        public DateTimeOffset At { get; init; }
        public AttemptOutcome Outcome { get; init; }
        public FailureCategory? Category { get; init; }
        public int? StatusCode { get; init; }
        public string? Error { get; init; }
        /// <summary>The provider's id for the message, so an attempt applied later (its step's save was lost) still carries it.</summary>
        public string? ProviderMessageId { get; init; }
        public int DurationMs { get; init; }
    }
}
