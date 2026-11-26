using System.Data.Common;
using System.Text.Json;

namespace Juice.Notifications
{
    /// <summary>
    /// What a producer sends and does by correlation key, in either hosting mode (design §4.2, phase-5-plan I58): the HTTP producer API's
    /// contract (§5.2–5.3), with its validation, idempotency and error codes. Embedded (<c>Juice.Notifications.Embedded</c>), each call
    /// writes in the host's open transaction (<see cref="INotificationTransaction"/>) and the host commits it; a refusal is a
    /// <see cref="NotificationRefusedException"/>, which the host lets roll its transaction back or catches.
    /// </summary>
    public interface INotificationClient
    {
        /// <summary>
        /// Accepts a notification: stored, then planned once the host's transaction commits. The same idempotency key again answers with
        /// the original request (<see cref="NotificationAccepted.Duplicate"/>) and creates no work.
        /// </summary>
        Task<NotificationAccepted> SendAsync(NotificationToSend notification, CancellationToken cancellationToken = default);

        /// <summary>Acknowledges the correlation (an escalation that stops on acknowledgement stops); again is no change.</summary>
        Task<CorrelationState> AcknowledgeAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default);

        /// <summary>Resolves the correlation: what is still due for it is cancelled. Again is no change.</summary>
        Task<CorrelationState> ResolveAsync(string correlationKey, string? outcome = null, string? by = null, CancellationToken cancellationToken = default);

        /// <summary>Cancels the correlation: what is still due for it is cancelled. Again is no change.</summary>
        Task<CorrelationState> CancelAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Holds the correlation's escalation (phase-5-plan p5-d3): a step that falls due waits until <see cref="ReleaseAsync"/>, then runs if
        /// the correlation is still open. Again with the same reason is no change; another reason replaces it. The hold stands until released,
        /// whatever the correlation's state (resolve, cancel and reopen leave it). Not found: <c>not_found</c>.
        /// <para>
        /// Embedded, the hold's writes lock the correlation's escalation rows until the host's transaction ends, and the escalation stage skips
        /// them meanwhile: keep that transaction short.
        /// </para>
        /// </summary>
        Task<CorrelationHold> HoldAsync(string correlationKey, string reason, string? by = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Releases the hold: steps that fell due under it run now, if the correlation is still open. Not held: no change. Not found:
        /// <c>not_found</c>. Embedded, as <see cref="HoldAsync"/>: keep the host's transaction short; the stage is woken once it commits.
        /// </summary>
        Task<CorrelationHold> ReleaseAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// A correlation's hold after a call: its state, whether it is held (since when, why, by whom), whether the call changed anything, and how
    /// many parked escalations a release made due (0 for a hold).
    /// </summary>
    public sealed record CorrelationHold(CorrelationState State, bool Held, DateTimeOffset? HeldAt, string? Reason, string? HeldBy, bool Changed,
        int Resumed);

    /// <summary>
    /// A notification request (design §5.2), as the HTTP API's body: the type (its latest version unless <see cref="TypeVersion"/>), the
    /// idempotency key, and the rest as the type allows. Embedded, recipients are routes and addresses only (no people).
    /// </summary>
    public sealed record NotificationToSend(string Type, string IdempotencyKey)
    {
        public int? TypeVersion { get; init; }
        public string? CorrelationKey { get; init; }
        public IReadOnlyList<NotificationRecipientSpec>? Recipients { get; init; }
        public NotificationActorSpec? Actor { get; init; }
        /// <summary><c>low</c>, <c>normal</c>, <c>high</c> or <c>critical</c>; the type's default when null.</summary>
        public string? Priority { get; init; }
        public IReadOnlyDictionary<string, string>? Labels { get; init; }
        public JsonElement? Data { get; init; }
        public IReadOnlyDictionary<string, string>? Links { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
    }

    /// <summary>A recipient (<see cref="RecipientKinds"/>): <c>route</c>, or an <c>address</c> on a channel.</summary>
    public sealed record NotificationRecipientSpec(string Kind, string? Channel = null, string? Address = null, string? Id = null, string? Key = null,
        string? Resolver = null);

    /// <summary>Who caused it: <c>user</c>, <c>system</c> or <c>service</c>, and an id.</summary>
    public sealed record NotificationActorSpec(string Kind, string Id);

    /// <summary>An accepted request; <see cref="Duplicate"/> when its idempotency key was seen before and this is the original.</summary>
    public sealed record NotificationAccepted(Guid Id, string Type, int TypeVersion, DateTimeOffset AcceptedAt, bool Duplicate);

    /// <summary>A field the request got wrong, by JSON pointer (<c>/data/amount</c>).</summary>
    public sealed record NotificationFieldError(string Pointer, string Message);

    /// <summary>
    /// The request was refused, as the HTTP API would refuse it: its <see cref="Code"/> (<see cref="ErrorCodes"/>), a sentence, and the fields
    /// it got wrong. Nothing of it was written.
    /// </summary>
    public sealed class NotificationRefusedException(string code, string message, IReadOnlyList<NotificationFieldError>? errors = null)
        : Exception(message)
    {
        public string Code { get; } = code;
        public IReadOnlyList<NotificationFieldError> Errors { get; } = errors ?? [];
    }

    /// <summary>
    /// The host's open transaction, which an embedded client writes in (phase-5-plan I59): its connection and transaction, and a way to hear
    /// that it committed. The host commits or rolls back; a client call with no transaction open is refused (<see cref="ErrorCodes.NoTransaction"/>).
    /// </summary>
    public interface INotificationTransaction
    {
        /// <summary>The host's connection, open; null when it has none.</summary>
        DbConnection? Connection { get; }

        /// <summary>Its current transaction; null when none is open.</summary>
        DbTransaction? Transaction { get; }

        /// <summary>
        /// Runs <paramref name="action"/> once the transaction commits (never when it rolls back): the pipeline takes the request at once rather
        /// than at its next poll. A host that can't tell returns false, and the request waits for the poll.
        /// </summary>
        bool OnCommitted(Action action);
    }
}
