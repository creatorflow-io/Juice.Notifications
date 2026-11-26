using Juice.Notifications.Intake;

namespace Juice.Notifications.Tests
{
    /// <summary>A request's occurrence time (phase 5, M45-n): at most 5 minutes ahead of the service's clock, at most 7 days old; absent is fine.</summary>
    public sealed class OccurredAtRulesTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

        private static List<FieldError> Check(DateTimeOffset? occurredAt)
        {
            var invalid = new List<FieldError>();
            RequestRules.Occurred(occurredAt, Now, invalid);
            return invalid;
        }

        [Fact]
        public void Five_minutes_ahead_is_taken_and_a_second_more_is_refused()
        {
            Assert.Empty(Check(Now.AddMinutes(5)));
            Assert.Equal("/occurredAt", Assert.Single(Check(Now.AddMinutes(5).AddSeconds(1))).Pointer);
        }

        [Fact]
        public void Seven_days_old_is_taken_and_a_second_older_is_refused()
        {
            Assert.Empty(Check(Now.AddDays(-7)));
            Assert.Equal("/occurredAt", Assert.Single(Check(Now.AddDays(-7).AddSeconds(-1))).Pointer);
        }

        [Fact]
        public void Absent_or_now_is_taken()
        {
            Assert.Empty(Check(null));
            Assert.Empty(Check(Now));
        }
    }
}
