using System.Text.Json;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Workflows;

namespace Juice.Notifications.Tests
{
    /// <summary>Escalation policies and the escalate step as declared (phase-3-plan I34).</summary>
    public sealed class EscalationRulesTests
    {
        private static readonly RecipientResolverConfig Staff = new()
        {
            Key = "staff", Kind = "fake", Handles = ["group", "user"], Settings = "{}", DefinitionHash = "h",
        };

        private static string Pointers(string steps, params RecipientResolverConfig[] resolvers)
            => string.Join(",", EscalationRules.Parse(JsonElement.Parse(steps), resolvers).Errors.Select(e => e.Pointer));

        [Fact]
        public void A_policy_lists_steps_of_a_wait_and_the_recipients_they_add()
        {
            var (steps, errors) = EscalationRules.Parse(JsonElement.Parse("""
                [ { "afterMinutes": 0, "recipients": [ { "kind": "route" } ] },
                  { "afterMinutes": 15, "recipients": [ { "kind": "group", "id": "sre", "resolver": "staff" }, { "kind": "user", "id": "lead", "resolver": "staff" } ] } ]
                """), [Staff]);
            Assert.Empty(errors);
            Assert.Equal([0, 15], steps!.Select(s => s.AfterMinutes));
            Assert.Equal(["group:sre", "user:lead"], steps![1].Recipients.Select(r => r.Name));
            Assert.Equal("staff", steps[1].Recipients[0].Resolver);
        }

        [Theory]
        [InlineData("""[]""", "/steps")]
        [InlineData("""{}""", "/steps")]
        [InlineData("""[{ "afterMinutes": -1, "recipients": [ { "kind": "route" } ] }]""", "/steps/0/afterMinutes")]
        [InlineData("""[{ "afterMinutes": 10081, "recipients": [ { "kind": "route" } ] }]""", "/steps/0/afterMinutes")]
        [InlineData("""[{ "afterMinutes": 5, "recipients": [] }]""", "/steps/0/recipients")]
        [InlineData("""[{ "afterMinutes": 5, "recipients": [ { "kind": "address", "channel": "email", "address": "a@b.c" } ] }]""", "/steps/0/recipients/0/kind")]
        [InlineData("""[{ "afterMinutes": 5, "recipients": [ { "kind": "group", "id": "sre" } ] }]""", "/steps/0/recipients/0/resolver")]
        [InlineData("""[{ "afterMinutes": 5, "recipients": [ { "kind": "role", "id": "oncall", "resolver": "staff" } ] }]""", "/steps/0/recipients/0/resolver")]
        [InlineData("""[{ "afterMinutes": 5, "recipients": [ { "kind": "group", "id": "", "resolver": "staff" } ] }]""", "/steps/0/recipients/0/id")]
        [InlineData("""[{ "afterMinutes": 5, "recipients": [ { "kind": "route", "id": "x" } ] }]""", "/steps/0/recipients/0/id")]
        [InlineData("""[{ "afterMinutes": 5, "recipients": [ { "kind": "route" } ], "extra": true }]""", "/steps/0/extra")]
        public void A_bad_policy_is_refused_where_it_is_wrong(string steps, string pointer)
            => Assert.Equal(pointer, Pointers(steps, Staff));

        [Fact]
        public void A_resolver_that_is_disabled_or_missing_is_refused_when_saved_but_not_when_read()
        {
            const string steps = """[{ "afterMinutes": 5, "recipients": [ { "kind": "group", "id": "sre", "resolver": "staff" } ] }]""";
            Assert.Equal("/steps/0/recipients/0/resolver", Pointers(steps));
            var disabled = new RecipientResolverConfig { Key = "staff", Kind = "fake", Handles = ["group"], Settings = "{}", DefinitionHash = "h", Enabled = false };
            Assert.Equal("/steps/0/recipients/0/resolver", Pointers(steps, disabled));
            Assert.Single(EscalationRules.Read(steps));
        }

        [Fact]
        public void The_escalate_step_names_a_policy_or_its_label_and_needs_a_correlation()
        {
            var (workflow, errors) = Workflow.Parse(JsonElement.Parse("""[{"step":"send"},{"step":"escalate","policyFrom":"labels.policy"}]"""), CorrelationRules.Optional);
            Assert.Empty(errors);
            Assert.Equal(new EscalateStep(null, "policy"), workflow!.Steps[1]);
            Assert.Equal(new EscalateStep("ops", null), Workflow.Parse(JsonElement.Parse("""[{"step":"send"},{"step":"escalate","policy":"ops"}]"""),
                CorrelationRules.Required).Workflow!.Steps[1]);
            Assert.Equal("/workflow/1", Assert.Single(Workflow.Parse(JsonElement.Parse("""[{"step":"send"},{"step":"escalate","policy":"ops"}]"""),
                CorrelationRules.None).Errors).Pointer);
        }
    }
}
