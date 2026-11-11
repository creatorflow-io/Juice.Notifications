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
    }

    /// <summary>The <c>kind</c> of a recipient in a request (design §5.2). Phase 1 resolves <c>route</c> and <c>address</c>.</summary>
    public static class RecipientKinds
    {
        public const string Route = "route";
        public const string Address = "address";
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
    }

    /// <summary>What happened to a recipient, so "why didn't I get it?" has an answer.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<RecipientOutcome>))]
    public enum RecipientOutcome { Pending, Planned, Muted, OptedOut, Deferred, Cancelled, Truncated, ResolverUnavailable }

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

    /// <summary>Who owns a row: the deployment's configuration, or the API. Each leaves the other's alone.</summary>
    public static class ManagedBy
    {
        public const string Configuration = "config";
        public const string Api = "api";
    }

    /// <summary>The display groups every tenant starts with (design D12).</summary>
    /// <summary>How a template is rendered: Scriban (design D22), or the producer's data sent as is (webhook only).</summary>
    public static class TemplateEngines
    {
        public const string Scriban = "scriban";
        public const string Passthrough = "passthrough";
    }

    public static class DisplayGroups
    {
        public const string Approval = "approval";
        public const string System = "system";
        public const string General = "general";
    }
}
