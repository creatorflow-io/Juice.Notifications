// A producer against the local stack (deploy/local). Start the stack and the service first (scripts/run-app.sh, or --profile app), then:
//   dotnet run --project samples/Juice.Notifications.Samples.Producer [path/to/deploy/local/.env]
// It registers the 'sample' application (as the platform admin), routes its type to the Mailpit mailbox and to a signed webhook this program
// receives itself, declares the type, sends one notification, verifies the webhook's signature, and prints the delivery history.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Juice.Notifications;
using Juice.Notifications.Api.Contracts;

var settings = Settings.Load(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "../../../../../deploy/local/.env"));
var api = settings.Get("NOTIFICATIONS_API", "http://localhost:5280");
var tenant = settings.Get("TENANT", "0193f0aa-0000-7000-8000-0000000000a1");
var mailbox = settings.Get("MAILBOX", "ops@example.com");
var webhookSecret = settings.Get("SAMPLE_WEBHOOK_SECRET", "");
var tokenUrl = settings.Get("TOKEN_URL", "http://127.0.0.1:8081/realms/juice/protocol/openid-connect/token");

using var http = new HttpClient();
var admin = Client(api, await TokenAsync(http, tokenUrl, "notifications-admin", settings.Get("NOTIFICATIONS_ADMIN_CLIENT_SECRET", "")));
var producer = Client(api, await TokenAsync(http, tokenUrl, "sample-producer", settings.Get("SAMPLE_PRODUCER_CLIENT_SECRET", "")));

// The webhook receiver: on this machine's private address (the destination policy never allows loopback), allowlisted for the tenant.
await using var receiver = await Receiver.StartAsync(webhookSecret);
Console.WriteLine($"Receiving webhooks at {receiver.Url}");

var t = $"/api/v1/tenants/{tenant}";
await Step("Register the application 'sample'", admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody("sample", "Sample", "sample-producer")), HttpStatusCode.Conflict);
await Step($"Let it send for tenant {tenant}", admin.PutAsync($"/api/v1/applications/sample/tenants/{tenant}", null));
await Step("Allow the receiver's address", admin.PutAsJsonAsync($"{t}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port])));
await Step("Email integration (deployment SMTP server 'local' = Mailpit)", admin.PutAsJsonAsync($"{t}/integrations/mail",
    new PutIntegrationBody("email", "smtp", Json("""{ "server": "local", "from": "notifications@juice.local", "fromName": "Juice" }"""), null, null, null)));
await Step($"Target: {mailbox}", admin.PutAsJsonAsync($"{t}/integrations/mail/targets/ops", new PutTargetBody(mailbox, null, null)));
await Step("Webhook integration, signed with the secret the service reads from NOTIFICATIONS_SECRET_SAMPLE_HOOK",
    admin.PutAsJsonAsync($"{t}/integrations/sample-hooks", new PutIntegrationBody("webhook", "webhook", null,
        webhookSecret.Length > 0 ? "env:NOTIFICATIONS_SECRET_SAMPLE_HOOK" : null, null, null)));
await Step("Target: this program's receiver", admin.PutAsJsonAsync($"{t}/integrations/sample-hooks/targets/sample-receiver", new PutTargetBody(receiver.Url, null, null)));
await Step("Route: sample.disk.* in prod goes to both", admin.PutAsJsonAsync($"{t}/routes/sample-disk",
    new PutRouteBody(Json("""{ "type": "sample.disk.*", "labels": { "env": "prod" } }"""), ["ops", "sample-receiver"], null)));

await Step("Declare the type sample.disk.full", producer.PutAsJsonAsync("/api/v1/applications/sample/types", new DeclareTypeBody(
    "sample.disk.full", "Disk full", "alerts", null, "high", null, null,
    Json("""{ "type": "object", "required": ["host", "used"], "properties": { "host": { "type": "string" }, "used": { "type": "integer" } } }"""),
    null, null, null,
    Json("""{ "email": "Subject: Disk {{ data.used }}% on {{ data.host }}\n\nHost {{ data.host }} is at {{ data.used }}% disk.", "webhook": "passthrough" }"""))));

var sent = await producer.PostAsJsonAsync($"{t}/notifications", new SendNotificationBody("sample.disk.full", null, $"sample-{Guid.NewGuid():N}", null, null, null,
    null, new Dictionary<string, string> { ["env"] = "prod" }, Json("""{ "host": "db-1", "used": 97 }"""), null, null));
await Step("Send a notification", Task.FromResult(sent));
var accepted = (await sent.Content.ReadFromJsonAsync<AcceptedResponse>())!;

NotificationResponse? detail = null;
for (var i = 0; i < 100; i++)
{
    detail = await producer.GetFromJsonAsync<NotificationResponse>($"{t}/notifications/{accepted.Id}");
    if (detail!.Deliveries.Count >= 2 && detail.Deliveries.All(d => d.Status is not (DeliveryStatus.Pending or DeliveryStatus.Retrying or DeliveryStatus.Sending)))
    {
        break;
    }
    await Task.Delay(200);
}
Console.WriteLine();
foreach (var d in detail!.Deliveries)
{
    Console.WriteLine($"  {d.Channel,-8} {d.Status,-10} attempts {d.Attempts}  delivery {d.Id}");
}
foreach (var hook in receiver.Received)
{
    Console.WriteLine($"  webhook received: id {hook.Id}, signature {(webhookSecret.Length == 0 ? "not signed (no SAMPLE_WEBHOOK_SECRET)" : hook.Verified ? "verified" : "INVALID")}");
}
var history = await admin.GetFromJsonAsync<DeliveryPageResponse>($"{t}/deliveries?request={accepted.Id}");
Console.WriteLine($"\nHistory: {history!.Items.Count} deliveries for request {accepted.Id} (GET {t}/deliveries?request={accepted.Id}).");
Console.WriteLine("Read the email in Mailpit: http://localhost:8026");
return detail.Deliveries.All(d => d.Status is DeliveryStatus.Delivered or DeliveryStatus.Accepted) && receiver.Received.All(h => webhookSecret.Length == 0 || h.Verified) ? 0 : 1;

static JsonElement Json(string json) => JsonElement.Parse(json);

static HttpClient Client(string api, string token)
{
    var client = new HttpClient { BaseAddress = new Uri(api) };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return client;
}

static async Task<string> TokenAsync(HttpClient http, string url, string client, string secret)
{
    var response = await http.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "client_credentials", ["client_id"] = client, ["client_secret"] = secret,
    }));
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
}

static async Task Step(string what, Task<HttpResponseMessage> call, HttpStatusCode? alsoFine = null)
{
    var response = await call;
    Console.WriteLine($"{(int)response.StatusCode} {what}");
    if (!response.IsSuccessStatusCode && response.StatusCode != alsoFine)
    {
        Console.WriteLine(await response.Content.ReadAsStringAsync());
        Environment.Exit(1);
    }
}

/// <summary>Settings from the environment, else from the local stack's .env file.</summary>
sealed class Settings(Dictionary<string, string> file)
{
    public static Settings Load(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path).Where(l => l.Contains('=') && !l.TrimStart().StartsWith('#')))
            {
                var at = line.IndexOf('=');
                values[line[..at].Trim()] = line[(at + 1)..].Trim();
            }
        }
        return new Settings(values);
    }

    public string Get(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? file.GetValueOrDefault(name) ?? fallback;
}

/// <summary>
/// A webhook receiver, and the reference check for receivers (Standard Webhooks, design §10.4): HMAC-SHA256 over
/// <c>{webhook-id}.{webhook-timestamp}.{body}</c> with the shared secret, compared in constant time with each <c>v1,</c> signature in
/// <c>webhook-signature</c>, and a timestamp within five minutes. Deduplicate on <c>webhook-id</c>: it is the delivery id, the same on every attempt.
/// </summary>
sealed class Receiver : IAsyncDisposable
{
    public sealed record Hook(string Id, bool Verified);

    private readonly WebApplication _app;

    public ConcurrentQueue<Hook> Received { get; } = new();
    public IPAddress Address { get; }
    public int Port { get; private set; }
    public string Url => $"http://{Address}:{Port}/hook";

    private Receiver(IPAddress address, string secret)
    {
        Address = address;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{address}:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        var seen = new ConcurrentDictionary<string, bool>();
        _app.MapPost("/hook", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var id = request.Headers["webhook-id"].ToString();
            var verified = Verify(secret, id, request.Headers["webhook-timestamp"].ToString(), request.Headers["webhook-signature"].ToString(), body);
            if (seen.TryAdd(id, true))
            {
                Received.Enqueue(new Hook(id, verified));
            }
            return verified || secret.Length == 0 ? Results.NoContent() : Results.Unauthorized();
        });
    }

    public static bool Verify(string secret, string id, string timestamp, string signatures, string body)
    {
        if (secret.Length == 0 || !long.TryParse(timestamp, out var seconds)
            || (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > TimeSpan.FromMinutes(5))
        {
            return false;
        }
        var expected = Encoding.ASCII.GetBytes("v1," + Convert.ToBase64String(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{id}.{seconds}.{body}"))));
        return signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(candidate => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(candidate)));
    }

    public static async Task<Receiver> StartAsync(string secret)
    {
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
            ?? throw new InvalidOperationException("This machine has no private IPv4 address for the webhook receiver.");
        var receiver = new Receiver(address, secret);
        await receiver._app.StartAsync();
        receiver.Port = new Uri(receiver._app.Urls.First()).Port;
        return receiver;
    }

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
