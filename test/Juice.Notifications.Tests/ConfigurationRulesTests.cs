using Juice.Notifications.Configuration;

namespace Juice.Notifications.Tests
{
    /// <summary>Allowlist destinations are stored in the form the send-time destination policy reads, so what the API accepts is what matches.</summary>
    public sealed class ConfigurationRulesTests
    {
        [Theory]
        // Networks in canonical form: host bits dropped, so two spellings are one entry.
        [InlineData("10.20.0.0/16", "10.20.0.0/16")]
        [InlineData("10.20.1.5/16", "10.20.0.0/16")]
        // One address is the network of that address, matched as an address rather than as a host name's text.
        [InlineData("10.0.0.5", "10.0.0.5/32")]
        [InlineData("fd00::5", "fd00::5/128")]
        // The policy compares IPv4 as IPv4, so IPv4-mapped forms are stored as IPv4.
        [InlineData("::ffff:10.0.0.5", "10.0.0.5/32")]
        [InlineData("::ffff:10.20.0.0/112", "10.20.0.0/16")]
        // Host names as DNS sees them: ASCII (punycode), lowercase, no trailing dot.
        [InlineData("hooks.example.com", "hooks.example.com")]
        [InlineData("example.com.", "example.com")]
        [InlineData("bücher.internal", "xn--bcher-kva.internal")]
        public void A_destination_is_stored_as_the_policy_reads_it(string given, string stored)
            => Assert.Equal(stored, ConfigurationRules.Destination(given));

        [Theory]
        [InlineData("10.20.0.0/40")]
        [InlineData("not a host")]
        [InlineData("")]
        public void Something_else_is_not_a_destination(string given) => Assert.Null(ConfigurationRules.Destination(given));

        /// <summary>What's stored is what matches at send time, including an IPv4-mapped entry and a host written with its trailing dot.</summary>
        [Fact]
        public void A_stored_destination_covers_what_it_names()
        {
            var mapped = new Providers.AllowedDestination(ConfigurationRules.Destination("::ffff:10.0.0.5")!, []);
            Assert.True(mapped.Covers("hooks.internal", System.Net.IPAddress.Parse("10.0.0.5"), 443));

            var host = new Providers.AllowedDestination(ConfigurationRules.Destination("internal.example.com.")!, []);
            Assert.True(host.Covers("internal.example.com.", System.Net.IPAddress.Parse("10.0.0.6"), 443));
            Assert.True(host.Covers("internal.example.com", System.Net.IPAddress.Parse("10.0.0.6"), 443));
        }
    }
}
