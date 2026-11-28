using Juice.Notifications.Configuration;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// Who may change or delete a setup row (phase-5 decision G1): the API its own, the configuration also the API's; the others are refused
    /// with the owner's code. The webhooks alias's owners are retired (M50).
    /// </summary>
    public sealed class OwnershipTests
    {
        [Theory]
        // The API (null) changes only its own rows.
        [InlineData(null, null, null)]
        [InlineData(ManagedBy.Api, null, null)]
        [InlineData(ManagedBy.Configuration, null, ErrorCodes.ManagedByConfiguration)]
        // The configuration takes over the API's rows, as the provisioning file always has.
        [InlineData(ManagedBy.Api, ManagedBy.Configuration, null)]
        [InlineData(ManagedBy.Configuration, ManagedBy.Configuration, null)]
        // The service's own rows are no one's to refuse here (their own check does).
        [InlineData(ManagedBy.Service, null, null)]
        [InlineData(ManagedBy.Service, ManagedBy.Configuration, null)]
        public void Each_owner_keeps_to_its_own_rows(string? owner, string? caller, string? refused)
        {
            var outcome = ConfigurationHandlers.Owned<bool>(owner, caller, "route ops");
            Assert.Equal(refused, outcome?.Code);
            if (outcome is not null)
            {
                Assert.Equal(OutcomeKind.Conflict, outcome.Kind);
            }
        }

        [Fact]
        public void Ownership_AliasOwnersGone_OnlyApiConfigService()
        {
            // The constants are gone: only these three remain, and a write naming an old owner string is refused as invalid.
            Assert.Equal(["Api", "Configuration", "Service"], typeof(ManagedBy).GetFields().Select(f => f.Name).Order().ToArray());
            Assert.Null(typeof(ErrorCodes).GetField("ManagedByAlias"));
            Assert.Throws<ArgumentException>(() => ConfigurationHandlers.Caller("webhooks" + "-alias"));
            Assert.Throws<ArgumentException>(() => ConfigurationHandlers.Caller("alias" + "-config"));
            Assert.Throws<ArgumentException>(() => ConfigurationHandlers.Caller(ManagedBy.Service));
            Assert.Throws<ArgumentException>(() => ConfigurationHandlers.Caller("someone"));
        }

        [Fact]
        public void A_command_names_only_an_in_process_owner()
        {
            Assert.Equal(ManagedBy.Api, ConfigurationHandlers.Caller(null));
            Assert.Equal(ManagedBy.Api, ConfigurationHandlers.Caller(ManagedBy.Api));
            Assert.Equal(ManagedBy.Configuration, ConfigurationHandlers.Caller(ManagedBy.Configuration));
        }

        [Fact]
        public void The_provisioned_path_is_the_configuration_owner()
        {
            Assert.True(new PutRoute(Guid.NewGuid(), "r", null, null, null) { ManagedBy = ManagedBy.Configuration }.Provisioned);
            Assert.False(new PutRoute(Guid.NewGuid(), "r", null, null, null).Provisioned);
        }
    }
}
