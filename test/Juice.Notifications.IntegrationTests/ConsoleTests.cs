using System.Net;
using System.Net.Http.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>The App with a stand-in Console in its web root, as the image has the built one (phase-4c-plan I49).</summary>
    public sealed class ConsoleApp : IAsyncLifetime
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("notifications-console-").FullName;

        public NotificationsApp App { get; }

        public ConsoleApp()
        {
            File.WriteAllText(Path.Combine(Root, "index.html"), "<!doctype html><title>console</title>");
            File.WriteAllText(Path.Combine(Root, "main-ABCD1234.js"), "export const x = 1;");
            File.WriteAllText(Path.Combine(Root, "chunk-Ce0mZ_Dd.js"), "export const y = 2;");
            File.WriteAllText(Path.Combine(Root, "logo-darkmode.svg"), "<svg/>");
            Directory.CreateDirectory(Path.Combine(Root, "media"));
            File.WriteAllText(Path.Combine(Root, "media", "roboto-Ab_cD-12.woff2"), "font");
            File.WriteAllText(Path.Combine(Root, "media", "banner.png"), "png");
            File.WriteAllText(Path.Combine(Root, "media", "settings-override.json"), "{}");
            App = new NotificationsApp(host => host.UseSetting("webroot", Root));
        }

        public ValueTask InitializeAsync() => App.InitializeAsync();

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>
    /// The App serves the Console from its web root (phase-4c-plan I49, proof point 7): files as files, any other path outside the API as the
    /// index page, and API, hub and health paths never as the Console.
    /// </summary>
    public sealed class ConsoleHostingTests(ConsoleApp fixture) : IClassFixture<ConsoleApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task Console_files_routes_and_api_paths_are_told_apart()
        {
            LocalStack.RequireAvailable();
            using var client = fixture.App.Client();

            using var script = await client.GetAsync("/main-ABCD1234.js", Ct);
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
            Assert.Contains("immutable", script.Headers.CacheControl?.ToString());
            using var chunk = await client.GetAsync("/chunk-Ce0mZ_Dd.js", Ct);
            Assert.Contains("immutable", chunk.Headers.CacheControl?.ToString());
            // A plain asset whose name happens to end in a dash and 8 letters isn't a bundle: it may change.
            using var asset = await client.GetAsync("/logo-darkmode.svg", Ct);
            Assert.True(asset.Headers.CacheControl?.NoCache);
            // Media: hashed, for good; unhashed (added to public/media), may change.
            using var font = await client.GetAsync("/media/roboto-Ab_cD-12.woff2", Ct);
            Assert.Contains("immutable", font.Headers.CacheControl?.ToString());
            using var banner = await client.GetAsync("/media/banner.png", Ct);
            Assert.True(banner.Headers.CacheControl?.NoCache);
            // Nor any other type there, whatever its name: only the fonts and images Angular hashes.
            using var data = await client.GetAsync("/media/settings-override.json", Ct);
            Assert.True(data.Headers.CacheControl?.NoCache);

            // Keys with a dot under a tenant (a type, a correlation) are routes too, though they look like file names.
            foreach (var path in new[] { "/", "/tenants/0193f0aa-0000-7000-8000-0000000000a1/deliveries?status=Failed", "/applications",
                "/tenants/0193f0aa-0000-7000-8000-0000000000a1/types/sample.build.done", "/tenants/0193f0aa-0000-7000-8000-0000000000a1/correlations/sample/deploy.v1.2" })
            {
                using var route = await client.GetAsync(path, Ct);
                Assert.Equal(HttpStatusCode.OK, route.StatusCode);
                Assert.Equal("text/html", route.Content.Headers.ContentType?.MediaType);
                Assert.Equal("DENY", route.Headers.GetValues("X-Frame-Options").Single());
                Assert.True(route.Headers.CacheControl?.NoCache, path);
            }

            // A missing file is never answered with the index page (a stale chunk must fail, not load HTML as script).
            using var missing = await client.GetAsync("/chunk-GONE1234.js", Ct);
            // (401 here: an unmatched path falls to the API's signed-in policy, which still isn't the Console.)
            Assert.NotEqual(HttpStatusCode.OK, missing.StatusCode);
            Assert.NotEqual("text/html", missing.Content.Headers.ContentType?.MediaType);

            foreach (var path in new[] { "/api/v1/nope", "/api/v1/access", "/hubs/nope", "/health/nope" })
            {
                using var api = await client.GetAsync(path, Ct);
                Assert.NotEqual("text/html", api.Content.Headers.ContentType?.MediaType);
                Assert.True(api.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, $"{path}: {api.StatusCode}");
            }
        }
    }

    /// <summary>
    /// <c>GET /api/v1/access</c> (phase-4c-plan I50): the tenants the caller administers, as <c>ITenantAccess</c> decides; the Console's switcher
    /// and menu come from it.
    /// </summary>
    public sealed class AccessTests(ConsoleApp fixture) : IClassFixture<ConsoleApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid TestTenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000000a1");

        private async Task<AccessResponse> AccessAsync(string token)
        {
            using var client = fixture.App.Client(token);
            return (await client.GetFromJsonAsync<AccessResponse>("/api/v1/access", Ct))!;
        }

        [Fact]
        public async Task A_tenant_admin_is_offered_only_their_tenant()
        {
            LocalStack.RequireAvailable();
            var access = await AccessAsync(await LocalStack.TenantAdminTokenAsync());
            Assert.False(access.PlatformAdmin);
            Assert.False(access.AllTenants);
            Assert.Equal([TestTenant], access.Tenants.Select(t => t.Id));
            // Phase 5 (M44): the service has every page, and a tenant admin every right in its tenant.
            Assert.Equal(AccessModes.Service, access.Mode);
            Assert.Equal(AdminPages.Service, access.Pages);
            Assert.Equal(18, access.Pages.Distinct().Count());
            Assert.All(AdminPages.Embedded, page => Assert.Contains(page, access.Pages));
            Assert.Equal(["read", "operate", "configure"], access.Tenants.Single().Rights);
        }

        [Fact]
        public async Task A_platform_admin_is_offered_every_tenant_with_a_linked_application()
        {
            LocalStack.RequireAvailable();
            var (linked, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
            await using (var db = fixture.App.Database.Context())
            {
                var app = Seed.Application("acc" + Guid.NewGuid().ToString("N")[..8]);
                db.AddRange(app, new ApplicationTenant { ApplicationId = app.Id, TenantId = linked, LinkedAt = Seed.Now });
                await db.SaveChangesAsync(Ct);
            }
            var access = await AccessAsync(await LocalStack.AdminTokenAsync());
            Assert.True(access.PlatformAdmin);
            Assert.True(access.AllTenants);
            Assert.Contains(linked, access.Tenants.Select(t => t.Id));
            Assert.DoesNotContain(other, access.Tenants.Select(t => t.Id));
            Assert.Equal(access.Tenants.Select(t => t.Id).Order(), access.Tenants.Select(t => t.Id));
        }

        /// <summary>The local realm's Console admins, signed in as people (their realm roles, as the Console's client issues them).</summary>
        [Fact]
        public async Task The_local_console_admins_tess_and_paul_are_a_tenant_admin_and_a_platform_admin()
        {
            LocalStack.RequireAvailable();
            var tess = await AccessAsync(await LocalStack.PersonTokenAsync("tess"));
            Assert.False(tess.PlatformAdmin);
            Assert.Equal([TestTenant], tess.Tenants.Select(t => t.Id));
            var paul = await AccessAsync(await LocalStack.PersonTokenAsync("paul"));
            Assert.True(paul.PlatformAdmin);
        }

        [Fact]
        public async Task Someone_without_an_admin_role_is_offered_nothing_and_no_token_gets_401()
        {
            LocalStack.RequireAvailable();
            var access = await AccessAsync(await LocalStack.PersonTokenAsync());
            Assert.False(access.PlatformAdmin);
            Assert.False(access.AllTenants);
            Assert.Empty(access.Tenants);
            using var anonymous = fixture.App.Client();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/access", Ct)).StatusCode);
        }
    }
}
