namespace Juice.Notifications.Api.Contracts
{
    /// <summary>A notification in the tenant's list (phase-4c-plan I52): newest first, for its admins.</summary>
    public sealed record NotificationListItemResponse(Guid Id, string Type, int TypeVersion, string Application, string? CorrelationKey,
        NotificationPriority Priority, NotificationState State, string? Error, string? Actor, DateTimeOffset AcceptedAt);

    /// <summary>A page of notifications; <c>next</c> is the <c>before</c> cursor for the next page, null on the last.</summary>
    public sealed record NotificationPageResponse(IReadOnlyList<NotificationListItemResponse> Items, Guid? Next);

    /// <summary>One of a recipient's deliveries.</summary>
    public sealed record RecipientDeliveryResponse(Guid Id, string Channel, string DestinationKey, DeliveryStatus Status, FailureCategory? FailureCategory);

    /// <summary>The digest a recipient waits in, or was sent with.</summary>
    public sealed record RecipientDigestResponse(Guid Id, DigestState State, DateTimeOffset ClosesAt, DateTimeOffset? ClosedAt);

    /// <summary>
    /// Someone a notification addressed, and what came of it (phase-4c-plan I52, deferred #36): <c>outcome</c> says why there is no delivery
    /// (<c>muted</c>, <c>optedOut</c>, <c>deferred</c> by quiet hours, …), <c>bypassed</c> the rules a mandatory type went past, <c>digest</c>
    /// the digest it waits in. A person is named by subscriber id (and <c>via</c>, the names that reached them); a route by its key; an external
    /// recipient by its channel and address.
    /// </summary>
    public sealed record RecipientResponse(Guid Id, RecipientKind Kind, string NamedAs, IReadOnlyList<string> Via, Guid? SubscriberId, string? Route,
        string? Channel, string? Address, string Resolver, RecipientOutcome Outcome, IReadOnlyList<string> Bypassed, int EscalationStep,
        RecipientDigestResponse? Digest, DateTimeOffset CreatedAt, IReadOnlyList<RecipientDeliveryResponse> Deliveries);

    /// <summary>A page of recipients, oldest first; <c>next</c> is the <c>after</c> cursor for the next page, null on the last.</summary>
    public sealed record RecipientPageResponse(IReadOnlyList<RecipientResponse> Items, Guid? Next);

    /// <summary>A correlation in the tenant's list (phase-4c-plan I52): newest first, with the application it belongs to (keys are per application).</summary>
    public sealed record CorrelationListItemResponse(Guid Id, string Application, string Key, CorrelationState State, string? AcknowledgedBy,
        DateTimeOffset? AcknowledgedAt, DateTimeOffset? ResolvedAt, string? Outcome, DateTimeOffset CreatedAt, DateTimeOffset LastRequestAt);

    public sealed record CorrelationPageResponse(IReadOnlyList<CorrelationListItemResponse> Items, Guid? Next);

    /// <summary>Deliveries of one channel in one status.</summary>
    public sealed record OverviewDeliveriesResponse(string Channel, DeliveryStatus Status, int Count);

    /// <summary>An integration whose deliveries failed in the window: how many, and the latest's error.</summary>
    public sealed record OverviewIntegrationResponse(string Integration, int Failed, DateTimeOffset LastFailedAt, string? LastError);

    /// <summary>
    /// The tenant's overview (phase-4c-plan p4c-d6): deliveries created since <c>since</c> by channel and status, the integrations that failed
    /// in that window, the open storms, and the notifications still being planned or running. Read at <c>at</c>, cached for 30 seconds.
    /// </summary>
    public sealed record OverviewResponse(DateTimeOffset At, DateTimeOffset Since, IReadOnlyList<OverviewDeliveriesResponse> Deliveries,
        IReadOnlyList<OverviewIntegrationResponse> FailingIntegrations, int OpenStorms, int Unfinished);
}
