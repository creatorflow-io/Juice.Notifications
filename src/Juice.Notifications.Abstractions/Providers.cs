namespace Juice.Notifications
{
    /// <summary>
    /// One delivery as a provider sends it: the content rendered before the first attempt, never re-rendered (design D10). <c>Content</c> is the
    /// channel's content as JSON (email: subject, text, html; webhook: contentType, body). <c>Destination</c> is the address, resolved for this
    /// attempt (a secret reference is read now, never stored). <c>Attempt</c> counts from 1.
    /// </summary>
    public sealed record RenderedDelivery(Guid DeliveryId, Guid TenantId, string Channel, string Content, string Destination, int Attempt,
        NotificationPriority Priority, bool Mandatory);

    /// <summary>The integration a delivery goes through: its settings (JSON) and its secret, resolved for this attempt.</summary>
    public sealed record IntegrationContext(Guid IntegrationId, string Key, string Kind, string Settings, string? Secret);

    /// <summary>What one attempt did (design §9.3, §10.2).</summary>
    public sealed record SendResult(AttemptOutcome Outcome, FailureCategory? Category, string? ProviderMessageId, int? StatusCode, string? Error)
    {
        public static SendResult Delivered(int? status = null, string? id = null) => new(AttemptOutcome.Delivered, null, id, status, null);
        public static SendResult Accepted(string? id = null) => new(AttemptOutcome.Accepted, null, id, null, null);
        public static SendResult Retry(FailureCategory category, string error, int? status = null) => new(AttemptOutcome.RetryableFailure, category, null, status, error);
        public static SendResult Permanent(FailureCategory category, string error, int? status = null) => new(AttemptOutcome.PermanentFailure, category, null, status, error);
        public static SendResult Unknown(string error) => new(AttemptOutcome.Unknown, FailureCategory.Provider, null, null, error);
    }

    /// <summary>
    /// Sends deliveries of one channel through one kind of integration (design §9.3). A provider never throws for a send that failed: it says how
    /// it failed, so the dispatcher can retry, fail over (phase 4) or stop.
    /// </summary>
    public interface INotificationProvider
    {
        string Channel { get; }

        string Kind { get; }

        /// <summary>Whether the provider accepts the delivery id as an idempotency key, which makes retrying after an unknown outcome safe.</summary>
        bool Idempotent { get; }

        /// <summary>Checks an integration's settings when it is saved; null when they are fine.</summary>
        string? CheckSettings(string settings);

        Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct);
    }
}
