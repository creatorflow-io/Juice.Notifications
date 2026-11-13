using Juice.Notifications.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.Tests
{
    /// <summary>How long a failing intake message is retried before it is parked.</summary>
    public sealed class RabbitMQIntakeOptionsTests
    {
        [Theory]
        [InlineData("1.00:00:00", "00:00:30", 2881)] // the defaults: a day of attempts every 30 s
        [InlineData("00:00:02", "00:00:01", 3)]
        [InlineData("00:00:02.5", "00:00:01", 3)]
        [InlineData("00:00:00", "00:00:30", 1)] // parked on the first failure
        [InlineData("30.00:00:00", "00:00:01", 2592001)] // the longest, and fits
        public void A_message_gets_its_first_attempt_and_one_per_RetryDelay_within_ParkAfter(string parkAfter, string retryDelay, int attempts)
        {
            var options = new RabbitMQIntakeOptions { ParkAfter = TimeSpan.Parse(parkAfter), RetryDelay = TimeSpan.Parse(retryDelay) };
            Assert.Equal(attempts, options.ParkAfterAttempts);
        }

        [Fact]
        public void The_defaults_park_after_a_day_on_the_parked_queue()
        {
            var options = new RabbitMQIntakeOptions();
            Assert.Equal((TimeSpan.FromDays(1), "notifications.requests.parked"), (options.ParkAfter, options.ParkedName));
        }

        [Theory]
        [InlineData("-00:00:01")]
        [InlineData("30.00:00:01")]
        public void ParkAfter_out_of_range_is_refused_at_registration(string parkAfter)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Intake:RabbitMQ:Enabled"] = "true",
                ["Intake:RabbitMQ:UserName"] = "notifications",
                ["Intake:RabbitMQ:Password"] = "secret",
                ["Intake:RabbitMQ:ParkAfter"] = parkAfter,
            }).Build();
            var refused = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddNotificationsRabbitMQIntake(configuration));
            Assert.Contains("ParkAfter", refused.Message);
        }
    }
}
