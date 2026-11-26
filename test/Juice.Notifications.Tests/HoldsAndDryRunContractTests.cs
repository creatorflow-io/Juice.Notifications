using System.Text.Json;
using System.Text.Json.Serialization;
using Juice.Notifications.Hosting;
using Juice.Notifications.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Tests
{
    /// <summary>The M44-0 contract (phase-5 waves §4): what other milestones build on before holds and dry run are built.</summary>
    public sealed class HoldsAndDryRunContractTests
    {
        private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        [Fact]
        public void Would_send_travels_as_the_api_writes_it_and_reads_in_any_case()
        {
            Assert.Equal("\"wouldSend\"", JsonSerializer.Serialize(DeliveryStatus.WouldSend, Api));
            Assert.Equal(DeliveryStatus.WouldSend, JsonSerializer.Deserialize<DeliveryStatus>("\"wouldSend\""));
            Assert.Equal(DeliveryStatus.WouldSend, JsonSerializer.Deserialize<DeliveryStatus>("\"WouldSend\""));
        }

        [Fact]
        public void Would_send_is_added_last_so_no_stored_or_numbered_status_moves()
        {
            Assert.Equal(DeliveryStatus.WouldSend, Enum.GetValues<DeliveryStatus>()[^1]);
            Assert.Equal(8, (int)DeliveryStatus.Cancelled);
        }

        [Fact]
        public void Dry_run_in_the_settings_fails_at_registration_until_it_is_built()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:DryRun"] = "true" }).Build();
            var refused = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddNotificationsPipeline(configuration, (_, _) => { }));
            Assert.Contains("M44-2", refused.Message);
        }

        [Fact]
        public async Task Dry_run_set_in_code_fails_the_host_at_start_until_it_is_built()
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddNotificationsPipeline(new ConfigurationBuilder().Build(), (_, _) => { });
            builder.Services.Configure<DeliveryOptions>(o => o.DryRun = true);
            using var host = builder.Build();
            // Before any hosted service (the migrator, the stages) starts: the options are checked as the host starts, not when first read.
            var refused = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains("M44-2", refused.Message);
        }

        [Fact]
        public void Without_dry_run_the_pipeline_registers_as_before()
        {
            var services = new ServiceCollection().AddNotificationsPipeline(new ConfigurationBuilder().Build(), (_, _) => { });
            using var provider = services.BuildServiceProvider();
            Assert.False(provider.GetRequiredService<IOptions<DeliveryOptions>>().Value.DryRun);
        }

        [Fact]
        public void The_alias_owns_its_rows_under_its_own_name()
        {
            Assert.Equal("webhooks-alias", ManagedBy.WebhooksAlias);
            Assert.Equal("managed_by_alias", ErrorCodes.ManagedByAlias);
            Assert.Equal("not_allowed", ErrorCodes.NotAllowed);
        }

        [Fact]
        public void A_correlation_hold_reads_back_as_it_was_written()
        {
            var at = DateTimeOffset.UnixEpoch;
            var hold = new CorrelationHold(CorrelationState.Open, true, at, "silence:7", "ops", Changed: true, Resumed: 0);
            var read = JsonSerializer.Deserialize<CorrelationHold>(JsonSerializer.Serialize(hold, Api), Api);
            Assert.Equal(hold, read);
        }
    }
}
