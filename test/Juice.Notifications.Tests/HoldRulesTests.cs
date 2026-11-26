using Juice.Notifications.Correlations;

namespace Juice.Notifications.Tests
{
    /// <summary>The hold rules (phase-5-plan p5-d3; m44-architecture §4.1).</summary>
    public sealed class HoldRulesTests
    {
        private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

        [Fact]
        public void Unheld_holds_the_same_reason_is_no_change_and_another_replaces_it()
        {
            Assert.Equal(HoldDecision.Hold, HoldRules.Hold(null, null, "silence:7"));
            Assert.Equal(HoldDecision.None, HoldRules.Hold(At, "silence:7", "silence:7"));
            Assert.Equal(HoldDecision.ReplaceReason, HoldRules.Hold(At, "silence:7", "silence:8"));
            // Ordinal: a reason in another case is another reason.
            Assert.Equal(HoldDecision.ReplaceReason, HoldRules.Hold(At, "silence:7", "Silence:7"));
        }

        [Fact]
        public void A_release_releases_what_is_held_and_leaves_the_unheld_alone()
        {
            Assert.Equal(HoldDecision.Release, HoldRules.Release(At));
            Assert.Equal(HoldDecision.None, HoldRules.Release(null));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" silence:7")]
        [InlineData("silence:7 ")]
        [InlineData("silence\u00077")]
        [InlineData("silence\n7")]
        public void A_reason_is_a_label(string? reason)
        {
            Assert.False(HoldRules.IsReason(reason));
            Assert.Equal(HoldDecision.Invalid, HoldRules.Hold(null, null, reason));
            Assert.Equal(HoldDecision.Invalid, HoldRules.Hold(At, "silence:7", reason));
        }

        [Fact]
        public void A_reason_is_up_to_200_characters_and_may_be_any_script()
        {
            Assert.True(HoldRules.IsReason(new string('a', 200)));
            Assert.False(HoldRules.IsReason(new string('a', 201)));
            Assert.True(HoldRules.IsReason("bảo trì: kho lạnh"));
        }
    }
}
