using System.Net;
using Juice.Notifications.Providers;
using Juice.Notifications.Tests.ProviderContracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Tests
{
    /// <summary>Vault and Key Vault secret references (phase-4-plan p4-d5, I47) against fakes of their APIs.</summary>
    public sealed class SecretResolverTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly FakeHttpApi _api = new();
        private readonly MovingClock _clock = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));

        private sealed class MovingClock(DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        private SecretResolver Resolver(SecretsOptions options) => new(new SecretsHttp(_api), Options.Create(options), _clock, NullLogger<SecretResolver>.Instance);

        static SecretResolverTests()
        {
            Environment.SetEnvironmentVariable("NOTIFICATIONS_TEST_VAULT_TOKEN", "hvs.test");
            Environment.SetEnvironmentVariable("NOTIFICATIONS_TEST_SECRET_ID", "secret-id-1");
            Environment.SetEnvironmentVariable("NOTIFICATIONS_TEST_AZURE_SECRET", "azure-secret");
        }

        private static SecretsOptions Vault(bool appRole = false) => new()
        {
            Vault = appRole
                ? new VaultOptions { Address = new Uri("https://vault.example.com/"), RoleId = "role-1", SecretIdRef = "env:NOTIFICATIONS_TEST_SECRET_ID", Namespace = "team" }
                : new VaultOptions { Address = new Uri("https://vault.example.com/"), TokenRef = "env:NOTIFICATIONS_TEST_VAULT_TOKEN" },
        };

        [Fact]
        public async Task A_vault_field_is_read_with_the_token_and_kept_five_minutes()
        {
            _api.Then(HttpStatusCode.OK, """{"data":{"data":{"key":"s3cret","other":"x"},"metadata":{"version":3}}}""");
            _api.Then(HttpStatusCode.OK, """{"data":{"data":{"key":"rotated"}}}""");
            var resolver = Resolver(Vault());

            Assert.Equal("s3cret", await resolver.ResolveAsync("vault:notifications/acme/smtp#key", Ct));
            Assert.Equal("s3cret", await resolver.ResolveAsync("vault:notifications/acme/smtp#key", Ct));
            _clock.Now = _clock.Now.AddMinutes(6);
            Assert.Equal("rotated", await resolver.ResolveAsync("vault:notifications/acme/smtp#key", Ct));

            var read = _api.Requests.First();
            Assert.Equal("https://vault.example.com/v1/secret/data/notifications/acme/smtp", read.Uri.ToString());
            Assert.Equal("hvs.test", read.Headers["X-Vault-Token"]);
            Assert.Equal(2, _api.Requests.Count);
        }

        [Fact]
        public async Task AppRole_signs_in_once_and_keeps_its_token()
        {
            _api.Then(HttpStatusCode.OK, """{"auth":{"client_token":"hvs.approle","lease_duration":3600}}""");
            _api.Then(HttpStatusCode.OK, """{"data":{"data":{"a":"1"}}}""");
            _api.Then(HttpStatusCode.OK, """{"data":{"data":{"b":"2"}}}""");
            var resolver = Resolver(Vault(appRole: true));

            Assert.Equal("1", await resolver.ResolveAsync("vault:p#a", Ct));
            Assert.Equal("2", await resolver.ResolveAsync("vault:p#b", Ct));

            var requests = _api.Requests.ToList();
            Assert.Equal("https://vault.example.com/v1/auth/approle/login", requests[0].Uri.ToString());
            Assert.Contains("\"secret_id\":\"secret-id-1\"", requests[0].Body);
            Assert.Equal("team", requests[0].Headers["X-Vault-Namespace"]);
            Assert.All(requests.Skip(1), r => Assert.Equal("hvs.approle", r.Headers["X-Vault-Token"]));
            Assert.Equal(3, requests.Count);
        }

        /// <summary>A secret the vault doesn't have is unset (nobody's mistake to retry); a vault that's down or refusing is unavailable (retried, failed over).</summary>
        [Fact]
        public async Task A_missing_secret_is_unset_and_an_unreachable_vault_unavailable()
        {
            var resolver = Resolver(Vault());
            _api.Then(HttpStatusCode.NotFound, """{"errors":[]}""");
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolver.ResolveAsync("vault:p#a", Ct));
            Assert.IsNotType<SecretUnavailableException>(missing);
            _api.Then(HttpStatusCode.ServiceUnavailable, """{"errors":["sealed"]}""");
            await Assert.ThrowsAsync<SecretUnavailableException>(async () => await resolver.ResolveAsync("vault:p#a", Ct));
            _api.ThenRefused();
            await Assert.ThrowsAsync<SecretUnavailableException>(async () => await resolver.ResolveAsync("vault:p#a", Ct));
            _api.Then(HttpStatusCode.OK, """{"data":{"data":{"other":"1"}}}""");
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolver.ResolveAsync("vault:p#a", Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await Resolver(new SecretsOptions()).ResolveAsync("vault:p#a", Ct));
        }

        [Fact]
        public async Task Key_Vault_signs_in_with_client_credentials_or_the_managed_identity()
        {
            var credentials = Resolver(new SecretsOptions
            {
                KeyVault = new KeyVaultOptions { VaultUri = new Uri("https://acme.vault.azure.net/"), TenantId = "t1", ClientId = "c1", ClientSecretRef = "env:NOTIFICATIONS_TEST_AZURE_SECRET" },
            });
            _api.Then(HttpStatusCode.OK, """{"access_token":"aad-1","expires_in":3599,"token_type":"Bearer"}""");
            _api.Then(HttpStatusCode.OK, """{"value":"kv-secret","id":"https://acme.vault.azure.net/secrets/notifications-smtp/1"}""");
            Assert.Equal("kv-secret", await credentials.ResolveAsync("keyvault:notifications-smtp", Ct));
            var (login, read) = (_api.Requests.First(), _api.Requests.Last());
            Assert.Equal("https://login.microsoftonline.com/t1/oauth2/v2.0/token", login.Uri.ToString());
            Assert.Contains("client_secret=azure-secret", login.Body);
            Assert.Contains("scope=https%3A%2F%2Fvault.azure.net%2F.default", login.Body);
            Assert.Equal("https://acme.vault.azure.net/secrets/notifications-smtp?api-version=7.4", read.Uri.ToString());
            Assert.Equal("Bearer aad-1", read.Headers["Authorization"]);

            var managed = Resolver(new SecretsOptions { KeyVault = new KeyVaultOptions { VaultUri = new Uri("https://acme.vault.azure.net/") } });
            _api.Then(HttpStatusCode.OK, """{"access_token":"mi-1","expires_in":"3599"}""");
            _api.Then(HttpStatusCode.OK, """{"value":"v2"}""");
            Assert.Equal("v2", await managed.ResolveAsync("keyvault:other", Ct));
            var imds = _api.Requests.ElementAt(2);
            Assert.StartsWith("http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=", imds.Uri.ToString());
            Assert.Equal("true", imds.Headers["Metadata"]);
        }

        [Theory]
        [InlineData("vault:notifications/acme/smtp#password", true, true)]
        [InlineData("vault:other/smtp#password", true, false)]
        [InlineData("vault:notifications/acme/smtp", false, false)]
        [InlineData("vault:notifications/../root#k", false, false)]
        [InlineData("keyvault:notifications-acme-smtp", true, true)]
        [InlineData("keyvault:prod-db-password", true, false)]
        [InlineData("keyvault:bad_name", false, false)]
        [InlineData("env:NOTIFICATIONS_SECRET_X", true, true)]
        public void References_are_checked_and_the_API_kept_to_its_prefixes(string reference, bool valid, bool fromApi)
        {
            var options = new DeliveryOptions { ApiVaultPrefix = "notifications/", ApiKeyVaultPrefix = "notifications-" };
            Assert.Equal(valid, SecretReferences.IsValid(reference));
            Assert.Equal(fromApi, SecretReferences.IsAllowedFromApi(reference, options));
        }
    }
}
