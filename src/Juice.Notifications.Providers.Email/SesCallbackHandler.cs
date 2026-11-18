using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Email
{
    /// <summary>
    /// SNS's signing certificates, by URL: fetched once over HTTPS from an <c>sns.{region}.amazonaws.com</c> host and kept (SNS rotates them
    /// under new URLs). At most <see cref="Max"/> are kept; past that the oldest is dropped.
    /// </summary>
    public sealed partial class SnsCertificates(EmailHttp http, IOptions<DeliveryOptions> options)
    {
        private const int Max = 32;
        private readonly ConcurrentDictionary<string, X509Certificate2> _certificates = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _order = new();

        [GeneratedRegex(@"^sns\.[a-z0-9-]+\.amazonaws\.com(\.cn)?$")]
        private static partial Regex Host();

        /// <summary>An SNS URL: HTTPS on SNS's own host, no user info, no port other than 443. Certificates and subscription links must be one.</summary>
        public static bool IsSnsUrl(string? value, out Uri url)
        {
            url = null!;
            return Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed is { Scheme: "https", UserInfo: "", Port: 443 }
                && Host().IsMatch(parsed.Host) && (url = parsed) is not null;
        }

        public async Task<X509Certificate2?> GetAsync(Uri url, CancellationToken ct)
        {
            if (_certificates.TryGetValue(url.AbsoluteUri, out var known))
            {
                return known;
            }
            if (!url.AbsolutePath.EndsWith(".pem", StringComparison.Ordinal))
            {
                return null;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var (answer, _) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            if (answer is not { Status: 200 })
            {
                return null;
            }
            X509Certificate2 certificate;
            try
            {
                certificate = X509Certificate2.CreateFromPem(answer.Body);
            }
            catch (CryptographicException)
            {
                return null;
            }
            if (_certificates.TryAdd(url.AbsoluteUri, certificate))
            {
                _order.Enqueue(url.AbsoluteUri);
                while (_certificates.Count > Max && _order.TryDequeue(out var oldest))
                {
                    _certificates.TryRemove(oldest, out _);
                }
            }
            return _certificates.GetValueOrDefault(url.AbsoluteUri, certificate);
        }
    }

    /// <summary>
    /// SES's events through SNS (phase-4-plan p4-d6, M26): the configuration set's event destination publishes to an SNS topic, and the topic's
    /// HTTPS subscription posts here. Each message is checked before it is read:
    /// <list type="bullet">
    /// <item>its topic is the integration's <c>settings.topicArn</c> (anyone can make a topic and subscribe this URL: SNS's signature alone
    /// proves only that SNS sent it);</item>
    /// <item>its signature (version 1, SHA1, or 2, SHA256) against the certificate at <c>SigningCertURL</c>, fetched only from SNS's own host.</item>
    /// </list>
    /// A subscription confirmation is confirmed by visiting its <c>SubscribeURL</c> (again only on SNS's host). A notification's SES event becomes
    /// a callback event, its id the SNS message id: <c>Delivery</c> a receipt; <c>Bounce</c> a hard bounce when <c>Permanent</c>, a soft one
    /// otherwise; <c>Complaint</c> a complaint. Identity notifications (<c>notificationType</c>) read the same. The delivery is found by the
    /// <c>delivery_id</c> tag, or else by SES's message id.
    /// </summary>
    public sealed class SesCallbackHandler(EmailHttp http, SnsCertificates certificates, IOptions<DeliveryOptions> options, ILogger<SesCallbackHandler> logger)
        : ICallbackHandler
    {
        public string Provider => "ses";

        public bool Handles(string integrationKind) => integrationKind == SesProvider.KindName;

        private sealed record SnsMessage(string? Type, string? MessageId, string? Token, string? TopicArn, string? Subject, string? Message,
            string? SubscribeURL, string? Timestamp, string? SignatureVersion, string? Signature, string? SigningCertURL);

        public async Task<CallbackResult> ReadAsync(CallbackRequest request, IntegrationContext integration, CancellationToken ct)
        {
            if (SesProvider.Read(integration.Settings)?.TopicArn is not { } topic)
            {
                return CallbackResult.Refused("the integration names no topic (settings.topicArn), so no SNS message can be trusted");
            }
            SnsMessage? sns;
            try
            {
                sns = JsonSerializer.Deserialize<SnsMessage>(request.Body);
            }
            catch (JsonException)
            {
                return CallbackResult.Refused("not an SNS message");
            }
            if (sns is not { Type: { } type, MessageId: { } messageId, Message: { } message, Timestamp: { } timestamp, Signature: { } signature })
            {
                return CallbackResult.Refused("not an SNS message");
            }
            if (!string.Equals(sns.TopicArn, topic, StringComparison.Ordinal))
            {
                return CallbackResult.Refused("from another topic than the integration's");
            }
            if (!SnsCertificates.IsSnsUrl(sns.SigningCertURL, out var certificateUrl))
            {
                return CallbackResult.Refused("its signing certificate isn't on SNS's host");
            }
            var toSign = StringToSign(type, sns);
            if (toSign is null)
            {
                return CallbackResult.Refused($"an SNS message of type {type} isn't read");
            }
            var hash = sns.SignatureVersion switch { "1" => HashAlgorithmName.SHA1, "2" => HashAlgorithmName.SHA256, _ => default };
            if (hash == default)
            {
                return CallbackResult.Refused("an unknown signature version");
            }
            var certificate = await certificates.GetAsync(certificateUrl, ct);
            if (certificate is null)
            {
                return CallbackResult.Refused("its signing certificate couldn't be read");
            }
            if (!Verify(certificate, toSign, signature, hash))
            {
                return CallbackResult.Refused("a bad signature");
            }

            switch (type)
            {
                case "SubscriptionConfirmation":
                    return await ConfirmAsync(sns, integration, ct);
                case "UnsubscribeConfirmation":
                    logger.LogWarning("The SNS subscription of integration {Integration} to {Topic} was removed.", integration.Key, topic);
                    return CallbackResult.Of();
                default:
                    return Read(messageId, message);
            }
        }

        private static bool Verify(X509Certificate2 certificate, string toSign, string signature, HashAlgorithmName hash)
        {
            try
            {
                using var rsa = certificate.GetRSAPublicKey();
                return rsa is not null && rsa.VerifyData(Encoding.UTF8.GetBytes(toSign), Convert.FromBase64String(signature), hash, RSASignaturePadding.Pkcs1);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>The string SNS signs: name and value lines, in this order, of the fields the type has (a notification's subject only when set).</summary>
        internal static string? StringToSign(string type, SnsMessageFields sns)
        {
            var fields = type switch
            {
                "Notification" => new[] { ("Message", sns.Message), ("MessageId", sns.MessageId), ("Subject", sns.Subject), ("Timestamp", sns.Timestamp),
                    ("TopicArn", sns.TopicArn), ("Type", sns.Type) },
                "SubscriptionConfirmation" or "UnsubscribeConfirmation" => [("Message", sns.Message), ("MessageId", sns.MessageId),
                    ("SubscribeURL", sns.SubscribeURL), ("Timestamp", sns.Timestamp), ("Token", sns.Token), ("TopicArn", sns.TopicArn), ("Type", sns.Type)],
                _ => null,
            };
            return fields is null ? null : string.Concat(fields.Where(f => f.Item2 is not null).Select(f => $"{f.Item1}\n{f.Item2}\n"));
        }

        private static string? StringToSign(string type, SnsMessage m)
            => StringToSign(type, new SnsMessageFields(m.Type, m.MessageId, m.Token, m.TopicArn, m.Subject, m.Message, m.SubscribeURL, m.Timestamp));

        private async Task<CallbackResult> ConfirmAsync(SnsMessage sns, IntegrationContext integration, CancellationToken ct)
        {
            if (!SnsCertificates.IsSnsUrl(sns.SubscribeURL, out var url))
            {
                return CallbackResult.Refused("its subscription link isn't on SNS's host");
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            if (answer is { Status: 200 })
            {
                logger.LogInformation("Integration {Integration} confirmed its SNS subscription to {Topic}.", integration.Key, sns.TopicArn);
                return CallbackResult.Of();
            }
            // Not confirmed: refused (401), and SNS sends the confirmation again on its delivery policy's schedule.
            logger.LogWarning("Integration {Integration} couldn't confirm its SNS subscription to {Topic}: {Error}", integration.Key, sns.TopicArn,
                answer is null ? failure!.Error : $"{answer.Status}");
            return CallbackResult.Refused("the subscription couldn't be confirmed");
        }

        /// <summary>An SES event (the configuration set's <c>eventType</c>, or an identity notification's <c>notificationType</c>) as callback events.</summary>
        internal static CallbackResult Read(string snsMessageId, string message)
        {
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(message);
            }
            catch (JsonException)
            {
                return CallbackResult.Of(); // verified, but not an SES event: nothing to apply
            }
            using (json)
            {
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return CallbackResult.Of();
                }
                var kind = Text(root, "eventType") ?? Text(root, "notificationType");
                var mail = root.TryGetProperty("mail", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
                var sesId = mail.ValueKind == JsonValueKind.Object ? Text(mail, "messageId") : null;
                Guid? deliveryId = mail.ValueKind == JsonValueKind.Object && mail.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
                    && tags.TryGetProperty("delivery_id", out var tag) && tag.ValueKind == JsonValueKind.Array && tag.GetArrayLength() > 0
                    && tag[0].ValueKind == JsonValueKind.String && Guid.TryParse(tag[0].GetString(), out var parsed) ? parsed : null;
                DateTimeOffset At(JsonElement e) => e.ValueKind == JsonValueKind.Object && Text(e, "timestamp") is { } t && DateTimeOffset.TryParse(t,
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at
                    : mail.ValueKind == JsonValueKind.Object && Text(mail, "timestamp") is { } mt && DateTimeOffset.TryParse(mt,
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var mat) ? mat
                    : DateTimeOffset.UtcNow;
                CallbackEvent? e = kind switch
                {
                    "Delivery" => new CallbackEvent(snsMessageId, CallbackKind.Delivered, At(Child(root, "delivery"))),
                    "Bounce" => Bounce(snsMessageId, Child(root, "bounce"), At),
                    "Complaint" => new CallbackEvent(snsMessageId, CallbackKind.Complained, At(Child(root, "complaint")))
                    {
                        Detail = Text(Child(root, "complaint"), "complaintFeedbackType") ?? "reported as spam",
                    },
                    _ => null,
                };
                return e is null ? CallbackResult.Of() : CallbackResult.Of(e with { DeliveryId = deliveryId, ProviderMessageId = sesId });
            }
        }

        private static CallbackEvent Bounce(string id, JsonElement bounce, Func<JsonElement, DateTimeOffset> at)
        {
            var hard = Text(bounce, "bounceType") == "Permanent";
            var diagnostic = Child(bounce, "bouncedRecipients") is { ValueKind: JsonValueKind.Array } recipients && recipients.GetArrayLength() > 0
                ? Text(recipients[0], "diagnosticCode") : null;
            var detail = $"{Text(bounce, "bounceType") ?? "Undetermined"} bounce ({Text(bounce, "bounceSubType") ?? "General"})"
                + (diagnostic is null ? "" : ": " + diagnostic);
            return new CallbackEvent(id, hard ? CallbackKind.Bounced : CallbackKind.SoftBounced, at(bounce)) { Detail = Reasons.Clean(detail) };
        }

        private static JsonElement Child(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var child) ? child : default;

        private static string? Text(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>The fields of an SNS message its signature covers.</summary>
    internal sealed record SnsMessageFields(string? Type, string? MessageId, string? Token, string? TopicArn, string? Subject, string? Message,
        string? SubscribeURL, string? Timestamp);
}
