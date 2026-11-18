using System.Text.Json;
using Juice.Notifications.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Chat
{
    /// <summary>The chat channels this package defines (design D24): Slack reaches people (linked accounts) and channels; Teams, channels only.</summary>
    public static class ChatChannels
    {
        public const string Slack = "slack";
        public const string Teams = "teams";

        public static readonly ChannelDefinition SlackChannel = new(Slack, AddressKind.Account, ChannelAudience.People | ChannelAudience.SharedTargets,
            ContentShape.Chat);

        /// <summary>Workflow webhooks reach the channel each was made for (phase-4-plan p4-d2: the Teams bot, which could reach people, comes later).</summary>
        public static readonly ChannelDefinition TeamsChannel = new(Teams, AddressKind.None, ChannelAudience.SharedTargets, ContentShape.Chat);
    }

    /// <summary>The client the chat providers share (as <see cref="ProviderHttp.Handler"/>); tests replace it with one over a fake API.</summary>
    public sealed class ChatHttp(HttpMessageHandler handler) : IDisposable
    {
        public HttpClient Client { get; } = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        public static ChatHttp Create(DeliveryOptions options) => new(ProviderHttp.Handler(options));

        public void Dispose() => Client.Dispose();
    }

    internal static class ChatMessages
    {
        public const int MaxActions = 5;

        public static ChatContent? Read(string content)
        {
            try
            {
                return JsonSerializer.Deserialize<ChatContent>(content);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The type's actions (<c>[{ id, label }]</c>), the first <see cref="MaxActions"/>.</summary>
        public static IReadOnlyList<(string Id, string Label)> Actions(ChatContent content)
        {
            if (content.Actions is not { ValueKind: JsonValueKind.Array } actions)
            {
                return [];
            }
            return actions.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(a => (a.GetProperty("id").GetString()!,
                    a.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString()! : a.GetProperty("id").GetString()!))
                .Take(MaxActions).ToList();
        }

        /// <summary>An https URL on one of <paramref name="hosts"/> (a host itself, or one under it as <c>.domain</c>), port 443, no user info.</summary>
        public static bool IsHttpsOn(string value, out Uri url, params string[] hosts)
        {
            url = null!;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed is not { Scheme: "https", UserInfo: "", Port: 443 })
            {
                return false;
            }
            var host = parsed.IdnHost;
            if (!hosts.Any(h => h.StartsWith('.') ? host.EndsWith(h, StringComparison.OrdinalIgnoreCase) && host.Length > h.Length
                : string.Equals(host, h, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            url = parsed;
            return true;
        }
    }

    public static class ChatProviderRegistration
    {
        /// <summary>
        /// The <c>slack</c> and <c>teams</c> channels; the Slack app (<c>slack</c>), Slack incoming webhooks (<c>slack-webhook</c>) and Teams workflow
        /// webhooks (<c>teams-workflow</c>); and the Slack app's callbacks (<c>/api/v1/callbacks/slack/…</c>).
        /// </summary>
        public static IServiceCollection AddChatProviders(this IServiceCollection services)
        {
            services.AddSingleton(ChatChannels.SlackChannel);
            services.AddSingleton(ChatChannels.TeamsChannel);
            services.TryAddSingleton(sp => ChatHttp.Create(sp.GetRequiredService<IOptions<DeliveryOptions>>().Value));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, SlackProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, SlackWebhookProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, TeamsWorkflowProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallbackHandler, SlackCallbackHandler>());
            return services;
        }
    }
}
