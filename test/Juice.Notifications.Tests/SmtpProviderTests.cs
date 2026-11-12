using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Providers;
using Juice.Notifications.Rendering;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// The SMTP provider's outcome table (design §10.2–10.3, phase-1-plan M6) against a scripted SMTP server on loopback: a 4xx is "not now",
    /// a 5xx is the recipient's, the sender's or the message's fault, a 250 after the body is accepted whatever QUIT does, and a lost connection
    /// is retryable before the body and unknown after it. Also the settings check made when an integration is saved.
    /// </summary>
    public sealed class SmtpProviderTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly TimeSpan Short = TimeSpan.FromSeconds(1);

        /// <summary>What the fake server does at one step: answer with a reply, never answer, or drop the connection (a reset).</summary>
        private abstract record Act
        {
            public static Act Say(string reply) => new Reply(reply);
            public static readonly Act Hang = new HangAct();
            public static readonly Act Drop = new DropAct();
        }

        private sealed record Reply(string Text) : Act;
        private sealed record HangAct : Act;
        private sealed record DropAct : Act;

        /// <summary>
        /// A one-connection SMTP server on loopback. Each command verb (and <c>GREETING</c>, and <c>BODY</c> for the reply after the message's
        /// final dot) has a scripted act; anything not scripted gets a plain success.
        /// </summary>
        private sealed class FakeSmtpServer : IAsyncDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new();
            private readonly Dictionary<string, Act> _script;
            private readonly Task _run;

            public ConcurrentQueue<string> Commands { get; } = new();
            public bool BodyReceived { get; private set; }
            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public FakeSmtpServer(Dictionary<string, Act> script)
            {
                _script = new Dictionary<string, Act>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GREETING"] = Act.Say("220 fake ESMTP"),
                    ["EHLO"] = Act.Say("250-fake\r\n250 8BITMIME"),
                    ["HELO"] = Act.Say("250 fake"),
                    ["MAIL"] = Act.Say("250 2.1.0 ok"),
                    ["RCPT"] = Act.Say("250 2.1.5 ok"),
                    ["DATA"] = Act.Say("354 go ahead"),
                    ["BODY"] = Act.Say("250 2.0.0 queued as FAKE1"),
                    ["RSET"] = Act.Say("250 ok"),
                    ["NOOP"] = Act.Say("250 ok"),
                    ["QUIT"] = Act.Say("221 bye"),
                };
                foreach (var (verb, act) in script)
                {
                    _script[verb] = act;
                }
                _listener.Start();
                _run = Task.Run(RunAsync);
            }

            private async Task RunAsync()
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                try
                {
                    if (!await DoAsync(_script["GREETING"], writer, client))
                    {
                        return;
                    }
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        Commands.Enqueue(line);
                        var verb = line.Split(' ', ':')[0];
                        var act = _script.TryGetValue(verb, out var scripted) ? scripted : Act.Say("502 not implemented");
                        if (!await DoAsync(act, writer, client))
                        {
                            return;
                        }
                        if (verb.Equals("DATA", StringComparison.OrdinalIgnoreCase) && act is Reply { Text: var text } && text.StartsWith("354"))
                        {
                            if (_script.TryGetValue("MIDBODY", out var midBody))
                            {
                                // Read a little of the body, then act (a reset halfway through a large message).
                                for (var i = 0; i < 20 && await reader.ReadLineAsync(_stop.Token) is { }; i++)
                                {
                                }
                                await DoAsync(midBody, writer, client);
                                return;
                            }
                            while (await reader.ReadLineAsync(_stop.Token) is { } bodyLine && bodyLine != ".")
                            {
                            }
                            BodyReceived = true;
                            if (!await DoAsync(_script["BODY"], writer, client))
                            {
                                return;
                            }
                        }
                        if (verb.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    // The client went away, or the test ended.
                }
            }

            /// <summary>False when the connection is finished with.</summary>
            private async Task<bool> DoAsync(Act act, StreamWriter writer, TcpClient client)
            {
                switch (act)
                {
                    case Reply reply:
                        await writer.WriteLineAsync(reply.Text.AsMemory(), _stop.Token);
                        return true;
                    case HangAct:
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                        return false;
                    default:
                        client.Client.LingerState = new LingerOption(true, 0);
                        client.Client.Close();
                        return false;
                }
            }

            public async ValueTask DisposeAsync()
            {
                await _stop.CancelAsync();
                _listener.Stop();
                try
                {
                    await _run;
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                }
                _stop.Dispose();
            }
        }

        private static SmtpProvider Provider(int port, string host = "127.0.0.1") => new(Options.Create(new DeliveryOptions
        {
            ConnectTimeout = Short,
            RequestTimeout = Short,
            SmtpServers = new() { ["fake"] = new SmtpServer { Host = host, Port = port, Security = SmtpSecurity.None } },
        }));

        private static Task<SendResult> SendAsync(int port, string to = "someone@example.com", string settings = """{ "server": "fake", "from": "sender@example.com" }""",
            string text = "db-1 is at 95%")
        {
            var content = JsonSerializer.Serialize(new EmailContent("Disk almost full", text, "<p>" + text + "</p>"));
            var delivery = new RenderedDelivery(Guid.NewGuid(), Guid.NewGuid(), Channels.Email, content, to, 1, NotificationPriority.Normal, false);
            return Provider(port).SendAsync(delivery, new IntegrationContext(Guid.NewGuid(), "mail", "smtp", settings, null), Ct);
        }

        private static async Task<(SendResult Result, FakeSmtpServer Server)> SendThroughAsync(Dictionary<string, Act> script)
        {
            var server = new FakeSmtpServer(script);
            var result = await SendAsync(server.Port);
            await server.DisposeAsync();
            return (result, server);
        }

        private static void Is(SendResult result, AttemptOutcome outcome, FailureCategory? category, int? status = null)
        {
            Assert.True(outcome == result.Outcome && category == result.Category, $"expected {outcome} {category}, got {result.Outcome} {result.Category}: {result.Error}");
            if (status is not null)
            {
                Assert.Equal(status, result.StatusCode);
            }
        }

        /// <summary>A 250 after the body is accepted, with the server's reply kept as the provider's message id.</summary>
        [Fact]
        public async Task A_250_after_the_body_is_accepted()
        {
            var (result, server) = await SendThroughAsync([]);

            Is(result, AttemptOutcome.Accepted, null);
            Assert.Contains("queued as FAKE1", result.ProviderMessageId);
            Assert.True(server.BodyReceived);
            Assert.Contains(server.Commands, c => c.StartsWith("RCPT TO:<someone@example.com>", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(server.Commands, c => c.Equals("QUIT", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Once the server said 250, a server that drops the connection at QUIT, or never answers it, doesn't make the send unknown.</summary>
        [Fact]
        public async Task A_250_stays_accepted_whatever_QUIT_does()
        {
            Is((await SendThroughAsync(new() { ["QUIT"] = Act.Drop })).Result, AttemptOutcome.Accepted, null);
            Is((await SendThroughAsync(new() { ["QUIT"] = Act.Hang })).Result, AttemptOutcome.Accepted, null);
        }

        /// <summary>Any 4xx, at any step (greylisting at RCPT included), is the server saying "not now": retryable, the provider's.</summary>
        [Theory]
        [InlineData("MAIL", "421 4.3.2 service shutting down", 421)]
        [InlineData("MAIL", "452 4.3.1 insufficient storage", 452)]
        [InlineData("RCPT", "451 4.7.1 greylisted, try again later", 451)]
        [InlineData("RCPT", "450 4.2.1 mailbox busy", 450)]
        [InlineData("DATA", "451 4.3.0 try later", 451)]
        [InlineData("BODY", "452 4.3.1 queue full", 452)]
        public async Task Any_4xx_is_retryable(string step, string reply, int status)
        {
            var (result, _) = await SendThroughAsync(new() { [step] = Act.Say(reply) });
            Is(result, AttemptOutcome.RetryableFailure, FailureCategory.Provider, status);
        }

        /// <summary>A 5xx names whose fault it is: the recipient's (Destination), the sender's (Provider, the integration's), or the message's (Content).</summary>
        [Theory]
        [InlineData("RCPT", "550 5.1.1 no such user", FailureCategory.Destination, 550)]
        [InlineData("RCPT", "553 5.1.3 bad recipient address", FailureCategory.Destination, 553)]
        [InlineData("MAIL", "550 5.7.1 sender rejected", FailureCategory.Provider, 550)]
        [InlineData("MAIL", "553 5.1.8 bad sender domain", FailureCategory.Provider, 553)]
        [InlineData("DATA", "554 5.5.1 no valid recipients", FailureCategory.Content, 554)]
        [InlineData("BODY", "554 5.7.1 message refused as spam", FailureCategory.Content, 554)]
        [InlineData("BODY", "552 5.3.4 message too big", FailureCategory.Content, 552)]
        public async Task A_5xx_is_permanent_and_says_whose_fault(string step, string reply, FailureCategory category, int status)
        {
            var (result, _) = await SendThroughAsync(new() { [step] = Act.Say(reply) });
            Is(result, AttemptOutcome.PermanentFailure, category, status);
        }

        /// <summary>A timeout or a dropped connection before any of the body went out: nothing was sent, so retryable.</summary>
        [Theory]
        [InlineData("GREETING", false)]
        [InlineData("GREETING", true)]
        [InlineData("EHLO", true)]
        [InlineData("MAIL", false)]
        [InlineData("RCPT", false)]
        [InlineData("RCPT", true)]
        [InlineData("DATA", false)]
        [InlineData("DATA", true)]
        public async Task Lost_before_the_body_is_retryable(string step, bool drop)
        {
            var (result, server) = await SendThroughAsync(new() { [step] = drop ? Act.Drop : Act.Hang });
            Is(result, AttemptOutcome.RetryableFailure, FailureCategory.Provider);
            Assert.False(server.BodyReceived);
        }

        /// <summary>A timeout or a dropped connection after the body went out and before the reply: the server may have queued it, so unknown.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Lost_after_the_body_is_unknown(bool drop)
        {
            var (result, server) = await SendThroughAsync(new() { ["BODY"] = drop ? Act.Drop : Act.Hang });
            Is(result, AttemptOutcome.Unknown, FailureCategory.Provider);
            Assert.True(server.BodyReceived);
        }

        /// <summary>
        /// Lost halfway through the body: the server never got the final dot, so it can't have queued the message, and a retry is safe (I9).
        /// </summary>
        [Fact]
        public async Task Lost_partway_through_the_body_is_retryable()
        {
            var server = new FakeSmtpServer(new() { ["MIDBODY"] = Act.Drop });
            var result = await SendAsync(server.Port, text: string.Join('\n', Enumerable.Repeat(new string('x', 70), 20_000)));
            await server.DisposeAsync();
            Is(result, AttemptOutcome.RetryableFailure, FailureCategory.Provider);
            Assert.False(server.BodyReceived);
        }

        /// <summary>Nothing listening is a failed connect: retryable.</summary>
        [Fact]
        public async Task A_closed_port_is_retryable()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            Is(await SendAsync(port), AttemptOutcome.RetryableFailure, FailureCategory.Provider);
        }

        /// <summary>What can be told before connecting fails without a connection, and without throwing: the server, the sender, the recipient.</summary>
        [Theory]
        [InlineData("""{ "server": "other", "from": "sender@example.com" }""", "someone@example.com", FailureCategory.Provider)]
        [InlineData("""{ "from": "sender@example.com" }""", "someone@example.com", FailureCategory.Provider)]
        [InlineData("""{ "server": "fake" }""", "someone@example.com", FailureCategory.Provider)]
        [InlineData("""{ "server": "fake", "from": "not an address" }""", "someone@example.com", FailureCategory.Provider)]
        [InlineData("""{ "server": "fake", "from": "a@@example.com" }""", "someone@example.com", FailureCategory.Provider)]
        [InlineData("""{ "server": 1, "from": "sender@example.com" }""", "someone@example.com", FailureCategory.Provider)]
        [InlineData("""{ "server": "fake", "from": "sender@example.com" }""", "not an email", FailureCategory.Destination)]
        public async Task Bad_settings_or_destination_fail_permanently_without_sending(string settings, string to, FailureCategory category)
        {
            await using var server = new FakeSmtpServer([]);

            var result = await SendAsync(server.Port, to, settings);

            Is(result, AttemptOutcome.PermanentFailure, category);
            Assert.DoesNotContain(server.Commands, c => c.StartsWith("DATA", StringComparison.OrdinalIgnoreCase));
        }

        private static SmtpProvider Checker() => Provider(25, "smtp.example.com");

        /// <summary>Settings that will send are accepted.</summary>
        [Theory]
        [InlineData("""{ "server": "fake", "from": "sender@example.com" }""")]
        [InlineData("""{ "server": "fake", "from": "sender@example.com", "fromName": "Alerts" }""")]
        [InlineData("""{ "server": "fake", "from": "a@xn--bcher-kva.de" }""")] // punycode: MimeKit reads it back decoded, still the bare address
        [InlineData("""{ "Server": "fake", "From": "sender@example.com", "FromName": null }""")]
        public void Good_settings_pass_the_check(string settings) => Assert.Null(Checker().CheckSettings(settings));

        /// <summary>
        /// The save-time check returns a message, never throws: an unknown or missing server, a missing, malformed or padded sender, a long
        /// sender name, and fields of the wrong JSON type.
        /// </summary>
        [Theory]
        [InlineData("""{ "server": "other", "from": "sender@example.com" }""", "settings.server")]
        [InlineData("""{ "from": "sender@example.com" }""", "settings.server")]
        [InlineData("""{ "server": "fake" }""", "settings.from")]
        [InlineData("""{ "server": "fake", "from": "not an address" }""", "settings.from")]
        [InlineData("""{ "server": "fake", "from": " sender@example.com " }""", "settings.from")]
        [InlineData("""{ "server": "fake", "from": "sender@example.com " }""", "settings.from")]
        [InlineData("""{ "server": "fake", "from": "Alerts <sender@example.com>" }""", "settings.from")]
        [InlineData("""{ "server": "fake", "from": "a@example.com, b@example.com" }""", "settings.from")]
        [InlineData("""{ "server": 1, "from": "sender@example.com" }""", "wrong type")]
        [InlineData("""{ "server": "fake", "from": ["sender@example.com"] }""", "wrong type")]
        [InlineData("""{ "server": "fake", "from": "sender@example.com", "fromName": 7 }""", "wrong type")]
        [InlineData("""null""", "settings.server")]
        public void Bad_settings_return_a_message_instead_of_throwing(string settings, string mentions)
        {
            var error = Checker().CheckSettings(settings);
            Assert.NotNull(error);
            Assert.Contains(mentions, error);
        }

        /// <summary>A sender name longer than 200 characters is refused.</summary>
        [Fact]
        public void A_long_sender_name_is_refused()
        {
            var settings = JsonSerializer.Serialize(new { server = "fake", from = "sender@example.com", fromName = new string('n', 201) });
            Assert.Contains("fromName", Checker().CheckSettings(settings));
            Assert.Null(Checker().CheckSettings(JsonSerializer.Serialize(new { server = "fake", from = "sender@example.com", fromName = new string('n', 200) })));
        }
    }
}
