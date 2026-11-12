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
        [InlineData("""{ "labels": { "env": "=prod" } }""", true)]
        [InlineData("""{ "labels": { "env": "=staging" } }""", false)]
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

        /// <summary>Patterns that backtrack without end on the classic engine run in linear time: one tenant's route can't stall the planner.</summary>
        [Fact]
        public void Patterns_that_would_backtrack_run_fast_and_never_throw()
        {
            var longType = new string('a', 190);
            var (types, _) = RouteMatch.Parse(JsonElement.Parse("""{ "type": "*a*a*a*a*a*a*a*a*a*a*a*a*a*a*z" }"""));
            var (label, _) = RouteMatch.Parse(JsonElement.Parse("""{ "labels": { "v": "=~(a+)+$" } }"""));
            var labels = new Dictionary<string, string> { ["v"] = new string('a', 5000) + "!" };

            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(types!.Matches(longType, Labels, NotificationPriority.High));
            Assert.False(label!.Matches("t.x", labels, NotificationPriority.High));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
        }

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
        // Backreferences and lookarounds need the backtracking engine, which routes don't use.
        [InlineData("""{ "labels": { "a": "=~(a)\\1" } }""", "/match/labels/a")]
        [InlineData("""{ "labels": { "a": "=~(?=a)a" } }""", "/match/labels/a")]
        // A pattern that closes the anchoring group itself would match any value.
        [InlineData("""{ "labels": { "a": "=~x)|(?:.*" } }""", "/match/labels/a")]
        // Free-spacing mode would turn the anchors after a '#' into a comment.
        [InlineData("""{ "labels": { "a": "=~(?x) a # c" } }""", "/match/labels/a")]
        [InlineData("""{ "labels": { "a": "=~(?ix:a)" } }""", "/match/labels/a")]
        // Regexes are costly to build: few and short per route.
        [InlineData("""{ "labels": { "a": "=~a", "b": "=~b", "c": "=~c", "d": "=~d", "e": "=~e", "f": "=~f", "g": "=~g", "h": "=~h", "i": "=~i", "j": "=~j", "k": "=~k" } }""", "/match/labels")]
        [InlineData("""{ "minPriority": "urgent" }""", "/match/minPriority")]
        [InlineData("""{ "severity": "high" }""", "/match/severity")]
        public void A_bad_match_is_refused_at_the_field(string match, string pointer)
        {
            var (parsed, errors) = RouteMatch.Parse(JsonElement.Parse(match));
            Assert.Null(parsed);
            Assert.Contains(errors, e => e.Pointer == pointer);
        }

        private static string Types(int count, string extra = "") => "[" + string.Join(",", Enumerable.Range(0, count).Select(i => $"\"t{i}.*\"")) + extra + "]";

        private static string LabelMap(int count, Func<int, string> matcher)
            => "{" + string.Join(",", Enumerable.Range(0, count).Select(i => $"\"l{i}\": \"{matcher(i)}\"")) + "}";

        /// <summary>The limits are inclusive: exactly the most is accepted, one more is refused (mutation testing found these boundaries untested).</summary>
        [Fact]
        public void Limits_accept_the_most_and_refuse_one_more()
        {
            static bool Ok(string match) => RouteMatch.Parse(JsonElement.Parse(match)).Match is not null;

            Assert.True(Ok($$"""{ "type": {{Types(20)}} }"""));
            Assert.False(Ok($$"""{ "type": {{Types(21)}} }"""));
            Assert.True(Ok($$"""{ "labels": {{LabelMap(RouteMatch.MaxLabelMatchers, i => "=v")}} }"""));
            Assert.False(Ok($$"""{ "labels": {{LabelMap(RouteMatch.MaxLabelMatchers + 1, i => "=v")}} }"""));
            Assert.True(Ok($$"""{ "labels": {{LabelMap(RouteMatch.MaxRegexMatchers, i => "=~a")}} }"""));
            Assert.True(Ok($$"""{ "labels": { "a": "=~{{new string('a', RouteMatch.MaxRegexLength)}}" } }"""));
            Assert.False(Ok($$"""{ "labels": { "a": "=~{{new string('a', RouteMatch.MaxRegexLength + 1)}}" } }"""));
            Assert.True(Ok($$"""{ "labels": { "a": "{{new string('a', RouteMatch.MaxPatternLength)}}" } }"""));
            Assert.False(Ok($$"""{ "labels": { "a": "{{new string('a', RouteMatch.MaxPatternLength + 1)}}" } }"""));
            // A list of types is strings only, and every one a valid pattern.
            Assert.False(Ok("""{ "type": ["a.*", 5] }"""));
            Assert.False(Ok("""{ "type": ["a.*", "Not Valid"] }"""));
            Assert.False(Ok("""{ "labels": [] }"""));
        }

        /// <summary><c>in(…)</c> needs its closing parenthesis; without it the matcher is a plain value.</summary>
        [Fact]
        public void In_needs_its_closing_parenthesis()
        {
            Assert.True(Matches("""{ "labels": { "env": "in(dev, prod)" } }"""));
            Assert.False(Matches("""{ "labels": { "env": "in(dev, prod" } }"""));
        }
    }
}
