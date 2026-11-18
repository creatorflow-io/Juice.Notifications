using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.Notifications.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Telegram
{
    /// <summary>The <c>telegram</c> channel (design D24): group and channel chats as targets, and people's own chats, linked through the bot.</summary>
    public static class TelegramChannel
    {
        public const string Key = "telegram";

        public static readonly ChannelDefinition Definition = new(Key, AddressKind.Account, ChannelAudience.People | ChannelAudience.SharedTargets,
            ContentShape.Chat);
    }

    /// <summary>The client the Telegram provider and its webhook share (as <see cref="ProviderHttp.Handler"/>); tests replace it.</summary>
    public sealed class TelegramHttp(HttpMessageHandler handler) : IDisposable
    {
        public HttpClient Client { get; } = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        public static TelegramHttp Create(DeliveryOptions options) => new(ProviderHttp.Handler(options));

        public void Dispose() => Client.Dispose();
    }

    /// <summary>A chat message as Telegram shows it (HTML parse mode): the title in bold, the text, buttons below.</summary>
    internal static class TelegramMessages
    {
        public const int MaxText = 4096;

        /// <summary>Telegram's <c>callback_data</c>: <c>a:{delivery id, 32 hex}:{action id}</c>, at most 64 bytes.</summary>
        public static string? CallbackData(Guid deliveryId, string actionId)
        {
            var data = $"a:{deliveryId:N}:{actionId}";
            return Encoding.UTF8.GetByteCount(data) <= 64 ? data : null;
        }

        public static (Guid DeliveryId, string ActionId)? ReadCallbackData(string? data)
            => data is { Length: > 36 } && data.StartsWith("a:", StringComparison.Ordinal) && data[34] == ':'
                && Guid.TryParseExact(data[2..34], "N", out var id) && data.Length > 35 ? (id, data[35..]) : null;

        /// <summary>HTML parse mode's escaping: <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, so no data value adds a tag or a link.</summary>
        public static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        public static Dictionary<string, object> Message(ChatContent content, Guid deliveryId, string chatId)
        {
            var html = "<b>" + Escape(content.Title) + "</b>" + (content.Text.Length > 0 ? "\n\n" + Escape(content.Text) : "");
            if (html.Length > MaxText)
            {
                // Cut the text, not the markup: the bold title stays closed.
                var room = MaxText - ("<b>" + Escape(content.Title) + "</b>\n\n").Length - 1;
                html = "<b>" + Escape(content.Title) + "</b>\n\n" + Escape(ChannelContent.Cut(content.Text, Math.Max(0, room / 5))!) + "…";
            }
            var rows = new List<object[]>();
            var actions = content.Actions is { ValueKind: JsonValueKind.Array } a
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String)
                    .Select(x => (Id: x.GetProperty("id").GetString()!, Label: x.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : x.GetProperty("id").GetString()!))
                    .Take(5).ToList()
                : [];
            var buttons = actions.Select(x => (x.Label, Data: CallbackData(deliveryId, x.Id))).Where(x => x.Data is not null)
                .Select(x => (object)new { text = ChannelContent.Cut(x.Label, 64), callback_data = x.Data }).ToArray();
            if (buttons.Length > 0)
            {
                rows.Add(buttons);
            }
            if (content.Url is { } url && url.StartsWith("https://", StringComparison.Ordinal))
            {
                rows.Add([new { text = "Open", url }]);
            }
            var message = new Dictionary<string, object>
            {
                ["chat_id"] = chatId, ["text"] = html, ["parse_mode"] = "HTML", ["link_preview_options"] = new { is_disabled = true },
            };
            if (rows.Count > 0)
            {
                message["reply_markup"] = new { inline_keyboard = rows };
            }
            return message;
        }
    }

    /// <summary>
    /// Telegram's Bot API (<c>telegram</c>; phase-4-plan M28): <c>sendMessage</c> with the integration's bot token as its secret, to a group or
    /// channel chat (a target's address: its id, <c>-100…</c>, or a public channel's <c>@name</c>; the bot must be a member, an admin in a
    /// channel) or to a person's linked chat. The type's actions are inline buttons, answered through the bot's webhook
    /// (<see cref="TelegramCallbackHandler"/>), registered with <c>setWebhook</c> (<see cref="ICallbackRegistration"/>). No idempotency key.
    /// <list type="bullet">
    /// <item><c>ok</c>: <c>Delivered</c>, the id <c>{chat}:{message_id}</c>;</item>
    /// <item>429: <c>RateLimited</c>; 5xx: retryable <c>Provider</c>; 401, 404 (a bad token): permanent <c>Provider</c>;</item>
    /// <item>403 blocked by the person or the account deactivated, 400 chat not found: <c>Destination</c>, the address gone; 403 removed from a
    /// group or channel: <c>Destination</c>; other 400s: <c>Content</c>.</item>
    /// </list>
    /// </summary>
    public sealed partial class TelegramProvider(TelegramHttp http, IOptions<DeliveryOptions> options) : INotificationProvider, ITargetAddresses, ICallbackRegistration
    {
        public const string KindName = "telegram";

        public string Channel => TelegramChannel.Key;
        public string Kind => KindName;
        public bool Idempotent => false;
        public bool RequiresSecret => true;

        [GeneratedRegex(@"^(-?\d{1,20}|@[A-Za-z][A-Za-z0-9_]{4,31})$")]
        internal static partial Regex Chat();

        [GeneratedRegex(@"^\d{3,20}:[A-Za-z0-9_-]{20,100}$")]
        private static partial Regex Token();

        public string? CheckSettings(string settings) => null;

        public string? NormalizeTarget(string address) => Chat().IsMatch(address.Trim()) ? address.Trim() : null;

        public string TargetRule => "The address is a Telegram chat's id (-100… for a group or channel) or a public channel's @name.";

        /// <summary>
        /// The webhook's secret token, which Telegram sends back in <c>X-Telegram-Bot-Api-Secret-Token</c>: derived from the bot token and the
        /// integration, so no second secret is kept and each integration's differs.
        /// </summary>
        public static string WebhookSecret(string botToken, Guid integrationId)
            => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(botToken), Encoding.UTF8.GetBytes("juice-telegram-webhook:" + integrationId)));

        private static Uri Method(string token, string method) => new($"https://api.telegram.org/bot{token}/{method}");

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            if (integration.Secret is not { } token || !Token().IsMatch(token))
            {
                return SendResult.Permanent(FailureCategory.Provider, "The Telegram integration's secret isn't a bot token.");
            }
            if (!Chat().IsMatch(delivery.Destination))
            {
                return SendResult.Permanent(FailureCategory.Destination, "The destination isn't a Telegram chat.");
            }
            ChatContent? content;
            try
            {
                content = JsonSerializer.Deserialize<ChatContent>(delivery.Content);
            }
            catch (JsonException)
            {
                content = null;
            }
            if (content is null)
            {
                return SendResult.Permanent(FailureCategory.Content, "The stored content isn't a chat message.");
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, Method(token, "sendMessage"))
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(TelegramMessages.Message(content, delivery.DeliveryId, delivery.Destination))),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            return failure is null ? Classify(answer!, delivery.Destination) : Redact(failure, token);
        }

        /// <summary>The token is part of the URL: an exception's message mustn't carry it into the delivery's history.</summary>
        private static SendResult Redact(SendResult result, string token)
            => result.Error?.Contains(token, StringComparison.Ordinal) == true ? result with { Error = result.Error.Replace(token, "***") } : result;

        internal static SendResult Classify(ApiAnswer answer, string chat)
        {
            var status = answer.Status;
            string? description = null;
            long? messageId = null;
            try
            {
                using var json = JsonDocument.Parse(answer.Body);
                var root = json.RootElement;
                description = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && root.TryGetProperty("result", out var result)
                    && result.TryGetProperty("message_id", out var id) && id.TryGetInt64(out var value))
                {
                    messageId = value;
                }
            }
            catch (JsonException)
            {
            }
            var text = $"{status}: {description ?? "no description"}";
            if (status is >= 200 and < 300)
            {
                return SendResult.Delivered(status, messageId is { } m ? $"{chat}:{m}" : null);
            }
            var lower = description?.ToLowerInvariant() ?? "";
            return status switch
            {
                429 => SendResult.Retry(FailureCategory.RateLimited, text, status),
                >= 500 => SendResult.Retry(FailureCategory.Provider, text, status),
                401 or 404 => SendResult.Permanent(FailureCategory.Provider, text, status),
                403 when lower.Contains("blocked by the user") || lower.Contains("user is deactivated") => SendResult.Gone(text, status),
                403 => SendResult.Permanent(FailureCategory.Destination, text, status),
                400 when lower.Contains("chat not found") || lower.Contains("user not found") => SendResult.Gone(text, status),
                400 when lower.Contains("not enough rights") || lower.Contains("have no rights") => SendResult.Permanent(FailureCategory.Destination, text, status),
                400 or 413 => SendResult.Permanent(FailureCategory.Content, text, status),
                _ => SendResult.Permanent(FailureCategory.Provider, text, status),
            };
        }

        public async Task<string?> RegisterCallbacksAsync(IntegrationContext integration, Uri url, CancellationToken ct)
        {
            if (integration.Secret is not { } token || !Token().IsMatch(token))
            {
                return "The integration's secret isn't a bot token.";
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, Method(token, "setWebhook"))
            {
                Content = JsonContent(new
                {
                    url = url.AbsoluteUri, secret_token = WebhookSecret(token, integration.IntegrationId),
                    allowed_updates = new[] { "message", "callback_query" }, drop_pending_updates = false,
                }),
            };
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            if (failure is not null)
            {
                return "Telegram didn't answer: " + Redact(failure, token).Error;
            }
            return answer!.Status == (int)HttpStatusCode.OK ? null : $"Telegram refused the webhook: {answer.Field("description") ?? answer.Status.ToString()}";
        }

        private static ByteArrayContent JsonContent(object value)
        {
            var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return content;
        }
    }

    public static class TelegramRegistration
    {
        /// <summary>The <c>telegram</c> channel, its provider (kind <c>telegram</c>) and the bot's webhook (<c>/api/v1/callbacks/telegram/…</c>).</summary>
        public static IServiceCollection AddTelegram(this IServiceCollection services)
        {
            services.AddSingleton(TelegramChannel.Definition);
            services.TryAddSingleton(sp => TelegramHttp.Create(sp.GetRequiredService<IOptions<DeliveryOptions>>().Value));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, TelegramProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallbackHandler, TelegramCallbackHandler>());
            return services;
        }
    }
}
