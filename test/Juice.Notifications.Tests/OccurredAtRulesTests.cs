using Juice.Notifications.Workflows;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// The first escalation step's time from a request's occurrence (phase 5, M45-n, the architect's decision): any occurrence is accepted and
    /// kept; the escalation takes it within [now − 7 days, now + 5 minutes], clamping beyond, and the step is never before now.
    /// </summary>
    public sealed class OccurredAtRulesTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

        [Fact]
        public void Thirty_days_old_is_clamped_to_7_days_and_the_step_is_due_at_once()
            => Assert.Equal((Now, (WorkflowRunner.Clamped?)WorkflowRunner.Clamped.Past), WorkflowRunner.FirstStepAt(Now.AddDays(-30), 5, Now));

        [Fact]
        public void An_hour_ahead_is_clamped_to_5_minutes_ahead()
            => Assert.Equal((Now.AddMinutes(5 + 5), (WorkflowRunner.Clamped?)WorkflowRunner.Clamped.Future), WorkflowRunner.FirstStepAt(Now.AddHours(1), 5, Now));

        [Fact]
        public void Within_the_bounds_nothing_is_clamped()
        {
            Assert.Equal((Now.AddMinutes(3), (WorkflowRunner.Clamped?)null), WorkflowRunner.FirstStepAt(Now.AddMinutes(-2), 5, Now));
            Assert.Equal((Now.AddMinutes(10), (WorkflowRunner.Clamped?)null), WorkflowRunner.FirstStepAt(Now.AddMinutes(5), 5, Now));
            Assert.Equal((Now, (WorkflowRunner.Clamped?)null), WorkflowRunner.FirstStepAt(Now.AddDays(-7), 5, Now));
        }

        [Fact]
        public void Without_an_occurrence_the_wait_runs_from_now()
            => Assert.Equal((Now.AddMinutes(5), (WorkflowRunner.Clamped?)null), WorkflowRunner.FirstStepAt(null, 5, Now));
    }
}
