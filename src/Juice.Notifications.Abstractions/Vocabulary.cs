// Enums travel as strings ("high", "planning"). The API writes them camelCase; the attribute lets any client read them, case-insensitively.
using System.Text.Json.Serialization;

namespace Juice.Notifications
{
    /// <summary>Orders the dispatcher's queue and can select channels. Separate from <c>Mandatory</c> (design D9).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<NotificationPriority>))]
    public enum NotificationPriority { Low, Normal, High, Critical }

    /// <summary>A notification's processing state: one per accepted request.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<NotificationState>))]
    public enum NotificationState
    {
        /// <summary>Accepted, waiting for the planner.</summary>
        Planning,
        /// <summary>Recipients resolved; deliveries follow from them.</summary>
        Planned,
        /// <summary>The request's <c>expiresAt</c> passed before planning.</summary>
        Expired,
        /// <summary>Planning could not complete (for example, its type version is gone). The reason is recorded.</summary>
        Failed,
        /// <summary>
        /// Recipients resolved; the type's workflow is running (phase-3-plan I27): a step may be waiting. <c>Planned</c> follows when the workflow
        /// ends. A type whose workflow is just <c>send</c> never runs: it goes from <c>Planning</c> to <c>Planned</c>.
        /// </summary>
        Running,
        /// <summary>A <c>condition</c> step didn't hold; its waiting recipients were cancelled.</summary>
        ConditionNotMet,
        /// <summary>Its correlation was resolved or cancelled, or an admin cancelled it, before the workflow ended.</summary>
        Cancelled,
    }

    /// <summary>
    /// Where a correlation stands (design §5.3): requests sharing a key are one thing, opened by the first, acknowledged, then resolved or
    /// cancelled by the producer. A request on a closed one reopens it (design §19 decision 14).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<CorrelationState>))]
    public enum CorrelationState { Open, Acknowledged, Resolved, Cancelled }

    /// <summary>A digest's state (phase-3-plan I31): taking members until it closes; closed (its deliveries planned); cancelled (no one left).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DigestState>))]
    public enum DigestState { Open, Closed, Cancelled }

    /// <summary>An escalation's state (design §7.1): running its policy's steps, stopped (the correlation closed or was acknowledged), or past its last step.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<EscalationState>))]
    public enum EscalationState { Running, Stopped, Exhausted }

    public static class DigestRules
    {
        /// <summary>Notifications a digest's content lists; the rest are counted as <c>more</c> (design §7.1).</summary>
        public const int MaxListed = 100;
    }

    /// <summary>The <c>kind</c> of a recipient in a request (design §5.2). <c>route</c> and <c>address</c> are built in; the people kinds go
    /// through a resolver the tenant configures (phase-2-plan M10).</summary>
    public static class RecipientKinds
    {
        public const string Route = "route";
        public const string Address = "address";
        public const string User = "user";
        public const string Group = "group";
        public const string Role = "role";
        public const string Topic = "topic";
        /// <summary>
        /// A person's own contact point, by id: only the service's own requests name one (a verification code, which goes to a point before it is
        /// verified), so the address never appears in a request.
        /// </summary>
        public const string ContactPoint = "contact-point";

        /// <summary>Kinds a tenant-configured resolver handles; each names one thing by <c>id</c> (a topic by <c>key</c>).</summary>
        public static readonly IReadOnlyList<string> People = [User, Group, Role, Topic];
    }

    /// <summary>Who a recipient is (design §3).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<RecipientKind>))]
    public enum RecipientKind
    {
        /// <summary>A person known to the service (phase 2).</summary>
        Subscriber,
        /// <summary>A matching route: its integration targets are the destinations.</summary>
        Route,
        /// <summary>An address the type allows (<c>externalRecipients</c>).</summary>
        External,
        /// <summary>
        /// A record, never delivered: a name the request gave (<c>group:sre</c>) that reached no one because its resolver stayed unavailable
        /// (<see cref="RecipientOutcome.ResolverUnavailable"/>), or not everyone because it reached past the bound (<see cref="RecipientOutcome.Truncated"/>).
        /// </summary>
        Name,
        /// <summary>A contact point the service's own request named (<see cref="RecipientKinds.ContactPoint"/>): <c>NamedAs</c> is <c>contact-point:{id}</c>.</summary>
        ContactPoint,
    }

    /// <summary>What happened to a recipient, so "why didn't I get it?" has an answer.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<RecipientOutcome>))]
    public enum RecipientOutcome { Pending, Planned, Muted, OptedOut, Deferred, Cancelled, Truncated, ResolverUnavailable, Failed }

    /// <summary>The rules a mandatory type goes past, as a recipient's <c>Bypassed</c> records them (design §6.4).</summary>
    public static class BypassedRules
    {
        /// <summary>A tenant mute that matched through a wildcard: only one naming the type exactly holds a mandatory type.</summary>
        public const string TenantMute = "tenant-mute";
        public const string OptOut = "opt-out";
        public const string PersonalMute = "personal-mute";
        public const string QuietHours = "quiet-hours";
    }

    /// <summary>A preference's mode (design §12). <c>digest</c> acts as <c>on</c> until digests arrive (phase 3).</summary>
    public static class PreferenceModes
    {
        public const string On = "on";
        public const string Off = "off";
        public const string Digest = "digest";

        public static readonly IReadOnlyList<string> All = [On, Off, Digest];
    }

    /// <summary>Whether a contact point still works (design §9): a failed one isn't planned until the person verifies it again.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ContactHealth>))]
    public enum ContactHealth
    {
        Ok,
        /// <summary>Soft bounces (phase 4 bounce callbacks); still planned.</summary>
        Bouncing,
        /// <summary>The provider said the address doesn't exist.</summary>
        Failed,
    }

    /// <summary>Push platforms a device registration names (design §9.2).</summary>
    public static class DevicePlatforms
    {
        public const string Fcm = "fcm";
        public const string Apns = "apns";
        public const string WebPush = "webpush";

        public static readonly IReadOnlyList<string> All = [Fcm, Apns, WebPush];
    }

    /// <summary>A delivery's state (design §10.2).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DeliveryStatus>))]
    public enum DeliveryStatus
    {
        Pending,
        /// <summary>Claimed by a dispatcher and being sent.</summary>
        Sending,
        Retrying,
        /// <summary>The provider took it (SMTP 250, a message id). Not proof the person received it.</summary>
        Accepted,
        Delivered,
        Bounced,
        /// <summary>Permanent, attempts exhausted, or an unknown outcome where retrying is unsafe. See <see cref="FailureCategory"/>.</summary>
        Failed,
        Expired,
        Cancelled,
        /// <summary>
        /// The dispatcher ran in dry run (phase-5-plan I67): every check passed and the rendered body is stored; nothing was sent. Terminal, not
        /// retryable, and not a failure.
        /// </summary>
        WouldSend,
    }

    /// <summary>One attempt's outcome (design §10.2).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AttemptOutcome>))]
    public enum AttemptOutcome
    {
        Delivered,
        Accepted,
        RetryableFailure,
        PermanentFailure,
        /// <summary>The request may have reached the provider, but no answer came back.</summary>
        Unknown,
    }

    /// <summary>Why an attempt failed, which decides retry and failover (design §9, §10.3).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<FailureCategory>))]
    public enum FailureCategory
    {
        /// <summary>The provider or our integration with it: another integration may work.</summary>
        Provider,
        /// <summary>The address or endpoint itself: no integration will help.</summary>
        Destination,
        /// <summary>The payload: no integration will help.</summary>
        Content,
        /// <summary>Blocked by the destination policy or the allowlist.</summary>
        Policy,
        /// <summary>Retry later on the same integration.</summary>
        RateLimited,
    }

    /// <summary>
    /// Channel keys the code names. Which channels exist is the registry's business (<see cref="IChannelRegistry"/>, design D24): the core knows
    /// only <c>inbox</c> by name, and phase 1 registers <c>webhook</c> and <c>email</c>.
    /// </summary>
    public static class Channels
    {
        public const string Webhook = "webhook";
        public const string Email = "email";
        public const string Inbox = "inbox";
    }

    /// <summary>Whether a type's requests carry a correlation key (design §5.1).</summary>
    public static class CorrelationRules
    {
        public const string None = "none";
        public const string Optional = "optional";
        public const string Required = "required";

        public static readonly IReadOnlyList<string> All = [None, Optional, Required];
    }

    /// <summary>
    /// Who owns a row: the deployment's configuration, or the API. Each leaves the other's alone. The service's own rows (the inbox integration
    /// every tenant gets) are neither's to change.
    /// </summary>
    public static class ManagedBy
    {
        public const string Configuration = "config";
        public const string Api = "api";
        public const string Service = "service";
    }

    /// <summary>How a template is rendered: Scriban (design D22), or the producer's data sent as is (webhook only).</summary>
    public static class TemplateEngines
    {
        public const string Scriban = "scriban";
        public const string Passthrough = "passthrough";
    }

    /// <summary>The display groups every tenant starts with (design D12), in order, with their first names.</summary>
    public static class DisplayGroups
    {
        public const string Approval = "approval";
        public const string System = "system";
        public const string General = "general";

        public static readonly IReadOnlyList<(string Key, string Name)> BuiltIn = [(Approval, "Approvals"), (System, "System"), (General, "General")];
    }
}
