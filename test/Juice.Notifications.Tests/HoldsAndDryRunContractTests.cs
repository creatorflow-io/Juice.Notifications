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

        /// <summary>M44-2: the guard is gone: dry run in the settings is read as set, and the host warns of it at start.</summary>
        [Fact]
        public async Task Dry_run_in_the_settings_is_honoured_and_warned_of_at_start()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:DryRun"] = "true" }).Build();
            var services = new ServiceCollection().AddLogging().AddNotificationsPipeline(configuration, (_, _) => { });
            using var provider = services.BuildServiceProvider();
            Assert.True(provider.GetRequiredService<IOptions<DeliveryOptions>>().Value.DryRun);
            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(DryRunNotice));
            var logged = new Logged();
            await new DryRunNotice(provider.GetRequiredService<IOptions<DeliveryOptions>>(), logged).StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Dispatcher in dry run: deliveries are recorded as would-send and not sent", Assert.Single(logged.Warnings));
        }

        private sealed class Logged : Microsoft.Extensions.Logging.ILogger<DryRunNotice>
        {
            public List<string> Warnings { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                {
                    Warnings.Add(formatter(state, exception));
                }
            }
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
