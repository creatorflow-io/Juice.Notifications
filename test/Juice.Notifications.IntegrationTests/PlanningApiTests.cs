using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>A tenant's configuration (phase-1-plan M4) and the planner running in the real host: a request becomes recipients.</summary>
    public sealed class PlanningApiTests(ProducerApp producer) : IClassFixture<ProducerApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string Tenant = $"/api/v1/tenants/{ProducerApp.Tenant}";
        private static readonly string SendUrl = $"{Tenant}/notifications";

        private static JsonElement Json(string json) => JsonElement.Parse(json);

        /// <summary>The problem's code; a response without one fails with its status and body, so the cause shows.</summary>
        private static async Task<string> CodeAsync(HttpResponseMessage r)
        {
            var body = await r.Content.ReadAsStringAsync(Ct);
            using var json = body.Length > 0 ? JsonDocument.Parse(body) : null;
            return json is not null && json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("code", out var code)
                ? code.GetString()!
                : throw new Xunit.Sdk.XunitException($"{(int)r.StatusCode} {r.StatusCode} without a problem code: {body}");
        }

        private async Task<HttpClient> AdminAsync() => producer.App.Client(await LocalStack.AdminTokenAsync());

        private static async Task Ok(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        }

        private async Task<string> DeclareAsync(HttpClient client, bool external = false)
        {
            var key = $"{ProducerApp.Key}.p{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            await Ok(client.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types",
                new DeclareTypeBody(key, "Planned", "things", null, null, null, external, Json("""{ "type": "object" }"""), null, null, null,
                    Json("""{ "email": "Hello" }""")), Ct));
            return key;
        }

        /// <summary>Sends, then waits for the planner (running in the host) to finish with it.</summary>
        private async Task<(Guid Id, NotificationResponse Detail)> SendAndPlanAsync(HttpClient client, string type, Dictionary<string, string>? labels = null,
            List<RecipientBody>? recipients = null)
        {
            var body = new SendNotificationBody(type, null, Guid.NewGuid().ToString("N"), null, recipients, null, null, labels, Json("{}"), null, null);
            var accepted = await (await client.PostAsJsonAsync(SendUrl, body, Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            for (var waited = 0; waited < 100; waited++)
            {
                var detail = await client.GetFromJsonAsync<NotificationResponse>($"{SendUrl}/{accepted!.Id}", Ct);
                if (detail!.State != NotificationState.Planning)
                {
                    return (accepted.Id, detail);
                }
                await Task.Delay(100, Ct);
            }
            throw new TimeoutException("The planner never planned the notification.");
        }

        private async Task<List<string>> NamedAsync(Guid requestId)
        {
            await using var db = producer.App.Database.Context();
            var notification = await db.Notifications.SingleAsync(n => n.RequestId == requestId, Ct);
            return await db.Recipients.Where(r => r.NotificationId == notification.Id).OrderBy(r => r.NamedAs).Select(r => r.NamedAs).ToListAsync(Ct);
        }

        private async Task ConfigureAsync(HttpClient admin, string route, string match)
        {
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/mail", new PutIntegrationBody("email", "smtp",
                Json("""{ "server": "local", "from": "notifications@juice.local" }"""), null, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/mail/targets/ops", new PutTargetBody("Ops@Example.com", null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/routes/{route}", new PutRouteBody(Json(match), ["ops"], null), Ct));
        }

        [Fact]
        public async Task A_request_is_planned_into_one_recipient_per_matching_route()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();
            using var client = await producer.ProducerAsync();
            var type = await DeclareAsync(client);
            await ConfigureAsync(admin, "prod-" + type.Split('.')[1], $$"""{ "type": "{{type}}", "labels": { "env": "prod" } }""");

            var (prodId, prod) = await SendAndPlanAsync(client, type, new() { ["env"] = "prod" });
            var (testId, test) = await SendAndPlanAsync(client, type, new() { ["env"] = "test" });

            Assert.Equal(NotificationState.Planned, prod.State);
            Assert.Equal(["route:prod-" + type.Split('.')[1]], await NamedAsync(prodId));
            Assert.Equal(NotificationState.Planned, test.State);
            Assert.Empty(await NamedAsync(testId));
        }

        [Fact]
        public async Task Named_addresses_are_planned_alongside_routes_and_a_route_never_removes_them()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();
            using var client = await producer.ProducerAsync();
            var type = await DeclareAsync(client, external: true);
            await ConfigureAsync(admin, "all-" + type.Split('.')[1], $$"""{ "type": "{{type}}" }""");

            var (id, _) = await SendAndPlanAsync(client, type, recipients: [new("address", "email", "a@example.com"), new("route")]);
            var (onlyAddress, _) = await SendAndPlanAsync(client, type, recipients: [new("address", "email", "b@example.com")]);

            Assert.Equal(["address:email:a@example.com", "route:all-" + type.Split('.')[1]], await NamedAsync(id));
            Assert.Equal(["address:email:b@example.com"], await NamedAsync(onlyAddress));
        }

        [Fact]
        public async Task The_configuration_api_checks_what_it_stores()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();
            // Another tenant: this test's catch-all route mustn't reach the other tests' notifications.
            var Tenant = $"/api/v1/tenants/{ProducerApp.OtherTenant}";

            var badKind = await admin.PutAsJsonAsync($"{Tenant}/integrations/x", new PutIntegrationBody("email", "webhook", null, null, null, null), Ct);
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidConfiguration), (badKind.StatusCode, await CodeAsync(badKind)));
            var secret = await admin.PutAsJsonAsync($"{Tenant}/integrations/x", new PutIntegrationBody("email", "smtp", null, "password123", null, null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, secret.StatusCode);
            // A host variable outside the deployment's prefix can't be named: it could be sent somewhere as a password or a signing key.
            var hostSecret = await admin.PutAsJsonAsync($"{Tenant}/integrations/x", new PutIntegrationBody("webhook", "webhook", null, "env:POSTGRES_PASSWORD", null, null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, hostSecret.StatusCode);
            var noServer = await admin.PutAsJsonAsync($"{Tenant}/integrations/x", new PutIntegrationBody("email", "smtp", Json("""{ "server": "elsewhere", "from": "a@b.c" }"""), null, null, null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, noServer.StatusCode);
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/hooks", new PutIntegrationBody("webhook", "webhook", null, "env:NOTIFICATIONS_SECRET_HOOK", null, null), Ct));
            var channelChange = await admin.PutAsJsonAsync($"{Tenant}/integrations/hooks", new PutIntegrationBody("email", "smtp", null, null, null, null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, channelChange.StatusCode);

            var badUrl = await admin.PutAsJsonAsync($"{Tenant}/integrations/hooks/targets/t1", new PutTargetBody("ftp://example.com", null, null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/hooks/targets/t1", new PutTargetBody("https://hooks.example.com/x", null, null), Ct));
            var unknownTarget = await admin.PutAsJsonAsync($"{Tenant}/routes/r1", new PutRouteBody(Json("{}"), ["t1", "nope"], null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, unknownTarget.StatusCode);
            var badMatch = await admin.PutAsJsonAsync($"{Tenant}/routes/r1", new PutRouteBody(Json("""{ "labels": { "a": "=~(" } }"""), ["t1"], null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, badMatch.StatusCode);
            await Ok(admin.PutAsJsonAsync($"{Tenant}/routes/r1", new PutRouteBody(Json("{}"), ["t1"], null), Ct));

            await Ok(admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody("10.20.0.0/16", [8080, 443]), Ct));
            var allowlist = await admin.GetFromJsonAsync<List<AllowlistResponse>>($"{Tenant}/allowlist", Ct);
            Assert.Contains(allowlist!, a => a.Destination == "10.20.0.0/16" && a.Ports.SequenceEqual([443, 8080]));
            var badCidr = await admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody("10.20.0.0/40", null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, badCidr.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Tenant}/allowlist?destination=10.20.0.0%2F16", Ct)).StatusCode);

            var integrations = await admin.GetFromJsonAsync<List<IntegrationResponse>>($"{Tenant}/integrations", Ct);
            Assert.Contains(integrations!, i => i.Key == "hooks" && i.Targets.Any(t => t.Key == "t1" && t.Address == "https://hooks.example.com/x"));
        }

        [Fact]
        public async Task Rows_from_the_deployments_configuration_refuse_api_changes()
        {
            LocalStack.RequireAvailable();
            var Tenant = $"/api/v1/tenants/{ProducerApp.OtherTenant}"; // its catch-all route stays away from the other tests
            await using (var db = producer.App.Database.Context())
            {
                db.Routes.Add(new Route
                {
                    TenantId = ProducerApp.OtherTenant, Key = "from-config", Match = "{}", Targets = ["ops"], ManagedBy = ManagedBy.Configuration, DefinitionHash = "h",
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(Ct);
            }
            using var admin = await AdminAsync();

            var change = await admin.PutAsJsonAsync($"{Tenant}/routes/from-config", new PutRouteBody(null, null, false), Ct);
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ManagedByConfiguration), (change.StatusCode, await CodeAsync(change)));
            var delete = await admin.DeleteAsync($"{Tenant}/routes/from-config", Ct);
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ManagedByConfiguration), (delete.StatusCode, await CodeAsync(delete)));
        }

        /// <summary>
        /// A tenant admin configures the one tenant its token names (ITenantAccess, owner's decision 11 Nov); another tenant, an unknown one and a
        /// producer all get the same 403.
        /// </summary>
        [Fact]
        public async Task A_tenant_admin_configures_only_its_own_tenant()
        {
            LocalStack.RequireAvailable();
            using var tenantAdmin = producer.App.Client(await LocalStack.TenantAdminTokenAsync());

            await Ok(tenantAdmin.PutAsJsonAsync($"{Tenant}/integrations/own-hooks", new PutIntegrationBody("webhook", "webhook", null, null, null, null), Ct));
            Assert.Equal(HttpStatusCode.OK, (await tenantAdmin.GetAsync($"{Tenant}/integrations", Ct)).StatusCode);

            foreach (var other in new[] { ProducerApp.OtherTenant, Guid.CreateVersion7() })
            {
                var refused = await tenantAdmin.PutAsJsonAsync($"/api/v1/tenants/{other}/integrations/x", new PutIntegrationBody("webhook", "webhook", null, null, null, null), Ct);
                Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotTenantAdmin), (refused.StatusCode, await CodeAsync(refused)));
                Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync($"/api/v1/tenants/{other}/routes", Ct)).StatusCode);
            }

            using var client = await producer.ProducerAsync();
            var byProducer = await client.GetAsync($"{Tenant}/routes", Ct);
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotTenantAdmin), (byProducer.StatusCode, await CodeAsync(byProducer)));
            // Not a platform admin: applications stay out of its reach.
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync("/api/v1/applications", Ct)).StatusCode);
        }
    }
}
