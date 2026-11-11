using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers
{
    /// <summary>
    /// Webhook signatures in the Standard Webhooks form (design D14), ported from Juice.Observability: HMAC-SHA256 of
    /// <c>{webhook-id}.{webhook-timestamp}.{body}</c>, sent as <c>webhook-signature: v1,{base64}</c>.
    /// </summary>
    public static class WebhookSigner
    {
        public const string IdHeader = "webhook-id";
        public const string TimestampHeader = "webhook-timestamp";
        public const string SignatureHeader = "webhook-signature";

        public static string Sign(string secret, string id, long timestamp, string body)
            => "v1," + Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));

        /// <summary>The receiver's check, and the reference for receivers in other languages (design §10.4).</summary>
        public static bool Verify(string secret, string id, string timestampHeader, string signatureHeader, string body, DateTimeOffset now, TimeSpan tolerance)
        {
            if (!long.TryParse(timestampHeader, out var timestamp) || (now - DateTimeOffset.FromUnixTimeSeconds(timestamp)).Duration() > tolerance)
            {
                return false;
            }
            var expected = Encoding.ASCII.GetBytes(Sign(secret, id, timestamp, body));
            return signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(candidate => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(candidate)));
        }
    }

    /// <summary>
    /// The signed webhook (design §9, D14, D16). <c>webhook-id</c> is the delivery id, the same on every attempt, so receivers deduplicate on it
    /// (§10.4). Each tenant has its own HTTP client: no redirects, no proxy, and a connect callback that resolves the host and checks every
    /// address against <see cref="DestinationPolicy"/> and the tenant's allowlist before connecting to a checked address. A client is replaced when
    /// its tenant's allowlist changes, so a pooled connection never outlives a policy change.
    /// </summary>
    public sealed class WebhookProvider(IServiceScopeFactory scopes, IOptions<DeliveryOptions> options, TimeProvider clock) : INotificationProvider, IDisposable
    {
        private readonly ConcurrentDictionary<Guid, (string Fingerprint, Lazy<HttpClient> Client)> _clients = new();

        public string Channel => Channels.Webhook;

        public string Kind => "webhook";

        public bool Idempotent => false;

        public string? CheckSettings(string settings) => null;

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            var content = JsonSerializer.Deserialize<WebhookContent>(delivery.Content)!;
            if (!Uri.TryCreate(delivery.Destination, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
            {
                return SendResult.Permanent(FailureCategory.Destination, "The target's address isn't an http or https URL.");
            }
            var id = delivery.DeliveryId.ToString();
            var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(content.Body, Encoding.UTF8, MediaTypeHeaderValue.Parse(content.ContentType).MediaType!),
            };
            request.Headers.Add(WebhookSigner.IdHeader, id);
            request.Headers.Add(WebhookSigner.TimestampHeader, timestamp.ToString());
            if (integration.Secret is { } secret)
            {
                request.Headers.Add(WebhookSigner.SignatureHeader, WebhookSigner.Sign(secret, id, timestamp, content.Body));
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Value.ConnectTimeout + options.Value.RequestTimeout);
            var sent = false;
            try
            {
                var client = await ClientAsync(delivery.TenantId, ct);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                sent = true;
                await DrainAsync(response, timeout.Token);
                var status = (int)response.StatusCode;
                return status switch
                {
                    >= 200 and < 300 => SendResult.Delivered(status),
                    429 => SendResult.Retry(FailureCategory.RateLimited, "429 Too Many Requests", status),
                    408 or >= 500 => SendResult.Retry(FailureCategory.Provider, $"{status} from the receiver", status),
                    404 or 410 => SendResult.Permanent(FailureCategory.Destination, $"{status}: the receiver says this destination doesn't exist", status),
                    _ => SendResult.Permanent(FailureCategory.Content, $"{status}: the receiver refused the request", status),
                };
            }
            catch (Exception ex) when (Find<DestinationBlockedException>(ex) is { } blocked)
            {
                return SendResult.Permanent(FailureCategory.Policy, blocked.Reason);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException or OperationCanceledException or IOException))
            {
                // Before the request went out (connect failed, DNS), another attempt is safe. After it, the receiver may have it: unknown (§10.3).
                return !sent && IsConnectFailure(ex)
                    ? SendResult.Retry(FailureCategory.Provider, "Couldn't connect: " + ex.Message)
                    : SendResult.Unknown("No answer after the request was sent: " + ex.Message);
            }
        }

        private static bool IsConnectFailure(Exception ex)
            => ex is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError }
            || Find<SocketException>(ex) is not null;

        private static T? Find<T>(Exception? ex) where T : Exception
        {
            for (; ex is not null; ex = ex.InnerException)
            {
                if (ex is T match)
                {
                    return match;
                }
            }
            return null;
        }

        private async Task DrainAsync(HttpResponseMessage response, CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[8192];
            var left = options.Value.MaxResponseBytes;
            int read;
            while (left > 0 && (read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, left)), ct)) > 0)
            {
                left -= read;
            }
        }

        private async Task<HttpClient> ClientAsync(Guid tenantId, CancellationToken ct)
        {
            var allowlist = await AllowlistAsync(tenantId, ct);
            var fingerprint = string.Join(';', allowlist.Select(a => $"{a.Destination}:{string.Join(',', a.Ports)}"));
            while (true)
            {
                var current = _clients.GetOrAdd(tenantId, t => (fingerprint, new Lazy<HttpClient>(() => CreateClient(t))));
                if (current.Fingerprint == fingerprint)
                {
                    return current.Client.Value;
                }
                var fresh = (fingerprint, new Lazy<HttpClient>(() => CreateClient(tenantId)));
                if (_clients.TryUpdate(tenantId, fresh, current))
                {
                    if (current.Client.IsValueCreated)
                    {
                        var old = current.Client.Value;
                        _ = Task.Delay(options.Value.RequestTimeout + TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
                    }
                    return fresh.Item2.Value;
                }
            }
        }

        private async Task<List<AllowedDestination>> AllowlistAsync(Guid tenantId, CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            return (await db.Allowlist.AsNoTracking().Where(a => a.TenantId == tenantId).OrderBy(a => a.Destination).ToListAsync(ct))
                .Select(a => new AllowedDestination(a.Destination, a.Ports)).ToList();
        }

        private HttpClient CreateClient(Guid tenantId)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                ConnectTimeout = options.Value.ConnectTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(1),
                // Runs for every new connection, so DNS is checked again each time (DNS rebinding can't slip past an earlier check).
                ConnectCallback = async (context, ct) =>
                {
                    var host = context.DnsEndPoint.Host;
                    var port = context.DnsEndPoint.Port;
                    IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
                    var scheme = context.InitialRequestMessage.RequestUri?.Scheme ?? "";
                    if (DestinationPolicy.Check(scheme, host, port, addresses, await AllowlistAsync(tenantId, ct), options.Value.AllowHttpToAllowlisted) is { } reason)
                    {
                        throw new DestinationBlockedException(reason);
                    }
                    var socket = await DestinationPolicy.ConnectAsync(addresses, port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public void Dispose()
        {
            foreach (var (_, client) in _clients.Values)
            {
                if (client.IsValueCreated)
                {
                    client.Value.Dispose();
                }
            }
        }
    }
}
