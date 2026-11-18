using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Push
{
    /// <summary>
    /// An APNs integration's settings: the team and the signing key's id (with the key itself, the <c>.p8</c> PEM, as the secret), the app's
    /// bundle id (the topic), and the environment (<c>production</c> or <c>sandbox</c>).
    /// </summary>
    public sealed record ApnsSettings(string? TeamId, string? KeyId, string? BundleId, string? Environment);

    /// <summary>
    /// Apple Push Notification service, token-based (<c>apns</c>; phase-4-plan M30): HTTP/2 to <c>/3/device/{token}</c> with a provider token
    /// (an ES256 JWT, renewed every 50 minutes) as bearer. <c>apns-collapse-id</c> and <c>apns-id</c> are the delivery id. Not idempotent.
    /// <list type="bullet">
    /// <item><c>200</c>: <c>Accepted</c>, the <c>apns-id</c>;</item>
    /// <item>410 <c>Unregistered</c>, 400 <c>BadDeviceToken</c> or <c>DeviceTokenNotForTopic</c>: <c>Destination</c>, gone;</item>
    /// <item>429: <c>RateLimited</c>; 500, 503: retryable <c>Provider</c>;</item>
    /// <item>403 (the provider token refused), a wrong topic: permanent <c>Provider</c>; a payload too large and other 400s: <c>Content</c>.</item>
    /// </list>
    /// </summary>
    public sealed partial class ApnsProvider(PushHttp http, IOptions<DeliveryOptions> options, TimeProvider clock) : INotificationProvider
    {
        public const string KindName = DevicePlatforms.Apns;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> _tokens = new(StringComparer.Ordinal);

        public string Channel => PushChannel.Key;
        public string Kind => KindName;
        public bool Idempotent => false;
        public bool RequiresSecret => true;

        [GeneratedRegex(@"^[A-Z0-9]{10}$")]
        private static partial Regex AppleId();

        [GeneratedRegex(@"^[A-Za-z0-9.-]{1,155}$")]
        private static partial Regex BundleId();

        [GeneratedRegex(@"^[0-9a-fA-F]{64,200}$")]
        private static partial Regex DeviceToken();

        internal static ApnsSettings? Read(string settings)
        {
            try
            {
                return JsonSerializer.Deserialize<ApnsSettings>(settings, Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public string? CheckSettings(string settings)
        {
            var s = Read(settings);
            if (s?.TeamId is null || !AppleId().IsMatch(s.TeamId) || s.KeyId is null || !AppleId().IsMatch(s.KeyId))
            {
                return "settings.teamId and settings.keyId are the 10-character ids Apple gives the team and the signing key.";
            }
            if (s.BundleId is null || !BundleId().IsMatch(s.BundleId))
            {
                return "settings.bundleId is the app's bundle id.";
            }
            return s.Environment is null or "production" or "sandbox" ? null : "settings.environment is production or sandbox.";
        }

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            var settings = Read(integration.Settings);
            if (settings is not { TeamId: { } team, KeyId: { } keyId, BundleId: { } topic })
            {
                return SendResult.Permanent(FailureCategory.Provider, "The APNs integration's settings name no team, key or bundle id.");
            }
            if (!DeviceToken().IsMatch(delivery.Destination))
            {
                return SendResult.Gone("The device token isn't an APNs token.");
            }
            if (PushContent.Read(delivery.Content) is not { } content)
            {
                return SendResult.Permanent(FailureCategory.Content, "The stored content isn't a push message.");
            }
            string token;
            try
            {
                token = ProviderToken(team, keyId, integration.Secret ?? "");
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                return SendResult.Permanent(FailureCategory.Provider, "The APNs signing key (secret) can't be read: " + ex.Message);
            }
            var host = settings.Environment == "sandbox" ? "api.sandbox.push.apple.com" : "api.push.apple.com";
            var high = delivery.Priority is NotificationPriority.High or NotificationPriority.Critical;
            var payload = new Dictionary<string, object>
            {
                ["aps"] = new Dictionary<string, object>
                {
                    ["alert"] = new { title = content.Title, body = PushContent.Body(content, 1500) }, ["sound"] = "default",
                },
                ["deliveryId"] = delivery.DeliveryId.ToString(),
            };
            if (content.Url is { } url)
            {
                payload["url"] = url;
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/3/device/{delivery.Destination}")
            {
                Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
            request.Headers.TryAddWithoutValidation("apns-topic", topic);
            request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            request.Headers.TryAddWithoutValidation("apns-priority", high ? "10" : "5");
            request.Headers.TryAddWithoutValidation("apns-id", delivery.DeliveryId.ToString());
            request.Headers.TryAddWithoutValidation("apns-collapse-id", delivery.DeliveryId.ToString());
            request.Headers.TryAddWithoutValidation("apns-expiration", clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            if (answer is { Status: 403 })
            {
                _tokens.TryRemove(team + "/" + keyId, out _); // ExpiredProviderToken and friends: the next attempt signs a new one
            }
            return failure ?? Classify(answer!);
        }

        /// <summary>The provider token (ES256, <c>kid</c> the key id, <c>iss</c> the team), signed anew after 50 minutes (Apple refuses one older than an hour).</summary>
        private string ProviderToken(string team, string keyId, string pem)
        {
            var now = clock.GetUtcNow();
            var cacheKey = team + "/" + keyId;
            if (_tokens.TryGetValue(cacheKey, out var cached) && cached.Until > now)
            {
                return cached.Token;
            }
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            var token = PushContent.Jwt(new { alg = "ES256", kid = keyId }, new { iss = team, iat = now.ToUnixTimeSeconds() },
                bytes => key.SignData(bytes, HashAlgorithmName.SHA256));
            _tokens[cacheKey] = (token, now.AddMinutes(50));
            return token;
        }

        internal static SendResult Classify(ApiAnswer answer)
        {
            var status = answer.Status;
            if (status == 200)
            {
                return SendResult.Accepted(answer.Header("apns-id"));
            }
            var reason = answer.Field("reason");
            var text = $"{status}: {reason ?? "refused"}";
            return (status, reason) switch
            {
                (410, _) or (400, "BadDeviceToken" or "DeviceTokenNotForTopic") => SendResult.Gone(text, status),
                (429, _) => SendResult.Retry(FailureCategory.RateLimited, text, status),
                (>= 500, _) => SendResult.Retry(FailureCategory.Provider, text, status),
                (403, _) or (400, "BadTopic" or "TopicDisallowed" or "MissingTopic" or "BadCertificate" or "BadCertificateEnvironment") or (404, _)
                    => SendResult.Permanent(FailureCategory.Provider, text, status),
                (400 or 413, _) => SendResult.Permanent(FailureCategory.Content, text, status),
                _ => SendResult.Permanent(FailureCategory.Provider, text, status),
            };
        }
    }
}
