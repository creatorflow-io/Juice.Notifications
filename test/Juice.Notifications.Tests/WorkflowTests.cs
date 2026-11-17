using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Workflows;

namespace Juice.Notifications.Tests
{
    /// <summary>Workflows as types declare them (phase-3-plan I27–I30): what is accepted, what is refused and where, and how conditions read.</summary>
    public sealed class WorkflowTests
    {
        private static (Workflow? Workflow, IReadOnlyList<FieldError> Errors) Parse(string json, string correlation = CorrelationRules.Optional)
            => Workflow.Parse(JsonElement.Parse(json), correlation);

        private static string Pointers(string json, string correlation = CorrelationRules.Optional)
            => string.Join(",", Parse(json, correlation).Errors.Select(e => e.Pointer));

        [Fact]
        public void The_steps_parse_in_order()
        {
            var (workflow, errors) = Parse("""
                [ { "step": "condition", "when": { "labels": { "severity": "in(critical,high)" } } },
                  { "step": "throttle", "limit": 5, "windowSeconds": 60, "by": "labels.service" },
                  { "step": "delay", "seconds": 30 },
                  { "step": "send" } ]
                """);
            Assert.Empty(errors);
            Assert.Collection(workflow!.Steps,
                s => Assert.IsType<ConditionStep>(s),
                s => Assert.Equal(new ThrottleStep(5, TimeSpan.FromMinutes(1), "labels.service"), s),
                s => Assert.Equal(new DelayStep(TimeSpan.FromSeconds(30)), s),
                s => Assert.IsType<SendStep>(s));
            Assert.False(workflow.IsSendOnly);
            Assert.True(Parse("""[{"step":"send"}]""").Workflow!.IsSendOnly);
            Assert.Same(Workflow.SendOnly, Workflow.Read(Workflow.SendOnlyJson));
        }

        [Theory]
        [InlineData("""{}""", "/workflow")]
        [InlineData("""[]""", "/workflow")]
        [InlineData("""[{"step":"delay","seconds":30}]""", "/workflow")] // never sends
        [InlineData("""["send"]""", "/workflow/0")]
        [InlineData("""[{"step":"wait"},{"step":"send"}]""", "/workflow/0/step")]
        [InlineData("""[{"step":"escalate"},{"step":"send"}]""", "/workflow/0/step")]
        [InlineData("""[{"step":"digest","windowSeconds":60}]""", "/workflow/0")] // holds for a send that isn't there
        [InlineData("""[{"step":"digest","windowSeconds":60},{"step":"delay","seconds":5},{"step":"send"}]""", "/workflow/0")]
        [InlineData("""[{"step":"digest"},{"step":"send"}]""", "/workflow/0/windowSeconds")]
        [InlineData("""[{"step":"digest","windowSeconds":60,"by":"label"},{"step":"send"}]""", "/workflow/0/by")]
        [InlineData("""[{"step":"send","extra":1}]""", "/workflow/0/extra")]
        [InlineData("""[{"step":"delay","seconds":0},{"step":"send"}]""", "/workflow/0/seconds")]
        [InlineData("""[{"step":"delay","seconds":604801},{"step":"send"}]""", "/workflow/0/seconds")]
        [InlineData("""[{"step":"delay","seconds":"30"},{"step":"send"}]""", "/workflow/0/seconds")]
        [InlineData("""[{"step":"throttle","limit":0,"windowSeconds":60},{"step":"send"}]""", "/workflow/0/limit")]
        [InlineData("""[{"step":"throttle","limit":5,"windowSeconds":86401},{"step":"send"}]""", "/workflow/0/windowSeconds")]
        [InlineData("""[{"step":"throttle","limit":5,"windowSeconds":60,"by":"route"},{"step":"send"}]""", "/workflow/0/by")]
        [InlineData("""[{"step":"throttle","limit":5,"windowSeconds":60,"by":"labels.1x"},{"step":"send"}]""", "/workflow/0/by")]
        [InlineData("""[{"step":"condition"},{"step":"send"}]""", "/workflow/0/when")]
        [InlineData("""[{"step":"condition","when":{"data":{"x":"y"}}},{"step":"send"}]""", "/workflow/0/when/data")]
        [InlineData("""[{"step":"condition","when":{"labels":{"a":"=~(a)\\1"}}},{"step":"send"}]""", "/workflow/0/when/labels/a")]
        [InlineData("""[{"step":"condition","when":{"correlation":["closed"]}},{"step":"send"}]""", "/workflow/0/when/correlation")]
        [InlineData("""[{"step":"condition","when":{"labels":{},"correlation":["open"]}},{"step":"send"}]""", "/workflow/0/when")]
        [InlineData("""[{"step":"condition","when":{"all":[{"any":[{"all":[{"labels":{"a":"b"}}]}]}]}},{"step":"send"}]""", "/workflow/0/when/all/0/any/0/all")]
        public void A_bad_workflow_is_refused_where_it_is_wrong(string json, string pointer)
            => Assert.Equal(pointer, Pointers(json));

        [Fact]
        public void A_digest_holds_for_the_send_after_it()
        {
            var (workflow, errors) = Parse("""[{"step":"digest","windowSeconds":30,"by":"subscriber"},{"step":"send"}]""");
            Assert.Empty(errors);
            Assert.Equal(new DigestStep(TimeSpan.FromSeconds(30), DigestKeys.Subscriber), workflow!.Steps[0]);
            Assert.Equal(DigestKeys.Route, Assert.IsType<DigestStep>(Parse("""[{"step":"digest","windowSeconds":30},{"step":"send"}]""").Workflow!.Steps[0]).By);
            const string byCorrelation = """[{"step":"digest","windowSeconds":30,"by":"correlation"},{"step":"send"}]""";
            Assert.Equal("/workflow/0/by", Pointers(byCorrelation, CorrelationRules.Optional));
            Assert.Empty(Pointers(byCorrelation, CorrelationRules.Required));
        }

        [Fact]
        public void Steps_that_read_the_correlation_need_a_type_that_carries_one()
        {
            const string condition = """[{"step":"condition","when":{"any":[{"correlation":["open"]}]}},{"step":"send"}]""";
            Assert.Equal("/workflow/0/when", Pointers(condition, CorrelationRules.None));
            Assert.Empty(Pointers(condition, CorrelationRules.Optional));
            const string throttle = """[{"step":"throttle","limit":1,"windowSeconds":60,"by":"correlation"},{"step":"send"}]""";
            Assert.Equal("/workflow/0/by", Pointers(throttle, CorrelationRules.Optional));
            Assert.Empty(Pointers(throttle, CorrelationRules.Required));
        }

        [Fact]
        public void A_condition_reads_labels_and_the_correlation_state_and_a_missing_one_doesnt_match()
        {
            var (workflow, _) = Parse("""
                [ { "step": "condition", "when": { "all": [
                    { "labels": { "env": "prod", "team": "!=ops" } },
                    { "any": [ { "correlation": ["open", "acknowledged"] }, { "labels": { "force": "yes" } } ] } ] } },
                  { "step": "send" } ]
                """);
            var when = Assert.IsType<ConditionStep>(workflow!.Steps[0]).When;
            Assert.True(when.ReadsCorrelation);
            Assert.True(when.Holds(new Dictionary<string, string> { ["env"] = "prod" }, "open"));
            Assert.True(when.Holds(new Dictionary<string, string> { ["env"] = "prod", ["force"] = "yes" }, null));
            Assert.False(when.Holds(new Dictionary<string, string> { ["env"] = "prod" }, null)); // no correlation: no state matches
            Assert.False(when.Holds(new Dictionary<string, string> { ["env"] = "prod" }, "resolved"));
            Assert.False(when.Holds(new Dictionary<string, string> { ["env"] = "prod", ["team"] = "ops" }, "open"));
            Assert.False(when.Holds(new Dictionary<string, string>(), "open")); // a missing label doesn't match
        }

        [Fact]
        public void A_throttle_counts_by_the_type_the_correlation_or_a_label_hashing_long_values()
        {
            var request = new NotificationRequest
            {
                IdempotencyKey = "k", TypeKey = "t", SchemaHash = "h", Data = "{}", Recipients = "[]", CorrelationKey = "alert:1",
                Labels = new Dictionary<string, string> { ["service"] = "billing", ["long"] = new string('x', 300) },
            };
            Assert.Equal("", WorkflowRunner.ThrottleKey(ThrottleKeys.Type, request));
            Assert.Equal("alert:1", WorkflowRunner.ThrottleKey(ThrottleKeys.Correlation, request));
            Assert.Equal("billing", WorkflowRunner.ThrottleKey("labels.service", request));
            Assert.Equal("", WorkflowRunner.ThrottleKey("labels.missing", request));
            Assert.StartsWith("sha256:", WorkflowRunner.ThrottleKey("labels.long", request));
        }
    }
}
