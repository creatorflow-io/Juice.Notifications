using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.Notifications.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Email
{
    /// <summary>
    /// The HTTP client the email providers and their callback handlers share: no redirects, the deployment's connect timeout, pooled connections
    /// renewed every few minutes (DNS changes reach it). Replaced in tests by one over a fake of the provider's API.
    /// </summary>
    public sealed class EmailHttp(HttpMessageHandler handler) : IDisposable
    {
        public HttpClient Client { get; } = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        public static EmailHttp Create(DeliveryOptions options) => new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxResponseHeadersLength = 64,
        });

        public void Dispose() => Client.Dispose();
    }

    /// <summary>An HTTP API's answer: the status, its body (read up to a limit) and its headers; or no answer, as a send result.</summary>
    internal sealed record ApiAnswer(int Status, string Body, IReadOnlyDictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

        /// <summary>A string field of the body's JSON object, or null.</summary>
        public string? Field(params string[] path)
        {
            try
            {
                using var json = JsonDocument.Parse(Body);
                var e = json.RootElement;
                foreach (var name in path)
                {
                    if (e.ValueKind == JsonValueKind.Array)
                    {
                        if (e.GetArrayLength() == 0)
                        {
                            return null;
                        }
                        e = e[0];
                    }
                    if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e))
                    {
                        return null;
                    }
                }
                return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    internal static class ProviderHttp
    {
        private const int MaxBody = 64 * 1024;

        /// <summary>
        /// Sends a request with the deployment's request timeout. No connection is retryable (nothing went out); no answer once the request
        /// went (a timeout, a connection lost) is an unknown outcome: the provider may have taken the message.
        /// </summary>
        public static async Task<(ApiAnswer? Answer, SendResult? Failure)> SendAsync(HttpClient http, HttpRequestMessage request, TimeSpan timeout,
            CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var body = await ReadAsync(response, deadline.Token);
                var headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
                return (new ApiAnswer((int)response.StatusCode, body, headers), null);
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError)
            {
                return (null, SendResult.Retry(FailureCategory.Provider, "Couldn't connect: " + ex.Message));
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException && !ct.IsCancellationRequested)
            {
                return (null, SendResult.Unknown("unknown_outcome: no answer from the provider: " + ex.Message));
            }
        }

        private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxBody];
            var read = 0;
            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), ct) is var n and > 0)
            {
                read += n;
            }
            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        /// <summary>The bare sender address, checked when an integration is saved.</summary>
        public static bool IsAddress(string? value)
            => value is { Length: > 0 and <= 254 } && value == value.Trim() && !value.Contains('<') && !value.Contains('>')
            && MailAddress.TryCreate(value, out var parsed) && parsed.Address == value && parsed.Host.Length > 0;

        public static bool IsRecipient(string value) => MailAddress.TryCreate(value, out var parsed) && parsed.Address == value;
    }

    /// <summary>A provider's reason as kept on a delivery or a contact point: no address in it (§13), and not too long.</summary>
    internal static partial class Reasons
    {
        [GeneratedRegex(@"[^\s<>""'(),;:\[\]]+@[^\s<>""'(),;:\[\]]+")]
        private static partial Regex Address();

        public static string? Clean(string? reason)
            => reason is null ? null : Address().Replace(reason.Length > 1000 ? reason[..1000] : reason, "***");
    }

    public static class EmailProviderRegistration
    {
        /// <summary>
        /// The SES and SendGrid providers (integration kinds <c>ses</c> and <c>sendgrid</c>, channel <c>email</c>) and their callback handlers
        /// (<c>/api/v1/callbacks/ses/…</c>, <c>/api/v1/callbacks/sendgrid/…</c>).
        /// </summary>
        public static IServiceCollection AddEmailProviders(this IServiceCollection services)
        {
            services.TryAddSingleton(sp => EmailHttp.Create(sp.GetRequiredService<IOptions<DeliveryOptions>>().Value));
            services.TryAddSingleton<SnsCertificates>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, SesProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, SendGridProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallbackHandler, SesCallbackHandler>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallbackHandler, SendGridCallbackHandler>());
            return services;
        }
    }
}
