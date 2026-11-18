using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Juice.Notifications.Rendering;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Email
{
    /// <summary>
    /// A SendGrid integration's settings: the sender (a verified sender identity), the API's region (<c>global</c> or <c>eu</c>), and the event
    /// webhook's verification key (the public key SendGrid shows when its signed event webhook is turned on), which callbacks are checked with.
    /// </summary>
    public sealed record SendGridSettings(string? From, string? FromName, string? Region, string? EventWebhookKey);

    /// <summary>
    /// Email through SendGrid (v3 <c>mail/send</c>; phase-4-plan M26), with the integration's API key as its secret. The delivery id goes as the
    /// custom argument <c>delivery_id</c>, which every event of the message carries back (<see cref="SendGridCallbackHandler"/>). SendGrid has no
    /// idempotency key, so the provider isn't idempotent.
    /// <list type="bullet">
    /// <item><c>202</c>: <c>Accepted</c>, with the <c>X-Message-Id</c>;</item>
    /// <item><c>429</c>: <c>RateLimited</c>; 5xx: retryable <c>Provider</c>;</item>
    /// <item><c>401</c>, <c>403</c> (a key without the permission, an unverified sender, a suspended account): permanent <c>Provider</c>;</item>
    /// <item><c>400</c> naming the recipient's field: permanent <c>Destination</c>, the address gone; another <c>400</c>, or <c>413</c>: <c>Content</c>.</item>
    /// </list>
    /// </summary>
    public sealed class SendGridProvider(EmailHttp http, IOptions<DeliveryOptions> options) : INotificationProvider
    {
        public const string KindName = "sendgrid";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public string Channel => Channels.Email;
        public string Kind => KindName;
        public bool Idempotent => false;
        public bool RequiresSecret => true;

        internal static SendGridSettings? Read(string settings)
        {
            try
            {
                return JsonSerializer.Deserialize<SendGridSettings>(settings, Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The event webhook's verification key: a base64 ECDSA P-256 public key, or null when it isn't one.</summary>
        internal static ECDsa? VerificationKey(string? value)
        {
            if (value is null)
            {
                return null;
            }
            var key = ECDsa.Create();
            try
            {
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(value), out _);
                return key;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                key.Dispose();
                return null;
            }
        }

        public string? CheckSettings(string settings)
        {
            var s = Read(settings);
            if (s is null)
            {
                return "settings is an object with from and optionally fromName, region and eventWebhookKey.";
            }
            if (!EmailAddresses.IsAddress(s.From))
            {
                return "settings.from is the sender's bare email address, a sender identity verified in SendGrid.";
            }
            if (s.FromName is { Length: > 200 })
            {
                return "settings.fromName is up to 200 characters.";
            }
            if (s.Region is not (null or "global" or "eu"))
            {
                return "settings.region is global or eu.";
            }
            if (s.EventWebhookKey is not null)
            {
                using var key = VerificationKey(s.EventWebhookKey);
                if (key is null)
                {
                    return "settings.eventWebhookKey is the signed event webhook's verification key, as SendGrid shows it (base64).";
                }
            }
            return null;
        }

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            var settings = Read(integration.Settings);
            if (settings?.From is null)
            {
                return SendResult.Permanent(FailureCategory.Provider, "The SendGrid integration's settings name no sender.");
            }
            if (integration.Secret is not { Length: > 0 } key)
            {
                return SendResult.Permanent(FailureCategory.Provider, "The SendGrid integration has no API key (secretRef).");
            }
            if (!EmailAddresses.IsRecipient(delivery.Destination))
            {
                return SendResult.Permanent(FailureCategory.Destination, "The destination isn't an email address.");
            }
            EmailContent content;
            try
            {
                content = JsonSerializer.Deserialize<EmailContent>(delivery.Content)!;
            }
            catch (JsonException ex)
            {
                return SendResult.Permanent(FailureCategory.Content, "The stored content isn't an email: " + ex.Message);
            }
            if (content?.Subject is null || content.Text is null)
            {
                return SendResult.Permanent(FailureCategory.Content, "The stored content has no subject or text.");
            }
            var parts = new List<object> { new { type = "text/plain", value = content.Text } };
            if (!string.IsNullOrEmpty(content.Html))
            {
                parts.Add(new { type = "text/html", value = content.Html });
            }
            var body = new
            {
                personalizations = new[] { new { to = new[] { new { email = delivery.Destination } } } },
                from = string.IsNullOrWhiteSpace(settings.FromName) ? (object)new { email = settings.From } : new { email = settings.From, name = settings.FromName },
                subject = content.Subject,
                content = parts,
                custom_args = new { delivery_id = delivery.DeliveryId.ToString() },
            };
            var host = settings.Region == "eu" ? "api.eu.sendgrid.com" : "api.sendgrid.com";
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v3/mail/send")
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            return failure ?? Classify(answer!);
        }

        internal static SendResult Classify(ApiAnswer answer)
        {
            var status = answer.Status;
            if (status is >= 200 and < 300)
            {
                return SendResult.Accepted(answer.Header("X-Message-Id"));
            }
            var message = answer.Field("errors", "message");
            var field = answer.Field("errors", "field");
            var error = $"{status}: {Reasons.Clean(message) ?? "refused"}" + (field is null ? "" : $" ({field})");
            return status switch
            {
                429 => SendResult.Retry(FailureCategory.RateLimited, error, status),
                401 or 403 => SendResult.Permanent(FailureCategory.Provider, error, status),
                400 when field is not null && field.StartsWith("personalizations", StringComparison.Ordinal)
                    && (field.EndsWith(".email", StringComparison.Ordinal) || field.Contains(".to", StringComparison.Ordinal))
                    => SendResult.Gone(error, status),
                400 when field is not null && field.StartsWith("from", StringComparison.Ordinal) => SendResult.Permanent(FailureCategory.Provider, error, status),
                400 or 413 => SendResult.Permanent(FailureCategory.Content, error, status),
                >= 500 => SendResult.Retry(FailureCategory.Provider, error, status),
                _ => SendResult.Permanent(FailureCategory.Provider, error, status),
            };
        }
    }
}
