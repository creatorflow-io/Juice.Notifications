using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Juice.Notifications.Admin;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Embedded;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Juice.Notifications.IntegrationTests.EmbeddedHostTests;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The tenant-admin API in a host that embeds the pipeline (phase-5 M44-3; m44-architecture §3.4, §5.4, §9): mapped under the host's prefix,
    /// behind the host's authentication, with the host's roles as rights (Admin: all; Operator: read and operate; Viewer: none), only the
    /// embedded pages, only the host's tenant, and the admin's own JSON whatever the host's options are.
    /// </summary>
    public sealed class EmbeddedAdminTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000ad301");
        private const string Prefix = "/notifications";
        private static readonly string Api = $"{Prefix}/api/v1/tenants/{Tenant}";
        private readonly ScratchDatabase _database = new();

        public ValueTask InitializeAsync() => _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        /// <summary>The host's sign-in: the role in the <c>X-Role</c> header, a user named in <c>X-User</c>; none is anonymous.</summary>
        private sealed class HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
        {
            public const string Name = "host";

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                if (Request.Headers["X-Role"].ToString() is not { Length: > 0 } role)
                {
                    return Task.FromResult(AuthenticateResult.NoResult());
                }
                var user = Request.Headers["X-User"].ToString() is { Length: > 0 } name ? name : "alice";
                var identity = new ClaimsIdentity([new Claim("preferred_username", user), new Claim(ClaimTypes.Role, role)], Name);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Name)));
            }
        }

        /// <summary>The host's roles as rights (p5-d6), for its one tenant.</summary>
        private sealed class RoleAccess() : SingleTenantAdminAccess(Tenant)
        {
            protected override ValueTask<AdminRights> RightsAsync(ClaimsPrincipal user, CancellationToken ct)
                => ValueTask.FromResult(user.IsInRole("Admin") ? AdminRights.All
                    : user.IsInRole("Operator") ? AdminRights.Read | AdminRights.Operate
                    : AdminRights.None);
        }

        /// <summary>A faulty access that grants everything everywhere: the embedded tenant check must still hold.</summary>
        private sealed class EverywhereAccess : INotificationsAdminAccess
        {
            public ValueTask<AdminRights> RightsAsync(ClaimsPrincipal user, Guid tenantId, CancellationToken ct = default) => ValueTask.FromResult(AdminRights.All);

            public ValueTask<AdminTenants> TenantsAsync(ClaimsPrincipal user, CancellationToken ct = default) => ValueTask.FromResult(new AdminTenants(true, []));
        }

        private static IServiceCollection Embedded(IServiceCollection services, string connectionString, bool runStages = false)
            => services.AddJuiceNotificationsEmbedded(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Migrate"] = "true",
                ["Stages:Run"] = runStages ? "true" : "false",
                ["Stages:Planner:Poll"] = "00:00:00.2",
                ["Stages:DeliveryPlanner:Poll"] = "00:00:00.2",
                ["Stages:Dispatcher:Poll"] = "00:00:00.2",
                // The receiver is plain http on this machine's private address, allowlisted through the admin.
                ["Delivery:AllowHttpToAllowlisted"] = "true",
            }).Build(), o =>
            {
                EmbeddedHost.Configure(connectionString)(o);
                o.TenantId = Tenant;
                o.ApplicationKey = "adminhost";
                o.Types.Clear();
                o.Types.Add(new Types.TypeDeclaration("adminhost.thing", "A thing", "things", null, null, null, null,
                    JsonSerializer.SerializeToElement(new { type = "object" }), CorrelationRules.Optional, null, null,
                    JsonSerializer.SerializeToElement(new { webhook = "passthrough" })));
            });

        /// <summary>
        /// A minimal host: its own authentication and PascalCase JSON, the embedded pipeline, the admin mapped under the prefix; and, for a request
        /// with <c>X-Host-Transaction: rollback</c>, a transaction of the host's own open around it and rolled back after (a unit-of-work host).
        /// </summary>
        private async Task<WebApplication> HostAsync(bool everywhere = false, bool runStages = false)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAuthentication(HeaderAuthentication.Name).AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>(HeaderAuthentication.Name, null);
            builder.Services.AddAuthorization();
            // The host's own API writes PascalCase: the admin's answers must not follow it.
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = null);
            builder.Services.AddSingleton<RecordingSink>();
            builder.Services.AddSingleton<INotificationsAuditSink>(sp => sp.GetRequiredService<RecordingSink>());
            // The host's own context and transaction, as EmbeddedHostTests' host has them: the client writes in it.
            builder.Services.AddDbContext<HostDbContext>(o => o.UseNpgsql(_database.ConnectionString));
            builder.Services.AddSingleton<EmbeddedHost.Committed>();
            builder.Services.AddScoped<HostTransaction>();
            builder.Services.AddScoped<INotificationTransaction>(sp => sp.GetRequiredService<HostTransaction>());
            Embedded(builder.Services, _database.ConnectionString, runStages);
            builder.Services.AddJuiceNotificationsAdmin();
            if (everywhere)
            {
                builder.Services.AddScoped<INotificationsAdminAccess, EverywhereAccess>();
            }
            else
            {
                builder.Services.AddScoped<INotificationsAdminAccess, RoleAccess>();
            }
            var app = builder.Build();
            app.Use(async (http, next) =>
            {
                if (http.Request.Headers["X-Host-Transaction"] != "rollback")
                {
                    await next(http);
                    return;
                }
                var host = http.RequestServices.GetRequiredService<HostDbContext>();
                await using var transaction = await host.Database.BeginTransactionAsync(http.RequestAborted);
                host.Things.Add(new Thing { Id = Guid.CreateVersion7(), Name = "rolled back with the request" });
                await host.SaveChangesAsync(http.RequestAborted);
                await next(http);
                await transaction.RollbackAsync(CancellationToken.None);
            });
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapJuiceNotificationsAdmin(Prefix);
            await app.StartAsync(Ct);
            return app;
        }

        /// <summary>The host's schema and table, beside the pipeline's (as EmbeddedHostTests creates them).</summary>
        private async Task HostSchemaAsync()
        {
            await using var db = new HostDbContext(new DbContextOptionsBuilder<HostDbContext>().UseNpgsql(_database.ConnectionString).Options);
            await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS host; CREATE TABLE IF NOT EXISTS host.things (id uuid PRIMARY KEY, name text NOT NULL);", Ct);
        }

        private static HttpClient Client(WebApplication app, string? role, string? user = null)
        {
            var client = app.GetTestClient();
            if (role is not null)
            {
                client.DefaultRequestHeaders.Add("X-Role", role);
            }
            if (user is not null)
            {
                client.DefaultRequestHeaders.Add("X-User", user);
            }
            return client;
        }

        private static async Task<(HttpStatusCode Status, string Code)> ProblemAsync(Task<HttpResponseMessage> call)
        {
            using var response = await call;
            var body = await response.Content.ReadAsStringAsync(Ct);
            if (body.Length == 0)
            {
                return (response.StatusCode, "");
            }
            using var json = JsonDocument.Parse(body);
            return (response.StatusCode, json.RootElement.TryGetProperty("code", out var code) ? code.GetString()! : "");
        }

        private static async Task<HttpResponseMessage> OkAsync(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
            return response;
        }

        [Fact]
        public async Task The_access_answer_names_the_mode_the_embedded_pages_and_the_callers_rights()
        {
            LocalStack.RequireAvailable();
            await using var app = await HostAsync();
            using var admin = Client(app, "Admin");
            var access = (await (await OkAsync(admin.GetAsync($"{Prefix}/api/v1/access", Ct))).Content.ReadFromJsonAsync<AccessResponse>(Ct))!;
            Assert.Equal((false, false, AccessModes.Embedded), (access.PlatformAdmin, access.AllTenants, access.Mode));
            Assert.Equal(AdminPages.Embedded, access.Pages);
            var tenant = Assert.Single(access.Tenants);
            Assert.Equal(Tenant, tenant.Id);
            Assert.Equal(["read", "operate", "configure"], tenant.Rights);

            using var operatorClient = Client(app, "Operator");
            var operated = (await (await OkAsync(operatorClient.GetAsync($"{Prefix}/api/v1/access", Ct))).Content.ReadFromJsonAsync<AccessResponse>(Ct))!;
            Assert.Equal(["read", "operate"], Assert.Single(operated.Tenants).Rights);

            using var viewer = Client(app, "Viewer");
            Assert.Empty((await (await OkAsync(viewer.GetAsync($"{Prefix}/api/v1/access", Ct))).Content.ReadFromJsonAsync<AccessResponse>(Ct))!.Tenants);
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotTenantAdmin), await ProblemAsync(viewer.GetAsync($"{Api}/routes", Ct)));

            using var anonymous = Client(app, null);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Prefix}/api/v1/access", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Api}/routes", Ct)).StatusCode);
        }

        [Fact]
        public async Task An_operator_reads_and_retries_but_doesnt_configure()
        {
            LocalStack.RequireAvailable();
            await using var app = await HostAsync();
            using var operatorClient = Client(app, "Operator");
            await OkAsync(operatorClient.GetAsync($"{Api}/routes", Ct));
            await OkAsync(operatorClient.GetAsync($"{Api}/deliveries", Ct));
            // Operate passes the rights filter: an unknown delivery is the handler's not_found, not a refusal.
            Assert.Equal((HttpStatusCode.NotFound, ErrorCodes.NotFound), await ProblemAsync(operatorClient.PostAsync($"{Api}/deliveries/{Guid.CreateVersion7()}/retry", null, Ct)));
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotAllowed),
                await ProblemAsync(operatorClient.PutAsJsonAsync($"{Api}/routes/ops", new PutRouteBody(JsonSerializer.SerializeToElement(new { }), [], true), Ct)));
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotAllowed),
                await ProblemAsync(operatorClient.PostAsJsonAsync($"{Api}/correlations/alert%3A1/resolve?application=adminhost", new ResolveCorrelationBody(null, null), Ct)));
        }

        /// <summary>An admin configures the whole embedded setup end to end; each change is heard by the host's audit sink, naming the admin.</summary>
        [Fact]
        public async Task An_admin_configures_the_embedded_setup_and_the_host_hears_each_change_by_the_admins_name()
        {
            LocalStack.RequireAvailable();
            await using var app = await HostAsync();
            using var admin = Client(app, "Admin", user: "ada");
            await OkAsync(admin.PutAsJsonAsync($"{Api}/integrations/hooks", new PutIntegrationBody("webhook", "webhook", null, TestHooks.SecretRef, null, null), Ct));
            await OkAsync(admin.PutAsJsonAsync($"{Api}/integrations/hooks/targets/ops", new PutTargetBody("https://hooks.example.com/ops", null, null), Ct));
            await OkAsync(admin.PutAsJsonAsync($"{Api}/routes/all", new PutRouteBody(JsonSerializer.SerializeToElement(new { type = "adminhost.*" }), ["ops"], true), Ct));
            using (var muted = await OkAsync(admin.PostAsJsonAsync($"{Api}/mutes", new AddMuteBody(JsonSerializer.SerializeToElement(new { type = "adminhost.thing" }), null,
                DateTimeOffset.UtcNow.AddHours(1), "maintenance"), Ct)))
            {
                Assert.Equal(HttpStatusCode.Created, muted.StatusCode);
                Assert.StartsWith($"/api/v1/tenants/{Tenant}/mutes/", muted.Headers.Location?.OriginalString);
            }
            await OkAsync(admin.PutAsJsonAsync($"{Api}/escalation-policies/page", new PutEscalationPolicyBody(
                JsonSerializer.SerializeToElement(new[] { new { afterMinutes = 5, recipients = new[] { new { kind = "route" } } } }), true), Ct));
            await OkAsync(admin.PutAsJsonAsync($"{Api}/allowlist", new PutAllowlistBody("10.20.0.0/16", [8443]), Ct));
            await OkAsync(admin.PutAsJsonAsync($"{Api}/settings", new TenantSettingsBody(50), Ct));
            await OkAsync(admin.PutAsJsonAsync($"{Api}/types/adminhost.thing/templates/webhook", new PutTemplateBody("{\"x\":1}", null), Ct));
            foreach (var page in new[] { "integrations", "routes", "mutes", "escalation-policies", "allowlist", "settings", "types", "overview", "notifications",
                "correlations", "storms", "deliveries", "providers" })
            {
                await OkAsync(admin.GetAsync($"{Api}/{page}", Ct));
            }

            var sink = app.Services.GetRequiredService<RecordingSink>();
            foreach (var table in new[] { "integrations", "integration_targets", "routes", "mutes", "escalation_policies", "destination_allowlist" })
            {
                Assert.Contains(sink.Changes, c => c.Table == table && c.TenantId == Tenant.ToString());
            }
            Assert.Contains(sink.Changes, c => c.Table == "routes" && c.User == "ada");
        }

        /// <summary>
        /// An admin change commits on its own transaction (m44-architecture §6.4): with the host's own transaction open around the request and
        /// rolled back after, the change stays, and the host's row doesn't.
        /// </summary>
        [Fact]
        public async Task An_admin_change_commits_on_its_own_while_the_hosts_transaction_rolls_back()
        {
            LocalStack.RequireAvailable();
            await HostSchemaAsync();
            await using var app = await HostAsync();
            using var admin = Client(app, "Admin");
            admin.DefaultRequestHeaders.Add("X-Host-Transaction", "rollback");
            await OkAsync(admin.PutAsJsonAsync($"{Api}/allowlist", new PutAllowlistBody("own-transaction.example.com", [8443]), Ct));

            await using var db = _database.Context();
            Assert.True(await db.Allowlist.AnyAsync(a => a.TenantId == Tenant && a.Destination == "own-transaction.example.com", Ct));
            await using var host = new HostDbContext(new DbContextOptionsBuilder<HostDbContext>().UseNpgsql(_database.ConnectionString).Options);
            Assert.Empty(await host.Things.ToListAsync(Ct));
        }

        /// <summary>
        /// The embedded setup is the admin's (m44-architecture §9): an integration, target, route and allowlist entry put through the admin, and a
        /// request sent through the client in the host's transaction is delivered by that route.
        /// </summary>
        [Fact]
        public async Task A_request_sent_through_the_client_is_delivered_by_the_route_the_admin_configured()
        {
            LocalStack.RequireAvailable();
            TestHooks.SetSecret();
            await HostSchemaAsync();
            await using var receiver = await DeliveryTests.Receiver.StartAsync();
            await using var app = await HostAsync(runStages: true);
            using var admin = Client(app, "Admin");
            await OkAsync(admin.PutAsJsonAsync($"{Api}/integrations/hooks", new PutIntegrationBody("webhook", "webhook", null, TestHooks.SecretRef, null, null), Ct));
            await OkAsync(admin.PutAsJsonAsync($"{Api}/integrations/hooks/targets/receiver", new PutTargetBody(receiver.Url, null, null), Ct));
            await OkAsync(admin.PutAsJsonAsync($"{Api}/routes/all", new PutRouteBody(JsonSerializer.SerializeToElement(new { type = "adminhost.*" }), ["receiver"], true), Ct));
            await OkAsync(admin.PutAsJsonAsync($"{Api}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port]), Ct));

            await using (var scope = app.Services.CreateAsyncScope())
            {
                var host = scope.ServiceProvider.GetRequiredService<HostDbContext>();
                await using var transaction = await host.Database.BeginTransactionAsync(Ct);
                await scope.ServiceProvider.GetRequiredService<INotificationClient>().SendAsync(
                    new NotificationToSend("adminhost.thing", "admin-route-1") { Data = JsonSerializer.SerializeToElement(new { n = 7 }) }, Ct);
                await transaction.CommitAsync(Ct);
                foreach (var action in scope.ServiceProvider.GetRequiredService<HostTransaction>().OnCommit)
                {
                    action();
                }
            }
            for (var i = 0; i < 300 && receiver.Requests.IsEmpty; i++)
            {
                await Task.Delay(100, Ct);
            }
            var received = Assert.Single(receiver.Requests);
            Assert.Contains("\"n\":7", received.Body);
        }

        [Fact]
        public async Task Another_tenant_is_forbidden_even_to_an_access_that_grants_everything()
        {
            LocalStack.RequireAvailable();
            await using var app = await HostAsync(everywhere: true);
            using var admin = Client(app, "Admin");
            var other = $"{Prefix}/api/v1/tenants/{Guid.CreateVersion7()}";
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.TenantForbidden), await ProblemAsync(admin.GetAsync($"{other}/routes", Ct)));
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.TenantForbidden),
                await ProblemAsync(admin.PutAsJsonAsync($"{other}/allowlist", new PutAllowlistBody("10.0.0.0/8", null), Ct)));
            await OkAsync(admin.GetAsync($"{Api}/routes", Ct));
        }

        /// <summary>Only the embedded pages are mapped, only under the prefix: the service's other routes don't exist here.</summary>
        [Fact]
        public async Task Routes_the_mode_lacks_and_routes_outside_the_prefix_are_not_there()
        {
            LocalStack.RequireAvailable();
            await using var app = await HostAsync();
            using var admin = Client(app, "Admin");
            foreach (var path in new[] { $"{Api}/subscribers", $"{Api}/groups", $"{Api}/topics", $"{Api}/resolvers", $"{Api}/resolver-kinds", $"{Api}/display-groups",
                $"{Api}/me/inbox", $"{Api}/notifications/x", $"{Prefix}/api/v1/applications", $"{Prefix}/api/v1/callbacks/telegram/x",
                $"/api/v1/tenants/{Tenant}/routes", "/api/v1/access" })
            {
                Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(path, Ct)).StatusCode);
            }
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await admin.PostAsJsonAsync($"{Api}/notifications", new { }, Ct)).StatusCode);
        }

        /// <summary>The host's JSON is PascalCase; the admin's answers stay camelCase with camelCase enums, problems included.</summary>
        [Fact]
        public async Task The_admins_answers_keep_their_json_whatever_the_hosts_options()
        {
            LocalStack.RequireAvailable();
            await using var app = await HostAsync();
            using var admin = Client(app, "Admin");
            await OkAsync(admin.PutAsJsonAsync($"{Api}/allowlist", new PutAllowlistBody("json.example.com", [8443]), Ct));
            var allowlist = await (await OkAsync(admin.GetAsync($"{Api}/allowlist", Ct))).Content.ReadAsStringAsync(Ct);
            Assert.Contains("\"destination\":\"json.example.com\"", allowlist);
            Assert.Contains("\"managedBy\":\"api\"", allowlist);
            var overview = await (await OkAsync(admin.GetAsync($"{Api}/overview", Ct))).Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("\"At\":", overview);
            var access = await (await OkAsync(admin.GetAsync($"{Prefix}/api/v1/access", Ct))).Content.ReadAsStringAsync(Ct);
            Assert.Contains("\"mode\":\"embedded\"", access);
            using var refused = await admin.PutAsJsonAsync($"{Api}/routes/BAD KEY", new PutRouteBody(null, null, null), Ct);
            var problem = await refused.Content.ReadAsStringAsync(Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("\"code\":\"invalid_configuration\"", problem);
            Assert.Contains("\"pointer\":", problem);
            Assert.Contains("\"status\":400", problem);
        }

        [Fact]
        public void Mapping_without_an_access_or_with_a_bad_prefix_throws()
        {
            var builder = WebApplication.CreateBuilder();
            Embedded(builder.Services, "Host=localhost;Database=none");
            builder.Services.AddJuiceNotificationsAdmin();
            using var app = builder.Build();
            Assert.Throws<InvalidOperationException>(() => app.MapJuiceNotificationsAdmin(Prefix));

            var withAccess = WebApplication.CreateBuilder();
            Embedded(withAccess.Services, "Host=localhost;Database=none");
            withAccess.Services.AddJuiceNotificationsAdmin().AddScoped<INotificationsAdminAccess, RoleAccess>();
            using var mapped = withAccess.Build();
            foreach (var bad in new[] { "notifications", "/n/", "/{x}", "//n" })
            {
                Assert.Throws<ArgumentException>(() => mapped.MapJuiceNotificationsAdmin(bad));
            }
        }

        /// <summary>Embedded plus admin: still nothing of the service in the container (M43's composition rule).</summary>
        [Fact]
        public void The_admin_adds_nothing_of_the_service_to_the_container()
        {
            var services = new ServiceCollection().AddLogging();
            Embedded(services, "Host=localhost;Database=none");
            services.AddJuiceNotificationsAdmin();
            var named = services.SelectMany(d => new[] { d.ServiceType, d.ImplementationType, d.ImplementationInstance?.GetType() })
                .Where(t => t is not null).Select(t => t!.FullName ?? t.Name).Distinct().ToList();
            string[] forbidden = ["Microsoft.AspNetCore.Authentication", "Microsoft.AspNetCore.SignalR", "RabbitMQ", "Juice.EventBus.RabbitMQ", "Juice.Audit",
                "Juice.MultiTenant", "Juice.Notifications.Api.Producers", "Juice.Notifications.Api.TenantAccessAdminAccess", "Juice.Notifications.Messaging",
                "Juice.Notifications.People.IdentityRecipientResolver", "Juice.Notifications.People.ServiceTypesProvisioner", "Microsoft.AspNetCore.Http.Json.JsonOptions"];
            Assert.DoesNotContain(named, n => forbidden.Any(f => n.StartsWith(f, StringComparison.Ordinal)));
            Assert.DoesNotContain(services, d => d.ServiceType == typeof(IConfigureOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>));
            using var provider = services.BuildServiceProvider();
            Assert.Null(provider.GetService<IAdminFallbackCaller>());
            Assert.Null(provider.GetService<IAdminPlatformRole>());
            Assert.Equal(new Hosting.NotificationsHosting(Hosting.NotificationsMode.Embedded, Tenant), provider.GetRequiredService<Hosting.NotificationsHosting>());
        }

        /// <summary>
        /// The pages and rights as mapped (read from the endpoints, not listed by hand): every tenant route belongs to a page the mode has, and
        /// every change needs Configure but retry and replay (Operate) and the template preview (Read).
        /// </summary>
        [Fact]
        public async Task Every_mapped_route_belongs_to_an_embedded_page_and_needs_its_right()
        {
            LocalStack.RequireAvailable();
            await using var app = await HostAsync();
            AdminRoutes.AssertPagesAndRights(app.Services, AdminPages.Embedded, $"{Prefix}/api/v1/tenants/");
        }
    }

    /// <summary>The admin's mapped routes, as the endpoint data source has them, checked against the pages and the rights rule.</summary>
    internal static class AdminRoutes
    {
        public static void AssertPagesAndRights(IServiceProvider services, IReadOnlyList<string> pages, string tenantPrefix)
        {
            var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(e => ("/" + e.RoutePattern.RawText?.TrimStart('/')).StartsWith(tenantPrefix, StringComparison.Ordinal)
                    && e.Metadata.GetMetadata<AdminPageMetadata>() is not null)
                .ToList();
            Assert.NotEmpty(endpoints);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var endpoint in endpoints)
            {
                var page = endpoint.Metadata.GetMetadata<AdminPageMetadata>()!.Page;
                Assert.Contains(page, pages);
                seen.Add(page);
                var path = endpoint.RoutePattern.RawText!;
                foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                {
                    var right = NotificationsAdminRegistration.Needed(endpoint, method);
                    var expected = HttpMethods.IsGet(method) ? AdminRights.Read
                        : path.EndsWith("/retry", StringComparison.Ordinal) || path.EndsWith("/replay", StringComparison.Ordinal) ? AdminRights.Operate
                        : path.EndsWith("/templates/preview", StringComparison.Ordinal) ? AdminRights.Read
                        : AdminRights.Configure;
                    Assert.True(expected == right, $"{method} {path} needs {right}, not {expected}");
                }
            }
            // No page the mode has is without a route (the service's people pages are mapped by the service itself).
            Assert.All(pages.Where(p => p is not (AdminPages.DisplayGroups or AdminPages.Subscribers or AdminPages.Groups or AdminPages.Topics or AdminPages.Applications)),
                p => Assert.Contains(p, seen));
        }
    }
}
