using System.Security.Claims;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.People;

namespace Juice.Notifications.Inbox
{
    /// <summary>
    /// A signed-in person as their host judged their token (M54): the token's subject, a name for them if it has one, and the roles it carries.
    /// An embedded host's inbox records the roles it allowlists (<see cref="SubscriberRoleRecorder"/>); the service passes none.
    /// </summary>
    public sealed record InboxPerson(string Subject, string? Name, IReadOnlyList<string> Roles);

    /// <summary>
    /// The host's answer for a caller: the person, or why not (<see cref="ErrorCodes.NotAPerson"/>: a client's own token;
    /// <see cref="ErrorCodes.NotTenantMember"/>: not a member of the tenant, or no usable subject).
    /// </summary>
    public sealed record InboxAccessResult(InboxPerson? Person, string? Refusal)
    {
        public static InboxAccessResult Of(InboxPerson person) => new(person, null);

        public static readonly InboxAccessResult NotAPerson = new(null, ErrorCodes.NotAPerson);

        public static readonly InboxAccessResult NotAMember = new(null, ErrorCodes.NotTenantMember);
    }

    /// <summary>
    /// Who is a person of a tenant: the service answers from its tenant access and refuses a client's own token; an embedded host answers from its
    /// own claims (<see cref="SingleTenantInboxAccess"/>). Asked for every inbox request and hub connection, after the embedded tenant check.
    /// </summary>
    public interface INotificationsInboxAccess
    {
        ValueTask<InboxAccessResult> PersonAsync(ClaimsPrincipal user, Guid tenantId, CancellationToken ct = default);
    }

    /// <summary>
    /// For an embedded host: its one tenant; any other tenant is <see cref="InboxAccessResult.NotAMember"/>. The host maps its claims in
    /// <see cref="PersonAsync(ClaimsPrincipal, CancellationToken)"/>.
    /// </summary>
    public abstract class SingleTenantInboxAccess(Guid tenantId) : INotificationsInboxAccess
    {
        /// <summary>The host's tenant.</summary>
        protected Guid TenantId { get; } = tenantId;

        /// <summary>The person the caller is in the host's tenant, from the host's own claims, or why not.</summary>
        protected abstract ValueTask<InboxAccessResult> PersonAsync(ClaimsPrincipal user, CancellationToken ct);

        public async ValueTask<InboxAccessResult> PersonAsync(ClaimsPrincipal user, Guid tenantId, CancellationToken ct = default)
            => tenantId == TenantId ? await PersonAsync(user, ct) : InboxAccessResult.NotAMember;
    }
}
