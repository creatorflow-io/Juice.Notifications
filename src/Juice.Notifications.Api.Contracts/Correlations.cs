namespace Juice.Notifications.Api.Contracts
{
    /// <summary><c>POST …/correlations/{key}/acknowledge</c>: who acknowledged it, as the producer names them (shown on items).</summary>
    public sealed record AcknowledgeCorrelationBody(string? By);

    /// <summary><c>POST …/correlations/{key}/resolve</c>: what came of it (\"approved\"), shown on every recipient's item, and who decided.</summary>
    public sealed record ResolveCorrelationBody(string? Outcome, string? By);

    /// <summary><c>POST …/correlations/{key}/cancel</c>.</summary>
    public sealed record CancelCorrelationBody(string? By);

    public sealed record CorrelationNotificationResponse(Guid Id, Guid RequestId, string Type, NotificationState State, DateTimeOffset CreatedAt);

    /// <summary>
    /// A correlation (design §5.3): its state, who acknowledged and resolved it, and its notifications (the latest 100) with how their recipients
    /// and deliveries stand (counts by outcome and by status).
    /// </summary>
    public sealed record CorrelationResponse(string Key, CorrelationState State, string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt,
        DateTimeOffset? ResolvedAt, string? ResolvedBy, string? Outcome, DateTimeOffset? ReopenedAt, DateTimeOffset CreatedAt, DateTimeOffset LastRequestAt,
        IReadOnlyList<CorrelationNotificationResponse> Notifications, IReadOnlyDictionary<string, int> Recipients, IReadOnlyDictionary<string, int> Deliveries);

    /// <summary>An inbox item's correlation: open ones wait for someone; a resolved one shows its outcome (phase-3-plan I35).</summary>
    public sealed record InboxCorrelationResponse(CorrelationState State, string? Outcome, string? AcknowledgedBy, DateTimeOffset? ResolvedAt);
}
