using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Inbox;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// The inbox hub, <c>/hubs/inbox?tenant={tenantId}</c> (phase-2-plan I18): a signed-in person's session joins their own group and their tenant's,
    /// and is told <c>inboxChanged</c> after each committed change. Nothing is sent to the hub; read, archive and the rest go through the API. A
    /// browser passes its token as <c>access_token</c> in the query, as WebSockets can't carry a header.
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
            var (person, _) = await MeEndpoints.PersonAsync(user, tenantId, services, Context.ConnectionAborted);
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
    /// Tells the hub's groups (on every replica, through the Redis backplane when one is configured). Never throws: the change is committed, and
    /// a session that misses the message reloads on reconnect, so a failure (the backplane down) is logged, not passed on.
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

    /// <summary>Configuration section <c>Inbox:SignalR</c>.</summary>
    public sealed class InboxSignalROptions
    {
        public const string Section = "Inbox:SignalR";

        /// <summary>
        /// The Redis backplane's connection string, for several replicas (phase-2-plan I18). Without it one replica serves every session: a change
        /// made on another isn't told until the session reloads.
        /// </summary>
        public string? Redis { get; set; }
    }

    internal static class InboxHubRegistration
    {
        public static IServiceCollection AddInboxHub(this IServiceCollection services, IConfiguration configuration)
        {
            var options = configuration.GetSection(InboxSignalROptions.Section).Get<InboxSignalROptions>() ?? new InboxSignalROptions();
            var signalR = services.AddSignalR().AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
            if (!string.IsNullOrWhiteSpace(options.Redis))
            {
                signalR.AddStackExchangeRedis(options.Redis, redis => redis.Configuration.ChannelPrefix = RedisChannel.Literal("juice-notifications"));
            }
            // Replaces the core's silent default: the inbox provider and the API tell sessions through the hub.
            services.AddSingleton<IInboxEvents, HubInboxEvents>();
            return services;
        }
    }
}
