using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Delivering;
using Juice.Notifications.Inbox;
using Microsoft.AspNetCore.SignalR;

// The hub keeps the namespace it had in the service (Juice.Notifications.Api): its type is part of what a host's tests and filters name.
namespace Juice.Notifications.Api
{
    /// <summary>
    /// The inbox hub, <c>/hubs/inbox?tenant={tenantId}</c> (phase-2-plan I18): a signed-in person's session joins their own group and their tenant's,
    /// and is told <c>inboxChanged</c> after each committed change. Nothing is sent to the hub; read, archive and the rest go through the API. A
    /// browser passes its token as <c>access_token</c> in the query, as WebSockets can't carry a header; reading it from there is the host's
    /// authentication (and only for this path). The person is judged as the endpoints do (<see cref="InboxPeople"/>).
    /// </summary>
    public sealed class InboxHub(IServiceProvider services, ILogger<InboxHub> logger) : Hub
    {
        public const string Path = "/hubs/inbox";
        public const string Changed = "inboxChanged";

        public static string PersonGroup(Guid tenantId, Guid subscriberId) => $"tenant:{tenantId}:sub:{subscriberId}";

        public static string TenantGroup(Guid tenantId) => $"tenant:{tenantId}";

        public override async Task OnConnectedAsync()
        {
            var http = Context.GetHttpContext();
            if (!Guid.TryParse(http?.Request.Query["tenant"], out var tenantId) || Context.User is not { } user)
            {
                Context.Abort();
                return;
            }
            // The tenant is asked first, as the endpoints do: another tenant than an embedded host's never reaches the access, or a write.
            var hosting = services.GetRequiredService<Juice.Notifications.Hosting.NotificationsHosting>();
            if (hosting.Mode == Juice.Notifications.Hosting.NotificationsMode.Embedded && tenantId != hosting.EmbeddedTenantId)
            {
                logger.LogInformation("Inbox hub refused connection {Connection}: tenant {Tenant} isn't this deployment's", Context.ConnectionId, tenantId);
                Context.Abort();
                return;
            }
            // A tenant that isn't active has no live inbox (the tenant pattern): the hub is outside the API's tenant check (its tenant is a query).
            if (!await services.GetRequiredService<ITenantGate>().IsActiveAsync(tenantId, Context.ConnectionAborted))
            {
                logger.LogInformation("Inbox hub refused connection {Connection}: tenant {Tenant} isn't active", Context.ConnectionId, tenantId);
                Context.Abort();
                return;
            }
            var (person, _) = await InboxPeople.ResolveAsync(user, tenantId, services, Context.ConnectionAborted);
            if (person is not { } subscriberId)
            {
                // Not a person of this tenant: no group to join, so nothing to hear.
                logger.LogInformation("Inbox hub refused connection {Connection}: not a person of tenant {Tenant}", Context.ConnectionId, tenantId);
                Context.Abort();
                return;
            }
            await Groups.AddToGroupAsync(Context.ConnectionId, PersonGroup(tenantId, subscriberId), Context.ConnectionAborted);
            await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenantId), Context.ConnectionAborted);
            await base.OnConnectedAsync();
        }
    }

    /// <summary>
    /// Tells the hub's groups (on every replica, through the Redis backplane when the host configured one). Never throws: the change is committed,
    /// and a session that misses the message reloads on reconnect, so a failure (the backplane down) is logged, not passed on.
    /// </summary>
    internal sealed class HubInboxEvents(IHubContext<InboxHub> hub, ILogger<HubInboxEvents> logger) : IInboxEvents
    {
        public async Task PublishAsync(InboxChange change, CancellationToken ct)
        {
            var group = change.SubscriberId == Guid.Empty ? InboxHub.TenantGroup(change.TenantId) : InboxHub.PersonGroup(change.TenantId, change.SubscriberId);
            try
            {
                await hub.Clients.Group(group).SendAsync(InboxHub.Changed, new InboxChangedMessage(change.Kind, change.ItemIds), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Couldn't tell inbox sessions in tenant {Tenant} about a change ({Kind})", change.TenantId, change.Kind);
            }
        }
    }
}
