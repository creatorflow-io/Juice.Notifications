using Juice.Notifications.Inbox;
using StackExchange.Redis;

namespace Juice.Notifications.Api
{
    // The hub and the inbox's endpoints are in Juice.Notifications.Inbox (M54), which an embedded host maps too; this is the service's wiring of them.

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
            var signalR = services.AddJuiceNotificationsInbox();
            if (!string.IsNullOrWhiteSpace(options.Redis))
            {
                signalR.AddStackExchangeRedis(options.Redis, redis => redis.Configuration.ChannelPrefix = RedisChannel.Literal("juice-notifications"));
            }
            return services;
        }
    }
}
