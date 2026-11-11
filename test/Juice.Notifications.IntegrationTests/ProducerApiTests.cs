using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>The real host with one producer: application <c>sample</c>, behind the local Keycloak's <c>sample-producer</c> client, linked to one tenant.</summary>
    public sealed class ProducerApp : IAsyncLifetime
    {
        public const string Key = "sample";
        public static readonly Guid Tenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000000a1");
        public static readonly Guid OtherTenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000000b2");

        public NotificationsApp App { get; } = new();

        public async ValueTask InitializeAsync()
        {
            await App.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            (await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(Key, "Sample", "sample-producer"))).EnsureSuccessStatusCode();
            (await admin.PutAsync($"/api/v1/applications/{Key}/tenants/{Tenant}", null)).EnsureSuccessStatusCode();
        }

        public async Task<HttpClient> ProducerAsync() => App.Client(await LocalStack.ProducerTokenAsync());

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>Declaring types and sending notifications (phase-1-plan M2), with a real producer token.</summary>
    public sealed class ProducerApiTests(ProducerApp producer) : IClassFixture<ProducerApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string TypesUrl = $"/api/v1/applications/{ProducerApp.Key}/types";
        private static readonly string SendUrl = $"/api/v1/tenants/{ProducerApp.Tenant}/notifications";

        private static string NewType() => $"{ProducerApp.Key}.t{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];

        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static readonly JsonElement SubjectSchema = Json("""
            { "type": "object", "required": ["subject"], "properties": { "subject": { "type": "string", "maxLength": 100 }, "count": { "type": "integer" } } }
            """);

        private static DeclareTypeBody Type(string key, JsonElement? schema = null, bool external = false, string? correlation = null, string? templates = null)
            => new(key, "A thing happened", "things", null, "high", false, external, schema ?? SubjectSchema, correlation, null, null,
                Json(templates ?? """{ "email": "Subject: {{ data.subject }}", "webhook": "passthrough" }"""));

        private static SendNotificationBody Send(string type, string? key = null, string data = """{ "subject": "hello" }""", int? version = null,
            List<RecipientBody>? recipients = null, string? correlationKey = null)
            => new(type, version, key ?? Guid.NewGuid().ToString("N"), correlationKey, recipients, new ActorBody("system", "tests"), null,
                new Dictionary<string, string> { ["env"] = "test" }, Json(data), new Dictionary<string, string> { ["self"] = "https://example.com/x" }, null);

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpResponseMessage, object> Problems = new();

        /// <summary>The problem body, read once per response, so a test can check both the code and the errors of one response.</summary>
        private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
        {
            if (!Problems.TryGetValue(response, out var body))
            {
                body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
                Problems.AddOrUpdate(response, body);
            }
            return (JsonElement)body;
        }

        /// <summary>Stored JSON compared by meaning: jsonb keeps its own formatting and key order.</summary>
        private static void SameJson(string expected, string actual)
            => Assert.True(JsonElement.DeepEquals(JsonElement.Parse(expected), JsonElement.Parse(actual)), $"Expected {expected}, stored {actual}");

        private static async Task<string> CodeAsync(HttpResponseMessage response) => (await ProblemAsync(response)).GetProperty("code").GetString()!;

        private static async Task<List<string>> PointersAsync(HttpResponseMessage response)
            => [.. (await ProblemAsync(response)).GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("pointer").GetString()!)];

        private async Task<string> DeclaredAsync(HttpClient client, DeclareTypeBody body)
        {
            var response = await client.PutAsJsonAsync(TypesUrl, body, Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            return body.Key!;
        }

        // Types

        [Fact]
        public async Task Declaring_the_same_type_returns_its_version_and_a_change_adds_one()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var key = NewType();

            var first = await client.PutAsJsonAsync(TypesUrl, Type(key), Ct);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal($"{TypesUrl}/{key}/versions/1", first.Headers.Location?.ToString());

            // The same declaration, its JSON written in another order, is the same version.
            var reordered = Type(key, Json("""
                { "properties": { "count": { "type": "integer" }, "subject": { "maxLength": 100, "type": "string" } }, "required": ["subject"], "type": "object" }
                """));
            var again = await client.PutAsJsonAsync(TypesUrl, reordered, Ct);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(1, (await again.Content.ReadFromJsonAsync<TypeVersionResponse>(Ct))!.Version);

            var changed = await client.PutAsJsonAsync(TypesUrl, Type(key, templates: """{ "email": "Changed: {{ data.subject }}" }"""), Ct);
            Assert.Equal(HttpStatusCode.Created, changed.StatusCode);
            Assert.Equal(2, (await changed.Content.ReadFromJsonAsync<TypeVersionResponse>(Ct))!.Version);

            var versions = await client.GetFromJsonAsync<List<TypeVersionResponse>>($"{TypesUrl}/{key}/versions", Ct);
            Assert.Equal([1, 2], versions!.Select(v => v.Version));
            var v1 = await client.GetFromJsonAsync<TypeVersionDetailResponse>($"{TypesUrl}/{key}/versions/1", Ct);
            Assert.Equal("passthrough", v1!.Templates["webhook"]["en"]);
            Assert.Equal("Subject: {{ data.subject }}", v1.Templates["email"]["en"]);
            Assert.Equal(NotificationPriority.High, v1.Priority);
            Assert.Equal(CorrelationRules.Optional, v1.Correlation);
        }

        [Fact]
        public async Task Declaring_the_same_new_type_at_once_gives_everyone_version_1()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var key = NewType();

            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PutAsJsonAsync(TypesUrl, Type(key), Ct)));

            Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
            foreach (var r in responses)
            {
                Assert.Equal(1, (await r.Content.ReadFromJsonAsync<TypeVersionResponse>(Ct))!.Version);
            }
            await using var db = producer.App.Database.Context();
            Assert.Equal(1, await db.Types.CountAsync(t => t.Key == key, Ct));
        }

        public static TheoryData<string, string, string> BadDeclarations => new()
        {
            { "key outside the namespace", """{ "key": "other.thing" }""", "/key" },
            { "unknown channel", """{ "templates": { "slack": "x" } }""", "/templates/slack" },
            { "passthrough on email", """{ "templates": { "email": "passthrough" } }""", "/templates/email" },
            { "template that doesn't parse", """{ "templates": { "email": "{{ if }}" } }""", "/templates/email" },
            { "locale without en", """{ "templates": { "email": { "vi": "x" } } }""", "/templates/email" },
            { "schema that isn't a schema", """{ "schema": { "type": 5 } }""", "/schema" },
            { "schema from another draft", """{ "schema": { "$schema": "http://json-schema.org/draft-07/schema#" } }""", "/schema/$schema" },
            { "a $ref to another document", """{ "schema": { "$ref": "https://example.com/schemas/x.json" } }""", "/schema/$ref" },
            { "a workflow phase 1 doesn't run", """{ "workflow": [ { "step": "digest" } ] }""", "/workflow" },
            { "unknown priority", """{ "priority": "urgent" }""", "/priority" },
            { "bad redaction path", """{ "redact": [ "subject" ] }""", "/redact/0" },
            { "a locale tag too long to store", """{ "templates": { "email": { "en": "x", "en-aaaaaaaa-bbbbbbbb-cccccccc-dddddddd": "y" } } }""", "/templates/email/en-aaaaaaaa-bbbbbbbb-cccccccc-dddddddd" },
            { "a $schema that isn't a string", """{ "schema": { "$schema": 2020 } }""", "/schema/$schema" },
            { "a pattern that isn't a regex", """{ "schema": { "properties": { "a": { "pattern": "[" } } } }""", "/schema" },
            { "locales that differ only in case", """{ "templates": { "email": { "en": "x", "pt-BR": "y", "pt-br": "z" } } }""", "/templates/email/pt-br" },
            { "a locale with a trailing newline", """{ "templates": { "email": { "en": "x", "vi\n": "y" } } }""", "/templates/email/vi\n" },
        };

        [Theory]
        [MemberData(nameof(BadDeclarations))]
        public async Task A_bad_declaration_is_refused_with_the_field(string because, string change, string pointer)
        {
            LocalStack.RequireAvailable();
            _ = because;
            using var client = await producer.ProducerAsync();
            var good = JsonSerializer.SerializeToNode(Type(NewType()), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            foreach (var (name, value) in System.Text.Json.Nodes.JsonNode.Parse(change)!.AsObject())
            {
                good[name] = value?.DeepClone();
            }

            var response = await client.PutAsJsonAsync(TypesUrl, good, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(ErrorCodes.InvalidType, await CodeAsync(response));
            Assert.Contains(pointer, await PointersAsync(response));
        }

        [Fact]
        public async Task Only_the_application_itself_declares_its_types()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var other = await client.PutAsJsonAsync("/api/v1/applications/other/types", Type("other.thing"), Ct);
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotYourApplication), (other.StatusCode, await CodeAsync(other)));

            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            var byAdmin = await admin.PutAsJsonAsync(TypesUrl, Type(NewType()), Ct);
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotAProducer), (byAdmin.StatusCode, await CodeAsync(byAdmin)));
            // A platform admin can read them.
            var key = await DeclaredAsync(client, Type(NewType()));
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{TypesUrl}/{key}/versions", Ct)).StatusCode);
        }

        // Sending

        [Fact]
        public async Task An_accepted_request_is_stored_with_its_notification_due_for_planning()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclaredAsync(client, Type(NewType()));

            var response = await client.PostAsJsonAsync(SendUrl, Send(type), Ct);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var accepted = await response.Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            Assert.Equal(("accepted", type, 1), (accepted!.Status, accepted.Type, accepted.TypeVersion));
            Assert.Equal($"{SendUrl}/{accepted.Id}", response.Headers.Location?.ToString());

            var detail = await client.GetFromJsonAsync<NotificationResponse>($"{SendUrl}/{accepted.Id}", Ct);
            // The planner runs in the host, so the notification may already be planned.
            Assert.Contains(detail!.State, new[] { NotificationState.Planning, NotificationState.Planned });
            Assert.Equal("system:tests", detail.Actor);
            Assert.Equal(NotificationPriority.High, detail.Priority);
            Assert.Empty(detail.Deliveries);

            await using var db = producer.App.Database.Context();
            var notification = await db.Notifications.SingleAsync(n => n.RequestId == accepted.Id, Ct);
            SameJson("""[{"kind":"route"}]""", (await db.Requests.SingleAsync(r => r.Id == accepted.Id, Ct)).Recipients);
        }

        [Fact]
        public async Task The_same_idempotency_key_returns_the_original()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclaredAsync(client, Type(NewType()));
            var key = Guid.NewGuid().ToString("N");

            var first = await (await client.PostAsJsonAsync(SendUrl, Send(type, key), Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            var again = await client.PostAsJsonAsync(SendUrl, Send(type, key, """{ "subject": "something else" }"""), Ct);

            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(first!.Id, (await again.Content.ReadFromJsonAsync<AcceptedResponse>(Ct))!.Id);
        }

        /// <summary>Proof 2 (design §16): a duplicate request creates no new work, even sent 20 times at once.</summary>
        [Fact]
        public async Task Concurrent_identical_requests_make_one_request_and_one_notification()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclaredAsync(client, Type(NewType()));
            var key = Guid.NewGuid().ToString("N");

            var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.PostAsJsonAsync(SendUrl, Send(type, key), Ct)));

            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
            Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, r.StatusCode.ToString()));
            var ids = new HashSet<Guid>();
            foreach (var r in responses)
            {
                ids.Add((await r.Content.ReadFromJsonAsync<AcceptedResponse>(Ct))!.Id);
            }
            Assert.Single(ids);
            await using var db = producer.App.Database.Context();
            Assert.Equal(1, await db.Requests.CountAsync(r => r.IdempotencyKey == key, Ct));
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.RequestId == ids.Single(), Ct));
        }

        [Fact]
        public async Task Data_that_fails_the_schema_is_refused_with_pointers()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclaredAsync(client, Type(NewType()));

            var response = await client.PostAsJsonAsync(SendUrl, Send(type, data: """{ "subject": 5, "count": "many" }"""), Ct);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal(ErrorCodes.InvalidData, await CodeAsync(response));
            var pointers = await PointersAsync(response);
            Assert.Contains("/data/subject", pointers);
            Assert.Contains("/data/count", pointers);
        }

        [Fact]
        public async Task The_type_version_is_pinned()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclaredAsync(client, Type(NewType()));
            await DeclaredAsync(client, Type(type, Json("""{ "type": "object", "required": ["subject", "count"] }""")));

            // Version 1 doesn't need a count; the latest (2) does.
            var pinned = await client.PostAsJsonAsync(SendUrl, Send(type, version: 1), Ct);
            Assert.Equal(HttpStatusCode.Accepted, pinned.StatusCode);
            var pinnedBody = await pinned.Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            Assert.Equal(1, pinnedBody!.TypeVersion);
            var latest = await client.PostAsJsonAsync(SendUrl, Send(type), Ct);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, latest.StatusCode);
            var unknown = await client.PostAsJsonAsync(SendUrl, Send(type, version: 9), Ct);
            Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.UnknownType), (unknown.StatusCode, await CodeAsync(unknown)));

            await using var db = producer.App.Database.Context();
            var stored = await db.Requests.SingleAsync(r => r.Id == pinnedBody.Id, Ct);
            var v1 = await db.Types.SingleAsync(t => t.Key == type && t.Version == 1, Ct);
            Assert.Equal((v1.Id, v1.SchemaHash), (stored.TypeId, stored.SchemaHash));
        }

        [Fact]
        public async Task An_application_sends_only_its_own_types_to_its_own_tenants()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclaredAsync(client, Type(NewType()));

            var unlinked = await client.PostAsJsonAsync($"/api/v1/tenants/{ProducerApp.OtherTenant}/notifications", Send(type), Ct);
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.TenantNotLinked), (unlinked.StatusCode, await CodeAsync(unlinked)));
            var foreign = await client.PostAsJsonAsync(SendUrl, Send("other.thing"), Ct);
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.TypeNotOwned), (foreign.StatusCode, await CodeAsync(foreign)));
            var undeclared = await client.PostAsJsonAsync(SendUrl, Send(ProducerApp.Key + ".never.declared"), Ct);
            Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.UnknownType), (undeclared.StatusCode, await CodeAsync(undeclared)));

            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            var byAdmin = await admin.PostAsJsonAsync(SendUrl, Send(type), Ct);
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotAProducer), (byAdmin.StatusCode, await CodeAsync(byAdmin)));
        }

        [Fact]
        public async Task External_recipients_need_a_type_that_allows_them_and_a_real_address()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var closed = await DeclaredAsync(client, Type(NewType()));
            var open = await DeclaredAsync(client, Type(NewType(), external: true));

            var refused = await client.PostAsJsonAsync(SendUrl, Send(closed, recipients: [new("address", "email", "ops@example.com")]), Ct);
            Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.RecipientNotAllowed), (refused.StatusCode, await CodeAsync(refused)));
            var later = await client.PostAsJsonAsync(SendUrl, Send(open, recipients: [new("group", null, null)]), Ct);
            Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.RecipientNotAllowed), (later.StatusCode, await CodeAsync(later)));
            var malformed = await client.PostAsJsonAsync(SendUrl, Send(open, recipients: [new("address", "email", "Ops <ops@example.com>")]), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            Assert.Contains("/recipients/0/address", await PointersAsync(malformed));
            var webhook = await client.PostAsJsonAsync(SendUrl, Send(open, recipients: [new("address", "webhook", "https://example.com/hook")]), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, webhook.StatusCode);

            var accepted = await client.PostAsJsonAsync(SendUrl, Send(open, recipients: [new("address", "email", "Ops@Example.com"), new("route")]), Ct);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            await using var db = producer.App.Database.Context();
            var id = (await accepted.Content.ReadFromJsonAsync<AcceptedResponse>(Ct))!.Id;
            SameJson("""[{"kind":"address","channel":"email","address":"ops@example.com"},{"kind":"route"}]""",
                (await db.Requests.SingleAsync(r => r.Id == id, Ct)).Recipients);
        }

        [Fact]
        public async Task A_type_that_requires_a_correlation_key_refuses_a_request_without_one()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclaredAsync(client, Type(NewType(), correlation: CorrelationRules.Required));

            var missing = await client.PostAsJsonAsync(SendUrl, Send(type), Ct);
            // Well-formed, refused by the type's rule: 422, like a recipient the type doesn't allow.
            Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.InvalidRequest), (missing.StatusCode, await CodeAsync(missing)));
            Assert.Contains("/correlationKey", await PointersAsync(missing));
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(SendUrl, Send(type, correlationKey: "alert:1"), Ct)).StatusCode);
        }

        [Fact]
        public async Task A_disabled_application_can_neither_declare_nor_send()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            var type = await DeclaredAsync(client, Type(NewType()));
            await admin.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}", new UpdateApplicationBody(null, null, false), Ct);
            try
            {
                var send = await client.PostAsJsonAsync(SendUrl, Send(type), Ct);
                Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.ApplicationDisabled), (send.StatusCode, await CodeAsync(send)));
                var declare = await client.PutAsJsonAsync(TypesUrl, Type(NewType()), Ct);
                Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.ApplicationDisabled), (declare.StatusCode, await CodeAsync(declare)));
            }
            finally
            {
                await admin.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}", new UpdateApplicationBody(null, null, true), Ct);
            }
        }

        /// <summary>A key with a trailing newline is not the same key without it, so it is refused rather than stored as a near-duplicate.</summary>
        [Fact]
        public async Task Keys_with_a_trailing_newline_are_refused()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclaredAsync(client, Type(NewType()));
            var idempotency = await client.PostAsJsonAsync(SendUrl, Send(type, key: "k-" + Guid.NewGuid().ToString("N") + "\n"), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, idempotency.StatusCode);
            Assert.Contains("/idempotencyKey", await PointersAsync(idempotency));
            var correlation = await client.PostAsJsonAsync(SendUrl, Send(type, correlationKey: "alert:1\n"), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, correlation.StatusCode);
            Assert.Contains("/correlationKey", await PointersAsync(correlation));
        }

        [Fact]
        public async Task Locales_are_stored_lowercase()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var key = await DeclaredAsync(client, Type(NewType(), templates: """{ "email": { "EN": "x", "pt-BR": "y" } }"""));
            var v1 = await client.GetFromJsonAsync<TypeVersionDetailResponse>($"{TypesUrl}/{key}/versions/1", Ct);
            Assert.Equal(["en", "pt-br"], v1!.Templates["email"].Keys.Order());
        }

        [Fact]
        public async Task A_request_without_a_type_is_malformed_not_forbidden()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var response = await client.PostAsJsonAsync(SendUrl, Send(null!), Ct);
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest), (response.StatusCode, await CodeAsync(response)));
            Assert.Contains("/type", await PointersAsync(response));
        }

        /// <summary>JSON that repeats a name means different things to different readers, so it is refused, in data and in declarations.</summary>
        [Fact]
        public async Task Repeated_property_names_are_refused()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclaredAsync(client, Type(NewType()));
            var body = $$"""
                { "type": "{{type}}", "idempotencyKey": "{{Guid.NewGuid():N}}", "data": { "subject": "a", "subject": "b" } }
                """;
            var send = await client.PostAsync(SendUrl, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, send.StatusCode);
            Assert.Contains("/data/subject", await PointersAsync(send));

            var declaration = $$"""
                { "key": "{{NewType()}}", "name": "n", "category": "c", "schema": { "type": "object", "type": "string" } }
                """;
            var declare = await client.PutAsync(TypesUrl, new StringContent(declaration, System.Text.Encoding.UTF8, "application/json"), Ct);
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidType), (declare.StatusCode, await CodeAsync(declare)));
            Assert.Contains("/schema/type", await PointersAsync(declare));
        }

        [Fact]
        public async Task A_producer_reads_only_its_own_notifications()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{SendUrl}/{Guid.CreateVersion7()}", Ct)).StatusCode);
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            var type = await DeclaredAsync(client, Type(NewType()));
            var id = (await (await client.PostAsJsonAsync(SendUrl, Send(type), Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct))!.Id;
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"{SendUrl}/{id}", Ct)).StatusCode);
        }
    }
}
