using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Juice.Notifications.Callbacks;
using Juice.Notifications.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Sms
{
    /// <summary>The <c>sms</c> channel (design D24): people's verified phone numbers, and shared numbers (an on-call phone) as targets.</summary>
    public static class SmsChannel
    {
        public const string Key = "sms";

        public static readonly ChannelDefinition Definition = new(Key, AddressKind.Phone, ChannelAudience.People | ChannelAudience.SharedTargets, ContentShape.Text);

        /// <summary>E.164, as the core normalizes a request's phone address: <c>+</c>, then 7 to 15 digits, the first not 0.</summary>
        public static string? Normalize(string address)
        {
            var digits = string.Concat(address.Where(c => c is not (' ' or '-' or '.' or '(' or ')')));
            return digits.Length is >= 8 and <= 16 && digits[0] == '+' && digits[1] is >= '1' and <= '9' && digits[1..].All(char.IsAsciiDigit) ? digits : null;
        }
    }

    /// <summary>The client the Twilio provider shares (as <see cref="ProviderHttp.Handler"/>); tests replace it.</summary>
    public sealed class SmsHttp(HttpMessageHandler handler) : IDisposable
    {
        public HttpClient Client { get; } = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        public static SmsHttp Create(DeliveryOptions options) => new(ProviderHttp.Handler(options));

        public void Dispose() => Client.Dispose();
    }

    /// <summary>A Twilio integration's settings: the sender, a number of the account (<c>from</c>) or a Messaging Service (<c>messagingServiceSid</c>).</summary>
    public sealed record TwilioSettings(string? From, string? MessagingServiceSid);

    /// <summary>
    /// SMS through Twilio's Messages API (<c>twilio</c>; phase-4-plan M29, p4-d3: SMS only). The secret is <c>{accountSid}:{authToken}</c>: basic
    /// authentication, and the key Twilio signs status callbacks with. With <c>Callbacks:PublicUrl</c> set, each message asks for status callbacks
    /// (<see cref="TwilioCallbackHandler"/>). Twilio takes no idempotency key, so the provider isn't idempotent.
    /// <list type="bullet">
    /// <item><c>201</c>: <c>Accepted</c> with the message's sid (delivery comes later, by callback);</item>
    /// <item>429, or code 20429: <c>RateLimited</c>; 5xx: retryable <c>Provider</c>;</item>
    /// <item>401, 403, 404, the sender or the region refused (21212, 21408, 21606, 21659, 21660): permanent <c>Provider</c>;</item>
    /// <item>an invalid number, not a mobile one, or one that replied STOP (21211, 21614, 21610): <c>Destination</c>, the address gone; a number
    /// Twilio can't route to (21612): <c>Destination</c>; another 400: <c>Content</c>.</item>
    /// </list>
    /// </summary>
    public sealed partial class TwilioProvider(SmsHttp http, IOptions<DeliveryOptions> options, IOptions<CallbackOptions> callbacks)
        : INotificationProvider, ITargetAddresses
    {
        public const string KindName = "twilio";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public string Channel => SmsChannel.Key;
        public string Kind => KindName;
        public bool Idempotent => false;
        public bool RequiresSecret => true;

        [GeneratedRegex(@"^AC[0-9a-f]{32}$")]
        internal static partial Regex AccountSid();

        [GeneratedRegex(@"^MG[0-9a-f]{32}$")]
        private static partial Regex ServiceSid();

        internal static TwilioSettings? Read(string settings)
        {
            try
            {
                return JsonSerializer.Deserialize<TwilioSettings>(settings, Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The account and its auth token, from the secret <c>{accountSid}:{authToken}</c>.</summary>
        internal static (string Account, string Token)? Secrets(string? secret)
            => secret?.Split(':', 2) is [{ } account, { Length: > 0 } token] && AccountSid().IsMatch(account) ? (account, token) : null;

        public string? CheckSettings(string settings)
        {
            var s = Read(settings);
            if (s is null || (s.From is null) == (s.MessagingServiceSid is null))
            {
                return "settings names the sender: from (a number of the account, E.164) or messagingServiceSid (MG…), one of them.";
            }
            if (s.From is not null && SmsChannel.Normalize(s.From) != s.From)
            {
                return "settings.from is a phone number in E.164: +84901234567.";
            }
            return s.MessagingServiceSid is not null && !ServiceSid().IsMatch(s.MessagingServiceSid) ? "settings.messagingServiceSid is a Messaging Service's sid (MG…)." : null;
        }

        public string? NormalizeTarget(string address) => SmsChannel.Normalize(address);

        public string TargetRule => "The address is a phone number in E.164: +84901234567.";

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            if (Secrets(integration.Secret) is not { } secrets)
            {
                return SendResult.Permanent(FailureCategory.Provider, "The Twilio integration's secret isn't {accountSid}:{authToken}.");
            }
            var settings = Read(integration.Settings);
            if (settings?.From is null && settings?.MessagingServiceSid is null)
            {
                return SendResult.Permanent(FailureCategory.Provider, "The Twilio integration's settings name no sender.");
            }
            if (SmsChannel.Normalize(delivery.Destination) != delivery.Destination)
            {
                return SendResult.Permanent(FailureCategory.Destination, "The destination isn't a phone number in E.164.");
            }
            TextContent? content;
            try
            {
                content = JsonSerializer.Deserialize<TextContent>(delivery.Content);
            }
            catch (JsonException)
            {
                content = null;
            }
            if (content?.Text is not { Length: > 0 } text)
            {
                return SendResult.Permanent(FailureCategory.Content, "The stored content has no text.");
            }
            var form = new List<KeyValuePair<string, string>> { new("To", delivery.Destination), new("Body", text) };
            form.Add(settings.MessagingServiceSid is { } service ? new("MessagingServiceSid", service) : new("From", settings.From!));
            if (callbacks.Value.UrlFor("twilio", integration.IntegrationId) is { } statusUrl)
            {
                form.Add(new("StatusCallback", statusUrl.AbsoluteUri));
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.twilio.com/2010-04-01/Accounts/{secrets.Account}/Messages.json")
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{secrets.Account}:{secrets.Token}")));
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            return failure ?? Classify(answer!);
        }

        internal static SendResult Classify(ApiAnswer answer)
        {
            var status = answer.Status;
            if (status is >= 200 and < 300)
            {
                return SendResult.Accepted(answer.Field("sid"));
            }
            int? code = null;
            try
            {
                using var json = JsonDocument.Parse(answer.Body);
                if (json.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var value))
                {
                    code = value;
                }
            }
            catch (JsonException)
            {
            }
            var error = $"{status} {code?.ToString(CultureInfo.InvariantCulture) ?? ""}: {Reasons.Clean(answer.Field("message")) ?? "refused"}";
            return (status, code) switch
            {
                (429, _) or (_, 20429) => SendResult.Retry(FailureCategory.RateLimited, error, status),
                (>= 500, _) => SendResult.Retry(FailureCategory.Provider, error, status),
                (401 or 403 or 404, _) or (_, 20003 or 21212 or 21408 or 21606 or 21659 or 21660) => SendResult.Permanent(FailureCategory.Provider, error, status),
                (_, 21211 or 21614 or 21610) => SendResult.Gone(error, status),
                (_, 21612) => SendResult.Permanent(FailureCategory.Destination, error, status),
                (400 or 413, _) => SendResult.Permanent(FailureCategory.Content, error, status),
                _ => SendResult.Permanent(FailureCategory.Provider, error, status),
            };
        }
    }

    /// <summary>
    /// Twilio's status callbacks (phase-4-plan p4-d6, M29): form posts signed in <c>X-Twilio-Signature</c>, base64 HMAC-SHA1 with the auth token
    /// over the URL Twilio called followed by each parameter's name and value, sorted by name. The URL is the one the provider gave
    /// (<c>Callbacks:PublicUrl</c>), not what this host sees behind a proxy. The account must be the integration's.
    /// <list type="bullet">
    /// <item><c>delivered</c>: a receipt;</item>
    /// <item><c>undelivered</c> or <c>failed</c> with 30005 (unknown number) or 30006 (a landline, or unreachable carrier): a hard bounce; with
    /// 30003 (the phone unreachable): a soft bounce;</item>
    /// <item>other statuses and errors (queued, sent, carrier filtering): ignored.</item>
    /// </list>
    /// </summary>
    public sealed class TwilioCallbackHandler(IOptions<CallbackOptions> callbacks) : ICallbackHandler
    {
        public const string SignatureHeader = "X-Twilio-Signature";

        public string Provider => "twilio";

        public bool Handles(string integrationKind) => integrationKind == TwilioProvider.KindName;

        public static string Sign(string authToken, string url, IEnumerable<KeyValuePair<string, string>> parameters)
        {
            var data = new StringBuilder(url);
            foreach (var (name, value) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                data.Append(name).Append(value);
            }
            return Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(authToken), Encoding.UTF8.GetBytes(data.ToString())));
        }

        public Task<CallbackResult> ReadAsync(CallbackRequest request, IntegrationContext integration, CancellationToken ct)
            => Task.FromResult(Read(request, integration));

        private CallbackResult Read(CallbackRequest request, IntegrationContext integration)
        {
            if (TwilioProvider.Secrets(integration.Secret) is not { } secrets)
            {
                return CallbackResult.Refused("the integration's secret isn't {accountSid}:{authToken}");
            }
            if (request.Header(SignatureHeader) is not { } signature)
            {
                return CallbackResult.Refused("unsigned");
            }
            var form = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(request.Body));
            var parameters = form.AllKeys.Where(k => k is not null).Select(k => new KeyValuePair<string, string>(k!, form[k] ?? "")).ToList();
            var url = callbacks.Value.UrlFor(Provider, integration.IntegrationId)?.AbsoluteUri ?? request.Url.AbsoluteUri;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secrets.Token, url, parameters)), Encoding.ASCII.GetBytes(signature)))
            {
                return CallbackResult.Refused("a bad signature");
            }
            if (form["AccountSid"] != secrets.Account)
            {
                return CallbackResult.Refused("another account's message");
            }
            if (form["MessageSid"] is not { Length: > 0 } sid || form["MessageStatus"] is not { Length: > 0 } status)
            {
                return CallbackResult.Of();
            }
            var errorCode = form["ErrorCode"];
            CallbackKind? kind = status switch
            {
                "delivered" => CallbackKind.Delivered,
                "undelivered" or "failed" when errorCode is "30005" or "30006" => CallbackKind.Bounced,
                "undelivered" or "failed" when errorCode is "30003" => CallbackKind.SoftBounced,
                _ => null,
            };
            return kind is { } k
                ? CallbackResult.Of(new CallbackEvent($"{sid}:{status}", k, DateTimeOffset.UtcNow)
                {
                    ProviderMessageId = sid, Detail = errorCode is null ? null : $"Twilio error {errorCode} ({status})",
                })
                : CallbackResult.Of();
        }
    }

    public static class SmsRegistration
    {
        /// <summary>The <c>sms</c> channel, the Twilio provider (kind <c>twilio</c>) and its status callbacks (<c>/api/v1/callbacks/twilio/…</c>).</summary>
        public static IServiceCollection AddSms(this IServiceCollection services)
        {
            services.AddSingleton(SmsChannel.Definition);
            services.TryAddSingleton(sp => SmsHttp.Create(sp.GetRequiredService<IOptions<DeliveryOptions>>().Value));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, TwilioProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallbackHandler, TwilioCallbackHandler>());
            return services;
        }
    }
}
