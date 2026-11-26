using Juice.Notifications.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The API's and the alias's secret limits never overlap (M46-3 review): a host configured so that one writer could name the other's
    /// secrets (one env prefix starting with the other, or one folder for both) fails to start, whether its settings or its own code set them.
    /// No database is needed: the check runs before any stage.
    /// </summary>
    public sealed class SecretScopeStartTests
    {
        private const string Unreachable = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";

        private static IHost Build(Dictionary<string, string?> settings, Action<IServiceCollection>? after = null)
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddNotificationsPipeline(builder.Configuration, (_, db) => Data.NotificationsDbContext.Configure(db, Unreachable));
            after?.Invoke(builder.Services);
            return builder.Build();
        }

        public static TheoryData<string, string, string?, string?> Overlapping => new()
        {
            // The API's prefix covers the alias's: any admin could name JUICE_WEBHOOK_*.
            { "JUICE_", "JUICE_WEBHOOK_", null, null },
            // The reverse: the alias could name the API's.
            { "JUICE_WEBHOOK_OPS_", "JUICE_WEBHOOK_", null, null },
            { "JUICE_WEBHOOK_", "JUICE_WEBHOOK_", null, null },
            // One folder for both.
            { "NOTIFICATIONS_SECRET_", "JUICE_WEBHOOK_", "/run/secrets/shared", "/run/secrets/shared/" },
        };

        [Theory]
        [MemberData(nameof(Overlapping))]
        public async Task Overlapping_limits_fail_the_start(string api, string alias, string? apiFolder, string? aliasFolder)
        {
            using var host = Build(new()
            {
                ["Delivery:ApiSecretEnvPrefix"] = api,
                ["Delivery:AliasSecretEnvPrefix"] = alias,
                ["Delivery:ApiSecretDirectory"] = apiFolder,
                ["Delivery:AliasSecretDirectory"] = aliasFolder,
            });

            var failed = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains(failed.Failures, f => f.Contains("AliasSecret", StringComparison.Ordinal) && f.Contains("one writer could name the other", StringComparison.Ordinal));
        }

        /// <summary>A host that sets the alias limits in its own code (as Observability does from Webhooks:*) is checked the same way.</summary>
        [Fact]
        public async Task Limits_a_host_sets_in_code_are_checked_too()
        {
            using var host = Build(new() { ["Delivery:ApiSecretEnvPrefix"] = "JUICE_" },
                services => services.PostConfigure<Providers.DeliveryOptions>(o => o.AliasSecretEnvPrefix = "JUICE_WEBHOOK_"));

            await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public void Distinct_limits_are_fine()
        {
            using var host = Build(new()
            {
                ["Delivery:AliasSecretEnvPrefix"] = "JUICE_WEBHOOK_",
                ["Delivery:ApiSecretDirectory"] = "/run/secrets/notifications",
                ["Delivery:AliasSecretDirectory"] = "/run/secrets/juice",
            });

            var options = host.Services.GetRequiredService<IOptions<Providers.DeliveryOptions>>().Value;
            Assert.Equal(("NOTIFICATIONS_SECRET_", "JUICE_WEBHOOK_"), (options.ApiSecretEnvPrefix, options.AliasSecretEnvPrefix));
        }
    }
}
