namespace Juice.Notifications
{
    /// <summary>
    /// A provider's call to <c>POST /api/v1/callbacks/{provider}/{integrationId}</c> (phase-4-plan I40, design §19 decision 20), as its handler
    /// reads it: the raw body (signatures are over the bytes as sent), the headers, and the URL it was called on (some providers sign that too).
    /// </summary>
    public sealed record CallbackRequest(IReadOnlyDictionary<string, string> Headers, byte[] Body, Uri Url)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>What a provider reports (§9, §10.2).</summary>
    public enum CallbackKind
    {
        /// <summary>A delivery receipt: the strongest evidence a channel gives that the person got it.</summary>
        Delivered,
        /// <summary>A hard bounce: the address doesn't take mail (or messages). The delivery bounced and the contact point fails.</summary>
        Bounced,
        /// <summary>A soft bounce (a full mailbox, a server away): the contact point is marked bouncing, still used.</summary>
        SoftBounced,
        /// <summary>The person reported it as spam or unsubscribed through the provider: the contact point fails.</summary>
        Complained,
        /// <summary>
        /// A button pressed in a chat message (phase-4-plan I40, M27): <see cref="CallbackEvent.ActionId"/> by <see cref="CallbackEvent.Account"/>
        /// on <see cref="CallbackEvent.DeliveryId"/>; the person whose account it is acts on their inbox item for that notification.
        /// </summary>
        Interaction,
        /// <summary>
        /// A person sent the bot a link code (phase-4-plan I41): <see cref="CallbackEvent.Detail"/> is the code, <see cref="CallbackEvent.Account"/>
        /// the chat account that sent it, which becomes their verified contact point.
        /// </summary>
        Link,
    }

    /// <summary>
    /// One event in a callback. <see cref="EventId"/> is the provider's own id for it: an event seen before is ignored (providers repeat them).
    /// The delivery is named by <see cref="DeliveryId"/> when the provider echoes our id back, otherwise by <see cref="ProviderMessageId"/>, the id
    /// the provider gave the send.
    /// </summary>
    public sealed record CallbackEvent(string EventId, CallbackKind Kind, DateTimeOffset At)
    {
        public Guid? DeliveryId { get; init; }
        public string? ProviderMessageId { get; init; }
        /// <summary>The provider's reason (a bounce's diagnostic), kept on the delivery or the contact point. No address in it: handlers mask it.</summary>
        public string? Detail { get; init; }
        /// <summary>For an interaction: the action and the chat account that chose it.</summary>
        public string? ActionId { get; init; }
        public string? Account { get; init; }
    }

    /// <summary>An answer the provider expects other than <c>200</c> with no body (Slack's URL verification echoes a challenge).</summary>
    public sealed record CallbackReply(int Status, string ContentType, string Body);

    /// <summary>
    /// A callback read: verified with its events, or refused (<see cref="Refusal"/> says why, for the log; the caller only sees <c>401</c>).
    /// </summary>
    public sealed record CallbackResult(string? Refusal, IReadOnlyList<CallbackEvent> Events, CallbackReply? Reply = null)
    {
        public bool Verified => Refusal is null;

        public static CallbackResult Refused(string why) => new(why, []);
        public static CallbackResult Of(params CallbackEvent[] events) => new(null, events);
    }

    /// <summary>
    /// Verifies and reads one provider's callbacks (phase-4-plan I40): each provider package registers one for the integration kinds it sends
    /// through. It checks the request against the integration's secret (or the provider's published keys) before reading anything from it, and
    /// never throws for a request it can't read: it refuses it.
    /// </summary>
    public interface ICallbackHandler
    {
        /// <summary>The path segment: <c>ses</c>, <c>sendgrid</c>, <c>slack</c>, <c>twilio</c>, <c>telegram</c>.</summary>
        string Provider { get; }

        /// <summary>Whether an integration of this kind sends through this provider (a callback for another kind's integration is not found).</summary>
        bool Handles(string integrationKind);

        Task<CallbackResult> ReadAsync(CallbackRequest request, IntegrationContext integration, CancellationToken ct);
    }
}
