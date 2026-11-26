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
        /// When its event happened (phase 5, M45-n): absent, it is the acceptance time; a few minutes ahead of the service's clock (skew) or up to
        /// 7 days old is taken; further ahead, or older, is refused at the field.
        /// </summary>
        [Fact]
        public async Task An_occurrence_is_at_most_a_little_ahead_and_7_days_old_and_defaults_to_acceptance()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclareAsync(client);
            var now = DateTimeOffset.UtcNow;
            foreach (var taken in new DateTimeOffset?[] { null, now.AddMinutes(2), now.AddDays(-6.9), now.AddHours(-1) })
            {
                Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(SendUrl, Send(type, occurredAt: taken), Ct)).StatusCode);
            }
            foreach (var refused in new[] { now.AddMinutes(30), now.AddDays(-7.1) })
            {
                var (status, code, pointers) = await ProblemAsync(await client.PostAsJsonAsync(SendUrl, Send(type, occurredAt: refused), Ct));
                Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest), (status, code));
                Assert.Equal(["/occurredAt"], pointers);
            }

            // Kept as sent; absent, the acceptance time.
            var at = now.AddMinutes(-42);
            var sent = await (await client.PostAsJsonAsync(SendUrl, Send(type, occurredAt: at), Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            var plain = await (await client.PostAsJsonAsync(SendUrl, Send(type), Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            await using var db = producer.App.Database.Context();
            var stored = await db.Requests.AsNoTracking().SingleAsync(r => r.Id == sent!.Id, Ct);
            Assert.Equal(at.ToUnixTimeMilliseconds(), stored.OccurredAt!.Value.ToUnixTimeMilliseconds());
            var defaulted = await db.Requests.AsNoTracking().SingleAsync(r => r.Id == plain!.Id, Ct);
            Assert.Equal(defaulted.AcceptedAt, defaulted.OccurredAt);
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
