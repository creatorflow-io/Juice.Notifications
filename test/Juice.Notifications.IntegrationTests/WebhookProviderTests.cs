using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Providers;
using Juice.Notifications.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>A migrated scratch database and the services the webhook provider reads its tenants' allowlists through.</summary>
    public sealed class WebhookDatabase : IAsyncLifetime
    {
        public ScratchDatabase Database { get; } = new();
        public ServiceProvider? Services { get; private set; }

        public async ValueTask InitializeAsync()
        {
            await Database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using (await Database.MigratedAsync())
            {
            }
            var services = new ServiceCollection();
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, Database.ConnectionString));
            Services = services.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync()
        {
            if (Services is not null)
            {
                await Services.DisposeAsync();
            }
            await Database.DisposeAsync();
        }
    }

    /// <summary>
    /// The real webhook provider with its real destination policy (design §9, §10.3, §13.1; phase-1-plan M6), against raw TCP receivers on
    /// this machine's private address, allowlisted for the test's tenant (loopback is always blocked). The status is the outcome; a failure
    /// before the body went out is retryable, after it unknown; and the policy refuses what it must before connecting.
    /// </summary>
    public sealed class WebhookProviderTests(WebhookDatabase fixture) : IClassFixture<WebhookDatabase>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private const string Secret = "test-signing-secret";
        private const string Body = """{"id":"x","data":{"host":"db-1"}}""";
        private readonly Guid _tenant = Guid.NewGuid();

        /// <summary>What a fake receiver does once it has read a whole request.</summary>
        private delegate Task Behavior(NetworkStream stream, TcpClient client, CancellationToken stop);

        /// <summary>One request as the fake receiver read it: request line, headers (lower-cased names) and body.</summary>
        private sealed record RawRequest(string RequestLine, Dictionary<string, string> Headers, string Body);

        /// <summary>A raw HTTP/1.1 receiver on the machine's private IPv4 address, running <see cref="Behavior"/> for every connection.</summary>
        private sealed class FakeReceiver : IAsyncDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _stop = new();
            private readonly Behavior _behavior;
            private readonly Task _accept;

            public ConcurrentQueue<RawRequest> Requests { get; } = new();
            public IPAddress Address { get; }
            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public string Url(string scheme = "http") => $"{scheme}://{Address}:{Port}/hook";

            public FakeReceiver(IPAddress address, Behavior behavior)
            {
                Address = address;
                _behavior = behavior;
                _listener = new TcpListener(address, 0);
                _listener.Start();
                _accept = Task.Run(AcceptAsync);
            }

            private async Task AcceptAsync()
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        _ = Task.Run(() => ServeAsync(client));
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                }
            }

            private async Task ServeAsync(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        Requests.Enqueue(await ReadRequestAsync(stream, _stop.Token));
                        await _behavior(stream, client, _stop.Token);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
                    {
                    }
                }
            }

            private static async Task<RawRequest> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
            {
                var buffer = new List<byte>();
                var chunk = new byte[8192];
                int end;
                while ((end = IndexOf(buffer, "\r\n\r\n"u8.ToArray())) < 0)
                {
                    var read = await stream.ReadAsync(chunk, ct);
                    if (read == 0)
                    {
                        throw new IOException("closed before the headers ended");
                    }
                    buffer.AddRange(chunk.AsSpan(0, read));
                }
                var head = Encoding.ASCII.GetString(buffer.ToArray(), 0, end).Split("\r\n");
                var headers = head.Skip(1).Select(h => h.Split(':', 2)).ToDictionary(h => h[0].Trim().ToLowerInvariant(), h => h[1].Trim());
                var length = headers.TryGetValue("content-length", out var value) ? int.Parse(value) : 0;
                while (buffer.Count < end + 4 + length)
                {
                    var read = await stream.ReadAsync(chunk, ct);
                    if (read == 0)
                    {
                        throw new IOException("closed before the body ended");
                    }
                    buffer.AddRange(chunk.AsSpan(0, read));
                }
                return new RawRequest(head[0], headers, Encoding.UTF8.GetString(buffer.ToArray(), end + 4, length));
            }

            private static int IndexOf(List<byte> haystack, byte[] needle)
            {
                for (var i = 0; i <= haystack.Count - needle.Length; i++)
                {
                    if (needle.Select((b, j) => haystack[i + j] == b).All(x => x))
                    {
                        return i;
                    }
                }
                return -1;
            }

            public async ValueTask DisposeAsync()
            {
                await _stop.CancelAsync();
                _listener.Stop();
                await _accept;
                _stop.Dispose();
            }
        }

        private static Behavior Status(int status) => async (stream, _, ct)
            => await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Whatever\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), ct);

        private static readonly Behavior NeverAnswers = (_, _, ct) => Task.Delay(Timeout.Infinite, ct);

        private static readonly Behavior Reset = (_, client, _) =>
        {
            client.Client.LingerState = new LingerOption(true, 0);
            client.Client.Close();
            return Task.CompletedTask;
        };

        /// <summary>A 200 status line promising a body that then stalls.</summary>
        private static readonly Behavior OkThenStalls = async (stream, _, ct) =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nab"u8.ToArray(), ct);
            await Task.Delay(Timeout.Infinite, ct);
        };

        /// <summary>A 200 status line promising a body, then a reset partway through it.</summary>
        private static readonly Behavior OkThenResets = async (stream, client, ct) =>
        {
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nab"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
            await Task.Delay(200, ct);
            await Reset(stream, client, ct);
        };

        /// <summary>This machine's private IPv4 address, as <c>DeliveryTests.Receiver</c> finds it; the test is skipped when there is none.</summary>
        private static IPAddress PrivateAddress()
        {
            var address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && DestinationPolicy.Classify(a) == DestinationClass.Private);
            Assert.SkipWhen(address is null, "no private IPv4 address on this machine for the webhook receiver");
            return address!;
        }

        /// <summary>Allowlists <paramref name="address"/> with <paramref name="port"/> for this test's tenant (one entry per address, its ports added up).</summary>
        private async Task AllowAsync(IPAddress address, int port)
        {
            await using var db = fixture.Database.Context();
            var destination = $"{address}/32";
            if (await db.Allowlist.FindAsync([_tenant, destination], Ct) is { } existing)
            {
                existing.Ports = [.. existing.Ports, port];
            }
            else
            {
                db.Allowlist.Add(new AllowlistEntry { TenantId = _tenant, Destination = destination, Ports = [port], UpdatedAt = DateTimeOffset.UtcNow });
            }
            await db.SaveChangesAsync(Ct);
        }

        private async Task<FakeReceiver> ReceiverAsync(Behavior behavior)
        {
            var receiver = new FakeReceiver(PrivateAddress(), behavior);
            await AllowAsync(receiver.Address, receiver.Port);
            return receiver;
        }

        private async Task<SendResult> SendAsync(string url, bool allowHttp = true, Guid? deliveryId = null, string? secret = Secret)
        {
            using var provider = new WebhookProvider(fixture.Services!.GetRequiredService<IServiceScopeFactory>(), Options.Create(new DeliveryOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(1),
                RequestTimeout = TimeSpan.FromSeconds(1),
                AllowHttpToAllowlisted = allowHttp,
            }), TimeProvider.System);
            var content = JsonSerializer.Serialize(new WebhookContent("application/json", Body));
            var delivery = new RenderedDelivery(deliveryId ?? Guid.NewGuid(), _tenant, Channels.Webhook, content, url, 1, NotificationPriority.Normal, false);
            return await provider.SendAsync(delivery, new IntegrationContext(Guid.NewGuid(), "hooks", "webhook", "{}", secret), Ct);
        }

        private static void Is(SendResult result, AttemptOutcome outcome, FailureCategory? category, int? status = null)
        {
            Assert.True(outcome == result.Outcome && category == result.Category,
                $"expected {outcome} {category}, got {result.Outcome} {result.Category} {result.StatusCode}: {result.Error}");
            Assert.Equal(status, result.StatusCode);
        }

        /// <summary>A 200 is delivered, and the request carries the delivery id, a timestamp and a signature the reference verifier accepts.</summary>
        [Fact]
        public async Task A_200_is_delivered_with_a_signature_the_receiver_can_verify()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await ReceiverAsync(Status(200));
            var id = Guid.NewGuid();

            Is(await SendAsync(receiver.Url(), deliveryId: id), AttemptOutcome.Delivered, null, 200);

            var request = Assert.Single(receiver.Requests);
            Assert.Equal("POST /hook HTTP/1.1", request.RequestLine);
            Assert.Equal(Body, request.Body);
            Assert.Equal("application/json; charset=utf-8", request.Headers["content-type"]); // the signed bytes are UTF-8, and the receiver is told
            Assert.Equal(id.ToString(), request.Headers[WebhookSigner.IdHeader]);
            Assert.True(WebhookSigner.Verify(Secret, request.Headers[WebhookSigner.IdHeader], request.Headers[WebhookSigner.TimestampHeader],
                request.Headers[WebhookSigner.SignatureHeader], request.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
        }

        /// <summary>Webhooks are always signed (deferred #13): a stored integration without a secret fails at the provider and sends nothing.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task Nothing_is_sent_unsigned(string? secret)
        {
            LocalStack.RequireAvailable();
            await using var receiver = await ReceiverAsync(Status(200));

            Is(await SendAsync(receiver.Url(), secret: secret), AttemptOutcome.PermanentFailure, FailureCategory.Provider);

            Assert.Empty(receiver.Requests);
        }

        /// <summary>The status is the outcome: 2xx delivered; 429 rate-limited and 408 or 5xx retryable; 404 or 410 the destination's; other 4xx the content's.</summary>
        [Theory]
        [InlineData(204, AttemptOutcome.Delivered, null)]
        [InlineData(503, AttemptOutcome.RetryableFailure, FailureCategory.Provider)]
        [InlineData(500, AttemptOutcome.RetryableFailure, FailureCategory.Provider)]
        [InlineData(408, AttemptOutcome.RetryableFailure, FailureCategory.Provider)]
        [InlineData(429, AttemptOutcome.RetryableFailure, FailureCategory.RateLimited)]
        [InlineData(404, AttemptOutcome.PermanentFailure, FailureCategory.Destination)]
        [InlineData(410, AttemptOutcome.PermanentFailure, FailureCategory.Destination)]
        [InlineData(400, AttemptOutcome.PermanentFailure, FailureCategory.Content)]
        [InlineData(401, AttemptOutcome.PermanentFailure, FailureCategory.Content)]
        [InlineData(422, AttemptOutcome.PermanentFailure, FailureCategory.Content)]
        [InlineData(300, AttemptOutcome.PermanentFailure, FailureCategory.Content)] // 2xx ends at 299 (mutation testing)
        [InlineData(299, AttemptOutcome.Delivered, null)]
        public async Task The_status_is_the_outcome(int status, AttemptOutcome outcome, FailureCategory? category)
        {
            LocalStack.RequireAvailable();
            await using var receiver = await ReceiverAsync(Status(status));

            Is(await SendAsync(receiver.Url()), outcome, category, status);
            Assert.Single(receiver.Requests);
        }

        /// <summary>Only http and https: any other scheme fails as the destination's, without connecting.</summary>
        [Theory]
        [InlineData("ftp://example.com/hook")]
        [InlineData("file:///etc/passwd")]
        [InlineData("not a url")]
        public async Task Only_http_and_https_are_sent(string url)
        {
            LocalStack.RequireAvailable();
            Is(await SendAsync(url), AttemptOutcome.PermanentFailure, FailureCategory.Destination);
        }

        /// <summary>
        /// A redirect is never followed (it could point anywhere, past the destination policy): the 3xx is the receiver's answer, a refusal of this
        /// request, and the address it names is never asked.
        /// </summary>
        [Fact]
        public async Task A_redirect_is_not_followed()
        {
            LocalStack.RequireAvailable();
            await using var elsewhere = await ReceiverAsync(Status(200));
            await using var receiver = await ReceiverAsync(async (stream, _, ct) => await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 307 Temporary Redirect\r\nLocation: {elsewhere.Url()}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct));

            Is(await SendAsync(receiver.Url()), AttemptOutcome.PermanentFailure, FailureCategory.Content, 307);
            Assert.Single(receiver.Requests);
            Assert.Empty(elsewhere.Requests);
        }

        /// <summary>A receiver that read the request and never answers may have acted on it: unknown, not retryable.</summary>
        [Fact]
        public async Task No_answer_after_the_request_is_unknown()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await ReceiverAsync(NeverAnswers);

            Is(await SendAsync(receiver.Url()), AttemptOutcome.Unknown, FailureCategory.Provider);
            Assert.Single(receiver.Requests);
        }

        /// <summary>A reset after the receiver read the request is unknown too.</summary>
        [Fact]
        public async Task A_reset_after_the_request_was_read_is_unknown()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await ReceiverAsync(Reset);

            Is(await SendAsync(receiver.Url()), AttemptOutcome.Unknown, FailureCategory.Provider);
            Assert.Single(receiver.Requests);
        }

        /// <summary>Once the status line came, a body that stalls or breaks doesn't change the outcome.</summary>
        [Fact]
        public async Task A_200_whose_body_stalls_or_breaks_is_still_delivered()
        {
            LocalStack.RequireAvailable();
            await using var stalls = await ReceiverAsync(OkThenStalls);
            await using var resets = await ReceiverAsync(OkThenResets);

            Is(await SendAsync(stalls.Url()), AttemptOutcome.Delivered, null, 200);
            Is(await SendAsync(resets.Url()), AttemptOutcome.Delivered, null, 200);
        }

        /// <summary>A closed port is a failed connect: nothing went out, so another attempt is safe.</summary>
        [Fact]
        public async Task A_closed_port_is_retryable()
        {
            LocalStack.RequireAvailable();
            var address = PrivateAddress();
            var listener = new TcpListener(address, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            await AllowAsync(address, port);

            Is(await SendAsync($"http://{address}:{port}/hook"), AttemptOutcome.RetryableFailure, FailureCategory.Provider);
        }

        /// <summary>A public address off 443 is refused by the policy before any connection: permanent, the policy's.</summary>
        [Fact]
        public async Task A_public_address_off_443_is_refused_by_the_policy()
        {
            LocalStack.RequireAvailable();

            var result = await SendAsync("https://8.8.8.8:22/");

            Is(result, AttemptOutcome.PermanentFailure, FailureCategory.Policy);
            Assert.Equal("port_not_allowed:22", result.Error);
        }

        /// <summary>NAT64 for cloud metadata (169.254.169.254) is refused like the metadata address itself.</summary>
        [Fact]
        public async Task NAT64_to_cloud_metadata_is_refused_by_the_policy()
        {
            LocalStack.RequireAvailable();

            var result = await SendAsync("https://[64:ff9b::a9fe:a9fe]/");

            Is(result, AttemptOutcome.PermanentFailure, FailureCategory.Policy);
            Assert.StartsWith("blocked_address:", result.Error);
        }

        /// <summary>Plain http to an allowlisted private address is refused unless the deployment allows it, and nothing reaches the receiver.</summary>
        [Fact]
        public async Task Plain_http_to_an_allowlisted_address_needs_the_deployment_to_allow_it()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await ReceiverAsync(Status(200));

            var result = await SendAsync(receiver.Url(), allowHttp: false);

            Is(result, AttemptOutcome.PermanentFailure, FailureCategory.Policy);
            Assert.Equal("https_required", result.Error);
            Assert.Empty(receiver.Requests);
        }

        /// <summary>A private address that isn't allowlisted for this tenant is refused, even with a receiver listening on it.</summary>
        [Fact]
        public async Task A_private_address_not_allowlisted_is_refused()
        {
            LocalStack.RequireAvailable();
            await using var receiver = new FakeReceiver(PrivateAddress(), Status(200));

            var result = await SendAsync(receiver.Url());

            Is(result, AttemptOutcome.PermanentFailure, FailureCategory.Policy);
            Assert.StartsWith("private_address_not_allowlisted:", result.Error);
            Assert.Empty(receiver.Requests);
        }
    }
}
