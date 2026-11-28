using Juice.Notifications.Configuration;
using Juice.Notifications.Hosting;
using Juice.Notifications.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// A secret reference is checked against the limits of whoever writes the row (phase-5 §5.3): the API's (<c>ApiSecret*</c>) or the
    /// deployment configuration's (any). The retired alias's <c>AliasSecret*</c> keys are ignored with a warning (M50).
    /// </summary>
    public sealed class SecretScopeTests
    {
        private static DeliveryOptions Options() => new() { ApiSecretEnvPrefix = "NOTIFICATIONS_SECRET_" };

        [Theory]
        [InlineData(null, "env:NOTIFICATIONS_SECRET_HOOK", true)]
        [InlineData(null, "env:JUICE_WEBHOOK_HOOK", false)]
        [InlineData(ManagedBy.Api, "env:JUICE_WEBHOOK_HOOK", false)]
        [InlineData(ManagedBy.Api, "env:DATABASE_PASSWORD", false)]
        [InlineData(ManagedBy.Configuration, "env:DATABASE_PASSWORD", true)]
        [InlineData(ManagedBy.Configuration, null, true)]
        public void Each_writer_names_secrets_within_its_own_limits(string? owner, string? reference, bool allowed)
            => Assert.Equal(allowed, ConfigurationRules.IsSecretRef(reference, Options(), owner));

        [Fact]
        public async Task SecretScope_AliasPrefixSet_IgnoredWithWarning_ApiScopeUnchanged()
        {
            var settings = new Dictionary<string, string?>
            {
                ["AliasSecretEnvPrefix"] = "JUICE_WEBHOOK_",
                ["AliasSecretDirectory"] = "/run/secrets/alias",
            };
            var section = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var logged = new Logged();
            await new RetiredAliasSecretNotice(section, logged).StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, logged.Warnings.Count);
            Assert.Contains(logged.Warnings, w => w.Contains("AliasSecretEnvPrefix") && w.Contains("ignored"));
            Assert.Contains(logged.Warnings, w => w.Contains("AliasSecretDirectory") && w.Contains("ignored"));

            // The keys bind to nothing, and the API's limits stay as they were: a reference in the retired prefix is no one's but the deployment's.
            var options = section.Get<DeliveryOptions>() ?? new DeliveryOptions();
            Assert.Equal("NOTIFICATIONS_SECRET_", options.ApiSecretEnvPrefix);
            Assert.Null(options.ApiSecretDirectory);
            Assert.False(ConfigurationRules.IsSecretRef("env:JUICE_WEBHOOK_HOOK", options, ManagedBy.Api));
            Assert.False(ConfigurationRules.IsSecretRef("env:JUICE_WEBHOOK_HOOK", options, null));
            Assert.True(ConfigurationRules.IsSecretRef("env:NOTIFICATIONS_SECRET_HOOK", options, ManagedBy.Api));
        }

        [Fact]
        public async Task SecretScope_NoAliasKeys_NoWarning()
        {
            var logged = new Logged();
            await new RetiredAliasSecretNotice(new ConfigurationBuilder().Build(), logged).StartAsync(TestContext.Current.CancellationToken);
            Assert.Empty(logged.Warnings);
        }

        private sealed class Logged : ILogger<RetiredAliasSecretNotice>
        {
            public List<string> Warnings { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    Warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
