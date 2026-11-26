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
            // Read from its text (the event's converter reads dates as text) with the offset it was written with.
            Assert.Equal(new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.FromHours(7)), message.ExpiresAt);
            Assert.Equal(TimeSpan.FromHours(7), message.ExpiresAt!.Value.Offset);
            Assert.Equal(new RequestedRecipient("address", "email", "a@b.c"), Assert.Single(message.Recipients!));
            Assert.Equal((new RequestedActor("system", "x"), "prod", 2), (message.Actor, message.Labels!["env"], message.TypeVersion));
        }

        [Fact]
        public void An_occurrence_time_is_read_with_its_offset()
        {
            var message = JsonConvert.DeserializeObject<NotificationRequested>("""{ "Type": "a.b", "occurredAt": "2026-11-26T08:00:00+07:00" }""", JuiceReadSettings)!;
            Assert.Equal(new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.FromHours(7)), message.OccurredAt);
            Assert.Null(Read("{}").OccurredAt);
        }

        /// <summary>
        /// <c>dryRun</c> (M44-2c), as over HTTP: true, false or absent; anything else is kept for intake to refuse at <c>/dryRun</c>, not read as
        /// Newtonsoft would (<c>"true"</c>, <c>1</c>).
        /// </summary>
        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        [InlineData("null", null)]
        public void A_dry_run_request_is_read_as_a_boolean(string written, bool? expected)
        {
            var message = ReadMessage($$"""{ "Type": "a.b", "dryRun": {{written}} }""")!;
            Assert.Equal(expected, message.DryRun);
            Assert.Empty(NotificationRequestedConverter.TextErrors(message));
            Assert.Null(Read("{}").DryRun);
        }

        [Theory]
        [InlineData("\"true\"", "text")]
        [InlineData("1", "a number")]
        [InlineData("{}", "an object")]
        public void A_dry_run_that_isnt_a_boolean_is_kept_for_intake_to_refuse(string written, string kind)
        {
            var message = ReadMessage($$"""{ "Type": "a.b", "DRYRUN": {{written}}, "Priority": "high" }""")!;
            var error = Assert.Single(NotificationRequestedConverter.TextErrors(message));
            Assert.Equal(("/dryRun", $"This is true or false; it is {kind}."), (error.Pointer, error.Message));
            Assert.Equal((null, "high"), (message.DryRun, message.Priority));
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

        // Text fields take only text, as over HTTP (deferred #20), and keep date-like text as written.

        private static NotificationRequested? ReadMessage(string json) => JsonConvert.DeserializeObject<NotificationRequested>(json, JuiceReadSettings);

        [Fact]
        public void Text_fields_keep_their_text_and_dates_stay_dates()
        {
            var tenant = Guid.NewGuid();
            var message = ReadMessage($$"""
                {
                  "MessageId": "6f0a3c4e-1d2b-4c5d-8e9f-0a1b2c3d4e5f",
                  "CreatedAt": "2026-11-15T08:00:00.5+07:00",
                  "NotificationTenantId": "{{tenant}}",
                  "Type": "app.thing",
                  "TypeVersion": 2,
                  "IdempotencyKey": "2026-11-13T08:00:00.123Z",
                  "CorrelationKey": "2026-11-13",
                  "Recipients": [ { "Kind": "address", "Channel": "email", "Address": "a@example.com" }, null ],
                  "Actor": { "Kind": "user", "Id": "2026-01-01T00:00:00Z" },
                  "Priority": "high",
                  "Labels": { "at": "2026-11-13T08:00:00.123Z", "env": "prod" },
                  "Links": { "self": "https://example.com/x" },
                  "ExpiresAt": "2026-11-16T00:00:00.250Z"
                }
                """)!;

            Assert.Equal(Guid.Parse("6f0a3c4e-1d2b-4c5d-8e9f-0a1b2c3d4e5f"), message.MessageId);
            Assert.Equal(new DateTimeOffset(2026, 11, 15, 8, 0, 0, 500, TimeSpan.FromHours(7)), message.CreatedAt);
            Assert.Equal(TimeSpan.FromHours(7), message.CreatedAt.Offset);
            Assert.Equal((tenant, "app.thing", 2, "high"), (message.NotificationTenantId, message.Type, message.TypeVersion, message.Priority));
            // The strict string fields see these as text only because the event's converter reads dates as text: under Juice's settings they'd
            // arrive parsed, and be refused.
            Assert.Equal(("2026-11-13T08:00:00.123Z", "2026-11-13"), (message.IdempotencyKey, message.CorrelationKey));
            Assert.Equal("2026-11-13T08:00:00.123Z", message.Labels!["at"]);
            Assert.Equal("2026-01-01T00:00:00Z", message.Actor!.Id);
            Assert.Equal(new RequestedRecipient("address", "email", "a@example.com"), message.Recipients![0]);
            Assert.Null(message.Recipients[1]);
            Assert.Equal("https://example.com/x", message.Links!["self"]);
            Assert.Equal(new DateTimeOffset(2026, 11, 16, 0, 0, 0, 250, TimeSpan.Zero), message.ExpiresAt);
            // The same by runtime type, as the consumer reads it, and with the date-like field first or after the data.
            foreach (var json in new[]
            {
                """{ "IdempotencyKey": "2026-11-13T08:00:00.123Z" }""",
                """{ "Data": { "at": "2026-01-01T00:00:00Z" }, "IdempotencyKey": "2026-11-13T08:00:00.123Z" }""",
            })
            {
                var byType = (NotificationRequested?)JsonConvert.DeserializeObject(json, typeof(NotificationRequested), JuiceReadSettings);
                Assert.Equal(message.IdempotencyKey, byType!.IdempotencyKey);
                Assert.Equal(message.IdempotencyKey, ReadMessage(json)!.IdempotencyKey);
            }
            // And the caller's settings are as they were after the read.
            var serializer = Newtonsoft.Json.JsonSerializer.CreateDefault(JuiceReadSettings);
            using var reader = new JsonTextReader(new StringReader("""{ "IdempotencyKey": "2026-11-13T08:00:00.123Z" }"""));
            Assert.Equal(message.IdempotencyKey, serializer.Deserialize<NotificationRequested>(reader)!.IdempotencyKey);
            Assert.Equal(DateParseHandling.DateTimeOffset, serializer.DateParseHandling);
        }

        [Fact]
        public void Null_text_fields_stay_null()
        {
            var message = ReadMessage("""{ "Type": null, "Labels": { "a": null } }""")!;
            Assert.Null(message.Type);
            Assert.Null(message.Labels!["a"]);
            Assert.Null(message.Links);
            Assert.Null(ReadMessage("null"));
        }

        /// <summary>Kept for intake to refuse with a pointer, as the HTTP API's (deferred #20); the rest of the message is still read.</summary>
        [Theory]
        [InlineData("""{ "Type": 5 }""", "/type", "a number")]
        [InlineData("""{ "IdempotencyKey": 12345 }""", "/idempotencyKey", "a number")]
        [InlineData("""{ "CorrelationKey": true }""", "/correlationKey", "a boolean")]
        [InlineData("""{ "Priority": 1.5 }""", "/priority", "a number")]
        [InlineData("""{ "Labels": { "n": 5 } }""", "/labels/n", "a number")]
        [InlineData("""{ "Labels": { "Mixed.Case/x": 5 } }""", "/labels/Mixed.Case~1x", "a number")]
        [InlineData("""{ "Labels": { "it's": 5 } }""", "/labels/it's", "a number")]
        [InlineData("""{ "Links": { "self": false } }""", "/links/self", "a boolean")]
        [InlineData("""{ "Labels": { "o": { "deep": [1] } } }""", "/labels/o", "an object")]
        [InlineData("""{ "Labels": { "a": [ "x" ] } }""", "/labels/a", "an array")]
        [InlineData("""{ "Recipients": [ { "Kind": "address", "Channel": "email", "Address": 7 } ] }""", "/recipients/0/address", "a number")]
        [InlineData("""{ "Actor": { "Kind": "user", "Id": 42 } }""", "/actor/id", "a number")]
        [InlineData("""{ "Labels": { "": 5 } }""", "/labels/", "a number")]
        [InlineData("""{ "LABELS": { "N": 5 } }""", "/labels/N", "a number")]
        [InlineData("""{ "recipients": [ { "ADDRESS": 7 } ] }""", "/recipients/0/address", "a number")]
        public void A_value_that_isnt_text_where_text_goes_is_kept_for_intake_to_refuse(string json, string pointer, string kind)
        {
            // Fields around the bad one, at the top level only: the read goes on past it.
            var body = json.Trim();
            var message = ReadMessage($$"""{ "Priority": "high", {{body[1..^1].Trim()}}, "IdempotencyKey": "after" }""")!;
            var error = Assert.Single(NotificationRequestedConverter.TextErrors(message));
            Assert.Equal(pointer, error.Pointer);
            Assert.Contains(kind, error.Message);
            Assert.Equal("after", message.IdempotencyKey);
            Assert.Equal(json.Contains("\"Priority\"") ? null : "high", message.Priority);
        }

        [Fact]
        public void A_good_message_has_no_text_errors_and_one_message_doesnt_leak_into_the_next()
        {
            Assert.Single(NotificationRequestedConverter.TextErrors(ReadMessage("""{ "Labels": { "n": 5 } }""")!));
            Assert.Empty(NotificationRequestedConverter.TextErrors(ReadMessage("""{ "Labels": { "n": "5" } }""")!));
            // Kept beside the message, not in it: two reads of one body are equal records, and a copy carries no errors.
            var (first, second) = (ReadMessage("""{ "MessageId": "6f0a3c4e-1d2b-4c5d-8e9f-0a1b2c3d4e5f", "CreatedAt": "2026-11-15T08:00:00Z", "Type": 5 }""")!,
                ReadMessage("""{ "MessageId": "6f0a3c4e-1d2b-4c5d-8e9f-0a1b2c3d4e5f", "CreatedAt": "2026-11-15T08:00:00Z", "Type": 5 }""")!);
            Assert.Equal(first, second);
            Assert.Empty(NotificationRequestedConverter.TextErrors(first with { Priority = "high" }));
        }

        /// <summary>Label names Newtonsoft escapes in its path (a tab, a line separator, a quote) come back as sent.</summary>
        [Fact]
        public void A_label_name_with_escaped_characters_points_to_the_name_as_sent()
        {
            var lineSeparator = ((char)0x2028).ToString();
            var json = JsonConvert.SerializeObject(new Dictionary<string, object> { ["Labels"] = new Dictionary<string, object> { ["a\tb"] = 1, ["c" + lineSeparator + "d"] = 2, ["e'f"] = 3 } });
            var pointers = NotificationRequestedConverter.TextErrors(ReadMessage(json)!).Select(e => e.Pointer).ToList();
            Assert.Equal(["/labels/a\tb", "/labels/c" + lineSeparator + "d", "/labels/e'f"], pointers);
        }

        /// <summary>Outside the event (a producer's own use of the converter), a value that isn't text fails the read.</summary>
        [Fact]
        public void Outside_a_message_a_value_that_isnt_text_fails_the_read()
            => Assert.Contains("a number", Assert.Throws<JsonSerializationException>(
                () => JsonConvert.DeserializeObject<RequestedActor>("""{ "Kind": "user", "Id": 42 }""", JuiceReadSettings)).Message);

        /// <summary>
        /// A body buffered into a JToken with dates parsed first (a JObject read with Juice's settings, then ToObject) reaches the converter
        /// with the date already a date: it is read, as ISO 8601, not refused.
        /// </summary>
        [Fact]
        public void A_date_already_parsed_by_a_buffering_reader_is_read_as_ISO_text()
        {
            var buffered = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(
                """{ "IdempotencyKey": "2026-11-13T08:00:00.123Z", "Labels": { "at": "2026-11-13T08:00:00+07:00" } }""", JuiceReadSettings)!;
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Date, buffered["IdempotencyKey"]!.Type);
            var message = buffered.ToObject<NotificationRequested>()!;
            Assert.Empty(NotificationRequestedConverter.TextErrors(message));
            Assert.Equal("2026-11-13T08:00:00.123+00:00", message.IdempotencyKey);
            Assert.Equal("2026-11-13T08:00:00+07:00", message.Labels!["at"]);
        }

        [Fact]
        public void Paths_become_the_http_apis_pointers()
        {
            Assert.Equal("/labels/a.b", NotificationRequestedConverter.Pointer("Labels['a.b']"));
            Assert.Equal("/labels/a'b", NotificationRequestedConverter.Pointer(@"Labels['a\'b']"));
            Assert.Equal("/recipients/12/channel", NotificationRequestedConverter.Pointer("Recipients[12].Channel"));
            Assert.Equal("/labels/u", NotificationRequestedConverter.Pointer(@"Labels['\u']")); // a bare \u: the letter, not a crash
            Assert.Equal("/labels/", NotificationRequestedConverter.Pointer("Labels."));
        }

        [Fact]
        public void A_message_that_isnt_an_object_is_unreadable()
            => Assert.ThrowsAny<Newtonsoft.Json.JsonException>(() => ReadMessage("""[ 1 ]"""));
    }
}
