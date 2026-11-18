using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Push
{
    /// <summary>
    /// A Web Push integration's settings: the VAPID subject (<c>mailto:</c> or <c>https:</c>, how push services reach the sender) and public key
    /// (base64url, the uncompressed P-256 point the app subscribes with); the private key (base64url, 32 bytes) is the secret.
    /// </summary>
    public sealed record WebPushSettings(string? Subject, string? PublicKey);

    /// <summary>
    /// Web Push (<c>webpush</c>; phase-4-plan M30): the payload encrypted for the subscription (RFC 8291, <c>aes128gcm</c>) and the request signed
    /// with VAPID (RFC 8292). The endpoint is the browser's push service: only the known services' hosts are called (the endpoint comes from a
    /// person's browser, so it could name any address). <c>Topic</c> is the delivery id, so a resend replaces. Not idempotent.
    /// <list type="bullet">
    /// <item><c>201</c>: <c>Accepted</c>;</item>
    /// <item>404, 410 (the subscription expired or was removed): <c>Destination</c>, gone;</item>
    /// <item>429: <c>RateLimited</c>; 5xx: retryable <c>Provider</c>;</item>
    /// <item>401, 403 (the VAPID signature refused): permanent <c>Provider</c>; 413 and other 400s: <c>Content</c>.</item>
    /// </list>
    /// </summary>
    public sealed class WebPushProvider(PushHttp http, IOptions<DeliveryOptions> options, TimeProvider clock) : INotificationProvider
    {
        public const string KindName = DevicePlatforms.WebPush;

        /// <summary>The push services browsers use: a host, or <c>.domain</c> for any host under it.</summary>
        internal static readonly string[] Services =
        [
            "fcm.googleapis.com", "updates.push.services.mozilla.com", ".push.services.mozilla.com", ".notify.windows.com", "web.push.apple.com",
            ".push.apple.com",
        ];

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public string Channel => PushChannel.Key;
        public string Kind => KindName;
        public bool Idempotent => false;
        public bool RequiresSecret => true;

        public string? CheckSettings(string settings)
        {
            WebPushSettings? s;
            try
            {
                s = JsonSerializer.Deserialize<WebPushSettings>(settings, Json);
            }
            catch (JsonException)
            {
                s = null;
            }
            if (s?.Subject is not { } subject || !(subject.StartsWith("mailto:", StringComparison.Ordinal) || subject.StartsWith("https://", StringComparison.Ordinal))
                || subject.Length > 200)
            {
                return "settings.subject is a mailto: or https: URL push services can reach the sender at.";
            }
            try
            {
                return s.PublicKey is { } key && PushContent.FromBase64Url(key) is { Length: 65 } point && point[0] == 4
                    ? null : "settings.publicKey is the VAPID public key, an uncompressed P-256 point in base64url.";
            }
            catch (FormatException)
            {
                return "settings.publicKey is the VAPID public key, an uncompressed P-256 point in base64url.";
            }
        }

        internal static bool IsService(Uri endpoint)
            => endpoint is { Scheme: "https", UserInfo: "", Port: 443 } && Services.Any(h => h.StartsWith('.')
                ? endpoint.IdnHost.EndsWith(h, StringComparison.OrdinalIgnoreCase) && endpoint.IdnHost.Length > h.Length
                : string.Equals(endpoint.IdnHost, h, StringComparison.OrdinalIgnoreCase));

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            WebPushSettings? settings;
            People.WebPushSubscription? subscription;
            try
            {
                settings = JsonSerializer.Deserialize<WebPushSettings>(integration.Settings, Json);
                subscription = JsonSerializer.Deserialize<People.WebPushSubscription>(delivery.Destination, Json);
            }
            catch (JsonException)
            {
                return SendResult.Permanent(FailureCategory.Destination, "The subscription or the settings can't be read.");
            }
            if (settings is not { Subject: { } subject, PublicKey: { } publicKey } || integration.Secret is not { Length: > 0 } privateKey)
            {
                return SendResult.Permanent(FailureCategory.Provider, "The Web Push integration has no VAPID subject, public key or private key.");
            }
            if (subscription is not { Endpoint: { } endpointText, P256dh: { } p256dh, Auth: { } auth } || !Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
            {
                return SendResult.Gone("The subscription has no endpoint or keys.");
            }
            if (!IsService(endpoint))
            {
                return SendResult.Permanent(FailureCategory.Destination, $"The subscription's endpoint isn't a known push service ({endpoint.IdnHost}).");
            }
            if (PushContent.Read(delivery.Content) is not { } content)
            {
                return SendResult.Permanent(FailureCategory.Content, "The stored content isn't a push message.");
            }
            byte[] body;
            string vapid;
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    title = content.Title, body = PushContent.Body(content, 1000), url = content.Url, deliveryId = delivery.DeliveryId.ToString(),
                });
                body = Encrypt(payload, PushContent.FromBase64Url(p256dh), PushContent.FromBase64Url(auth));
                vapid = Vapid(endpoint, subject, publicKey, privateKey, clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
            {
                return SendResult.Permanent(FailureCategory.Destination, "The subscription's keys or the VAPID key can't be used: " + ex.Message);
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            request.Headers.TryAddWithoutValidation("TTL", "86400");
            request.Headers.TryAddWithoutValidation("Urgency", delivery.Priority is NotificationPriority.High or NotificationPriority.Critical ? "high" : "normal");
            request.Headers.TryAddWithoutValidation("Topic", delivery.DeliveryId.ToString("N"));
            request.Headers.TryAddWithoutValidation("Authorization", vapid);
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            return failure ?? Classify(answer!);
        }

        /// <summary>VAPID (RFC 8292): an ES256 JWT for the push service's origin, valid 12 hours, and the public key.</summary>
        internal static string Vapid(Uri endpoint, string subject, string publicKey, string privateKey, DateTimeOffset now)
        {
            var point = PushContent.FromBase64Url(publicKey);
            using var key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256, D = PushContent.FromBase64Url(privateKey),
                Q = new ECPoint { X = point[1..33], Y = point[33..65] },
            });
            var jwt = PushContent.Jwt(new { typ = "JWT", alg = "ES256" },
                new { aud = endpoint.GetLeftPart(UriPartial.Authority), exp = now.AddHours(12).ToUnixTimeSeconds(), sub = subject },
                bytes => key.SignData(bytes, HashAlgorithmName.SHA256));
            return $"vapid t={jwt}, k={publicKey}";
        }

        /// <summary>
        /// RFC 8291 with RFC 8188's <c>aes128gcm</c>, one record: an ephemeral P-256 key agrees a secret with the subscription's key, mixed with its
        /// auth secret into the content key and nonce. The body is the header (salt, record size, the ephemeral public key) and the ciphertext.
        /// </summary>
        internal static byte[] Encrypt(byte[] plaintext, byte[] userAgentPublic, byte[] authSecret, ECDiffieHellman? ephemeral = null, byte[]? salt = null)
        {
            using var own = ephemeral is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
            var server = ephemeral ?? own!;
            var serverParams = server.ExportParameters(false);
            var serverPublic = new byte[65];
            serverPublic[0] = 4;
            serverParams.Q.X!.CopyTo(serverPublic, 1);
            serverParams.Q.Y!.CopyTo(serverPublic, 33);
            using var peer = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = userAgentPublic[1..33], Y = userAgentPublic[33..65] },
            });
            var shared = server.DeriveRawSecretAgreement(peer.PublicKey);
            var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(userAgentPublic).Concat(serverPublic).ToArray();
            var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, keyInfo);
            salt ??= RandomNumberGenerator.GetBytes(16);
            var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
            var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
            var padded = plaintext.Append((byte)2).ToArray(); // the last (and only) record's delimiter
            var cipher = new byte[padded.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(cek, 16))
            {
                aes.Encrypt(nonce, padded, cipher, tag);
            }
            var header = new byte[16 + 4 + 1 + 65];
            salt.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 4096);
            header[20] = 65;
            serverPublic.CopyTo(header, 21);
            return [.. header, .. cipher, .. tag];
        }

        internal static SendResult Classify(ApiAnswer answer)
        {
            var status = answer.Status;
            var text = $"{status}: {(answer.Body.Length > 200 ? answer.Body[..200] : answer.Body)}".TrimEnd(' ', ':');
            return status switch
            {
                >= 200 and < 300 => SendResult.Accepted(answer.Header("Location")),
                404 or 410 => SendResult.Gone(text, status),
                429 => SendResult.Retry(FailureCategory.RateLimited, text, status),
                >= 500 => SendResult.Retry(FailureCategory.Provider, text, status),
                401 or 403 => SendResult.Permanent(FailureCategory.Provider, text, status),
                _ => SendResult.Permanent(FailureCategory.Content, text, status),
            };
        }
    }
}
