using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Juice.Notifications.Providers.Telegram
{
    /// <summary>
    /// The bot's webhook (phase-4-plan p4-d6, M28): Telegram sends each update with <c>X-Telegram-Bot-Api-Secret-Token</c>, the secret the
    /// integration's <c>setWebhook</c> set (<see cref="TelegramProvider.WebhookSecret"/>); anything without it is refused. Each update's id is
    /// its event id, with the integration's (Telegram repeats an update until it gets a 200; each bot numbers its own).
    /// <list type="bullet">
    /// <item><c>/start CODE</c> in a private chat (the bot's deep link <c>t.me/{bot}?start=CODE</c> sends it): a link request from the person's
    /// account, their user id, which is also their private chat's id. The bot answers in the chat (through the webhook's reply).</item>
    /// <item>an inline button of ours (<c>callback_query</c>, data <c>a:{delivery}:{action}</c>): an interaction by the person who pressed it;
    /// the button's spinner is answered.</item>
    /// </list>
    /// </summary>
    public sealed class TelegramCallbackHandler : ICallbackHandler
    {
        public const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

        public const string LinkReply = "Thanks. If the code was current, this chat now gets your notifications; you can see it in your inbox's settings.";

        public string Provider => "telegram";

        public bool Handles(string integrationKind) => integrationKind == TelegramProvider.KindName;

        public Task<CallbackResult> ReadAsync(CallbackRequest request, IntegrationContext integration, CancellationToken ct)
        {
            if (integration.Secret is not { Length: > 0 } token)
            {
                return Task.FromResult(CallbackResult.Refused("the integration has no bot token"));
            }
            var expected = Encoding.ASCII.GetBytes(TelegramProvider.WebhookSecret(token, integration.IntegrationId));
            if (request.Header(SecretHeader) is not { } sent || !CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(sent)))
            {
                return Task.FromResult(CallbackResult.Refused("no or a wrong secret token"));
            }
            try
            {
                return Task.FromResult(Read(request.Body, integration.IntegrationId));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return Task.FromResult(CallbackResult.Of());
            }
        }

        internal static CallbackResult Read(byte[] body, Guid integrationId)
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (!root.TryGetProperty("update_id", out var u) || !u.TryGetInt64(out var updateId))
            {
                return CallbackResult.Of();
            }
            // Update ids are numbered per bot, and a tenant can have several: the integration is part of the event's id.
            var eventId = $"u:{integrationId:N}:{updateId}";
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("chat", out var chat) && Text(chat, "type") == "private"
                && message.TryGetProperty("from", out var from) && !Bool(from, "is_bot") && Id(from) is { } user
                && Text(message, "text") is { } text && text.StartsWith("/start", StringComparison.Ordinal))
            {
                var code = text["/start".Length..].Trim();
                var at = message.TryGetProperty("date", out var d) && d.TryGetInt64(out var seconds) && seconds is > 0 and < 253402300799
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UtcNow;
                var reply = JsonSerializer.Serialize(new { method = "sendMessage", chat_id = Id(chat), text = code.Length > 0 ? LinkReply
                    : "Open the link from your inbox's settings to connect this chat." });
                return new CallbackResult(null, code.Length > 0 ? [new CallbackEvent(eventId, CallbackKind.Link, at) { Account = user, Detail = code }] : [],
                    new CallbackReply(200, "application/json", reply));
            }
            if (root.TryGetProperty("callback_query", out var query) && query.ValueKind == JsonValueKind.Object
                && query.TryGetProperty("from", out var presser) && Id(presser) is { } account
                && TelegramMessages.ReadCallbackData(Text(query, "data")) is { } pressed)
            {
                var answer = JsonSerializer.Serialize(new { method = "answerCallbackQuery", callback_query_id = Text(query, "id"), text = "Received" });
                return new CallbackResult(null, [new CallbackEvent(eventId, CallbackKind.Interaction, DateTimeOffset.UtcNow)
                {
                    DeliveryId = pressed.DeliveryId, ActionId = pressed.ActionId, Account = account,
                }], new CallbackReply(200, "application/json", answer));
            }
            return CallbackResult.Of();
        }

        private static string? Id(JsonElement e)
            => e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value)
                ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        private static string? Text(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
