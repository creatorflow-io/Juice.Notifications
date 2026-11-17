using System.Text.Json;

namespace Juice.Notifications.Api.Contracts
{
    /// <summary>
    /// An item in the person's inbox (design §7.3). <c>actions</c> are the type's (<c>[{ id, label }]</c>), shown from phase 2 and acted on in
    /// phase 3; <c>actionable</c> says the type declares some.
    /// </summary>
    public sealed record InboxItemResponse(Guid Id, string Type, string Group, string Title, string Body, string? Url, JsonElement? Actions, bool Actionable,
        NotificationPriority Priority, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, DateTimeOffset? ArchivedAt, InboxCorrelationResponse? Correlation = null,
        IReadOnlyList<InboxInteractionResponse>? Interactions = null);

    /// <summary>What the person did with the item: <c>read</c>, <c>acknowledged</c>, or <c>action</c> with its id (phase-3-plan I36).</summary>
    public sealed record InboxInteractionResponse(string Kind, string? ActionId, DateTimeOffset At);

    public sealed record InboxGroupCountResponse(string Key, string Name, int Position, int Open, int Unread);

    /// <summary><c>GET …/me/inbox/counts</c>: each group of the tenant's catalog in order, unread in all, and actionable and open.</summary>
    public sealed record InboxCountsResponse(IReadOnlyList<InboxGroupCountResponse> Groups, int Unread, int ActionableOpen);

    /// <summary><c>POST …/me/inbox/read-all</c>: how many items were marked read.</summary>
    public sealed record ReadAllResponse(int Marked);

    /// <summary>A display group of the tenant (design D12), and the types the tenant moved to it.</summary>
    public sealed record DisplayGroupResponse(string Key, string Name, int Position, bool BuiltIn, IReadOnlyList<string> Types, DateTimeOffset UpdatedAt);

    /// <summary><c>PUT …/display-groups/{key}</c>: a new group needs a name; a field left out is kept.</summary>
    public sealed record PutDisplayGroupBody(string? Name, int? Position);

    /// <summary><c>PUT …/types/{typeKey}/display-group</c>: the group the type's items go to in this tenant.</summary>
    public sealed record MoveTypeBody(string? Group);

    /// <summary>Where a type's items go now; <c>chosen</c> when the tenant chose it (otherwise the type's own); open items moved now.</summary>
    public sealed record TypeDisplayGroupResponse(string Type, string Group, bool Chosen, int ItemsMoved);

    /// <summary>
    /// What the inbox hub (<c>/hubs/inbox</c>) tells a person's sessions: <c>added</c> or <c>changed</c> with the items, or <c>reset</c> (reload).
    /// The hub's one event is <c>inboxChanged</c>; a client reloads its counts on any, and on reconnect.
    /// </summary>
    public sealed record InboxChangedMessage(string Kind, IReadOnlyList<Guid> Items);
}
