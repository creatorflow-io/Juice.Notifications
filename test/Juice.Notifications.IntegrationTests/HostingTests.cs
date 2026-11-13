using Juice.Notifications.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Configuration the host refuses at start, rather than failing on the first request. Needs no local stack.</summary>
    public sealed class HostingTests
    {
        private static void Register(params (string Key, string? Value)[] settings)
        {
            // Defaults first; a test's own settings come after and win.
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection([new("ConnectionStrings:Notifications", "Host=x"), new("Audit:Endpoint", "http://audit.test:8080")])
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                .Build();
            new ServiceCollection().AddLogging().AddJuiceNotifications(configuration);
        }

        private static readonly (string, string?) Authority = ("Auth:Authority", "https://idp.example/realms/juice");

        [Fact]
        public void A_missing_audit_endpoint_is_refused()
            => Assert.Contains("Audit:Endpoint is required", Assert.Throws<InvalidOperationException>(() => Register(Authority, ("Audit:Endpoint", null))).Message);

        [Fact]
        public void An_audit_endpoint_that_is_not_a_url_is_refused()
            => Assert.Contains("Audit:Endpoint is required", Assert.Throws<InvalidOperationException>(() => Register(Authority, ("Audit:Endpoint", "audit:8080"))).Message);

        [Fact]
        public void Audit_can_be_turned_off()
            => Register(Authority, ("Audit:Endpoint", null), ("Audit:Enabled", "false"));

        [Fact]
        public void A_missing_authority_is_refused()
            => Assert.Contains("Auth:Authority is required", Assert.Throws<InvalidOperationException>(() => Register()).Message);

        [Fact]
        public void An_http_authority_is_refused_while_https_metadata_is_required()
            => Assert.Contains("is not https", Assert.Throws<InvalidOperationException>(() => Register(("Auth:Authority", "http://idp.example/realms/juice"))).Message);

        [Fact]
        public void An_http_authority_is_allowed_for_local_development()
            => Register(("Auth:Authority", "http://localhost:8081/realms/juice"), ("Auth:RequireHttpsMetadata", "false"));

        [Fact]
        public void An_https_authority_is_allowed()
            => Register(("Auth:Authority", "https://idp.example/realms/juice"));
    }
}
