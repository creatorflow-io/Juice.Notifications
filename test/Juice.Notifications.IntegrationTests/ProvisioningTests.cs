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

        /// <summary>
        /// QA (M46c): every kind of setup row written as <c>alias-config</c> (integration, target, route, allowlist entry, escalation policy) is
        /// refused to the admin API (a change, a disable-only change, a delete) and to the alias's own API alike, with
        /// <c>managed_by_configuration</c>; a file that doesn't list them, and one with no tenants at all (which hands its own rows back), leave
        /// every one of them as it was.
        /// </summary>
        [Fact]
        public async Task Alias_config_rows_of_every_kind_are_refused_to_both_apis_and_survive_every_file()
        {
            LocalStack.RequireAvailable();
            const string AC = ManagedBy.AliasConfig;
            var settings = JsonElement.Parse("""{ "server": "local", "from": "ac@juice.local" }""");
            var steps = JsonElement.Parse("""[{"afterMinutes":5,"recipients":[{"kind":"route","key":"ac-route"}]}]""");
            foreach (var made in new Func<Task<OutcomeKind>>[]
            {
                async () => (await AsAsync(new PutIntegration(Tenant, "ac-mail", "email", "smtp", settings, null, 0, true) { ManagedBy = AC })).Kind,
                async () => (await AsAsync(new PutTarget(Tenant, "ac-mail", "ac-target", "ac@example.com", null, true) { ManagedBy = AC })).Kind,
                async () => (await AsAsync(new PutRoute(Tenant, "ac-route", JsonElement.Parse("""{"type":"x.*"}"""), ["ac-target"], true) { ManagedBy = AC })).Kind,
                async () => (await AsAsync(new PutAllowlistEntry(Tenant, "10.99.0.0/16", [8443]) { ManagedBy = AC })).Kind,
                async () => (await AsAsync(new PutEscalationPolicy(Tenant, "ac-policy", steps, false) { ManagedBy = AC })).Kind,
            })
            {
                Assert.Contains(await made(), new[] { OutcomeKind.Ok, OutcomeKind.Created });
            }

            using var admin = await AdminAsync();
            var refusals = new List<(string What, HttpResponseMessage Response)>
            {
                ("integration put", await admin.PutAsJsonAsync($"{Url}/integrations/ac-mail", new PutIntegrationBody("email", "smtp", settings, null, 1, true), Ct)),
                ("integration disable", await admin.PutAsJsonAsync($"{Url}/integrations/ac-mail", new PutIntegrationBody(null, null, null, null, null, false), Ct)),
                ("integration delete", await admin.DeleteAsync($"{Url}/integrations/ac-mail", Ct)),
                ("target put", await admin.PutAsJsonAsync($"{Url}/integrations/ac-mail/targets/ac-target", new PutTargetBody("other@example.com", null, null), Ct)),
                ("target disable", await admin.PutAsJsonAsync($"{Url}/integrations/ac-mail/targets/ac-target", new PutTargetBody(null, null, false), Ct)),
                ("target delete", await admin.DeleteAsync($"{Url}/integrations/ac-mail/targets/ac-target", Ct)),
                ("route put", await admin.PutAsJsonAsync($"{Url}/routes/ac-route", new PutRouteBody(JsonElement.Parse("""{"type":"y.*"}"""), ["ac-target"], true), Ct)),
                ("route disable", await admin.PutAsJsonAsync($"{Url}/routes/ac-route", new PutRouteBody(null, null, false), Ct)),
                ("route delete", await admin.DeleteAsync($"{Url}/routes/ac-route", Ct)),
                ("allowlist put", await admin.PutAsJsonAsync($"{Url}/allowlist", new PutAllowlistBody("10.99.0.0/16", [443]), Ct)),
                ("allowlist delete", await admin.DeleteAsync($"{Url}/allowlist?destination=10.99.0.0/16", Ct)),
                ("policy put", await admin.PutAsJsonAsync($"{Url}/escalation-policies/ac-policy", new PutEscalationPolicyBody(null, true), Ct)),
                ("policy delete", await admin.DeleteAsync($"{Url}/escalation-policies/ac-policy", Ct)),
            };
            foreach (var (what, response) in refusals)
            {
                Assert.True((response.StatusCode, await CodeAsync(response)) == (HttpStatusCode.Conflict, ErrorCodes.ManagedByConfiguration), $"admin {what}: {(int)response.StatusCode}");
                response.Dispose();
            }
            const string Alias = ManagedBy.WebhooksAlias;
            foreach (var (what, code) in new (string, string?)[]
            {
                ("integration put", (await AsAsync(new PutIntegration(Tenant, "ac-mail", null, null, null, null, null, false) { ManagedBy = Alias })).Code),
                ("integration delete", (await AsAsync(new DeleteIntegration(Tenant, "ac-mail") { ManagedBy = Alias })).Code),
                ("target put", (await AsAsync(new PutTarget(Tenant, "ac-mail", "ac-target", null, null, false) { ManagedBy = Alias })).Code),
                ("target delete", (await AsAsync(new DeleteTarget(Tenant, "ac-mail", "ac-target") { ManagedBy = Alias })).Code),
                ("route put", (await AsAsync(new PutRoute(Tenant, "ac-route", null, null, false) { ManagedBy = Alias })).Code),
                ("route delete", (await AsAsync(new DeleteRoute(Tenant, "ac-route") { ManagedBy = Alias })).Code),
                ("allowlist put", (await AsAsync(new PutAllowlistEntry(Tenant, "10.99.0.0/16", [443]) { ManagedBy = Alias })).Code),
                ("allowlist delete", (await AsAsync(new DeleteAllowlistEntry(Tenant, "10.99.0.0/16") { ManagedBy = Alias })).Code),
                ("policy put", (await AsAsync(new PutEscalationPolicy(Tenant, "ac-policy", null, true) { ManagedBy = Alias })).Code),
                ("policy delete", (await AsAsync(new DeleteEscalationPolicy(Tenant, "ac-policy") { ManagedBy = Alias })).Code),
            })
            {
                Assert.True(code == ErrorCodes.ManagedByConfiguration, $"alias {what}: {code}");
            }

            await ProvisionAsync(Initial);
            await ProvisionAsync("""{ "tenants": {} }""");

            await using var db = _app.Database.Context();
            var integration = await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == Tenant && i.Key == "ac-mail", Ct);
            Assert.Equal((AC, true, 0), (integration.ManagedBy, integration.Enabled, integration.Priority));
            var target = await db.IntegrationTargets.AsNoTracking().SingleAsync(t => t.TenantId == Tenant && t.Key == "ac-target", Ct);
            Assert.Equal((AC, true, "ac@example.com"), (target.ManagedBy, target.Enabled, target.Address));
            var route = await db.Routes.AsNoTracking().SingleAsync(r => r.TenantId == Tenant && r.Key == "ac-route", Ct);
            Assert.Equal((AC, true), (route.ManagedBy, route.Enabled));
            Assert.Equal(AC, (await db.Allowlist.AsNoTracking().SingleAsync(a => a.TenantId == Tenant && a.Destination == "10.99.0.0/16", Ct)).ManagedBy);
            var policy = await db.EscalationPolicies.AsNoTracking().SingleAsync(p => p.TenantId == Tenant && p.Key == "ac-policy", Ct);
            Assert.Equal((AC, false), (policy.ManagedBy, policy.StopOnAcknowledge));
        }

        /// <summary>
        /// QA (M46c): the alias's configuration taking over a row the alias made through its producer's API, while the alias's API edits that
        /// row at the same moment, 15 times over: no call fails with anything but <c>concurrent_change</c> (or, after a takeover, the alias's
        /// edit <c>managed_by_configuration</c>), and no update is lost: a takeover that answered Ok is what the row holds at the end.
        /// </summary>
        [Fact]
        public async Task A_takeover_racing_an_alias_edit_loses_no_update()
        {
            LocalStack.RequireAvailable();
            await AsAsync(new PutIntegration(Tenant, "race-mail", "email", "smtp", JsonElement.Parse("""{ "server": "local", "from": "r@juice.local" }"""), null, 0, true));
            await AsAsync(new PutTarget(Tenant, "race-mail", "race-target", "r@example.com", null, true));
            for (var round = 0; round < 15; round++)
            {
                var key = $"race-{round}";
                Assert.Contains((await AsAsync(new PutRoute(Tenant, key, JsonElement.Parse("""{"type":"a.*"}"""), ["race-target"], true) { ManagedBy = ManagedBy.WebhooksAlias })).Kind,
                    new[] { OutcomeKind.Ok, OutcomeKind.Created });
                Task<Outcome<RouteView>> Guarded(Func<Task<Outcome<RouteView>>> call) => call().ContinueWith(t => t.IsFaulted && Data.DbConflicts.Classify(t.Exception!.InnerException!) is { }
                    ? Outcome<RouteView>.Conflict(ErrorCodes.ConcurrentChange, "raced") : t.Result, TaskScheduler.Default);
                var edit = Guarded(() => AsAsync(new PutRoute(Tenant, key, JsonElement.Parse("""{"type":"edit.*"}"""), null, null) { ManagedBy = ManagedBy.WebhooksAlias }));
                var takeover = Guarded(() => AsAsync(new PutRoute(Tenant, key, JsonElement.Parse("""{"type":"config.*"}"""), null, null) { ManagedBy = ManagedBy.AliasConfig }));
                var (edited, taken) = (await edit, await takeover);
                Assert.True(edited.Kind is OutcomeKind.Ok || edited.Code is ErrorCodes.ConcurrentChange or ErrorCodes.ManagedByConfiguration, $"round {round} edit: {edited.Kind} {edited.Code}");
                Assert.True(taken.Kind is OutcomeKind.Ok || taken.Code is ErrorCodes.ConcurrentChange, $"round {round} takeover: {taken.Kind} {taken.Code}");
                await using var db = _app.Database.Context();
                var row = await db.Routes.AsNoTracking().SingleAsync(r => r.TenantId == Tenant && r.Key == key, Ct);
                if (taken.Kind == OutcomeKind.Ok)
                {
                    Assert.Equal((ManagedBy.AliasConfig, """{"type":"config.*"}"""), (row.ManagedBy, row.Match.Replace(" ", "")));
                }
                else
                {
                    Assert.Equal(ManagedBy.WebhooksAlias, row.ManagedBy);
                    Assert.Equal(edited.Kind == OutcomeKind.Ok ? """{"type":"edit.*"}""" : """{"type":"a.*"}""", row.Match.Replace(" ", ""));
                }
            }
        }

        /// <summary>
        /// QA (M46c, the policy fix): the same race for an escalation policy, 15 rounds. A takeover that answered Ok is what the policy holds at the
        /// end (owner alias-config, its steps); otherwise the alias's, with its edit's steps if that answered Ok. After every round, the alias's
        /// own PUT and DELETE and the admin API's get <c>managed_by_configuration</c> once the takeover has won.
        /// </summary>
        [Fact]
        public async Task A_policy_takeover_racing_an_alias_edit_loses_no_update_and_then_refuses_both_apis()
        {
            LocalStack.RequireAvailable();
            static JsonElement Steps(int minutes) => JsonElement.Parse($$"""[{"afterMinutes":{{minutes}},"recipients":[{"kind":"route"}]}]""");
            static int Minutes(EscalationPolicy p) => JsonDocument.Parse(p.Steps).RootElement[0].GetProperty("afterMinutes").GetInt32();
            using var admin = await AdminAsync();
            for (var round = 0; round < 15; round++)
            {
                var key = $"race-policy-{round}";
                Assert.Contains((await AsAsync(new PutEscalationPolicy(Tenant, key, Steps(1), false) { ManagedBy = ManagedBy.WebhooksAlias })).Kind,
                    new[] { OutcomeKind.Ok, OutcomeKind.Created });
                Task<Outcome<EscalationPolicyView>> Guarded(Func<Task<Outcome<EscalationPolicyView>>> call) => call().ContinueWith(t => t.IsFaulted && Data.DbConflicts.Classify(t.Exception!.InnerException!) is { }
                    ? Outcome<EscalationPolicyView>.Conflict(ErrorCodes.ConcurrentChange, "raced") : t.Result, TaskScheduler.Default);
                var edit = Guarded(() => AsAsync(new PutEscalationPolicy(Tenant, key, Steps(2), null) { ManagedBy = ManagedBy.WebhooksAlias }));
                var takeover = Guarded(() => AsAsync(new PutEscalationPolicy(Tenant, key, Steps(3), null) { ManagedBy = ManagedBy.AliasConfig }));
                var (edited, taken) = (await edit, await takeover);
                Assert.True(edited.Kind is OutcomeKind.Ok || edited.Code is ErrorCodes.ConcurrentChange or ErrorCodes.ManagedByConfiguration, $"round {round} edit: {edited.Kind} {edited.Code}");
                Assert.True(taken.Kind is OutcomeKind.Ok || taken.Code is ErrorCodes.ConcurrentChange, $"round {round} takeover: {taken.Kind} {taken.Code}");
                await using (var db = _app.Database.Context())
                {
                    var row = await db.EscalationPolicies.AsNoTracking().SingleAsync(p => p.TenantId == Tenant && p.Key == key, Ct);
                    if (taken.Kind == OutcomeKind.Ok)
                    {
                        Assert.Equal((ManagedBy.AliasConfig, 3), (row.ManagedBy, Minutes(row)));
                    }
                    else
                    {
                        Assert.Equal((ManagedBy.WebhooksAlias, edited.Kind == OutcomeKind.Ok ? 2 : 1), (row.ManagedBy, Minutes(row)));
                        continue;
                    }
                }
                Assert.Equal(ErrorCodes.ManagedByConfiguration, (await AsAsync(new PutEscalationPolicy(Tenant, key, Steps(4), null) { ManagedBy = ManagedBy.WebhooksAlias })).Code);
                Assert.Equal(ErrorCodes.ManagedByConfiguration, (await AsAsync(new DeleteEscalationPolicy(Tenant, key) { ManagedBy = ManagedBy.WebhooksAlias })).Code);
                using (var put = await admin.PutAsJsonAsync($"{Url}/escalation-policies/{key}", new PutEscalationPolicyBody(null, true), Ct))
                {
                    Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ManagedByConfiguration), (put.StatusCode, await CodeAsync(put)));
                }
                using (var delete = await admin.DeleteAsync($"{Url}/escalation-policies/{key}", Ct))
                {
                    Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ManagedByConfiguration), (delete.StatusCode, await CodeAsync(delete)));
                }
            }
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

        /// <summary>
        /// Rows the webhooks alias wrote from its producer's own configuration (<c>alias-config</c>, M46c) are neither this file's nor the API's:
        /// loading a file that doesn't list them leaves them, a file naming their key is refused, and the admin API and the alias's own API get
        /// <c>managed_by_configuration</c>, as legacy answered for a configuration-declared endpoint.
        /// </summary>
        [Fact]
        public async Task Alias_config_rows_are_left_alone_by_the_file_the_api_and_the_alias()
        {
            LocalStack.RequireAvailable();
            async Task<Outcome<T>> SendAsync<T>(Juice.MediatR.IRequest<Outcome<T>> command)
            {
                await using var scope = _app.Services.CreateAsyncScope();
                using var context = NotificationsMessageContext.Begin("test");
                return (await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>().Send(command, Ct))!;
            }
            var settings = JsonElement.Parse("""{ "server": "local", "from": "alias@juice.local" }""");
            var made = await SendAsync(new PutIntegration(Tenant, "from-alias-file", "email", "smtp", settings, null, 0, true) { ManagedBy = ManagedBy.AliasConfig });
            Assert.True(made.Kind is OutcomeKind.Ok or OutcomeKind.Created, $"{made.Kind} {made.Code} {made.Message}");

            await ProvisionAsync(Initial);

            using var admin = await AdminAsync();
            Assert.Equal(ManagedBy.AliasConfig, (await IntegrationsAsync(admin))["from-alias-file"].ManagedBy);
            var claimed = await Assert.ThrowsAsync<InvalidOperationException>(() => ProvisionAsync(Initial.Replace("\"mail\":",
                "\"from-alias-file\": { \"channel\": \"email\", \"kind\": \"smtp\", \"settings\": { \"server\": \"local\", \"from\": \"file@juice.local\" } },\n\"mail\":")));
            Assert.Contains("from-alias-file", claimed.Message);
            using (var edit = await admin.PutAsJsonAsync($"{Url}/integrations/from-alias-file", new PutIntegrationBody("email", "smtp", null, null, null, false), Ct))
            {
                Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ManagedByConfiguration), (edit.StatusCode, await CodeAsync(edit)));
            }
            Assert.Equal(ErrorCodes.ManagedByConfiguration,
                (await SendAsync(new PutIntegration(Tenant, "from-alias-file", null, null, null, null, null, false) { ManagedBy = ManagedBy.WebhooksAlias })).Code);
            Assert.Equal(ErrorCodes.ManagedByConfiguration, (await SendAsync(new DeleteIntegration(Tenant, "from-alias-file") { ManagedBy = ManagedBy.WebhooksAlias })).Code);
            var kept = (await IntegrationsAsync(admin))["from-alias-file"];
            Assert.Equal((ManagedBy.AliasConfig, true, "alias@juice.local"), (kept.ManagedBy, kept.Enabled, kept.Settings.GetProperty("from").GetString()));
            // Its own writer changes it, and takes over what the alias made through the producer's API (declared in its configuration later).
            Assert.Equal(OutcomeKind.Ok, (await SendAsync(new PutIntegration(Tenant, "from-alias-file", null, null, null, null, null, false) { ManagedBy = ManagedBy.AliasConfig })).Kind);
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

        [Fact]
        public async Task An_integration_the_alias_made_is_taken_over_by_alias_config_in_place_and_recorded()
        {
            LocalStack.RequireAvailable();
            var settings = JsonElement.Parse("""{ "server": "local", "from": "alias@juice.local" }""");
            var made = await AsOwnerAsync(new PutIntegration(Tenant, "taken", "email", "smtp", settings, null, 0, true) { ManagedBy = ManagedBy.WebhooksAlias });
            Assert.True(made.Kind is OutcomeKind.Ok or OutcomeKind.Created, $"{made.Kind} {made.Code} {made.Message}");
            Guid id;
            await using (var db = _app.Database.Context())
            {
                id = (await db.Integrations.SingleAsync(i => i.TenantId == Tenant && i.Key == "taken", Ct)).Id;
            }

            // The same definition, declared in the producer's configuration now.
            var taken = await AuditedAsync(new PutIntegration(Tenant, "taken", "email", "smtp", settings, null, 0, true) { ManagedBy = ManagedBy.AliasConfig });

            Assert.Equal(OutcomeKind.Ok, taken.Kind);
            await using (var db = _app.Database.Context())
            {
                var row = await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == Tenant && i.Key == "taken", Ct);
                Assert.Equal((id, ManagedBy.AliasConfig), (row.Id, row.ManagedBy));
            }
            var integrationChange = await ChangeAsync("integrations");
            Assert.Contains("ManagedBy", integrationChange);
            Assert.Contains(ManagedBy.AliasConfig, integrationChange);
            Assert.Equal(ErrorCodes.ManagedByConfiguration,
                (await AsOwnerAsync(new PutIntegration(Tenant, "taken", null, null, null, null, null, false) { ManagedBy = ManagedBy.WebhooksAlias })).Code);
        }

        [Fact]
        public async Task A_policy_the_alias_made_is_taken_over_by_alias_config_in_place_and_recorded()
        {
            LocalStack.RequireAvailable();
            var made = await AsOwnerAsync(new PutEscalationPolicy(Tenant, "taken-policy", PolicySteps, true) { ManagedBy = ManagedBy.WebhooksAlias });
            Assert.True(made.Kind is OutcomeKind.Ok or OutcomeKind.Created, $"{made.Kind} {made.Code} {made.Message}");
            Guid id;
            await using (var db = _app.Database.Context())
            {
                id = (await db.EscalationPolicies.SingleAsync(p => p.TenantId == Tenant && p.Key == "taken-policy", Ct)).Id;
            }

            var taken = await AuditedAsync(new PutEscalationPolicy(Tenant, "taken-policy", PolicySteps, true) { ManagedBy = ManagedBy.AliasConfig });

            Assert.Equal(OutcomeKind.Ok, taken.Kind);
            await using (var db = _app.Database.Context())
            {
                var row = await db.EscalationPolicies.AsNoTracking().SingleAsync(p => p.TenantId == Tenant && p.Key == "taken-policy", Ct);
                Assert.Equal((id, ManagedBy.AliasConfig), (row.Id, row.ManagedBy));
            }
            var policyChange = await ChangeAsync("escalation_policies");
            Assert.Contains("ManagedBy", policyChange);
            Assert.Contains(ManagedBy.AliasConfig, policyChange);
            Assert.Equal(ErrorCodes.ManagedByConfiguration,
                (await AsOwnerAsync(new PutEscalationPolicy(Tenant, "taken-policy", null, false) { ManagedBy = ManagedBy.WebhooksAlias })).Code);
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
