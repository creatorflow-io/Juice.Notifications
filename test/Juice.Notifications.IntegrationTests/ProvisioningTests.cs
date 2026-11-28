using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Configuration provisioning (phase-1-plan I12, p1-09): a tenant's setup from a file, loaded at start. Each test has its own host and database;
    /// the file the host starts with is <see cref="Initial"/>, and a test loads others as a restarted host would.
    /// </summary>
    public sealed class ProvisioningTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = Guid.Parse("0193f0aa-0000-7000-8000-00000000c0f1");
        private static readonly string Url = $"/api/v1/tenants/{Tenant}";
        private const string HookSecret = "PROVISIONING_TEST_HOOK_SECRET";

        private readonly string _directory = Directory.CreateTempSubdirectory("notifications-provisioning-").FullName;
        private readonly NotificationsApp _app;

        // Two integrations with a target each, a route naming both, an allowlist entry. The webhook's secret is a variable the API couldn't name
        // (it has no NOTIFICATIONS_SECRET_ prefix): the deployment's own configuration may.
        private const string Initial = $$"""
            {
              // comments are allowed
              "tenants": {
                "0193f0aa-0000-7000-8000-00000000c0f1": {
                  "integrations": {
                    "mail": { "channel": "email", "kind": "smtp", "settings": { "server": "local", "from": "notifications@juice.local" },
                              "targets": { "ops": { "address": "Ops@Example.com" } } },
                    "hook": { "channel": "webhook", "kind": "webhook", "secretRef": "env:{{HookSecret}}",
                              "targets": { "site": { "address": "https://hooks.example.com/in" } } }
                  },
                  "routes": { "all": { "match": {}, "targets": ["ops", "site"] } },
                  "allowlist": [ { "destination": "10.20.0.0/16", "ports": [8443] } ]
                }
              }
            }
            """;

        public ProvisioningTests()
        {
            var path = Write(Initial);
            _app = new NotificationsApp(host =>
            {
                host.UseSetting("Provisioning:Path", path);
                host.ConfigureTestServices(services => services.AddSingleton<Planning.IRecipientResolver>(new FakePeopleResolver()));
            });
        }

        public ValueTask InitializeAsync() => _app.InitializeAsync();

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }

        private string Write(string json)
        {
            var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }

        /// <summary>Loads a file as a host starting with it would.</summary>
        private Task ProvisionAsync(string json)
            => ActivatorUtilities.CreateInstance<ConfigurationProvisioner>(_app.Services, Options.Create(new ProvisioningOptions { Path = Write(json) }))
                .StartAsync(Ct);

        private async Task<HttpClient> AdminAsync() => _app.Client(await LocalStack.AdminTokenAsync());

        private static async Task<string?> CodeAsync(HttpResponseMessage r)
            => (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("code", out var code) ? code.GetString() : null;

        private async Task<Dictionary<string, IntegrationResponse>> IntegrationsAsync(HttpClient admin)
            => (await admin.GetFromJsonAsync<List<IntegrationResponse>>($"{Url}/integrations", Ct))!.ToDictionary(i => i.Key);

        private async Task<Outcome<T>> AsAsync<T>(Juice.MediatR.IRequest<Outcome<T>> command)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            using var context = NotificationsMessageContext.Begin("qa");
            return (await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>().Send(command, Ct))!;
        }
        private async Task<int> RouteTargetRowsAsync()
        {
            await using var db = _app.Database.Context();
            return await db.RouteTargets.CountAsync(r => r.TenantId == Tenant, Ct);
        }

        [Fact]
        public async Task A_file_sets_up_a_tenant_and_the_api_refuses_to_change_what_it_owns()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();

            var integrations = await IntegrationsAsync(admin);
            Assert.Equal(["hook", "mail"], integrations.Keys.Order());
            Assert.All(integrations.Values, i => Assert.Equal(ManagedBy.Configuration, i.ManagedBy));
            Assert.Equal("env:" + HookSecret, integrations["hook"].SecretRef);
            Assert.Equal("ops@example.com", Assert.Single(integrations["mail"].Targets).Address);
            var route = Assert.Single((await admin.GetFromJsonAsync<List<RouteResponse>>($"{Url}/routes", Ct))!);
            Assert.Equal(["ops", "site"], route.Targets);
            Assert.Equal(ManagedBy.Configuration, route.ManagedBy);
            var allowed = Assert.Single((await admin.GetFromJsonAsync<List<AllowlistResponse>>($"{Url}/allowlist", Ct))!);
            Assert.Equal(("10.20.0.0/16", 8443, ManagedBy.Configuration), (allowed.Destination, Assert.Single(allowed.Ports), allowed.ManagedBy));
            Assert.Equal(2, await RouteTargetRowsAsync());

            foreach (var refused in new[]
            {
                await admin.PutAsJsonAsync($"{Url}/integrations/mail", new PutIntegrationBody(null, null, null, null, 3, null), Ct),
                await admin.DeleteAsync($"{Url}/integrations/hook", Ct),
                await admin.PutAsJsonAsync($"{Url}/integrations/mail/targets/ops", new PutTargetBody(null, null, false), Ct),
                await admin.PutAsJsonAsync($"{Url}/routes/all", new PutRouteBody(null, null, false), Ct),
                await admin.DeleteAsync($"{Url}/routes/all", Ct),
                await admin.PutAsJsonAsync($"{Url}/allowlist", new PutAllowlistBody("10.20.0.0/16", [443]), Ct),
                await admin.DeleteAsync($"{Url}/allowlist?destination=10.20.0.0/16", Ct),
            })
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Equal(ErrorCodes.ManagedByConfiguration, await CodeAsync(refused));
            }

            // The API still couldn't name that secret itself, nor add a target the deployment's secret would sign for.
            var mine = await admin.PutAsJsonAsync($"{Url}/integrations/mine", new PutIntegrationBody("webhook", "webhook", null, "env:" + HookSecret, null, null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, mine.StatusCode);
            var extra = await admin.PutAsJsonAsync($"{Url}/integrations/hook/targets/mine", new PutTargetBody("https://elsewhere.example.com/in", null, null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
            Assert.Contains("/integration", await extra.Content.ReadAsStringAsync(Ct));

            // One audit record for the tenant's provisioning, by the deployment, with each row it wrote.
            var record = Assert.Single(_app.Audit.All, s => s.Access.TenantId == Tenant.ToString() && s.Access.Request?.Path == "configuration-provisioning");
            Assert.Equal(ConfigurationProvisioner.Actor, record.Access.User);
            Assert.Equal(["destination_allowlist", "integration_targets", "integration_targets", "integrations", "integrations", "routes"],
                record.Changes.Select(c => c.Tbl).Order());
            Assert.All(record.Changes, c => Assert.Equal((Tenant.ToString(), "Inserted"), (c.TenantId, c.Action)));
        }

        [Fact]
        public async Task Loading_the_same_file_again_writes_nothing()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();
            var before = await IntegrationsAsync(admin);
            var records = _app.Audit.All.Count;
            var rowVersions = await RouteTargetVersionsAsync();

            await ProvisionAsync(Initial);

            var after = await IntegrationsAsync(admin);
            Assert.All(after, a => Assert.Equal(before[a.Key].UpdatedAt, a.Value.UpdatedAt));
            Assert.Equal(records, _app.Audit.All.Count);
            // Not even the route's target rows, which aren't audited: nothing is written.
            Assert.Equal(rowVersions, await RouteTargetVersionsAsync());
        }

        /// <summary>Each route_targets row's version (PostgreSQL's xmin): it moves whenever the row is written.</summary>
        private async Task<List<string>> RouteTargetVersionsAsync()
        {
            await using var connection = new Npgsql.NpgsqlConnection(_app.Database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new Npgsql.NpgsqlCommand("SELECT target_id::text || ':' || xmin::text FROM notifications.route_targets ORDER BY 1", connection);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var versions = new List<string>();
            while (await reader.ReadAsync(Ct))
            {
                versions.Add(reader.GetString(0));
            }
            Assert.NotEmpty(versions);
            return versions;
        }

        [Fact]
        public async Task A_changed_file_updates_and_hands_back_what_it_no_longer_lists()
        {
            LocalStack.RequireAvailable();
            await ProvisionAsync($$"""
                { "tenants": { "{{Tenant}}": {
                    "integrations": {
                      "mail": { "channel": "email", "kind": "smtp", "settings": { "server": "local", "from": "notifications@juice.local" }, "priority": 5,
                                "targets": { "ops": { "address": "ops@example.com" } } }
                    },
                    "routes": { "all": { "match": { "minPriority": "high" }, "targets": ["ops"] } }
                } } }
                """);
            using var admin = await AdminAsync();

            var integrations = await IntegrationsAsync(admin);
            Assert.Equal((5, ManagedBy.Configuration), (integrations["mail"].Priority, integrations["mail"].ManagedBy));
            // No longer listed: the API's again, and disabled until an admin decides.
            Assert.Equal((ManagedBy.Api, false), (integrations["hook"].ManagedBy, integrations["hook"].Enabled));
            var site = Assert.Single(integrations["hook"].Targets);
            Assert.Equal((ManagedBy.Api, false), (site.ManagedBy, site.Enabled));
            var route = Assert.Single((await admin.GetFromJsonAsync<List<RouteResponse>>($"{Url}/routes", Ct))!);
            Assert.Equal(["ops"], route.Targets);
            Assert.Equal(1, await RouteTargetRowsAsync());
            Assert.Empty((await admin.GetFromJsonAsync<List<AllowlistResponse>>($"{Url}/allowlist", Ct))!);

            // The deployment's secret stays on the row, but the API uses it for nothing: disabling passes, anything else needs a secret of the
            // admin's own.
            Assert.Equal("env:" + HookSecret, integrations["hook"].SecretRef);
            var disabled = await admin.PutAsJsonAsync($"{Url}/integrations/hook", new PutIntegrationBody(null, null, null, null, null, false), Ct);
            Assert.True(disabled.IsSuccessStatusCode, await disabled.Content.ReadAsStringAsync(Ct));
            // So does a client that disables by sending back the whole row it read.
            var hook = integrations["hook"];
            var resent = await admin.PutAsJsonAsync($"{Url}/integrations/hook", new PutIntegrationBody(hook.Channel, hook.Kind, hook.Settings, hook.SecretRef,
                hook.Priority, false), Ct);
            Assert.True(resent.IsSuccessStatusCode, await resent.Content.ReadAsStringAsync(Ct));
            var siteRefused = await admin.PutAsJsonAsync($"{Url}/integrations/hook/targets/site", new PutTargetBody("https://elsewhere.example.com/in", null, true), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, siteRefused.StatusCode);
            var keepingSecret = await admin.PutAsJsonAsync($"{Url}/integrations/hook", new PutIntegrationBody(null, null, null, null, null, true), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, keepingSecret.StatusCode);
            Assert.Contains("/secretRef", await keepingSecret.Content.ReadAsStringAsync(Ct));
            var reenabled = await admin.PutAsJsonAsync($"{Url}/integrations/hook", new PutIntegrationBody(null, null, null, "env:NOTIFICATIONS_SECRET_TENANT_HOOK", null, true), Ct);
            Assert.True(reenabled.IsSuccessStatusCode, await reenabled.Content.ReadAsStringAsync(Ct));
            Assert.Equal("env:NOTIFICATIONS_SECRET_TENANT_HOOK", (await IntegrationsAsync(admin))["hook"].SecretRef);

            var record = _app.Audit.All.Last(s => s.Access.TenantId == Tenant.ToString() && s.Access.Request?.Path == "configuration-provisioning");
            Assert.Contains(record.Changes, c => c.Tbl == "destination_allowlist" && c.Action == "Deleted");
            Assert.Contains(record.Changes, c => c.Tbl == "integrations" && c.Action == "Modified" && c.Changes.Contains("\"Enabled\":false"));
        }

        [Fact]
        public async Task A_handed_back_secret_address_serves_the_api_for_nothing_but_disabling()
        {
            LocalStack.RequireAvailable();
            const string Chat = """
                "chat": { "channel": "webhook", "kind": "webhook", "secretRef": "env:NOTIFICATIONS_SECRET_CHAT",
                          "targets": { "room": { "addressRef": "env:DEPLOYMENT_ROOM_URL" } } }
                """;
            await ProvisionAsync($$"""{ "tenants": { "{{Tenant}}": { "integrations": { {{Chat}} } } } }""");
            await ProvisionAsync($$"""{ "tenants": { "{{Tenant}}": { "integrations": { {{Chat.Replace("\"room\": { \"addressRef\": \"env:DEPLOYMENT_ROOM_URL\" }", "")}} } } } }""");
            using var admin = await AdminAsync();
            var room = Assert.Single((await IntegrationsAsync(admin))["chat"].Targets);
            Assert.Equal((ManagedBy.Api, false, "env:DEPLOYMENT_ROOM_URL"), (room.ManagedBy, room.Enabled, room.AddressRef));

            var enabling = await admin.PutAsJsonAsync($"{Url}/integrations/chat/targets/room", new PutTargetBody(null, null, true), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, enabling.StatusCode);
            Assert.Contains("/addressRef", await enabling.Content.ReadAsStringAsync(Ct));
            var disabling = await admin.PutAsJsonAsync($"{Url}/integrations/chat/targets/room", new PutTargetBody(null, null, false), Ct);
            Assert.True(disabling.IsSuccessStatusCode, await disabling.Content.ReadAsStringAsync(Ct));
            var resent = await admin.PutAsJsonAsync($"{Url}/integrations/chat/targets/room", new PutTargetBody(null, "env:DEPLOYMENT_ROOM_URL", false), Ct);
            Assert.True(resent.IsSuccessStatusCode, await resent.Content.ReadAsStringAsync(Ct));
            var own = await admin.PutAsJsonAsync($"{Url}/integrations/chat/targets/room", new PutTargetBody("https://room.example.com/in", null, true), Ct);
            Assert.True(own.IsSuccessStatusCode, await own.Content.ReadAsStringAsync(Ct));
        }

        [Fact]
        public async Task The_file_gives_its_secret_only_to_an_integration_whose_targets_it_lists()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();
            await (await admin.PutAsJsonAsync($"{Url}/integrations/legacy", new PutIntegrationBody("webhook", "webhook", null, "env:NOTIFICATIONS_SECRET_LEGACY",
                null, null), Ct)).Content.ReadAsStringAsync(Ct);
            var site = await admin.PutAsJsonAsync($"{Url}/integrations/legacy/targets/theirs", new PutTargetBody("https://theirs.example.com/in", null, null), Ct);
            Assert.True(site.IsSuccessStatusCode, await site.Content.ReadAsStringAsync(Ct));

            static string File(string targets) => $$"""
                { "tenants": { "{{Tenant}}": { "integrations": {
                    "legacy": { "channel": "webhook", "kind": "webhook", "secretRef": "env:{{HookSecret}}", "targets": { {{targets}} } } } } } }
                """;
            // The deployment's secret would sign for the API's target at its address: refused, nothing changed.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ProvisionAsync(File("")));
            Assert.Contains("targets the API made (theirs)", error.Message);
            Assert.Equal(ManagedBy.Api, (await IntegrationsAsync(admin))["legacy"].ManagedBy);
            // Disabled in the file, it signs nothing: allowed. The API can't add a target under it, and is told where targets are set.
            await ProvisionAsync(File("").Replace("\"targets\"", "\"enabled\": false, \"targets\""));
            var added = await admin.PutAsJsonAsync($"{Url}/integrations/legacy/targets/more", new PutTargetBody("https://more.example.com/in", null, null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, added.StatusCode);
            Assert.Contains("set in the deployment's configuration", await added.Content.ReadAsStringAsync(Ct));

            // Listed in the file, the target is the deployment's to set.
            await ProvisionAsync(File("""
                "theirs": { "address": "https://theirs.example.com/in" }
                """));
            var legacy = (await IntegrationsAsync(admin))["legacy"];
            Assert.Equal((ManagedBy.Configuration, ManagedBy.Configuration), (legacy.ManagedBy, Assert.Single(legacy.Targets).ManagedBy));
        }

        [Fact]
        public async Task A_deployment_mail_password_doesnt_keep_the_api_from_its_targets()
        {
            LocalStack.RequireAvailable();
            // An SMTP password goes to the deployment's mail server, never to a target: the API may still add the integration's targets.
            await ProvisionAsync($$"""
                { "tenants": { "{{Tenant}}": { "integrations": {
                    "relay": { "channel": "email", "kind": "smtp", "secretRef": "env:DEPLOYMENT_SMTP_PASSWORD",
                               "settings": { "server": "local", "from": "relay@juice.local" } } } } } }
                """);
            using var admin = await AdminAsync();
            var target = await admin.PutAsJsonAsync($"{Url}/integrations/relay/targets/team", new PutTargetBody("team@example.com", null, null), Ct);
            Assert.True(target.IsSuccessStatusCode, await target.Content.ReadAsStringAsync(Ct));
        }

        [Fact]
        public async Task The_file_takes_over_a_key_the_api_made()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();
            var created = await admin.PutAsJsonAsync($"{Url}/integrations/legacy", new PutIntegrationBody("email", "smtp",
                JsonElement.Parse("""{ "server": "local", "from": "old@juice.local" }"""), null, 1, null), Ct);
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));

            await ProvisionAsync(Initial.Replace("\"mail\":", "\"legacy\": { \"channel\": \"email\", \"kind\": \"smtp\", \"settings\": { \"server\": \"local\", \"from\": \"new@juice.local\" } },\n\"mail\":"));

            var legacy = (await IntegrationsAsync(admin))["legacy"];
            Assert.Equal((ManagedBy.Configuration, 0, "new@juice.local"), (legacy.ManagedBy, legacy.Priority, legacy.Settings.GetProperty("from").GetString()));
        }

        [Fact]
        public async Task A_file_the_checks_refuse_changes_nothing_and_says_where()
        {
            LocalStack.RequireAvailable();
            // A new integration, and a route naming a target that doesn't exist: the integration isn't kept either.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ProvisionAsync($$"""
                { "tenants": { "{{Tenant}}": {
                    "integrations": { "extra": { "channel": "email", "kind": "smtp", "settings": { "server": "local", "from": "x@juice.local" } } },
                    "routes": { "broken": { "match": {}, "targets": ["nowhere"] }, "nomatch": { "targets": ["ops"] } }
                } } }
                """));
            Assert.Contains("route 'broken': /targets/0", error.Message);
            Assert.Contains("route 'nomatch': a route has a match", error.Message);
            using var admin = await AdminAsync();
            Assert.DoesNotContain("extra", (await IntegrationsAsync(admin)).Keys);
            Assert.Equal(ManagedBy.Configuration, (await IntegrationsAsync(admin))["mail"].ManagedBy);

            foreach (var (file, expected) in new[]
            {
                ("""{ "tenants": { "not-a-guid": {} } }""", "Invalid provisioning file"),
                ("""{ "tenant": {} }""", "Invalid provisioning file"),
                ($$"""{ "tenants": { "{{Tenant}}": { "routes": { "a": { "match": {}, "targets": ["ops"], "match": { "type": "x" } } } } } }""", "/match repeats"),
                ("""{ "tenants": """, "Invalid provisioning file"),
                // One tenant in two spellings, and a number written as a string.
                ($$"""{ "tenants": { "{{Tenant.ToString().ToUpperInvariant()}}": {}, "{{Tenant}}": {} } }""", "is listed twice"),
                ($$"""{ "tenants": { "{{Tenant}}": { "integrations": { "x": { "channel": "email", "kind": "smtp", "priority": "5" } } } } }""", "Invalid provisioning file"),
                // Names are as written: "Enabled" beside "enabled" would otherwise both bind, the last one winning.
                ($$"""{ "tenants": { "{{Tenant}}": { "routes": { "a": { "match": {}, "targets": ["ops"], "enabled": false, "Enabled": true } } } } }""", "Invalid provisioning file"),
            })
            {
                Assert.Contains(expected, (await Assert.ThrowsAsync<InvalidOperationException>(() => ProvisionAsync(file))).Message);
            }
            var missing = ActivatorUtilities.CreateInstance<ConfigurationProvisioner>(_app.Services,
                Options.Create(new ProvisioningOptions { Path = Path.Combine(_directory, "missing.json") }));
            Assert.Contains("can't be read", (await Assert.ThrowsAsync<InvalidOperationException>(() => missing.StartAsync(Ct))).Message);
        }

        [Fact]
        public async Task The_file_moves_a_target_between_integrations_of_one_channel()
        {
            LocalStack.RequireAvailable();
            Guid before;
            await using (var db = _app.Database.Context())
            {
                before = (await db.IntegrationTargets.SingleAsync(t => t.TenantId == Tenant && t.Key == "ops", Ct)).Id;
            }
            static string File(string integrations) => $$"""
                { "tenants": { "{{Tenant}}": {
                    "integrations": { {{integrations}} },
                    "routes": { "all": { "match": {}, "targets": ["ops", "site"] } },
                    "allowlist": [ { "destination": "10.20.0.0/16", "ports": [8443] } ]
                } } }
                """;
            const string Mail = """ "mail": { "channel": "email", "kind": "smtp", "settings": { "server": "local", "from": "notifications@juice.local" } } """;
            const string Hook = $$""" "hook": { "channel": "webhook", "kind": "webhook", "secretRef": "env:{{HookSecret}}", "targets": { "site": { "address": "https://hooks.example.com/in" } } } """;

            await ProvisionAsync(File(Mail + "," + Hook + """
                , "mail2": { "channel": "email", "kind": "smtp", "settings": { "server": "local", "from": "other@juice.local" },
                             "targets": { "ops": { "address": "Ops@Example.com" } } }
                """));

            using var admin = await AdminAsync();
            var integrations = await IntegrationsAsync(admin);
            Assert.Empty(integrations["mail"].Targets);
            Assert.Equal("ops", Assert.Single(integrations["mail2"].Targets).Key);
            await using (var db = _app.Database.Context())
            {
                // The same target: its deliveries' destination key (it:{id}) and the route's hold on it are kept.
                Assert.Equal(before, (await db.IntegrationTargets.SingleAsync(t => t.TenantId == Tenant && t.Key == "ops", Ct)).Id);
            }
            Assert.Equal(2, await RouteTargetRowsAsync());

            // Not to another channel: there it would be another kind of address.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ProvisionAsync(File(Mail + "," + $$"""
                "hook": { "channel": "webhook", "kind": "webhook", "secretRef": "env:{{HookSecret}}",
                          "targets": { "site": { "address": "https://hooks.example.com/in" }, "ops": { "address": "https://hooks.example.com/ops" } } }
                """)));
            Assert.Contains("moves only between integrations of one channel", error.Message);
        }

        [Fact]
        public async Task A_file_provisions_resolvers_and_hands_back_the_ones_it_drops()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();
            await ProvisionAsync($$"""{ "tenants": { "{{Tenant}}": { "resolvers": { "people": { "kind": "fake", "handles": ["user"] } } } } }""");

            var listed = Assert.Single(await (await admin.GetAsync($"{Url}/resolvers", Ct)).Content.ReadFromJsonAsync<List<ResolverResponse>>(Ct) ?? []);
            Assert.Equal(("people", "fake", "user", true, ManagedBy.Configuration),
                (listed.Key, listed.Kind, string.Join(",", listed.Handles), listed.Enabled, listed.ManagedBy));
            Assert.Equal(ErrorCodes.ManagedByConfiguration,
                await CodeAsync(await admin.PutAsJsonAsync($"{Url}/resolvers/people", new PutResolverBody(null, null, null, null, false), Ct)));
            Assert.Equal(ErrorCodes.ManagedByConfiguration, await CodeAsync(await admin.DeleteAsync($"{Url}/resolvers/people", Ct)));
            Assert.Contains("resolver 'bad'", (await Assert.ThrowsAsync<InvalidOperationException>(() => ProvisionAsync(
                $$"""{ "tenants": { "{{Tenant}}": { "resolvers": { "bad": { "kind": "nope" } } } } }"""))).Message);

            // Dropped from the file: back to the API, disabled.
            await ProvisionAsync($$"""{ "tenants": { "{{Tenant}}": { } } }""");
            listed = Assert.Single(await (await admin.GetAsync($"{Url}/resolvers", Ct)).Content.ReadFromJsonAsync<List<ResolverResponse>>(Ct) ?? []);
            Assert.Equal((false, ManagedBy.Api), (listed.Enabled, listed.ManagedBy));
        }

        [Fact]
        public async Task Replicas_starting_together_provision_once()
        {
            LocalStack.RequireAvailable();
            var other = Guid.CreateVersion7();
            var json = Initial.Replace(Tenant.ToString(), other.ToString());
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ProvisionAsync(json)));

            await using var db = _app.Database.Context();
            Assert.Equal(2, await db.Integrations.CountAsync(i => i.TenantId == other, Ct));
            Assert.Equal(2, await db.RouteTargets.CountAsync(r => r.TenantId == other, Ct));
            // Only the first wrote anything, so only it was recorded.
            Assert.Single(_app.Audit.All, s => s.Access.TenantId == other.ToString());
        }

        [Fact]
        public async Task Without_a_path_rows_stay_and_an_empty_file_hands_them_back()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();
            // A replica started without the setting leaves the provisioned rows alone.
            await ActivatorUtilities.CreateInstance<ConfigurationProvisioner>(_app.Services, Options.Create(new ProvisioningOptions())).StartAsync(Ct);
            Assert.All((await IntegrationsAsync(admin)).Values, i => Assert.Equal((ManagedBy.Configuration, true), (i.ManagedBy, i.Enabled)));

            await ProvisionAsync("""{ "tenants": {} }""");

            Assert.All((await IntegrationsAsync(admin)).Values, i => Assert.Equal((ManagedBy.Api, false), (i.ManagedBy, i.Enabled)));
            var route = Assert.Single((await admin.GetFromJsonAsync<List<RouteResponse>>($"{Url}/routes", Ct))!);
            Assert.Equal((ManagedBy.Api, false), (route.ManagedBy, route.Enabled));
        }

        // ─── Takeovers (M46c, #18): a new owner is a change, also when the definition is the same ────────

        private async Task<Outcome<T>> AsOwnerAsync<T>(Juice.MediatR.IRequest<Outcome<T>> command)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            using var context = NotificationsMessageContext.Begin("test");
            return (await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>().Send(command, Ct))!;
        }

        /// <summary>The command in an audit scope of its own, as a writer outside a request (the provisioner, the alias's tool) records it.</summary>
        private async Task<Outcome<T>> AuditedAsync<T>(Juice.MediatR.IRequest<Outcome<T>> command)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            using var context = NotificationsMessageContext.Begin("test");
            Outcome<T>? outcome = null;
            await scope.ServiceProvider.GetRequiredService<Juice.Audit.Api.Scopes.AuditScopeRunner>().RunAsync(
                new Juice.Audit.Api.Scopes.AuditScopeRequest("Takeover", Juice.Audit.Api.Scopes.AuditScopeMethods.Request, "takeover-test", Tenant.ToString()),
                async () =>
                {
                    outcome = await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>().Send(command, Ct);
                    return true;
                },
                _ => Juice.Audit.Api.Scopes.AuditScopeOutcome.Ok,
                audit => audit.SetUser("takeover-test"));
            return outcome!;
        }

        /// <summary>The update recorded on <paramref name="table"/> (one per test: each has its own host), waiting briefly for the record.</summary>
        private async Task<string> ChangeAsync(string table)
        {
            for (var i = 0; i < 50; i++)
            {
                var change = _app.Audit.All.SelectMany(s => s.Changes)
                    .LastOrDefault(c => c.Tbl == table && c.Action == "Modified");
                if (change is not null) return change.Changes;
                await Task.Delay(100, Ct);
            }
            Assert.Fail($"no change recorded on {table}: {string.Join("; ", _app.Audit.All.SelectMany(s => s.Changes).Select(c => $"{c.Tbl} {c.Action}"))}");
            return "";
        }

        private static readonly JsonElement PolicySteps = JsonSerializer.SerializeToElement(new[]
        {
            new { afterMinutes = 5, recipients = new[] { new { kind = "route", key = "ops" } } },
        });

        /// <summary>
        /// A row the retired alias made is the API's after the RetireAliasOwners migration; a file declaring its key takes it over in place: the
        /// same id, the owner moved to the configuration, the change recorded, and the API refused from then on.
        /// </summary>
        [Fact]
        public async Task Provisioning_FormerAliasRow_SameKey_TakenOverAsConfig_KeepsId()
        {
            LocalStack.RequireAvailable();
            var settings = JsonElement.Parse("""{ "server": "local", "from": "former@juice.local" }""");
            var made = await AsOwnerAsync(new PutIntegration(Tenant, "taken", "email", "smtp", settings, null, 0, true));
            Assert.True(made.Kind is OutcomeKind.Ok or OutcomeKind.Created, $"{made.Kind} {made.Code} {made.Message}");
            Guid id;
            await using (var db = _app.Database.Context())
            {
                var before = await db.Integrations.SingleAsync(i => i.TenantId == Tenant && i.Key == "taken", Ct);
                Assert.Equal(ManagedBy.Api, before.ManagedBy);
                id = before.Id;
            }

            // The same definition, declared in the deployment's configuration now.
            var taken = await AuditedAsync(new PutIntegration(Tenant, "taken", "email", "smtp", settings, null, 0, true) { ManagedBy = ManagedBy.Configuration });

            Assert.Equal(OutcomeKind.Ok, taken.Kind);
            await using (var db = _app.Database.Context())
            {
                var row = await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == Tenant && i.Key == "taken", Ct);
                Assert.Equal((id, ManagedBy.Configuration), (row.Id, row.ManagedBy));
            }
            var integrationChange = await ChangeAsync("integrations");
            Assert.Contains("ManagedBy", integrationChange);
            Assert.Contains(ManagedBy.Configuration, integrationChange);
            Assert.Equal(ErrorCodes.ManagedByConfiguration,
                (await AsOwnerAsync(new PutIntegration(Tenant, "taken", null, null, null, null, null, false))).Code);
        }

        /// <summary>
        /// An admin PUT races the file's takeover of the same route, 12 times over: the loser fails as a concurrent change (or, after the
        /// takeover, as managed by configuration), never silently; and the next start settles every route as the file states it.
        /// </summary>
        [Fact]
        public async Task Provisioning_ConcurrentAdminPut_ConcurrentChange_NextStartReconciles()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();
            const int Rounds = 12;
            for (var round = 0; round < Rounds; round++)
            {
                using var made = await admin.PutAsJsonAsync($"{Url}/routes/cc-{round}", new PutRouteBody(JsonElement.Parse("""{"type":"api.*"}"""), ["ops"], true), Ct);
                Assert.Equal(HttpStatusCode.OK, made.StatusCode);
            }
            var puts = Enumerable.Range(0, Rounds).Select(round => Task.Run(async () =>
            {
                using var response = await admin.PutAsJsonAsync($"{Url}/routes/cc-{round}", new PutRouteBody(JsonElement.Parse("""{"type":"edit.*"}"""), null, null), Ct);
                return (response.StatusCode, Code: await CodeAsync(response));
            })).ToList();
            // One start with every route in the file, against the 12 admin edits running now.
            Exception? raced = null;
            try
            {
                await ProvisionAsync(Initial.Replace("\"routes\": { \"all\"",
                    "\"routes\": { " + string.Concat(Enumerable.Range(0, Rounds).Select(r => $"\"cc-{r}\": {{ \"match\": {{ \"type\": \"file.*\" }}, \"targets\": [\"ops\"] }}, ")) + "\"all\""));
            }
            catch (InvalidOperationException ex)
            {
                raced = ex;
            }
            foreach (var (status, code) in await Task.WhenAll(puts))
            {
                Assert.True(status == HttpStatusCode.OK
                    || (status, code) == (HttpStatusCode.Conflict, ErrorCodes.ConcurrentChange)
                    || (status, code) == (HttpStatusCode.Conflict, ErrorCodes.ManagedByConfiguration), $"admin put: {(int)status} {code}");
            }

            // The next start reconciles whatever the race left.
            await ProvisionAsync(Initial.Replace("\"routes\": { \"all\"",
                "\"routes\": { " + string.Concat(Enumerable.Range(0, Rounds).Select(r => $"\"cc-{r}\": {{ \"match\": {{ \"type\": \"file.*\" }}, \"targets\": [\"ops\"] }}, ")) + "\"all\""));
            await using var db = _app.Database.Context();
            for (var round = 0; round < Rounds; round++)
            {
                var row = await db.Routes.AsNoTracking().SingleAsync(r => r.TenantId == Tenant && r.Key == $"cc-{round}", Ct);
                Assert.True((row.ManagedBy, row.Match.Replace(" ", "")) == (ManagedBy.Configuration, """{"type":"file.*"}"""),
                    $"cc-{round}: {row.ManagedBy} {row.Match} (first start: {raced?.Message ?? "ok"})");
            }
        }

        [Fact]
        public async Task A_policy_the_api_made_is_taken_over_by_the_configuration()
        {
            LocalStack.RequireAvailable();
            var made = await AsOwnerAsync(new PutEscalationPolicy(Tenant, "api-policy", PolicySteps, true));
            Assert.True(made.Kind is OutcomeKind.Ok or OutcomeKind.Created, $"{made.Kind} {made.Code} {made.Message}");

            var taken = await AsOwnerAsync(new PutEscalationPolicy(Tenant, "api-policy", PolicySteps, true) { ManagedBy = ManagedBy.Configuration });

            Assert.Equal(OutcomeKind.Ok, taken.Kind);
            await using (var db = _app.Database.Context())
            {
                Assert.Equal(ManagedBy.Configuration, (await db.EscalationPolicies.AsNoTracking().SingleAsync(p => p.TenantId == Tenant && p.Key == "api-policy", Ct)).ManagedBy);
            }
            Assert.Equal(ErrorCodes.ManagedByConfiguration, (await AsOwnerAsync(new PutEscalationPolicy(Tenant, "api-policy", null, false))).Code);
        }
    }
}
