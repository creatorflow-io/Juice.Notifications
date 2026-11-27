using Juice.Notifications.Configuration;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// Who may change or delete a setup row (phase-5 decision G1): each owner its own, the configuration also the API's; the others are refused
    /// with the owner's code.
    /// </summary>
    public sealed class OwnershipTests
    {
        [Theory]
        // The API (null) changes only its own rows.
        [InlineData(null, null, null)]
        [InlineData(ManagedBy.Api, null, null)]
        [InlineData(ManagedBy.Configuration, null, ErrorCodes.ManagedByConfiguration)]
        [InlineData(ManagedBy.WebhooksAlias, null, ErrorCodes.ManagedByAlias)]
        // The configuration takes over the API's rows, as the provisioning file always has, and leaves the alias's alone.
        [InlineData(ManagedBy.Api, ManagedBy.Configuration, null)]
        [InlineData(ManagedBy.Configuration, ManagedBy.Configuration, null)]
        [InlineData(ManagedBy.WebhooksAlias, ManagedBy.Configuration, ErrorCodes.ManagedByAlias)]
        // The alias keeps to its own rows.
        [InlineData(null, ManagedBy.WebhooksAlias, null)]
        [InlineData(ManagedBy.WebhooksAlias, ManagedBy.WebhooksAlias, null)]
        [InlineData(ManagedBy.Api, ManagedBy.WebhooksAlias, ErrorCodes.ManagedByApi)]
        [InlineData(ManagedBy.Configuration, ManagedBy.WebhooksAlias, ErrorCodes.ManagedByConfiguration)]
        // The alias's configuration (M46c): every other writer is refused as by a deployment's configuration; it keeps to its own rows, and takes
        // over the alias's (an endpoint made through the producer's API, then declared in its configuration).
        [InlineData(ManagedBy.AliasConfig, null, ErrorCodes.ManagedByConfiguration)]
        [InlineData(ManagedBy.AliasConfig, ManagedBy.WebhooksAlias, ErrorCodes.ManagedByConfiguration)]
        [InlineData(ManagedBy.AliasConfig, ManagedBy.Configuration, ErrorCodes.ManagedByConfiguration)]
        [InlineData(ManagedBy.AliasConfig, ManagedBy.AliasConfig, null)]
        [InlineData(ManagedBy.WebhooksAlias, ManagedBy.AliasConfig, null)]
        [InlineData(null, ManagedBy.AliasConfig, null)]
        [InlineData(ManagedBy.Api, ManagedBy.AliasConfig, ErrorCodes.ManagedByApi)]
        [InlineData(ManagedBy.Configuration, ManagedBy.AliasConfig, ErrorCodes.ManagedByConfiguration)]
        public void Each_owner_keeps_to_its_own_rows(string? owner, string? caller, string? refused)
        {
            var outcome = ConfigurationHandlers.Owned<bool>(owner, caller, "route ops");
            Assert.Equal(refused, outcome?.Code);
            if (outcome is not null)
            {
                Assert.Equal(OutcomeKind.Conflict, outcome.Kind);
            }
        }

        // A target put under an integration: the alias's configuration must own the integration, so it can't leave a target under one that
        // was handed back (webhooks-alias). Other callers keep the target's own check.
        [Theory]
        [InlineData(ManagedBy.WebhooksAlias, ManagedBy.AliasConfig, ErrorCodes.ManagedByAlias)]
        [InlineData(ManagedBy.Api, ManagedBy.AliasConfig, ErrorCodes.ManagedByApi)]
        [InlineData(ManagedBy.Configuration, ManagedBy.AliasConfig, ErrorCodes.ManagedByConfiguration)]
        [InlineData(ManagedBy.AliasConfig, ManagedBy.AliasConfig, null)]
        [InlineData(ManagedBy.WebhooksAlias, ManagedBy.WebhooksAlias, null)]
        [InlineData(ManagedBy.WebhooksAlias, null, null)]
        [InlineData(ManagedBy.Configuration, null, null)]
        [InlineData(ManagedBy.AliasConfig, ManagedBy.Configuration, null)]
        public void A_target_write_needs_the_alias_configuration_to_own_the_integration(string owner, string? caller, string? refused)
        {
            var outcome = ConfigurationHandlers.OwnedIntegrationOfTarget<TargetView>(owner, caller, "hook");
            Assert.Equal(refused, outcome?.Code);
            if (outcome is not null)
            {
                Assert.Equal(OutcomeKind.Conflict, outcome.Kind);
            }
        }

        [Fact]
        public void A_command_names_only_an_in_process_owner()
        {
            Assert.Equal(ManagedBy.Api, ConfigurationHandlers.Caller(null));
            Assert.Equal(ManagedBy.WebhooksAlias, ConfigurationHandlers.Caller(ManagedBy.WebhooksAlias));
            Assert.Equal(ManagedBy.AliasConfig, ConfigurationHandlers.Caller(ManagedBy.AliasConfig));
            Assert.Throws<ArgumentException>(() => ConfigurationHandlers.Caller(ManagedBy.Service));
            Assert.Throws<ArgumentException>(() => ConfigurationHandlers.Caller("someone"));
        }

        [Fact]
        public void The_provisioned_path_is_the_configuration_owner()
        {
            Assert.True(new PutRoute(Guid.NewGuid(), "r", null, null, null) { ManagedBy = ManagedBy.Configuration }.Provisioned);
            Assert.False(new PutRoute(Guid.NewGuid(), "r", null, null, null) { ManagedBy = ManagedBy.WebhooksAlias }.Provisioned);
            Assert.False(new PutRoute(Guid.NewGuid(), "r", null, null, null).Provisioned);
        }
    }
}
