using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Juice.Notifications.Providers
{
    // The HTTP plumbing every provider package over an HTTP API shares (phase-4-plan I39): one call with the deployment's timeout, its answer
    // read up to a limit, and how no connection and no answer become outcomes (§10.3).

    /// <summary>An HTTP API's answer: the status, its body (read up to a limit) and its headers; or no answer, as a send result.</summary>
    public sealed record ApiAnswer(int Status, string Body, IReadOnlyDictionary<string, string> Headers)
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

    public static class ProviderHttp
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

        /// <summary>
        /// The handler for a provider package's client to a provider's own API (never a tenant's address: those go through the webhook's checked
        /// connections): no redirects, the deployment's connect timeout, pooled connections renewed every 5 minutes so DNS changes reach it.
        /// </summary>
        public static SocketsHttpHandler Handler(DeliveryOptions options) => new()
        {
            AllowAutoRedirect = false,
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxResponseHeadersLength = 64,
        };

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
    }

    /// <summary>A provider's reason as kept on a delivery or a contact point: no address in it (§13), and not too long.</summary>
    public static partial class Reasons
    {
        [GeneratedRegex(@"[^\s<>""'(),;:\[\]]+@[^\s<>""'(),;:\[\]]+")]
        private static partial Regex Address();

        public static string? Clean(string? reason)
            => reason is null ? null : Address().Replace(reason.Length > 1000 ? reason[..1000] : reason, "***");
    }
}
