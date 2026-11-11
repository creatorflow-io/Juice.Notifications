using System.Text.Json;
using Juice.Notifications.Planning;

namespace Juice.Notifications.Tests
{
    /// <summary>Route matching (design §6.3): type patterns, the four label matchers and a minimum priority; everything given must match.</summary>
    public sealed class RouteMatchTests
    {
        private static readonly Dictionary<string, string> Labels = new() { ["env"] = "prod", ["severity"] = "high", ["service"] = "shop/billing" };

        private static bool Matches(string match, string type = "observability.alert.firing", NotificationPriority priority = NotificationPriority.High)
        {
            var (parsed, errors) = RouteMatch.Parse(JsonElement.Parse(match));
            Assert.Empty(errors);
            return parsed!.Matches(type, Labels, priority);
        }

        [Theory]
        [InlineData("""{ }""", true)]
        [InlineData("""{ "type": "observability.alert.firing" }""", true)]
        [InlineData("""{ "type": "observability.*" }""", true)]
        [InlineData("""{ "type": "*.firing" }""", true)]
        [InlineData("""{ "type": "observability.alert" }""", false)]
        [InlineData("""{ "type": ["workflow.*", "observability.alert.*"] }""", true)]
        [InlineData("""{ "type": ["workflow.*"] }""", false)]
        [InlineData("""{ "labels": { "env": "prod" } }""", true)]
        [InlineData("""{ "labels": { "env": "staging" } }""", false)]
        [InlineData("""{ "labels": { "region": "eu" } }""", false)]
        [InlineData("""{ "labels": { "env": "!=staging" } }""", true)]
        [InlineData("""{ "labels": { "env": "!=prod" } }""", false)]
        [InlineData("""{ "labels": { "region": "!=eu" } }""", true)]
        [InlineData("""{ "labels": { "service": "=~shop/.*" } }""", true)]
        [InlineData("""{ "labels": { "service": "=~billing" } }""", false)]
        [InlineData("""{ "labels": { "region": "=~.*" } }""", false)]
        [InlineData("""{ "labels": { "severity": "in(high,critical)" } }""", true)]
        [InlineData("""{ "labels": { "severity": "in(low, normal)" } }""", false)]
        [InlineData("""{ "labels": { "region": "in(eu)" } }""", false)]
        [InlineData("""{ "minPriority": "high" }""", true)]
        [InlineData("""{ "minPriority": "critical" }""", false)]
        [InlineData("""{ "type": "observability.*", "labels": { "env": "prod", "severity": "in(high)" }, "minPriority": "normal" }""", true)]
        [InlineData("""{ "type": "observability.*", "labels": { "env": "prod", "severity": "in(low)" } }""", false)]
        public void A_route_matches_when_everything_it_names_matches(string match, bool expected) => Assert.Equal(expected, Matches(match));

        [Fact]
        public void A_pattern_is_literal_except_for_the_star()
        {
            Assert.False(Matches("""{ "type": "observability.alert.firin." }"""));
            Assert.True(Matches("""{ "type": "observability.alert.firing" }"""));
        }

        [Theory]
        [InlineData("""[]""", "/match")]
        [InlineData("""{ "type": "Not Valid" }""", "/match/type")]
        [InlineData("""{ "type": [] }""", "/match/type")]
        [InlineData("""{ "labels": { "a": 5 } }""", "/match/labels/a")]
        [InlineData("""{ "labels": { "a": "=~(" } }""", "/match/labels/a")]
        [InlineData("""{ "minPriority": "urgent" }""", "/match/minPriority")]
        [InlineData("""{ "severity": "high" }""", "/match/severity")]
        public void A_bad_match_is_refused_at_the_field(string match, string pointer)
        {
            var (parsed, errors) = RouteMatch.Parse(JsonElement.Parse(match));
            Assert.Null(parsed);
            Assert.Contains(errors, e => e.Pointer == pointer);
        }
    }
}
