namespace Juice.Notifications
{
    public static class ClockExtensions
    {
        /// <summary>
        /// Now, rounded down to the microsecond PostgreSQL stores. Every timestamp the service writes comes from here, so a command's result shows exactly
        /// what a later read returns.
        /// </summary>
        public static DateTimeOffset UtcNow(this TimeProvider clock)
        {
            var now = clock.GetUtcNow();
            return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        }
    }
}
