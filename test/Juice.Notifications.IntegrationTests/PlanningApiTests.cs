using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
            // Webhooks are always signed (deferred #13): one without a secret is refused, and a change that leaves the secret out keeps it.
            var unsigned = await admin.PutAsJsonAsync($"{Tenant}/integrations/x", new PutIntegrationBody("webhook", "webhook", null, null, null, null), Ct);
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidConfiguration), (unsigned.StatusCode, await CodeAsync(unsigned)));
            Assert.Contains("/secretRef", await unsigned.Content.ReadAsStringAsync(Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/hooks", new PutIntegrationBody("webhook", "webhook", null, "env:NOTIFICATIONS_SECRET_HOOK", null, null), Ct));
            var kept = await admin.PutAsJsonAsync($"{Tenant}/integrations/hooks", new PutIntegrationBody(null, null, null, null, 5, null), Ct);
            Assert.Equal("env:NOTIFICATIONS_SECRET_HOOK", (await kept.Content.ReadFromJsonAsync<IntegrationResponse>(Ct))!.SecretRef);
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
            // A network is stored as the send-time policy reads it (IPNetwork), in its canonical form: 10.20.1.5/16 is 10.20.0.0/16, one entry.
            var hostBits = await admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody("10.20.1.5/16", [9090]), Ct);
            Assert.Equal(HttpStatusCode.OK, hostBits.StatusCode);
            Assert.Equal("10.20.0.0/16", (await hostBits.Content.ReadFromJsonAsync<AllowlistResponse>(Ct))!.Destination);
            Assert.Single((await admin.GetFromJsonAsync<List<AllowlistResponse>>($"{Tenant}/allowlist", Ct))!, a => a.Destination.StartsWith("10.20.", StringComparison.Ordinal));
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Tenant}/allowlist?destination=10.20.0.0%2F16", Ct)).StatusCode);

            // PostgreSQL stores no NUL character: a field error, not a 500.
            var nulSettings = await admin.PutAsJsonAsync($"{Tenant}/integrations/x", new PutIntegrationBody("webhook", "webhook", Json("""{ "a": "b\u0000" }"""), null, null, null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, nulSettings.StatusCode);
            var nulMatch = await admin.PutAsJsonAsync($"{Tenant}/routes/r2", new PutRouteBody(Json("""{ "labels": { "env": "a\u0000" } }"""), ["t1"], null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, nulMatch.StatusCode);
            // Nor half a surrogate pair: sent as written (a serializer may refuse to write it).
            var halfMatch = await admin.PutAsync($"{Tenant}/routes/r2", new StringContent("""{ "match": { "labels": { "env": "a\ud800" } }, "targets": ["t1"] }""",
                System.Text.Encoding.UTF8, "application/json"), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, halfMatch.StatusCode);
            foreach (var settings in new[] { """{ "a": "b\ud800" }""", """{ "\ud800": 1 }""", "\"\\ud800\"" })
            {
                var halfSettings = await admin.PutAsync($"{Tenant}/integrations/x", new StringContent(
                    $$"""{ "channel": "webhook", "kind": "webhook", "settings": {{settings}} }""", System.Text.Encoding.UTF8, "application/json"), Ct);
                Assert.Equal(HttpStatusCode.BadRequest, halfSettings.StatusCode);
                var halfResolver = await admin.PutAsync($"{Tenant}/resolvers/x", new StringContent(
                    $$"""{ "kind": "local", "settings": {{settings}} }""", System.Text.Encoding.UTF8, "application/json"), Ct);
                Assert.Equal(HttpStatusCode.BadRequest, halfResolver.StatusCode);
            }

            var integrations = await admin.GetFromJsonAsync<List<IntegrationResponse>>($"{Tenant}/integrations", Ct);
            Assert.Contains(integrations!, i => i.Key == "hooks" && i.Targets.Any(t => t.Key == "t1" && t.Address == "https://hooks.example.com/x"));

            // A target a route names can't be deleted, alone or with its integration: the route would plan recipients nothing can reach.
            var targetInUse = await admin.DeleteAsync($"{Tenant}/integrations/hooks/targets/t1", Ct);
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.TargetInUse), (targetInUse.StatusCode, await CodeAsync(targetInUse)));
            var integrationInUse = await admin.DeleteAsync($"{Tenant}/integrations/hooks", Ct);
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.TargetInUse), (integrationInUse.StatusCode, await CodeAsync(integrationInUse)));
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Tenant}/routes/r1", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Tenant}/integrations/hooks/targets/t1", Ct)).StatusCode);
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
        /// QA (M46-0): the in-process owners keep to their own rows through the real handlers and transaction behavior: the API refuses a
        /// configuration row (<c>managed_by_configuration</c>), configuration takes over an API row, and each refused row keeps its owner and definition.
        /// </summary>
        [Fact]
        public async Task In_process_owners_refuse_each_others_rows_and_configuration_takes_over_the_apis()
        {
            LocalStack.RequireAvailable();
            var tenant = ProducerApp.OtherTenant;
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var (api, config, taken) = ($"own-api-{suffix}", $"own-cfg-{suffix}", $"own-taken-{suffix}");
            await using (var db = producer.App.Database.Context())
            {
                foreach (var (key, owner) in new[] { (api, ManagedBy.Api), (config, ManagedBy.Configuration), (taken, ManagedBy.Api) })
                {
                    db.Routes.Add(new Route { TenantId = tenant, Key = key, Match = "{}", Targets = [], ManagedBy = owner, DefinitionHash = "h", UpdatedAt = DateTimeOffset.UtcNow });
                    db.EscalationPolicies.Add(new EscalationPolicy
                    {
                        TenantId = tenant, Key = key, Steps = """[{"afterMinutes":5,"recipients":[{"kind":"route"}]}]""", ManagedBy = owner, DefinitionHash = "h",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                }
                await db.SaveChangesAsync(Ct);
            }
            var match = JsonDocument.Parse("""{"type":"x.y"}""").RootElement;
            var steps = JsonDocument.Parse("""[{"afterMinutes":7,"recipients":[{"kind":"route"}]}]""").RootElement;

            async Task<string?> SendAsync<T>(Juice.MediatR.IRequest<Notifications.Outcome<T>> command)
            {
                await using var scope = producer.App.Services.CreateAsyncScope();
                using var context = NotificationsMessageContext.Begin("test");
                var outcome = await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>().Send(command, Ct);
                return outcome!.Kind == Notifications.OutcomeKind.Ok ? null : outcome.Code;
            }
            var config_ = ManagedBy.Configuration;
            // The API refuses the configuration's rows; configuration takes over an API row, as the provisioning file always has.
            Assert.Equal(ErrorCodes.ManagedByConfiguration, await SendAsync(new Configuration.PutRoute(tenant, config, match, [], false)));
            Assert.Equal(ErrorCodes.ManagedByConfiguration, await SendAsync(new Configuration.DeleteRoute(tenant, config)));
            Assert.Equal(ErrorCodes.ManagedByConfiguration, await SendAsync(new Configuration.PutEscalationPolicy(tenant, config, steps, false)));
            Assert.Equal(ErrorCodes.ManagedByConfiguration, await SendAsync(new Configuration.DeleteEscalationPolicy(tenant, config)));
            Assert.Null(await SendAsync(new Configuration.PutRoute(tenant, taken, null, null, false) { ManagedBy = config_ }));
            Assert.Null(await SendAsync(new Configuration.PutRoute(tenant, api, null, null, false)));

            await using var check = producer.App.Database.Context();
            var routes = (await check.Routes.AsNoTracking().Where(r => r.TenantId == tenant && r.Key.EndsWith(suffix)).ToListAsync(Ct)).ToDictionary(r => r.Key);
            Assert.Equal((ManagedBy.Api, "{}"), (routes[api].ManagedBy, routes[api].Match));
            Assert.Equal((ManagedBy.Configuration, "{}"), (routes[config].ManagedBy, routes[config].Match));
            Assert.Equal((ManagedBy.Configuration, false), (routes[taken].ManagedBy, routes[taken].Enabled));
            Assert.True(await check.EscalationPolicies.AnyAsync(p => p.TenantId == tenant && p.Key == api && p.ManagedBy == ManagedBy.Api, Ct));
        }

        /// <summary>A route's digest window (phase-5 M46-1): 0–3600 seconds, kept when a Put leaves it out, and shown with the route.</summary>
        [Fact]
        public async Task A_routes_digest_window_is_checked_kept_and_shown()
        {
            LocalStack.RequireAvailable();
            var Tenant = $"/api/v1/tenants/{ProducerApp.OtherTenant}";
            using var admin = await AdminAsync();
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/digest-hooks", new PutIntegrationBody("webhook", "webhook", Json("{}"), "env:NOTIFICATIONS_SECRET_HOOKS", 0, true), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/digest-hooks/targets/digest-t1", new PutTargetBody("https://hooks.example.com/digest", null, true), Ct));

            foreach (var refused in new[] { -1, 3601 })
            {
                var response = await admin.PutAsJsonAsync($"{Tenant}/routes/digested", new PutRouteBody(Json("{}"), ["digest-t1"], true, refused), Ct);
                Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidConfiguration), (response.StatusCode, await CodeAsync(response)));
            }
            var created = await admin.PutAsJsonAsync($"{Tenant}/routes/digested", new PutRouteBody(Json("{}"), ["digest-t1"], true, 60), Ct);
            Assert.Equal(60, (await created.Content.ReadFromJsonAsync<RouteResponse>(Ct))!.DigestSeconds);
            var kept = await admin.PutAsJsonAsync($"{Tenant}/routes/digested", new PutRouteBody(null, null, false), Ct);
            Assert.Equal((false, 60), (await kept.Content.ReadFromJsonAsync<RouteResponse>(Ct)) is { } k ? (k.Enabled, k.DigestSeconds) : default);
            var cleared = await admin.PutAsJsonAsync($"{Tenant}/routes/digested", new PutRouteBody(null, null, null, 0), Ct);
            Assert.Equal(0, (await cleared.Content.ReadFromJsonAsync<RouteResponse>(Ct))!.DigestSeconds);
        }

        /// <summary>A body can't name an owner: <c>managedBy</c> is no field of the API's, and what it writes is the API's.</summary>
        [Fact]
        public async Task A_body_naming_managed_by_changes_no_owner()
        {
            LocalStack.RequireAvailable();
            var Tenant = $"/api/v1/tenants/{ProducerApp.OtherTenant}";
            using var admin = await AdminAsync();

            var put = await admin.PutAsync($"{Tenant}/routes/named-owner", new StringContent(
                """{ "match": {}, "targets": [], "enabled": false, "managedBy": "config" }""", System.Text.Encoding.UTF8, "application/json"), Ct);

            await using var db = producer.App.Database.Context();
            var route = await db.Routes.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == ProducerApp.OtherTenant && r.Key == "named-owner", Ct);
            if (put.IsSuccessStatusCode)
            {
                Assert.Equal(ManagedBy.Api, route!.ManagedBy);
            }
            else
            {
                Assert.Null(route);
            }
            Assert.False(await db.Routes.AnyAsync(r => r.TenantId == ProducerApp.OtherTenant && r.Key == "named-owner" && r.ManagedBy == ManagedBy.Configuration, Ct));
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

            await Ok(tenantAdmin.PutAsJsonAsync($"{Tenant}/integrations/own-hooks", new PutIntegrationBody("webhook", "webhook", null, TestHooks.SecretRef, null, null), Ct));
            Assert.Equal(HttpStatusCode.OK, (await tenantAdmin.GetAsync($"{Tenant}/integrations", Ct)).StatusCode);

            // Another tenant than its token's is refused before the endpoint (the tenant pattern), the same for an unknown one.
            foreach (var other in new[] { ProducerApp.OtherTenant, Guid.CreateVersion7() })
            {
                var refused = await tenantAdmin.PutAsJsonAsync($"/api/v1/tenants/{other}/integrations/x", new PutIntegrationBody("webhook", "webhook", null, TestHooks.SecretRef, null, null), Ct);
                Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.TenantForbidden), (refused.StatusCode, await CodeAsync(refused)));
                Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync($"/api/v1/tenants/{other}/routes", Ct)).StatusCode);
            }

            using var client = await producer.ProducerAsync();
            var byProducer = await client.GetAsync($"{Tenant}/routes", Ct);
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotTenantAdmin), (byProducer.StatusCode, await CodeAsync(byProducer)));
            // Not a platform admin: applications stay out of its reach.
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync("/api/v1/applications", Ct)).StatusCode);
        }

        /// <summary>
        /// The Console's integration form (phase-4c-plan M38) offers what the deployment can send through: each channel's provider kinds, once
        /// each, in order, which sign, and never the inbox (the service's own). For the tenant's admins only.
        /// </summary>
        [Fact]
        public async Task The_providers_list_names_what_the_deployment_can_send_through()
        {
            LocalStack.RequireAvailable();
            using var tenantAdmin = producer.App.Client(await LocalStack.TenantAdminTokenAsync());

            var providers = await tenantAdmin.GetFromJsonAsync<List<ProviderResponse>>($"{Tenant}/providers", Ct);
            Assert.Contains(new ProviderResponse("email", "smtp", false, false), providers!);
            Assert.Contains(new ProviderResponse("webhook", "webhook", true, false), providers!);
            Assert.DoesNotContain(providers!, p => p.Channel == "inbox");
            var keys = providers!.Select(p => $"{p.Channel}/{p.Kind}").ToList();
            Assert.Equal(keys.Distinct().Order(StringComparer.Ordinal), keys);

            using var client = await producer.ProducerAsync();
            var byProducer = await client.GetAsync($"{Tenant}/providers", Ct);
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotTenantAdmin), (byProducer.StatusCode, await CodeAsync(byProducer)));
        }

        /// <summary>
        /// <c>GET …/resolver-kinds</c> (phase-4c-plan M40): what a tenant's resolver can be here and the recipient kinds each handles; never the
        /// built-in route and address. For the tenant's admins only.
        /// </summary>
        [Fact]
        public async Task The_resolver_kinds_name_what_a_tenants_resolver_can_be()
        {
            LocalStack.RequireAvailable();
            using var tenantAdmin = producer.App.Client(await LocalStack.TenantAdminTokenAsync());

            var kinds = (await tenantAdmin.GetFromJsonAsync<List<ResolverKindResponse>>($"{Tenant}/resolver-kinds", Ct))!;
            Assert.Equal(["group", "user"], Assert.Single(kinds, k => k.Kind == "local").Handles);
            Assert.Equal(["topic"], Assert.Single(kinds, k => k.Kind == "topic").Handles);
            Assert.Equal(["group", "role", "user"], Assert.Single(kinds, k => k.Kind == "identity").Handles);
            Assert.DoesNotContain(kinds, k => k.Kind is "route" or "address");
            Assert.Equal(kinds.Select(k => k.Kind).Order(StringComparer.Ordinal), kinds.Select(k => k.Kind));

            using var client = await producer.ProducerAsync();
            var byProducer = await client.GetAsync($"{Tenant}/resolver-kinds", Ct);
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotTenantAdmin), (byProducer.StatusCode, await CodeAsync(byProducer)));
        }
    }
}
