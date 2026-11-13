using System.Text.Json;
using Juice.Notifications.Messaging;
using Newtonsoft.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// A <see cref="NotificationRequested"/> read the way Juice's message serializer reads it (Newtonsoft, dates parsed as offsets): the data
    /// reaches intake as it was written.
    /// </summary>
    public sealed class NotificationRequestedTests
    {
        private static readonly JsonSerializerSettings JuiceReadSettings = new() { DateParseHandling = DateParseHandling.DateTimeOffset };

        private static NotificationRequested Read(string data)
            => JsonConvert.DeserializeObject<NotificationRequested>($$"""{ "NotificationTenantId": "0193f0aa-0000-7000-8000-0000000000a1", "Type": "a.b", "Data": {{data}} }""",
                JuiceReadSettings)!;

        [Theory]
        [InlineData("""{"at":"2026-11-13T08:00:00Z"}""")]
        [InlineData("""{"at":"2026-11-13T08:00:00.1234567+07:00","day":"2026-11-13"}""")]
        [InlineData("""{"nested":{"list":[1,"2",true,null,{"at":"2026-01-01T00:00:00Z"}]},"text":"café \"quoted\""}""")]
        [InlineData("""{}""")]
        public void Data_reaches_intake_as_written(string data)
        {
            var read = Read(data).Data!.Value;
            Assert.Equal(JsonSerializer.Serialize(JsonDocument.Parse(data).RootElement), JsonSerializer.Serialize(read));
        }

        [Theory]
        [InlineData("1.10", 1.1)]
        [InlineData("1e-30", 1e-30)]
        [InlineData("1E+2", 100.0)]
        [InlineData("-1.5", -1.5)]
        [InlineData("0.12345678901234567890123456789012", 0.12345678901234567890123456789012)]
        [InlineData("123456789012345678901234567890.5", 123456789012345678901234567890.5)]
        public void A_number_with_a_fraction_or_exponent_arrives_as_the_nearest_double(string written, double expected)
            => Assert.Equal(expected, Read($"{{\"n\":{written}}}").Data!.Value.GetProperty("n").GetDouble());

        [Theory]
        [InlineData("12345678901234567890123")]
        [InlineData("-42")]
        [InlineData("0")]
        public void An_integer_arrives_exactly(string written)
            => Assert.Equal(written, Read($"{{\"n\":{written}}}").Data!.Value.GetProperty("n").GetRawText());

        [Fact]
        public void A_repeated_property_name_is_kept_for_intake_to_report()
            => Assert.Equal(2, Read("""{"a":1,"a":2}""").Data!.Value.EnumerateObject().Count(p => p.Name == "a"));

        [Theory]
        [InlineData("null")]
        [InlineData("[1,2]")]
        [InlineData("\"text\"")]
        public void Data_that_isnt_an_object_still_arrives_for_intake_to_refuse(string data)
        {
            var read = Read(data).Data;
            Assert.Equal(data == "null" ? null : JsonDocument.Parse(data).RootElement.ValueKind, read?.ValueKind);
        }

        [Fact]
        public void Other_fields_are_read_too_and_the_settings_are_left_as_they_were()
        {
            var message = JsonConvert.DeserializeObject<NotificationRequested>("""
                { "Data": { "at": "2026-11-13T08:00:00Z" }, "ExpiresAt": "2026-11-14T08:00:00+07:00", "Recipients": [{ "kind": "address", "channel": "email", "address": "a@b.c" }],
                  "Actor": { "kind": "system", "id": "x" }, "Labels": { "env": "prod" }, "TypeVersion": 2 }
                """, JuiceReadSettings)!;
            // Read after Data with Juice's settings again: an offset date.
            Assert.Equal(new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.FromHours(7)), message.ExpiresAt);
            Assert.Equal(TimeSpan.FromHours(7), message.ExpiresAt!.Value.Offset);
            Assert.Equal(new RequestedRecipient("address", "email", "a@b.c"), Assert.Single(message.Recipients!));
            Assert.Equal((new RequestedActor("system", "x"), "prod", 2), (message.Actor, message.Labels!["env"], message.TypeVersion));
        }

        [Fact]
        public void A_producer_using_the_record_writes_the_data_as_it_holds_it()
        {
            var sent = new NotificationRequested { Type = "a.b", Data = JsonDocument.Parse("""{"at":"2026-11-13T08:00:00Z","ratio":1.5}""").RootElement };
            var written = JsonConvert.SerializeObject(sent);
            Assert.Contains("""{"at":"2026-11-13T08:00:00Z","ratio":1.5}""", written);
            Assert.Equal("""{"at":"2026-11-13T08:00:00Z","ratio":1.5}""", JsonSerializer.Serialize(JsonConvert.DeserializeObject<NotificationRequested>(written, JuiceReadSettings)!.Data));
            Assert.Contains("\"Data\":null", JsonConvert.SerializeObject(new NotificationRequested()));
        }
    }
}
