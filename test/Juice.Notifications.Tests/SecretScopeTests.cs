using Juice.Notifications.Configuration;
using Juice.Notifications.Providers;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// A secret reference is checked against the limits of whoever writes the row (phase-5 §5.3, M46-3): the API's (<c>ApiSecret*</c>), the
    /// deployment configuration's (any), or a producer alias's (<c>AliasSecret*</c>, nothing when unset). Neither limit widens the other.
    /// </summary>
    public sealed class SecretScopeTests
    {
        private static readonly string Folder = Path.Combine(Path.GetTempPath(), "alias-secrets");

        private static DeliveryOptions Options(bool alias) => new()
        {
            ApiSecretEnvPrefix = "NOTIFICATIONS_SECRET_",
            AliasSecretEnvPrefix = alias ? "JUICE_WEBHOOK_" : null,
            AliasSecretDirectory = alias ? Folder : null,
        };

        [Theory]
        [InlineData(null, "env:NOTIFICATIONS_SECRET_HOOK", true)]
        [InlineData(null, "env:JUICE_WEBHOOK_HOOK", false)]
        [InlineData(ManagedBy.Api, "env:JUICE_WEBHOOK_HOOK", false)]
        [InlineData(ManagedBy.WebhooksAlias, "env:JUICE_WEBHOOK_HOOK", true)]
        [InlineData(ManagedBy.WebhooksAlias, "env:JUICE_WEBHOOK_", false)]
        [InlineData(ManagedBy.WebhooksAlias, "env:NOTIFICATIONS_SECRET_HOOK", false)]
        [InlineData(ManagedBy.WebhooksAlias, "env:DATABASE_PASSWORD", false)]
        [InlineData(ManagedBy.WebhooksAlias, "vault:notifications/hook#key", false)]
        [InlineData(ManagedBy.Configuration, "env:DATABASE_PASSWORD", true)]
        [InlineData(ManagedBy.WebhooksAlias, null, true)]
        public void Each_writer_names_secrets_within_its_own_limits(string? owner, string? reference, bool allowed)
            => Assert.Equal(allowed, ConfigurationRules.IsSecretRef(reference, Options(alias: true), owner));

        [Fact]
        public void An_alias_file_is_one_directly_in_its_folder()
        {
            var options = Options(alias: true);
            Assert.True(ConfigurationRules.IsSecretRef("file:" + Path.Combine(Folder, "hook"), options, ManagedBy.WebhooksAlias));
            Assert.False(ConfigurationRules.IsSecretRef("file:" + Path.Combine(Folder, "nested", "hook"), options, ManagedBy.WebhooksAlias));
            Assert.False(ConfigurationRules.IsSecretRef("file:" + Path.Combine(Path.GetTempPath(), "hook"), options, ManagedBy.WebhooksAlias));
            // The API keeps its own folder: unset here, so no file.
            Assert.False(ConfigurationRules.IsSecretRef("file:" + Path.Combine(Folder, "hook"), options, ManagedBy.Api));
        }

        [Theory]
        [InlineData("JUICE_", "JUICE_WEBHOOK_", null, null, true)]
        [InlineData("JUICE_WEBHOOK_OPS_", "JUICE_WEBHOOK_", null, null, true)]
        [InlineData("NOTIFICATIONS_SECRET_", "JUICE_WEBHOOK_", null, null, false)]
        [InlineData("NOTIFICATIONS_SECRET_", "notifications_secret_alias_", null, null, true)]
        [InlineData("NOTIFICATIONS_SECRET_", null, null, null, false)]
        [InlineData("NOTIFICATIONS_SECRET_", "JUICE_WEBHOOK_", "/run/secrets/a", "/run/secrets/a/", true)]
        [InlineData("NOTIFICATIONS_SECRET_", "JUICE_WEBHOOK_", "/run/secrets/a", "/run/secrets/b", false)]
        public void The_api_and_alias_limits_may_not_overlap(string api, string? alias, string? apiFolder, string? aliasFolder, bool overlap)
            => Assert.Equal(overlap, SecretReferences.ScopeOverlap(new DeliveryOptions
            {
                ApiSecretEnvPrefix = api, AliasSecretEnvPrefix = alias, ApiSecretDirectory = apiFolder, AliasSecretDirectory = aliasFolder,
            }) is not null);

        [Theory]
        [InlineData("env:JUICE_WEBHOOK_HOOK")]
        [InlineData("env:NOTIFICATIONS_SECRET_HOOK")]
        public void Without_its_limits_the_alias_names_nothing(string reference)
            => Assert.False(ConfigurationRules.IsSecretRef(reference, Options(alias: false), ManagedBy.WebhooksAlias));
    }
}
