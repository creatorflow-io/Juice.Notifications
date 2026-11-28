using System.Security.Claims;
using Juice.Notifications.Api;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Hosting;
using Juice.Notifications.Intake;
using Juice.Notifications.People;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Inbox
{
    /// <summary>
    /// Who the inbox's caller is, for the endpoints and the hub alike (M54). In this order: embedded, the tenant is the host's (before the access is
    /// asked, so a faulty access can't open another tenant); then the host's access names the person or refuses; then the person is their
    /// subscriber of source <c>oidc</c> and the token's subject, created on first use or given the name the token gives, and their allowlisted roles are
    /// recorded when the host has an inbox (<see cref="SubscriberRoleRecorder"/>). Nothing is written for a refused caller.
    /// </summary>
    public static class InboxPeople
    {
        public const string OidcSource = SignedInRecipientResolver.Source;

        /// <summary>The person's subscriber in the tenant, or the refusal as the API answers it.</summary>
        public static async Task<(Guid? Subscriber, IResult? Refusal)> ResolveAsync(ClaimsPrincipal user, Guid tenantId, IServiceProvider services, CancellationToken ct)
        {
            var hosting = services.GetRequiredService<NotificationsHosting>();
            if (hosting.Mode == NotificationsMode.Embedded && tenantId != hosting.EmbeddedTenantId)
            {
                return (null, TenantForbidden());
            }
            var answer = await services.GetRequiredService<INotificationsInboxAccess>().PersonAsync(user, tenantId, ct);
            if (answer.Person is not { } person)
            {
                return (null, answer.Refusal == ErrorCodes.NotAPerson ? NotAPerson() : NotAMember());
            }
            // The subject becomes a subscriber's external id: one that couldn't be a recipient's is no person of this tenant.
            if (!RequestRules.IsRecipientId(person.Subject))
            {
                return (null, NotAMember());
            }
            var db = services.GetRequiredService<NotificationsDbContext>();
            var clock = services.GetRequiredService<TimeProvider>();
            var resolved = SubscriberDirectory.Fit(new ResolvedPerson(OidcSource, person.Subject, person.Name));
            var subscriber = await KnownAsync(db, tenantId, resolved, ct)
                ?? (await SubscriberDirectory.MapAsync(db, tenantId, [resolved], clock.GetUtcNow(), ct))[(OidcSource, person.Subject)];
            if (services.GetService<SubscriberRoleRecorder>() is { } roles)
            {
                await roles.RecordAsync(tenantId, subscriber, person.Roles, ct);
            }
            return (subscriber, null);
        }

        /// <summary>
        /// The person's subscriber when it is known and needs nothing written (one read): their identity's alias, or their own subscriber with the
        /// name the token gives (or none). Null sends them through the upsert, which creates them or refreshes the name.
        /// </summary>
        private static async Task<Guid?> KnownAsync(NotificationsDbContext db, Guid tenantId, ResolvedPerson person, CancellationToken ct)
        {
            var known = await db.Subscribers.AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.Source == person.Source && s.ExternalId == person.ExternalId)
                .Select(s => new
                {
                    s.Id, s.MergedInto, s.Name,
                    Alias = db.SubscriberAliases.Where(a => a.TenantId == tenantId && a.Source == person.Source && a.ExternalId == person.ExternalId)
                        .Select(a => (Guid?)a.SubscriberId).FirstOrDefault(),
                })
                .SingleOrDefaultAsync(ct);
            return known switch
            {
                { Alias: { } linked } => linked,
                { } own when person.Name is null || person.Name == own.Name => own.MergedInto ?? own.Id,
                _ => null,
            };
        }

        internal static IResult TenantForbidden()
            => ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.TenantForbidden, "This deployment's notifications are for one tenant only.");

        internal static IResult NotAMember() => ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotTenantMember, "The caller isn't a member of this tenant.");

        internal static IResult NotAPerson()
            => ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotAPerson, "The person's API takes a person's token, not a client's own.");
    }
}
