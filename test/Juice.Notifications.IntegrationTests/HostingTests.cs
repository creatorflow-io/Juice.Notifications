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
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)).Prepend(new("ConnectionStrings:Notifications", "Host=x")))
                .Build();
            new ServiceCollection().AddLogging().AddJuiceNotifications(configuration);
        }

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
