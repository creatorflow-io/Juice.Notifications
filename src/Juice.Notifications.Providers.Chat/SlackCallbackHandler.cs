using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Juice.Notifications.Providers.Chat
{
    /// <summary>
    /// The Slack app's callbacks (phase-4-plan p4-d6, M27), for both its Interactivity and its Event Subscriptions request URL
    /// (<c>/api/v1/callbacks/slack/{integrationId}</c>). Each is checked with the app's signing secret: <c>X-Slack-Signature</c> is
    /// <c>v0=</c> HMAC-SHA256 of <c>v0:{timestamp}:{body}</c>, and the timestamp within five minutes (Slack's own rule against replays).
    /// <list type="bullet">
    /// <item>a button of ours (<c>block_actions</c>, <c>action_id</c> <c>act:{id}</c>, the delivery id its value): an interaction by the person
    /// whose account pressed it;</item>
    /// <item><c>url_verification</c>: its challenge, answered;</item>
    /// <item>a direct message to the app (<c>message</c> in an <c>im</c>, from a person, not a bot): a link code, when it reads as one.</item>
    /// </list>
    /// Everything else is acknowledged and ignored.
    /// </summary>
    public sealed class SlackCallbackHandler(TimeProvider clock) : ICallbackHandler
    {
        public const string SignatureHeader = "X-Slack-Signature";
        public const string TimestampHeader = "X-Slack-Request-Timestamp";
        public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

        public string Provider => "slack";

        public bool Handles(string integrationKind) => integrationKind == SlackProvider.KindName;

        public static string Sign(string signingSecret, string timestamp, byte[] body)
            => "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret),
                Encoding.UTF8.GetBytes($"v0:{timestamp}:").Concat(body).ToArray()));

        public Task<CallbackResult> ReadAsync(CallbackRequest request, IntegrationContext integration, CancellationToken ct)
            => Task.FromResult(Read(request, integration));

        private CallbackResult Read(CallbackRequest request, IntegrationContext integration)
        {
            if (SlackProvider.Secrets(integration.Secret) is not { } secrets)
            {
                return CallbackResult.Refused("the integration's secret has no signing secret ({botToken}:{signingSecret})");
            }
            if (request.Header(SignatureHeader) is not { } signature || request.Header(TimestampHeader) is not { } timestamp)
            {
                return CallbackResult.Refused("unsigned");
            }
            if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > 253402300799
                || (clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
            {
                return CallbackResult.Refused("its timestamp is too far from now");
            }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secrets.SigningSecret, timestamp, request.Body)),
                    Encoding.ASCII.GetBytes(signature)))
            {
                return CallbackResult.Refused("a bad signature");
            }
            var body = Encoding.UTF8.GetString(request.Body);
            var form = request.Header("Content-Type")?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true
                || body.StartsWith("payload=", StringComparison.Ordinal);
            try
            {
                return form ? Interaction(HttpUtility.ParseQueryString(body)["payload"]) : Event(body);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                return CallbackResult.Of(); // signed, but not a shape we read: acknowledged, so Slack doesn't retry it
            }
        }

        private static CallbackResult Interaction(string? payload)
        {
            if (payload is null)
            {
                return CallbackResult.Of();
            }
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (Text(root, "type") != "block_actions" || !root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            {
                return CallbackResult.Of();
            }
            var user = root.TryGetProperty("user", out var u) ? Text(u, "id") : null;
            var events = new List<CallbackEvent>();
            foreach (var action in actions.EnumerateArray())
            {
                if (Text(action, "action_id") is not { } actionId || !actionId.StartsWith(SlackBlocks.ActionPrefix, StringComparison.Ordinal)
                    || !Guid.TryParse(Text(action, "value"), out var deliveryId) || user is null)
                {
                    continue;
                }
                var at = Text(action, "action_ts") ?? "";
                var when = double.TryParse(at, NumberStyles.Float, CultureInfo.InvariantCulture, out var ts) && ts is > 0 and < 253402300799
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(ts * 1000)) : DateTimeOffset.UtcNow;
                events.Add(new CallbackEvent($"action:{at}:{user}:{actionId}", CallbackKind.Interaction, when)
                {
                    DeliveryId = deliveryId, ActionId = actionId[SlackBlocks.ActionPrefix.Length..], Account = user,
                });
            }
            return new CallbackResult(null, events);
        }

        private static CallbackResult Event(string body)
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            switch (Text(root, "type"))
            {
                case "url_verification":
                    return new CallbackResult(null, [], new CallbackReply(200, "text/plain", Text(root, "challenge") ?? ""));
                case "event_callback" when Text(root, "event_id") is { } eventId && root.TryGetProperty("event", out var e)
                    && Text(e, "type") == "message" && Text(e, "channel_type") == "im" && !e.TryGetProperty("subtype", out _) && !e.TryGetProperty("bot_id", out _)
                    && Text(e, "user") is { } user && Text(e, "text") is { } text:
                    var at = root.TryGetProperty("event_time", out var t) && t.TryGetInt64(out var seconds) && seconds is > 0 and < 253402300799
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UtcNow;
                    return CallbackResult.Of(new CallbackEvent(eventId, CallbackKind.Link, at) { Account = user, Detail = text.Trim() });
                default:
                    return CallbackResult.Of();
            }
        }

        private static string? Text(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
