using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.Notifications.Rendering;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Email
{
    /// <summary>
    /// An SES integration's settings: the AWS region, the sender (an identity verified in SES), and optionally the configuration set whose event
    /// destination publishes to an SNS topic, and that topic, which callbacks must come from.
    /// </summary>
    public sealed record SesSettings(string? Region, string? From, string? FromName, string? ConfigurationSet, string? TopicArn);

    /// <summary>
    /// Email through Amazon SES (API v2, <c>SendEmail</c>; phase-4-plan M26), signed with the integration's access key: its secret is
    /// <c>{accessKeyId}:{secretAccessKey}</c>. The delivery id goes as the message tag <c>delivery_id</c>, which SES's event publishing echoes back
    /// in bounces, complaints and receipts (<see cref="SesCallbackHandler"/>). SES has no idempotency key, so the provider isn't idempotent: no
    /// answer is an unknown outcome, not retried (§10.3).
    /// <list type="bullet">
    /// <item><c>200</c>: <c>Accepted</c> with SES's message id;</item>
    /// <item>throttling (<c>429</c>, <c>ThrottlingException</c>): <c>RateLimited</c>; a sending quota used up: retryable <c>Provider</c>;</item>
    /// <item>credentials, account, sender or configuration refused (403, a suspended or paused account, an unverified identity in the sandbox):
    /// permanent <c>Provider</c>, which fails over;</item>
    /// <item>an address SES says is malformed: permanent <c>Destination</c>, the address gone; another rejected message: <c>Content</c>;</item>
    /// <item>5xx: retryable <c>Provider</c>.</item>
    /// </list>
    /// </summary>
    public sealed partial class SesProvider(EmailHttp http, IOptions<DeliveryOptions> options, TimeProvider clock) : INotificationProvider
    {
        public const string KindName = "ses";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public string Channel => Channels.Email;
        public string Kind => KindName;
        public bool Idempotent => false;
        public bool RequiresSecret => true;

        [GeneratedRegex(@"^[a-z]{2}(-gov)?-[a-z]+-\d{1,2}$")]
        internal static partial Regex Region();

        [GeneratedRegex(@"^arn:aws(-[a-z]+)*:sns:[a-z0-9-]+:\d{12}:[A-Za-z0-9_-]{1,256}$")]
        internal static partial Regex TopicArn();

        [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
        private static partial Regex ConfigurationSetName();

        internal static SesSettings? Read(string settings)
        {
            try
            {
                return JsonSerializer.Deserialize<SesSettings>(settings, Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public string? CheckSettings(string settings)
        {
            var s = Read(settings);
            if (s is null)
            {
                return "settings is an object with region, from and optionally fromName, configurationSet and topicArn.";
            }
            if (s.Region is null || !Region().IsMatch(s.Region))
            {
                return "settings.region is an AWS region, such as eu-west-1.";
            }
            if (!EmailAddresses.IsAddress(s.From))
            {
                return "settings.from is the sender's bare email address, an identity verified in SES.";
            }
            if (s.FromName is { Length: > 200 })
            {
                return "settings.fromName is up to 200 characters.";
            }
            if (s.ConfigurationSet is not null && !ConfigurationSetName().IsMatch(s.ConfigurationSet))
            {
                return "settings.configurationSet is the name of an SES configuration set.";
            }
            return s.TopicArn is not null && !TopicArn().IsMatch(s.TopicArn) ? "settings.topicArn is the ARN of the SNS topic SES publishes events to." : null;
        }

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            var settings = Read(integration.Settings);
            if (settings?.Region is null || !Region().IsMatch(settings.Region) || settings.From is null)
            {
                return SendResult.Permanent(FailureCategory.Provider, "The SES integration's settings name no region or sender.");
            }
            if (integration.Secret?.Split(':', 2) is not [{ Length: > 0 } accessKey, { Length: > 0 } secretKey])
            {
                return SendResult.Permanent(FailureCategory.Provider, "The SES integration's secret isn't {accessKeyId}:{secretAccessKey}.");
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

            var parts = new Dictionary<string, object> { ["Text"] = new { Data = content.Text, Charset = "UTF-8" } };
            if (!string.IsNullOrEmpty(content.Html))
            {
                parts["Html"] = new { Data = content.Html, Charset = "UTF-8" };
            }
            var body = new Dictionary<string, object?>
            {
                ["FromEmailAddress"] = From(settings.From, settings.FromName),
                ["Destination"] = new { ToAddresses = new[] { delivery.Destination } },
                ["Content"] = new
                {
                    Simple = new
                    {
                        Subject = new { Data = content.Subject, Charset = "UTF-8" },
                        Body = parts,
                    },
                },
                ["EmailTags"] = new[] { new { Name = "delivery_id", Value = delivery.DeliveryId.ToString() } },
            };
            if (settings.ConfigurationSet is { } set)
            {
                body["ConfigurationSetName"] = set;
            }
            var payload = JsonSerializer.SerializeToUtf8Bytes(body);
            var url = new Uri($"https://email.{settings.Region}.amazonaws.com/v2/email/outbound-emails");
            var now = clock.GetUtcNow();
            var signed = new Dictionary<string, string>
            {
                ["content-type"] = "application/json",
                ["host"] = url.Host,
                [AwsSigV4.DateHeader] = AwsSigV4.Stamp(now),
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(payload) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(AwsSigV4.DateHeader, signed[AwsSigV4.DateHeader]);
            request.Headers.TryAddWithoutValidation("Authorization",
                AwsSigV4.Authorization("POST", url, signed, payload, accessKey, secretKey, settings.Region, "ses", now));

            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            return failure ?? Classify(answer!);
        }

        /// <summary>The sender as SES takes it: the bare address, or a display name before it (encoded when it isn't plain ASCII).</summary>
        internal static string From(string address, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return address;
            }
            var display = name.All(c => c is >= ' ' and <= '~')
                ? "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
                : "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name)) + "?=";
            return $"{display} <{address}>";
        }

        internal static SendResult Classify(ApiAnswer answer)
        {
            var status = answer.Status;
            if (status is >= 200 and < 300)
            {
                return SendResult.Accepted(answer.Field("MessageId"));
            }
            // x-amzn-ErrorType: "MessageRejected:http://internal.amazon.com/coral/…"; or __type in the body.
            var type = (answer.Header("x-amzn-ErrorType") ?? answer.Field("__type") ?? "").Split(':')[0].Split('#')[^1];
            var message = answer.Field("message") ?? answer.Field("Message") ?? "";
            var error = $"{status} {type}: {Reasons.Clean(message)}".TrimEnd(' ', ':');
            return (status, type) switch
            {
                (429, _) or (_, "TooManyRequestsException" or "ThrottlingException" or "Throttling") => SendResult.Retry(FailureCategory.RateLimited, error, status),
                (_, "LimitExceededException") => SendResult.Retry(FailureCategory.Provider, error, status), // the sending quota: another integration may have room
                (401 or 403, _) or (_, "AccountSuspendedException" or "SendingPausedException" or "MailFromDomainNotVerifiedException"
                    or "NotFoundException" or "UnrecognizedClientException" or "InvalidClientTokenId" or "SignatureDoesNotMatch" or "AccessDeniedException")
                    => SendResult.Permanent(FailureCategory.Provider, error, status),
                // In the sandbox SES only sends to verified addresses: the account's limit, not the person's address.
                (_, "MessageRejected") when message.Contains("not verified", StringComparison.OrdinalIgnoreCase)
                    => SendResult.Permanent(FailureCategory.Provider, error, status),
                (400, "BadRequestException") when IsAddressProblem(message) => SendResult.Gone(error, status),
                (400 or 413, _) => SendResult.Permanent(FailureCategory.Content, error, status),
                (>= 500, _) => SendResult.Retry(FailureCategory.Provider, error, status),
                _ => SendResult.Permanent(FailureCategory.Provider, error, status),
            };
        }

        /// <summary>SES's words for a recipient it can't take ("Illegal address", "Missing final '@domain'", "Domain contains illegal character").</summary>
        private static bool IsAddressProblem(string message)
            => message.Contains("address", StringComparison.OrdinalIgnoreCase) && !message.Contains("from", StringComparison.OrdinalIgnoreCase)
            || message.Contains("@domain", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Domain contains", StringComparison.OrdinalIgnoreCase);
    }
}
