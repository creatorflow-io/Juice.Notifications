using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The request checks intake makes beyond the type's schema (phase-1-plan M2), through the API: priority, expiry, data shape and size, links,
    /// correlation. Added after mutation testing showed these refusals reached by no request.
    /// </summary>
    public sealed class IntakeValidationTests(ProducerApp producer) : IClassFixture<ProducerApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string SendUrl = $"/api/v1/tenants/{ProducerApp.Tenant}/notifications";

        private static async Task<string> DeclareAsync(HttpClient client, string? correlation = null)
        {
            var key = $"{ProducerApp.Key}.v{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            var declared = await client.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(key, "Checked", "things", null, null,
                null, null, JsonElement.Parse("""{ "type": "object" }"""), correlation, null, null, JsonElement.Parse("""{ "email": "Hello" }""")), Ct);
            Assert.True(declared.IsSuccessStatusCode, await declared.Content.ReadAsStringAsync(Ct));
            return key;
        }

        private static SendNotificationBody Send(string type, string? priority = null, DateTimeOffset? expiresAt = null, string data = "{}",
            Dictionary<string, string>? links = null, string? correlationKey = null, DateTimeOffset? occurredAt = null)
            => new(type, null, Guid.NewGuid().ToString("N"), correlationKey, null, null, priority, null, JsonElement.Parse(data), links, expiresAt, occurredAt);

        private static async Task<(HttpStatusCode Status, string? Code, List<string> Pointers)> ProblemAsync(HttpResponseMessage response)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            var code = body.TryGetProperty("code", out var c) ? c.GetString() : null;
            var pointers = body.TryGetProperty("errors", out var errors) ? errors.EnumerateArray().Select(e => e.GetProperty("pointer").GetString()!).ToList() : [];
            return (response.StatusCode, code, pointers);
        }

        [Fact]
        public async Task A_priority_is_one_of_the_four()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclareAsync(client);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(SendUrl, Send(type, priority: "critical"), Ct)).StatusCode);
            var (status, code, pointers) = await ProblemAsync(await client.PostAsJsonAsync(SendUrl, Send(type, priority: "urgent"), Ct));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest), (status, code));
            Assert.Equal(["/priority"], pointers);
        }

        /// <summary>
        /// When its event happened (phase 5, M45-n, the architect's decision): any time is accepted (an escalation clamps what it takes from it);
        /// absent, none is kept.
        /// </summary>
        [Fact]
        public async Task Any_occurrence_is_accepted_and_kept_only_when_sent()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclareAsync(client);
            var now = DateTimeOffset.UtcNow;
            foreach (var taken in new DateTimeOffset?[] { null, now.AddMinutes(2), now.AddDays(-6.9), now.AddHours(-1), now.AddMinutes(30), now.AddDays(-30) })
            {
                Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(SendUrl, Send(type, occurredAt: taken), Ct)).StatusCode);
            }

            // Kept as sent.
            var at = now.AddMinutes(-42);
            var sent = await (await client.PostAsJsonAsync(SendUrl, Send(type, occurredAt: at), Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            var plain = await (await client.PostAsJsonAsync(SendUrl, Send(type), Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            await using var db = producer.App.Database.Context();
            var stored = await db.Requests.AsNoTracking().SingleAsync(r => r.Id == sent!.Id, Ct);
            Assert.Equal(at.ToUnixTimeMilliseconds(), stored.OccurredAt!.Value.ToUnixTimeMilliseconds());
            // Absent: none kept (the escalation then times its first step from its own start).
            Assert.Null((await db.Requests.AsNoTracking().SingleAsync(r => r.Id == plain!.Id, Ct)).OccurredAt);
        }

        /// <summary>
        /// QA (M45-n): a time sent with an offset other than UTC (a producer that writes local time, ISO 8601 with <c>+07:00</c>) is the same
        /// instant, accepted and kept as it, not a 500 (PostgreSQL's timestamptz takes only UTC from Npgsql). The text is sent as is, not
        /// re-serialized from a UTC value; so is a bad one, which is refused at the field.
        /// </summary>
        [Fact]
        public async Task An_occurrence_with_a_non_utc_offset_is_the_same_instant()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclareAsync(client);
            var at = DateTimeOffset.UtcNow.AddMinutes(-10);
            var local = at.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
            var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Send(type), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
            json["occurredAt"] = local;
            var body = json.ToJsonString();
            var response = await client.PostAsync(SendUrl, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            var accepted = await response.Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            await using var db = producer.App.Database.Context();
            Assert.Equal(at.ToUnixTimeMilliseconds(), (await db.Requests.AsNoTracking().SingleAsync(r => r.Id == accepted!.Id, Ct)).OccurredAt!.Value.ToUnixTimeMilliseconds());

            // An expiry with the same offset: the same instant too.
            var expires = DateTimeOffset.UtcNow.AddHours(2);
            json["occurredAt"] = null;
            json["idempotencyKey"] = Guid.NewGuid().ToString("N");
            json["expiresAt"] = expires.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz");
            response = await client.PostAsync(SendUrl, new StringContent(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            accepted = await response.Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            Assert.Equal(expires.ToUnixTimeMilliseconds(), (await db.Requests.AsNoTracking().SingleAsync(r => r.Id == accepted!.Id, Ct)).ExpiresAt!.Value.ToUnixTimeMilliseconds());
        }

        /// <summary>QA (M45-n, 26 Nov): out of range is accepted and kept as sent (an hour ahead, thirty days old); only the escalate step clamps it.</summary>
        [Fact]
        public async Task An_occurrence_out_of_range_is_accepted_and_kept_as_sent()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclareAsync(client);
            var now = DateTimeOffset.UtcNow;
            var ahead = await (await client.PostAsJsonAsync(SendUrl, Send(type, occurredAt: now.AddHours(1)), Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            var old = await (await client.PostAsJsonAsync(SendUrl, Send(type, occurredAt: now.AddDays(-30)), Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            await using var db = producer.App.Database.Context();
            var future = await db.Requests.AsNoTracking().SingleAsync(r => r.Id == ahead!.Id, Ct);
            Assert.Equal(now.AddHours(1).ToUnixTimeMilliseconds(), future.OccurredAt!.Value.ToUnixTimeMilliseconds()); // as sent: the escalate step clamps
            var past = await db.Requests.AsNoTracking().SingleAsync(r => r.Id == old!.Id, Ct);
            Assert.Equal(now.AddDays(-30).ToUnixTimeMilliseconds(), past.OccurredAt!.Value.ToUnixTimeMilliseconds());
        }

        [Fact]
        public async Task A_request_cant_expire_before_it_arrives()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclareAsync(client);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(SendUrl, Send(type, expiresAt: DateTimeOffset.UtcNow.AddMinutes(5)), Ct)).StatusCode);
            var (status, _, pointers) = await ProblemAsync(await client.PostAsJsonAsync(SendUrl, Send(type, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal(["/expiresAt"], pointers);
        }

        [Fact]
        public async Task Data_is_an_object_of_at_most_256_KB()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclareAsync(client);
            foreach (var data in new[] { "[1, 2]", "\"text\"", "5" })
            {
                var (status, _, pointers) = await ProblemAsync(await client.PostAsJsonAsync(SendUrl, Send(type, data: data), Ct));
                Assert.Equal(HttpStatusCode.BadRequest, status);
                Assert.Equal(["/data"], pointers);
            }
            // {"x":"…"} at exactly the limit is accepted; one byte more is not.
            static string Sized(int bytes) => $$"""{"x":"{{new string('a', bytes - 8)}}"}""";
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(SendUrl, Send(type, data: Sized(256 * 1024)), Ct)).StatusCode);
            var (tooBig, _, at) = await ProblemAsync(await client.PostAsJsonAsync(SendUrl, Send(type, data: Sized(256 * 1024 + 1)), Ct));
            Assert.Equal(HttpStatusCode.BadRequest, tooBig);
            Assert.Equal(["/data"], at);
        }

        [Fact]
        public async Task Links_are_checked_and_kept()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclareAsync(client);
            var (status, _, pointers) = await ProblemAsync(await client.PostAsJsonAsync(SendUrl, Send(type, links: new() { ["self"] = "ftp://example.com/x" }), Ct));
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal(["/links/self"], pointers);

            var accepted = await (await client.PostAsJsonAsync(SendUrl, Send(type, links: new() { ["self"] = "https://example.com/x" }), Ct))
                .Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            var detail = await client.GetFromJsonAsync<NotificationResponse>($"{SendUrl}/{accepted!.Id}", Ct);
            Assert.Equal("https://example.com/x", detail!.Links["self"]);
            var without = await (await client.PostAsJsonAsync(SendUrl, Send(type), Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            Assert.Empty((await client.GetFromJsonAsync<NotificationResponse>($"{SendUrl}/{without!.Id}", Ct))!.Links);
        }

        [Fact]
        public async Task A_type_without_correlation_takes_no_correlation_key()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var none = await DeclareAsync(client, correlation: "none");
            var (status, code, pointers) = await ProblemAsync(await client.PostAsJsonAsync(SendUrl, Send(none, correlationKey: "incident-1"), Ct));
            Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidRequest), (status, code));
            Assert.Equal(["/correlationKey"], pointers);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(SendUrl, Send(none), Ct)).StatusCode);
            var optional = await DeclareAsync(client);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(SendUrl, Send(optional, correlationKey: "incident-1"), Ct)).StatusCode);
        }
    }
}
