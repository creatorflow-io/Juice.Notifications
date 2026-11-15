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
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
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
        public int EscalationStep { get; init; }
        public RecipientOutcome Outcome { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    /// <summary>One send of one destination on one channel, through one integration. Its id never changes across retries (design D6).</summary>
    public sealed class Delivery : Claimable, ITenantOwned
    {
        public Guid TenantId { get; init; }
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public required string Channel { get; init; }
        public Guid IntegrationId { get; init; }
        /// <summary><c>cp:…</c>, <c>it:…</c>, <c>ext:…</c> or <c>inbox:…</c> (design §3).</summary>
        public required string DestinationKey { get; init; }
        /// <summary>The address when it was planned, for history. A secret address is masked.</summary>
        public required string DestinationSnapshot { get; init; }
        public int TemplateVersion { get; init; }
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
