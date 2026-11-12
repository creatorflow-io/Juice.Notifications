namespace Juice.Notifications.Api.Contracts
{
    /// <summary>One delivery in a history list, newest first (phase-1-plan M8).</summary>
    public sealed record DeliveryHistoryResponse(Guid Id, Guid? RequestId, string? Type, string Channel, string Integration, string DestinationKey,
        DeliveryStatus Status, int Attempts, FailureCategory? FailureCategory, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? FirstAttemptAt,
        DateTimeOffset? CompletedAt);

    /// <summary>A page of history. <c>Next</c> is the cursor for the following page (<c>?before=</c>), null on the last page.</summary>
    public sealed record DeliveryPageResponse(IReadOnlyList<DeliveryHistoryResponse> Items, Guid? Next);

    public sealed record DeliveryItemResponse(Guid RecipientId, RecipientKind Kind, string NamedAs, string? Route, string Resolver);

    public sealed record DeliveryAttemptResponse(int Number, DateTimeOffset At, AttemptOutcome Outcome, FailureCategory? Category, string Integration,
        int? StatusCode, int DurationMs, string? Error, string? ProviderMessageId);

    public sealed record DeliveryDetailResponse(DeliveryHistoryResponse Delivery, string DestinationSnapshot, int TemplateVersion, string Locale,
        string RendererVersion, string? ProviderMessageId, IReadOnlyList<DeliveryItemResponse> Items, IReadOnlyList<DeliveryAttemptResponse> Attempts);

    public sealed record RetryResponse(Guid Id, DeliveryStatus Status, int Attempts, int Replays);
}
