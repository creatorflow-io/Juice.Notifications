using Juice.MultiTenant;
using Juice.MultiTenant.Testing;
using Juice.Notifications.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The tenant pattern (juice-docs decision record): the shared tenant checks every service passes, against this API, with the shared test
    /// tenants (A active, B suspended, C starting) as its directory and test callers instead of Keycloak's tokens.
    /// </summary>
    public sealed class TenancyTests : IAsyncLifetime
    {
        private readonly NotificationsApp _app = new(host =>
        {
            host.UseSetting("Tenancy:Directory", "Configured");
            var tenants = new[] { (TestTenants.A, "Active"), (TestTenants.B, "Suspended"), (TestTenants.C, "Initializing") };
            for (var i = 0; i < tenants.Length; i++)
            {
                host.UseSetting($"Tenancy:Tenants:{i}:Id", tenants[i].Item1.ToString());
                host.UseSetting($"Tenancy:Tenants:{i}:Status", tenants[i].Item2);
            }
            host.ConfigureTestServices(services =>
            {
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestCallerAuthentication>(TestCallerAuthentication.Scheme, null);
                services.PostConfigure<AuthenticationOptions>(o =>
                {
                    o.DefaultScheme = TestCallerAuthentication.Scheme;
                    o.DefaultAuthenticateScheme = TestCallerAuthentication.Scheme;
                    o.DefaultChallengeScheme = TestCallerAuthentication.Scheme;
                });
            });
        });

        public ValueTask InitializeAsync() => _app.InitializeAsync();

        public ValueTask DisposeAsync() => _app.DisposeAsync();

        [Fact]
        public async Task The_api_passes_the_shared_tenant_checks()
        {
            LocalStack.RequireAvailable();
            using var client = _app.Client();
            var failures = await TenantConformance.CheckAsync(client, new TenantConformanceTarget
            {
                MemberRequest = tenant => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tenants/{tenant}/me/inbox"),
                AdminRequest = tenant => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tenants/{tenant}/routes"),
                TenantAdminRole = new AuthOptions().TenantAdminRole,
                PlatformAdminRole = Roles.PlatformAdmin,
            });
            Assert.Empty(failures);
        }
    }
}
