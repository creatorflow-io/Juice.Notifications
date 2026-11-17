using System.Text.Json;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// Configuration hashes are taken over JSON with its numbers normalized (deferred #9): jsonb rewrites <c>1e2</c> as <c>100</c>, and a hash of
    /// the numbers as written would see a change after every round trip through the database. The normal form is exact: different values never
    /// share it, or a real change would hash as none and be dropped.
    /// </summary>
    public sealed class CanonicalJsonTests
    {
        private static string Normalized(string json) => CanonicalJson.WriteNormalized(JsonElement.Parse(json));

        [Theory]
        [InlineData("1e2", "1E2")]
        [InlineData("100", "1E2")]
        [InlineData("100.0", "1E2")]
        [InlineData("1E+2", "1E2")]
        [InlineData("10e1", "1E2")]
        [InlineData("1.50", "15E-1")]
        [InlineData("1.5E0", "15E-1")]
        [InlineData("0.001", "1E-3")]
        [InlineData("1E-3", "1E-3")]
        [InlineData("-12.340", "-1234E-2")]
        [InlineData("7", "7E0")]
        [InlineData("0", "0")]
        [InlineData("-0", "0")]
        [InlineData("-0.0e5", "0")]
        [InlineData("0.000", "0")]
        public void Spellings_of_one_number_are_one_text(string written, string normalized)
            => Assert.Equal(normalized, Normalized(written));

        [Theory]
        // Found by review: tiny numbers and numbers past 28 decimal places were rounded together.
        [InlineData("1e-50", "2e-50")]
        [InlineData("0.01234567890123456789012345678", "0.01234567890123456789012345679")]
        [InlineData("12345678901234567890123456789", "12345678901234567890123456788")]
        [InlineData("1e400", "1e401")]
        [InlineData("1.5", "1.05")]
        [InlineData("1", "-1")]
        [InlineData("1e99999999999999999999", "1e99999999999999999998")]
        public void Different_numbers_stay_different(string a, string b)
            => Assert.NotEqual(Normalized(a), Normalized(b));

        [Fact]
        public void Objects_are_sorted_and_nested_numbers_normalized()
            => Assert.Equal("""{"a":[1E2,{"b":15E-1}],"z":"1e2"}""", Normalized("""{ "z": "1e2", "a": [1e2, { "b": 1.50 }] }"""));

        [Fact]
        public void Write_keeps_numbers_as_written()
            => Assert.Equal("""{"a":1e2}""", CanonicalJson.Write(JsonElement.Parse("""{ "a": 1e2 }""")));

        [Theory]
        [InlineData("""{ "type": "\ud800" }""", "/m/type")]
        [InlineData("""{ "labels": { "\udc00": "x" } }""", "/m/labels")]
        [InlineData("""{ "type": ["ok", "a\ud800b"] }""", "/m/type/1")]
        [InlineData("""{ "type": "😀" }""", null)]
        [InlineData("""{ "type": "app.alert" }""", null)]
        public void FirstMalformed_finds_half_a_surrogate_pair(string json, string? at)
            => Assert.Equal(at, CanonicalJson.FirstMalformed(JsonElement.Parse(json), "/m"));

        [Theory]
        [InlineData("""{ "a": "x\ud800", "b": "\u0000" }""", "/m/a", Intake.RequestRules.MalformedMessage)]
        [InlineData("""{ "a": "x", "b": "\u0000" }""", "/m/b", Intake.RequestRules.NulMessage)]
        [InlineData("""{ "a": "x" }""", null, null)]
        public void Unstorable_finds_half_a_pair_then_a_nul(string json, string? at, string? message)
        {
            var error = CanonicalJson.Unstorable(JsonElement.Parse(json), "/m");
            Assert.Equal((at, message), (error?.Pointer, error?.Message));
        }
    }
}
