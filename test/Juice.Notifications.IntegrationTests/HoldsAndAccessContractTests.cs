using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// QA (M44-0, m44-architecture §3.3–§3.4): the wire shape of <c>GET /api/v1/access</c> and of the correlation view and list, read as raw
    /// JSON so casing, presence and nullability are what the Console and the other milestones see, not what a case-insensitive reader forgives.
    /// </summary>
    public sealed class HoldsAndAccessContractTests(InvestigationApp fixture) : IClassFixture<InvestigationApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid TestTenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000000a1");

        private static readonly string[] ServicePages =
        [
            "overview", "deliveries", "notifications", "correlations", "storms", "integrations", "routes", "mutes", "escalation-policies", "allowlist",
            "types", "display-groups", "subscribers", "groups", "topics", "resolvers", "settings", "applications",
        ];

        private static readonly string[] EmbeddedPages =
        [
            "overview", "deliveries", "notifications", "correlations", "storms", "integrations", "routes", "mutes", "escalation-policies", "allowlist",
            "types", "settings",
        ];

        private static async Task<JsonElement> JsonAsync(HttpClient client, string url)
        {
            using var response = await client.GetAsync(url, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode} {body}");
            return JsonElement.Parse(body);
        }

        private static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

        private static void AssertAccessShape(JsonElement access)
        {
            Assert.Equal(["platformAdmin", "allTenants", "tenants", "mode", "pages"], Names(access));
            Assert.Equal("service", access.GetProperty("mode").GetString());
            Assert.Equal(ServicePages, access.GetProperty("pages").EnumerateArray().Select(p => p.GetString()!).ToArray());
            foreach (var tenant in access.GetProperty("tenants").EnumerateArray())
            {
                Assert.Equal(["id", "name", "rights"], Names(tenant));
                Assert.Equal(JsonValueKind.Null, tenant.GetProperty("name").ValueKind);
                Assert.Equal(["read", "operate", "configure"], tenant.GetProperty("rights").EnumerateArray().Select(r => r.GetString()!).ToArray());
            }
        }

        [Fact]
        public void The_page_ids_are_the_designs_in_menu_order()
        {
            Assert.Equal(ServicePages, AdminPages.Service);
            Assert.Equal(EmbeddedPages, AdminPages.Embedded);
            // Embedded is the service less people, display groups and applications, in the same order.
            Assert.Equal(AdminPages.Service.Except(["display-groups", "subscribers", "groups", "topics", "resolvers", "applications"]), AdminPages.Embedded);
            Assert.Equal(["read", "operate", "configure"], AccessRights.All);
            Assert.Equal(("service", "embedded"), (AccessModes.Service, AccessModes.Embedded));
        }

        [Fact]
        public async Task Access_is_camel_case_with_mode_pages_and_rights_for_a_tenant_admin()
        {
            LocalStack.RequireAvailable();
            using var client = fixture.App.Client(await LocalStack.TenantAdminTokenAsync());
            var access = await JsonAsync(client, "/api/v1/access");
            AssertAccessShape(access);
            Assert.False(access.GetProperty("platformAdmin").GetBoolean());
            Assert.Equal(TestTenant, access.GetProperty("tenants").EnumerateArray().Single().GetProperty("id").GetGuid());
        }

        [Fact]
        public async Task Access_for_a_platform_admin_gives_every_listed_tenant_every_right()
        {
            LocalStack.RequireAvailable();
            await fixture.TenantAsync(); // at least one linked tenant
            using var client = fixture.App.Client(await LocalStack.AdminTokenAsync());
            var access = await JsonAsync(client, "/api/v1/access");
            AssertAccessShape(access);
            Assert.True(access.GetProperty("platformAdmin").GetBoolean());
            Assert.True(access.GetProperty("allTenants").GetBoolean());
            Assert.NotEmpty(access.GetProperty("tenants").EnumerateArray());
        }

        [Fact]
        public async Task Access_without_rights_is_200_with_no_tenants_and_still_carries_mode_and_pages()
        {
            LocalStack.RequireAvailable();
            using var client = fixture.App.Client(await LocalStack.PersonTokenAsync());
            var access = await JsonAsync(client, "/api/v1/access");
            AssertAccessShape(access);
            Assert.Empty(access.GetProperty("tenants").EnumerateArray());
        }

        [Fact]
        public async Task The_correlation_view_and_list_carry_the_hold_fields_null_when_not_held_and_set_when_held()
        {
            LocalStack.RequireAvailable();
            var (tenant, application, _) = await fixture.TenantAsync();
            var heldAt = new DateTimeOffset(2026, 11, 26, 8, 30, 15, 123, TimeSpan.Zero);
            // Ids are UUIDv7, which isn't ordered within a millisecond: give each its own.
            var plain = new Correlation { TenantId = tenant, ApplicationId = application.Id, Key = "qa/plain", State = CorrelationState.Open, CreatedAt = Seed.Now,
                LastRequestAt = Seed.Now, UpdatedAt = Seed.Now };
            await Task.Delay(5, Ct);
            var held = new Correlation { TenantId = tenant, ApplicationId = application.Id, Key = "qa/held", State = CorrelationState.Resolved, CreatedAt = Seed.Now,
                LastRequestAt = Seed.Now, UpdatedAt = Seed.Now, ResolvedAt = Seed.Now, Outcome = "fixed",
                // The hold is orthogonal to the state (§1.2): a resolved correlation may still be held.
                HeldAt = heldAt, HoldReason = "silence:7", HeldBy = "ops" };
            await Task.Delay(5, Ct);
            // The reason and who alone, with Unicode: stored as given.
            var unicode = new Correlation { TenantId = tenant, ApplicationId = application.Id, Key = "qa/unicode", State = CorrelationState.Acknowledged,
                CreatedAt = Seed.Now, LastRequestAt = Seed.Now, UpdatedAt = Seed.Now, AcknowledgedAt = Seed.Now, AcknowledgedBy = "ann",
                HeldAt = heldAt, HoldReason = "bảo trì:€", HeldBy = null };
            await using (var db = fixture.App.Database.Context())
            {
                db.AddRange(plain, held, unicode);
                await db.SaveChangesAsync(Ct);
            }
            using var admin = fixture.App.Client(await LocalStack.AdminTokenAsync());
            var url = $"/api/v1/tenants/{tenant}/correlations";

            var list = await JsonAsync(admin, url);
            Assert.Equal(["items", "next"], Names(list));
            var items = list.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(["qa/unicode", "qa/held", "qa/plain"], items.Select(i => i.GetProperty("key").GetString()!));
            Assert.All(items, i => Assert.Equal(
                ["id", "application", "key", "state", "acknowledgedBy", "acknowledgedAt", "resolvedAt", "outcome", "createdAt", "lastRequestAt", "heldAt"], Names(i)));
            Assert.Equal(JsonValueKind.Null, items[2].GetProperty("heldAt").ValueKind);
            Assert.Equal(heldAt, items[1].GetProperty("heldAt").GetDateTimeOffset());
            // The list has the start only: no reason or who (§3.3).
            Assert.False(items[1].TryGetProperty("holdReason", out _));
            Assert.Equal("resolved", items[1].GetProperty("state").GetString());

            string[] view =
            [
                "key", "state", "acknowledgedBy", "acknowledgedAt", "resolvedAt", "resolvedBy", "outcome", "reopenedAt", "createdAt", "lastRequestAt",
                "notifications", "recipients", "deliveries", "heldAt", "holdReason", "heldBy",
            ];
            var one = await JsonAsync(admin, $"{url}/{Uri.EscapeDataString(plain.Key)}?application={application.Key}");
            Assert.Equal(view, Names(one));
            Assert.All(["heldAt", "holdReason", "heldBy"], f => Assert.Equal(JsonValueKind.Null, one.GetProperty(f).ValueKind));

            var two = await JsonAsync(admin, $"{url}/{Uri.EscapeDataString(held.Key)}?application={application.Key}");
            Assert.Equal(view, Names(two));
            Assert.Equal((heldAt, "silence:7", "ops"),
                (two.GetProperty("heldAt").GetDateTimeOffset(), two.GetProperty("holdReason").GetString(), two.GetProperty("heldBy").GetString()));

            var three = await JsonAsync(admin, $"{url}/{Uri.EscapeDataString(unicode.Key)}?application={application.Key}");
            Assert.Equal(("bảo trì:€", JsonValueKind.Null), (three.GetProperty("holdReason").GetString(), three.GetProperty("heldBy").ValueKind));

            // The typed contract reads the same answer.
            var typed = await admin.GetFromJsonAsync<CorrelationResponse>($"{url}/{Uri.EscapeDataString(held.Key)}?application={application.Key}", Ct);
            Assert.Equal((heldAt, "silence:7", "ops"), (typed!.HeldAt, typed.HoldReason, typed.HeldBy));
        }

        [Fact]
        public async Task An_admin_action_on_a_held_correlation_leaves_the_hold_and_answers_with_it()
        {
            LocalStack.RequireAvailable();
            var (tenant, application, _) = await fixture.TenantAsync();
            var heldAt = new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
            var held = new Correlation { TenantId = tenant, ApplicationId = application.Id, Key = "qa-ack", State = CorrelationState.Open, CreatedAt = Seed.Now,
                LastRequestAt = Seed.Now, UpdatedAt = Seed.Now, HeldAt = heldAt, HoldReason = "silence:9", HeldBy = "ops" };
            await using (var db = fixture.App.Database.Context())
            {
                db.Add(held);
                await db.SaveChangesAsync(Ct);
            }
            using var admin = fixture.App.Client(await LocalStack.AdminTokenAsync());
            var url = $"/api/v1/tenants/{tenant}/correlations/{held.Key}";
            using var acknowledged = await admin.PostAsJsonAsync($"{url}/acknowledge?application={application.Key}", new AcknowledgeCorrelationBody(null), Ct);
            Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);
            var body = JsonElement.Parse(await acknowledged.Content.ReadAsStringAsync(Ct));
            Assert.Equal("acknowledged", body.GetProperty("state").GetString());
            Assert.Equal((heldAt, "silence:9", "ops"),
                (body.GetProperty("heldAt").GetDateTimeOffset(), body.GetProperty("holdReason").GetString(), body.GetProperty("heldBy").GetString()));
        }

        /// <summary>M44-0 maps nothing for holds (CorrelationHoldEndpoints is empty until M44-1): the routes don't exist yet.</summary>
        [Fact]
        public async Task The_hold_routes_are_not_mapped_before_M44_1()
        {
            LocalStack.RequireAvailable();
            using var producer = fixture.App.Client(await LocalStack.ProducerTokenAsync());
            var url = $"/api/v1/tenants/{ProducerApp.Tenant}/correlations/qa-none";
            using var hold = await producer.PostAsJsonAsync($"{url}/hold", new HoldCorrelationBody("silence:1", null), Ct);
            using var release = await producer.PostAsJsonAsync($"{url}/release", new ReleaseCorrelationBody(null), Ct);
            // 405, not 404: the path matches the GET route of a key, which takes every segment (a key may hold a '/'); no POST answers it.
            Assert.True(hold.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, hold.StatusCode.ToString());
            Assert.True(release.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, release.StatusCode.ToString());
        }
    }
}
