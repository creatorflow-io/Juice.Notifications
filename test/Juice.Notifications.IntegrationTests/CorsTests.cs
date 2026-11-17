using System.Net;
using Juice.Notifications.Api;
using Microsoft.AspNetCore.Hosting;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>A browser app on another origin (phase-2-plan M17, <c>@juice-js/notifications</c>): <c>Cors:Origins</c> lets it call the API and the hub.</summary>
    public sealed class CorsTests : IAsyncLifetime
    {
        private const string App = "https://app.example.test";
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly NotificationsApp _app = new(host => host.UseSetting("Cors:Origins:0", App));

        public ValueTask InitializeAsync() => _app.InitializeAsync();

        public ValueTask DisposeAsync() => _app.DisposeAsync();

        private static HttpRequestMessage Preflight(string path, string origin, string method = "GET") => new(HttpMethod.Options, path)
        {
            Headers = { { "Origin", origin }, { "Access-Control-Request-Method", method }, { "Access-Control-Request-Headers", "authorization" } },
        };

        [Fact]
        public async Task A_listed_origin_passes_preflight_without_a_token_and_another_doesnt()
        {
            LocalStack.RequireAvailable();
            using var client = _app.Client();
            var tenant = $"/api/v1/tenants/{ProducerApp.Tenant}/me/inbox";

            using var allowed = await client.SendAsync(Preflight(tenant, App), Ct);
            Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
            Assert.Equal(App, Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Contains("authorization", string.Join(",", allowed.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
            Assert.False(allowed.Headers.Contains("Access-Control-Allow-Credentials"));

            // The hub's negotiate request, a POST.
            using var hub = await client.SendAsync(Preflight("/hubs/inbox/negotiate?negotiateVersion=1", App, "POST"), Ct);
            Assert.Equal(App, Assert.Single(hub.Headers.GetValues("Access-Control-Allow-Origin")));

            using var other = await client.SendAsync(Preflight(tenant, "https://evil.example.test"), Ct);
            Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));

            // A real call from the app still needs its token; the answer says the app may read it.
            using var call = new HttpRequestMessage(HttpMethod.Get, tenant) { Headers = { { "Origin", App } } };
            using var refused = await client.SendAsync(call, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal(App, Assert.Single(refused.Headers.GetValues("Access-Control-Allow-Origin")));
        }

        [Theory]
        [InlineData("https://app.example.test", true)]
        [InlineData("https://app.example.test/", true)]
        [InlineData("http://localhost:4201", true)]
        [InlineData("https://app.example.test/path", false)]
        [InlineData("https://*.example.test", false)]
        [InlineData("app.example.test", false)]
        [InlineData("ftp://app.example.test", false)]
        public void Only_exact_origins_are_allowed(string origin, bool valid)
            => Assert.Equal(valid, new CorsSettings { Origins = [origin] }.Problem() is null);
    }
}
