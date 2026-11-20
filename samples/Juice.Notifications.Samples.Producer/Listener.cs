// The sample application's side of an approval (design §5.4, §7.3, §19 decision 13): the inbox decides nothing; a person's choice comes to
// the producer as notification.interaction over the broker, and the producer decides and closes the correlation. Run:
//   dotnet run --project samples/Juice.Notifications.Samples.Producer -- listen [path/to/deploy/local/.env]
// It sets up what a deployment would (a broker user for 'sample', allowed to bind only to sample.* on the events exchange, named on the
// application), binds a queue of its own to sample.notification.interaction, and resolves each correlation on the first approve or reject:
// outcome "Approved" or "Rejected". Every inbox item of it then shows the outcome instead of its buttons.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

static class Listener
{
    private const string Exchange = "juice.notifications.events";
    private const string Application = "sample";
    private const string BrokerUser = "sample";

    private static readonly Dictionary<string, string> Outcomes = new() { ["approve"] = "Approved", ["reject"] = "Rejected" };

    public static async Task<int> RunAsync(Settings settings, HttpClient admin, HttpClient producer)
    {
        var tenant = settings.Get("TENANT", "0193f0aa-0000-7000-8000-0000000000a1");
        var host = settings.Get("RABBITMQ_HOST", "127.0.0.1");
        var port = int.Parse(settings.Get("RABBITMQ_PORT", "5673"));
        var management = settings.Get("RABBITMQ_MANAGEMENT", "http://127.0.0.1:15673/api/");
        var servicePassword = settings.Get("RABBITMQ_PASSWORD", "");
        // The sample's own broker password: derived from the service's, so there is no other secret to keep for a local stack.
        var password = settings.Get("SAMPLE_BROKER_PASSWORD", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("sample-broker:" + servicePassword)))[..32].ToLowerInvariant());
        if (servicePassword.Length == 0)
        {
            Console.Error.WriteLine("RABBITMQ_PASSWORD is not set: run deploy/local/scripts/init.sh.");
            return 1;
        }

        // What the deployment does for an application that hears events (README: events to producers): its broker user, which may declare
        // server-named queues and bind them to its own events only.
        using (var broker = new HttpClient { BaseAddress = new Uri(management) })
        {
            broker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("notifications:" + servicePassword)));
            await Put(broker, $"users/{BrokerUser}", new { password, tags = "" });
            await Put(broker, $"permissions/%2F/{BrokerUser}", new
            {
                configure = @"^amq\.gen-.*$", write = @"^(notifications|amq\.gen-.*)$", read = $@"^({Exchange.Replace(".", @"\.")}|amq\.gen-.*)$",
            });
            await Put(broker, $"topic-permissions/%2F/{BrokerUser}", new { exchange = Exchange, write = "^$", read = $@"^{Application}\." });
            Console.WriteLine($"Broker user '{BrokerUser}': may bind to {Application}.* on {Exchange}");
        }
        var named = await admin.PutAsJsonAsync($"/api/v1/applications/{Application}", new UpdateApplicationBody(null, null, null, BrokerUser));
        Console.WriteLine($"{(int)named.StatusCode} Name it on the application '{Application}' (the service raises its events only then)");
        if (!named.IsSuccessStatusCode)
        {
            Console.WriteLine(await named.Content.ReadAsStringAsync());
            return 1;
        }

        await using var connection = await new ConnectionFactory { HostName = host, Port = port, UserName = BrokerUser, Password = password }
            .CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var queue = (await channel.QueueDeclareAsync("", durable: false, exclusive: true, autoDelete: true)).QueueName;
        await channel.QueueBindAsync(queue, Exchange, $"{Application}.notification.interaction");
        await channel.BasicQosAsync(0, 1, false);

        var t = $"/api/v1/tenants/{tenant}";
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, message) =>
        {
            try
            {
                using var body = JsonDocument.Parse(Encoding.UTF8.GetString(message.Body.Span));
                var e = body.RootElement;
                string? Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var (kind, action, key) = (Text("Kind"), Text("ActionId"), Text("CorrelationKey"));
                Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {Text("Type")}: {kind}{(action is null ? "" : $" {action}")} by subscriber {Text("SubscriberId")}"
                    + (key is null ? "" : $" on {key}"));
                // Its own tenant's events only (one broker user may hear several tenants), and only a choice on a correlation decides anything.
                if (kind == "action" && key is not null && action is not null && Outcomes.TryGetValue(action, out var outcome)
                    && e.TryGetProperty("NotificationTenantId", out var at) && at.GetGuid() == Guid.Parse(tenant))
                {
                    var resolved = await producer.PostAsJsonAsync($"{t}/correlations/{Uri.EscapeDataString(key)}/resolve",
                        new ResolveCorrelationBody(outcome, $"sample ({action} in the inbox)"));
                    Console.WriteLine(resolved.StatusCode switch
                    {
                        HttpStatusCode.OK or HttpStatusCode.NoContent => $"  resolved {key}: {outcome}",
                        HttpStatusCode.Conflict => $"  {key} was already closed",
                        var status => $"  resolving {key}: {(int)status} {await resolved.Content.ReadAsStringAsync()}",
                    });
                }
                await channel.BasicAckAsync(message.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  could not handle an event: {ex.Message}");
                await channel.BasicNackAsync(message.DeliveryTag, false, requeue: false);
            }
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer);
        Console.WriteLine($"Listening on {Application}.notification.interaction (Ctrl+C to stop). Approve or Reject in the inbox resolves the correlation.");

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.TrySetResult(); };
        await stop.Task;
        return 0;
    }

    private static async Task Put(HttpClient broker, string path, object body)
    {
        var response = await broker.PutAsJsonAsync(path, body);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PUT {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
    }
}
