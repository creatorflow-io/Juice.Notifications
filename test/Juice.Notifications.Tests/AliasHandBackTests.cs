using Juice.MediatR;
using Juice.Notifications.Configuration;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// The hand-back (phase-5 M46c) as a command: who may issue it. The rows it moves are checked against a database in the integration tests
    /// (<c>Juice.Notifications.IntegrationTests.AliasHandBackTests</c>).
    /// </summary>
    public sealed class AliasHandBackTests
    {
        // The handler refuses a wrong caller before it reads anything, so it needs no database.
        private static ConfigurationHandlers Handlers()
            => new(null!, null!, [], [], Options.Create(new Juice.Notifications.Providers.DeliveryOptions()), TimeProvider.System);

        [Theory]
        [InlineData(null)]
        [InlineData(ManagedBy.Api)]
        [InlineData(ManagedBy.WebhooksAlias)]
        [InlineData(ManagedBy.Configuration)]
        [InlineData(ManagedBy.Service)]
        [InlineData("someone")]
        public async Task HandBack_ByApiOrWebhooksAlias_RefusedManagedByConfiguration(string? caller)
        {
            var outcome = await Handlers().Handle(new HandBackAliasConfig(Guid.NewGuid(), "hook") { ManagedBy = caller }, TestContext.Current.CancellationToken);
            Assert.Equal(OutcomeKind.Conflict, outcome.Kind);
            Assert.Equal(ErrorCodes.ManagedByConfiguration, outcome.Code);
        }

        [Fact]
        public void The_command_is_a_tenant_command_with_an_owner()
        {
            var command = new HandBackAliasConfig(Guid.NewGuid(), "hook") { ManagedBy = ManagedBy.AliasConfig };
            Assert.IsAssignableFrom<ITenantCommand>(command);
            Assert.IsAssignableFrom<IOwnedSetupWrite>(command);
            Assert.Equal(ManagedBy.AliasConfig, command.ManagedBy);
        }
    }
}
