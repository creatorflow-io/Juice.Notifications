using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Push
{
    /// <summary>An FCM integration's settings: the Firebase project (left out: the service account's <c>project_id</c>).</summary>
    public sealed record FcmSettings(string? ProjectId);

    /// <summary>
    /// Firebase Cloud Messaging, HTTP v1 (<c>fcm</c>; phase-4-plan M30): the secret is the service account's JSON key; the provider signs a JWT
    /// with it for an access token (cached until five minutes before it expires) at Google's token endpoint, and sends to
    /// <c>projects/{project}/messages:send</c>. The collapse key is the delivery id, so a resend replaces rather than adds. Not idempotent.
    /// <list type="bullet">
    /// <item><c>200</c>: <c>Accepted</c>, the message's name;</item>
    /// <item><c>UNREGISTERED</c>, or a token FCM calls invalid: <c>Destination</c>, gone (the registration is deleted);</item>
    /// <item><c>QUOTA_EXCEEDED</c>, 429: <c>RateLimited</c>; <c>UNAVAILABLE</c>, <c>INTERNAL</c>, 5xx: retryable <c>Provider</c>;</item>
    /// <item>the key or project refused (401, 403, <c>SENDER_ID_MISMATCH</c>, <c>THIRD_PARTY_AUTH_ERROR</c>): permanent <c>Provider</c>; another
    /// <c>INVALID_ARGUMENT</c>: <c>Content</c>.</item>
    /// </list>
    /// </summary>
    public sealed class FcmProvider(PushHttp http, IOptions<DeliveryOptions> options, TimeProvider clock) : INotificationProvider
    {
        public const string KindName = DevicePlatforms.Fcm;
        /// <summary>Google's token endpoint, always: a key's own <c>token_uri</c> is never called, so a key can't point the service elsewhere.</summary>
        internal const string TokenUri = "https://oauth2.googleapis.com/token";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> _tokens = new(StringComparer.Ordinal);

        public string Channel => PushChannel.Key;
        public string Kind => KindName;
        public bool Idempotent => false;
        public bool RequiresSecret => true;

        public string? CheckSettings(string settings)
        {
            try
            {
                var s = JsonSerializer.Deserialize<FcmSettings>(settings, Json);
                return s?.ProjectId is { } p && (p.Length > 100 || !p.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                    ? "settings.projectId is a Firebase project id." : null;
            }
            catch (JsonException)
            {
                return "settings is an object with an optional projectId.";
            }
        }

        internal sealed record ServiceAccount(string ClientEmail, string PrivateKey, string? ProjectId);

        internal static ServiceAccount? Account(string? secret)
        {
            try
            {
                using var json = JsonDocument.Parse(secret ?? "");
                var root = json.RootElement;
                return root.TryGetProperty("client_email", out var email) && root.TryGetProperty("private_key", out var key)
                    ? new ServiceAccount(email.GetString()!, key.GetString()!, root.TryGetProperty("project_id", out var p) ? p.GetString() : null)
                    : null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            if (Account(integration.Secret) is not { } account)
            {
                return SendResult.Permanent(FailureCategory.Provider, "The FCM integration's secret isn't a service account key (JSON).");
            }
            string? project;
            try
            {
                project = JsonSerializer.Deserialize<FcmSettings>(integration.Settings, Json)?.ProjectId ?? account.ProjectId;
            }
            catch (JsonException)
            {
                project = account.ProjectId;
            }
            if (project is null)
            {
                return SendResult.Permanent(FailureCategory.Provider, "The FCM integration names no project.");
            }
            if (PushContent.Read(delivery.Content) is not { } content)
            {
                return SendResult.Permanent(FailureCategory.Content, "The stored content isn't a push message.");
            }
            var (token, refused) = await AccessTokenAsync(account, ct);
            if (token is null)
            {
                return refused!;
            }
            var id = delivery.DeliveryId.ToString();
            var data = new Dictionary<string, string> { ["deliveryId"] = id };
            if (content.Url is { } url)
            {
                data["url"] = url;
            }
            var high = delivery.Priority is NotificationPriority.High or NotificationPriority.Critical;
            var message = new
            {
                message = new
                {
                    token = delivery.Destination,
                    notification = new { title = content.Title, body = PushContent.Body(content) },
                    data,
                    android = new { collapse_key = id, priority = high ? "high" : "normal" },
                    apns = new { headers = new Dictionary<string, string> { ["apns-collapse-id"] = id } },
                },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(project)}/messages:send")
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(message)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            if (answer is { Status: 401 })
            {
                _tokens.TryRemove(account.ClientEmail, out _); // a revoked token: the next attempt asks for a new one
            }
            return failure ?? Classify(answer!);
        }

        private async Task<(string? Token, SendResult? Refused)> AccessTokenAsync(ServiceAccount account, CancellationToken ct)
        {
            var now = clock.GetUtcNow();
            if (_tokens.TryGetValue(account.ClientEmail, out var cached) && cached.Until > now)
            {
                return (cached.Token, null);
            }
            string assertion;
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(account.PrivateKey);
                assertion = PushContent.Jwt(new { alg = "RS256", typ = "JWT" }, new
                {
                    iss = account.ClientEmail, scope = "https://www.googleapis.com/auth/firebase.messaging", aud = TokenUri,
                    iat = now.ToUnixTimeSeconds(), exp = now.AddHours(1).ToUnixTimeSeconds(),
                }, bytes => rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                return (null, SendResult.Permanent(FailureCategory.Provider, "The service account's private key can't be read: " + ex.Message));
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUri)
            {
                Content = new FormUrlEncodedContent([new("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"), new("assertion", assertion)]),
            };
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            if (failure is not null)
            {
                // Nothing was sent to the device yet: no answer from the token endpoint is a plain retry, never an unknown outcome.
                return (null, SendResult.Retry(FailureCategory.Provider, "Google's token endpoint: " + failure.Error));
            }
            if (answer!.Status is not (>= 200 and < 300) || answer.Field("access_token") is not { } token)
            {
                return (null, answer.Status >= 500
                    ? SendResult.Retry(FailureCategory.Provider, $"Google's token endpoint answered {answer.Status}.", answer.Status)
                    : SendResult.Permanent(FailureCategory.Provider, $"Google refused the service account: {answer.Field("error_description") ?? answer.Field("error") ?? answer.Status.ToString()}", answer.Status));
            }
            int seconds;
            try
            {
                using var json = JsonDocument.Parse(answer.Body);
                seconds = json.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            }
            catch (JsonException)
            {
                seconds = 3600;
            }
            _tokens[account.ClientEmail] = (token, now.AddSeconds(Math.Max(60, seconds - 300)));
            return (token, null);
        }

        internal static SendResult Classify(ApiAnswer answer)
        {
            var status = answer.Status;
            if (status is >= 200 and < 300)
            {
                return SendResult.Accepted(answer.Field("name"));
            }
            string? code = null;
            try
            {
                using var json = JsonDocument.Parse(answer.Body);
                if (json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    code = details.EnumerateArray().Select(d => d.TryGetProperty("errorCode", out var c) ? c.GetString() : null).FirstOrDefault(c => c is not null);
                }
            }
            catch (JsonException)
            {
            }
            var message = answer.Field("error", "message");
            var text = $"{status} {code ?? answer.Field("error", "status")}: {message}".TrimEnd(' ', ':');
            return (status, code) switch
            {
                (_, "UNREGISTERED") => SendResult.Gone(text, status),
                (400, "INVALID_ARGUMENT") when message?.Contains("registration token", StringComparison.OrdinalIgnoreCase) == true => SendResult.Gone(text, status),
                (_, "QUOTA_EXCEEDED") or (429, _) => SendResult.Retry(FailureCategory.RateLimited, text, status),
                (_, "UNAVAILABLE" or "INTERNAL") or (>= 500, _) => SendResult.Retry(FailureCategory.Provider, text, status),
                (_, "SENDER_ID_MISMATCH" or "THIRD_PARTY_AUTH_ERROR") or (401 or 403 or 404, _) => SendResult.Permanent(FailureCategory.Provider, text, status),
                (400, _) => SendResult.Permanent(FailureCategory.Content, text, status),
                _ => SendResult.Permanent(FailureCategory.Provider, text, status),
            };
        }
    }
}
