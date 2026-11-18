using System.Text.Json;
using Juice.Notifications.Retention;

namespace Juice.Notifications.Tests
{
    /// <summary>Masking a type's <c>redact</c> paths (design §13.2, phase-4-plan I46).</summary>
    public sealed class RedactionTests
    {
        [Fact]
        public void Paths_are_masked_nested_or_not_and_their_values_returned()
        {
            var (data, values) = Redaction.Apply("""{"user":{"email":"pat@example.com","name":"Pat"},"pin":123456,"missing":null}""",
                ["data.user.email", "data.pin", "data.nothing.here", "data.missing"]);

            using var json = JsonDocument.Parse(data);
            Assert.Equal("***", json.RootElement.GetProperty("user").GetProperty("email").GetString());
            Assert.Equal("Pat", json.RootElement.GetProperty("user").GetProperty("name").GetString());
            Assert.Equal("***", json.RootElement.GetProperty("pin").GetString());
            Assert.False(json.RootElement.TryGetProperty("nothing", out _));
            Assert.Equal(["pat@example.com", "123456"], values);
        }

        [Fact]
        public void Copies_are_scrubbed_longest_first_and_short_values_left()
        {
            Assert.Equal("Hi ***, your code *** (ab)", Redaction.Scrub("Hi pat@example.com, your code 123456 (ab)", ["123456", "pat@example.com", "ab"]));
        }
    }
}
