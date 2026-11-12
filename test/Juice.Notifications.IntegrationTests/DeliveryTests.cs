using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The whole pipeline in the real host (phase-1-plan M5–M7): a request is planned, its deliveries rendered once, and sent by email through the
    /// local stack's SMTP server (Mailpit) and by signed webhook to a receiver on a private address.
    /// </summary>
    public sealed class DeliveryTests(ProducerApp producer) : IClassFixture<ProducerApp>
    {
        internal static CancellationToken Ct => TestContext.Current.CancellationToken;
        internal static readonly string Tenant = $"/api/v1/tenants/{ProducerApp.Tenant}";
        private const string HookSecretVariable = "NOTIFICATIONS_SECRET_TEST_HOOK";
        private const string HookSecret = "test-signing-secret";

        internal static JsonElement Json(string json) => JsonElement.Parse(json);

        internal static async Task Ok(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        }

        internal static string Short() => Guid.NewGuid().ToString("N")[..10];

        /// <summary>Declares a type, routes it to one new target, sends one request; returns the request id.</summary>
        internal static async Task<Guid> SendThroughRouteAsync(HttpClient admin, HttpClient client, string integration, string target, string templates)
        {
            var type = $"{ProducerApp.Key}.d{Short()}";
            await Ok(client.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(type, "Disk full", "alerts", null, "high",
                null, null, Json("""{ "type": "object", "required": ["host"] }"""), null, null, null, Json(templates)), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/routes/r-{Short()}", new PutRouteBody(Json($$"""{ "type": "{{type}}" }"""), [target], null), Ct));
            var accepted = await (await client.PostAsJsonAsync($"{Tenant}/notifications", new SendNotificationBody(type, null, Short(), null, null, null, null,
                new Dictionary<string, string> { ["env"] = "prod" }, Json("""{ "host": "db-1", "used": 97 }"""), null, null), Ct))
                .Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            return accepted!.Id;
        }

        /// <summary>Waits until every delivery of the notification is final (not pending or retrying).</summary>
        internal static async Task<NotificationResponse> DeliveredAsync(HttpClient client, Guid id, int expected = 1)
        {
            for (var i = 0; i < 150; i++)
            {
                var detail = await client.GetFromJsonAsync<NotificationResponse>($"{Tenant}/notifications/{id}", Ct);
                if (detail!.Deliveries.Count >= expected && detail.Deliveries.All(d => d.Status is not (DeliveryStatus.Pending or DeliveryStatus.Retrying or DeliveryStatus.Sending)))
                {
                    return detail;
                }
                await Task.Delay(100, Ct);
            }
            throw new TimeoutException("The deliveries never finished.");
        }

        [Fact]
        public async Task An_email_reaches_the_mailbox_through_smtp()
        {
            LocalStack.RequireAvailable();
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            using var client = await producer.ProducerAsync();
            var mailbox = $"ops-{Short()}@example.com";
            var target = "mail-" + Short();
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/mail", new PutIntegrationBody("email", "smtp",
                Json("""{ "server": "local", "from": "notifications@juice.local", "fromName": "Juice" }"""), null, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/mail/targets/{target}", new PutTargetBody(mailbox, null, null), Ct));

            var id = await SendThroughRouteAsync(admin, client, "mail", target, """{ "email": "Subject: Disk {{ data.used }}% on {{ data.host }}\n\nHost <{{ data.host }}> is at {{ data.used }}%." }""");
            var detail = await DeliveredAsync(client, id);

            var delivery = Assert.Single(detail.Deliveries);
            Assert.Equal((DeliveryStatus.Accepted, 1, "email"), (delivery.Status, delivery.Attempts, delivery.Channel));
            using var mailpit = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8026") };
            var found = await mailpit.GetFromJsonAsync<JsonElement>($"/api/v1/search?query={Uri.EscapeDataString("to:" + mailbox)}", Ct);
            var message = Assert.Single(found.GetProperty("messages").EnumerateArray());
            Assert.Equal("Disk 97% on db-1", message.GetProperty("Subject").GetString());
            Assert.Contains("Host <db-1> is at 97%.", message.GetProperty("Snippet").GetString());
        }

        [Fact]
        public async Task A_signed_webhook_is_delivered_with_the_same_id_on_every_attempt()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync(503, 200);
            Environment.SetEnvironmentVariable(HookSecretVariable, HookSecret);
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            using var client = await producer.ProducerAsync();
            var target = "hook-" + Short();
            await Ok(admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port]), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/hooks", new PutIntegrationBody("webhook", "webhook", null, "env:" + HookSecretVariable, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/hooks/targets/{target}", new PutTargetBody(receiver.Url, null, null), Ct));

            var id = await SendThroughRouteAsync(admin, client, "hooks", target, """{ "webhook": "passthrough" }""");
            var detail = await DeliveredAsync(client, id);

            var delivery = Assert.Single(detail.Deliveries);
            Assert.Equal((DeliveryStatus.Delivered, 2), (delivery.Status, delivery.Attempts));
            var requests = receiver.Requests.ToList();
            Assert.Equal(2, requests.Count);
            Assert.All(requests, r => Assert.Equal(delivery.Id.ToString(), r.Id));
            Assert.All(requests, r => Assert.True(WebhookSigner.Verify(HookSecret, r.Id, r.Timestamp, r.Signature, r.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5))));
            var envelope = JsonElement.Parse(requests[1].Body);
            Assert.Equal(id, envelope.GetProperty("id").GetGuid());
            Assert.Equal("db-1", envelope.GetProperty("data").GetProperty("host").GetString());

            await using var db = producer.App.Database.Context();
            var attempts = await db.Attempts.Where(a => a.DeliveryId == delivery.Id).OrderBy(a => a.Number).ToListAsync(Ct);
            Assert.Equal([(1, AttemptOutcome.RetryableFailure, (int?)503), (2, AttemptOutcome.Delivered, 200)],
                attempts.Select(a => (a.Number, a.Outcome, a.StatusCode)));
        }

        [Fact]
        public async Task A_destination_that_says_it_is_gone_fails_without_a_retry()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync(404);
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            using var client = await producer.ProducerAsync();
            var target = "gone-" + Short();
            await Ok(admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port]), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/plain-hooks", new PutIntegrationBody("webhook", "webhook", null, null, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/plain-hooks/targets/{target}", new PutTargetBody(receiver.Url, null, null), Ct));

            var detail = await DeliveredAsync(client, await SendThroughRouteAsync(admin, client, "plain-hooks", target, """{ "webhook": "passthrough" }"""));

            var delivery = Assert.Single(detail.Deliveries);
            Assert.Equal((DeliveryStatus.Failed, FailureCategory.Destination, 1), (delivery.Status, delivery.FailureCategory, delivery.Attempts));
            Assert.Single(receiver.Requests);
        }

        [Fact]
        public async Task Loopback_is_never_reached_even_when_a_target_names_it()
        {
            LocalStack.RequireAvailable();
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            using var client = await producer.ProducerAsync();
            var target = "local-" + Short();
            // Refused as an allowlist entry, and blocked at send time whatever the allowlist says.
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody("127.0.0.1", [5280]), Ct)).StatusCode);
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/plain-hooks", new PutIntegrationBody("webhook", "webhook", null, null, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/plain-hooks/targets/{target}", new PutTargetBody("http://127.0.0.1:5280/hook", null, null), Ct));

            var detail = await DeliveredAsync(client, await SendThroughRouteAsync(admin, client, "plain-hooks", target, """{ "webhook": "passthrough" }"""));

            var delivery = Assert.Single(detail.Deliveries);
            Assert.Equal((DeliveryStatus.Failed, FailureCategory.Policy), (delivery.Status, delivery.FailureCategory));
        }

        /// <summary>A webhook receiver on this machine's private address (loopback is always blocked); answers with the given statuses, then 200.</summary>
        internal sealed class Receiver : IAsyncDisposable
        {
            public sealed record Received(string Id, string Timestamp, string Signature, string Body, string? TraceParent = null);

            private readonly WebApplication _app;
            private readonly ConcurrentQueue<int> _statuses;

            public ConcurrentQueue<Received> Requests { get; } = new();
            public IPAddress Address { get; }
            public int Port { get; private set; }
            public string Url => $"http://{Address}:{Port}/hook";

            private Receiver(IPAddress address, int[] statuses)
            {
                Address = address;
                _statuses = new(statuses);
                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.UseUrls($"http://{address}:0");
                builder.Logging.ClearProviders();
                _app = builder.Build();
                _app.MapPost("/hook", async (HttpRequest request) =>
                {
                    using var reader = new StreamReader(request.Body);
                    Requests.Enqueue(new Received(request.Headers[WebhookSigner.IdHeader].ToString(), request.Headers[WebhookSigner.TimestampHeader].ToString(),
                        request.Headers[WebhookSigner.SignatureHeader].ToString(), await reader.ReadToEndAsync(), request.Headers["traceparent"].FirstOrDefault()));
                    return Results.StatusCode(_statuses.TryDequeue(out var status) ? status : 200);
                });
            }

            public static async Task<Receiver> StartAsync(params int[] statuses)
            {
                var address = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && DestinationPolicy.Classify(a) == DestinationClass.Private);
                Assert.SkipWhen(address is null, "no private IPv4 address on this machine for the webhook receiver");
                var receiver = new Receiver(address!, statuses);
                await receiver._app.StartAsync();
                receiver.Port = new Uri(receiver._app.Urls.First()).Port;
                return receiver;
            }

            public async ValueTask DisposeAsync()
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }
    }
}
