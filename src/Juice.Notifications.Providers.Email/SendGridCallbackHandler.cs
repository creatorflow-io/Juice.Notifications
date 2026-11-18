using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Juice.Notifications.Providers.Email
{
    /// <summary>
    /// SendGrid's signed event webhook (phase-4-plan p4-d6, M26): an ECDSA P-256 signature (SHA-256, DER, base64) over the timestamp header and the
    /// body as sent, checked with the integration's <c>settings.eventWebhookKey</c>. The body is a batch of events, each with its own
    /// <c>sg_event_id</c>:
    /// <list type="bullet">
    /// <item><c>delivered</c>: a receipt;</item>
    /// <item><c>bounce</c>: a hard bounce, or a soft one when its <c>type</c> is <c>blocked</c> (the receiving server refused it for now);</item>
    /// <item><c>dropped</c> for a bounced or invalid address: a hard bounce; for a spam report or an unsubscribe: a complaint (SendGrid's
    /// suppression list stops it, and the contact point should stop too);</item>
    /// <item><c>spamreport</c>, <c>unsubscribe</c>, <c>group_unsubscribe</c>: a complaint.</item>
    /// </list>
    /// Others (<c>processed</c>, <c>deferred</c>, opens, clicks) are ignored. The delivery is found by the <c>delivery_id</c> custom argument, or
    /// else by the message id (the <c>sg_message_id</c> up to its first dot, as <c>X-Message-Id</c> gave it).
    /// </summary>
    public sealed class SendGridCallbackHandler : ICallbackHandler
    {
        public const string SignatureHeader = "X-Twilio-Email-Event-Webhook-Signature";
        public const string TimestampHeader = "X-Twilio-Email-Event-Webhook-Timestamp";

        public string Provider => "sendgrid";

        public bool Handles(string integrationKind) => integrationKind == SendGridProvider.KindName;

        public Task<CallbackResult> ReadAsync(CallbackRequest request, IntegrationContext integration, CancellationToken ct)
        {
            using var key = SendGridProvider.VerificationKey(SendGridProvider.Read(integration.Settings)?.EventWebhookKey);
            if (key is null)
            {
                return Task.FromResult(CallbackResult.Refused("the integration has no event webhook verification key (settings.eventWebhookKey)"));
            }
            if (request.Header(SignatureHeader) is not { Length: > 0 } signature || request.Header(TimestampHeader) is not { Length: > 0 } timestamp)
            {
                return Task.FromResult(CallbackResult.Refused("unsigned"));
            }
            if (!Verify(key, timestamp, request.Body, signature))
            {
                return Task.FromResult(CallbackResult.Refused("a bad signature"));
            }
            return Task.FromResult(Read(request.Body));
        }

        internal static bool Verify(ECDsa key, string timestamp, byte[] body, string signature)
        {
            try
            {
                var signed = Encoding.UTF8.GetBytes(timestamp).Concat(body).ToArray();
                return key.VerifyData(signed, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                return false;
            }
        }

        internal static CallbackResult Read(byte[] body)
        {
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return CallbackResult.Refused("signed, but not a batch of events");
            }
            using (json)
            {
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return CallbackResult.Refused("signed, but not a batch of events");
                }
                var events = new List<CallbackEvent>();
                foreach (var e in json.RootElement.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object || Text(e, "sg_event_id") is not { } id || Kind(e) is not { } kind)
                    {
                        continue;
                    }
                    var at = e.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var seconds)
                        && seconds is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UtcNow;
                    var reason = Text(e, "reason") ?? Text(e, "response");
                    events.Add(new CallbackEvent(id, kind.Kind, at)
                    {
                        DeliveryId = Text(e, "delivery_id") is { } d && Guid.TryParse(d, out var deliveryId) ? deliveryId : null,
                        ProviderMessageId = Text(e, "sg_message_id")?.Split('.')[0],
                        Detail = Reasons.Clean(kind.Detail ?? reason),
                    });
                }
                return new CallbackResult(null, events);
            }
        }

        private static (CallbackKind Kind, string? Detail)? Kind(JsonElement e)
        {
            var reason = Text(e, "reason") ?? "";
            return Text(e, "event") switch
            {
                "delivered" => (CallbackKind.Delivered, null),
                "bounce" when Text(e, "type") == "blocked" => (CallbackKind.SoftBounced, null),
                "bounce" => (CallbackKind.Bounced, null),
                "dropped" when reason.Contains("Bounced", StringComparison.OrdinalIgnoreCase) || reason.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                    => (CallbackKind.Bounced, "dropped: " + reason),
                "dropped" when reason.Contains("Spam Reporting", StringComparison.OrdinalIgnoreCase) || reason.Contains("Unsubscribed", StringComparison.OrdinalIgnoreCase)
                    => (CallbackKind.Complained, "dropped: " + reason),
                "spamreport" => (CallbackKind.Complained, "reported as spam"),
                "unsubscribe" or "group_unsubscribe" => (CallbackKind.Complained, "unsubscribed through SendGrid"),
                _ => null,
            };
        }

        private static string? Text(JsonElement e, string name)
            => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
