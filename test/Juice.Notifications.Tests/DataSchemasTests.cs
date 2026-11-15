using System.Text.Json;
using Juice.Notifications.Types;

namespace Juice.Notifications.Tests
{
    /// <summary>Declared schemas: one self-contained 2020-12 document whose references all resolve inside it.</summary>
    public sealed class DataSchemasTests
    {
        private static IReadOnlyList<FieldError> Check(string schema) => DataSchemas.Check(JsonElement.Parse(schema), "/schema");

        [Theory]
        [InlineData("""{ "type": "object" }""")]
        [InlineData("""{ "$defs": { "name": { "type": "string" } }, "properties": { "a": { "$ref": "#/$defs/name" } } }""")]
        [InlineData("""{ "properties": { "a": { "$anchor": "thing", "type": "string" }, "b": { "$ref": "#thing" } } }""")]
        [InlineData("""{ "items": { "$ref": "#" } }""")]
        [InlineData("""{ "$defs": { "a/b": { "type": "string" } }, "properties": { "x": { "$ref": "#/$defs/a~1b" } } }""")]
        // Data that only looks like keywords: a property called "$ref", and "$ref" or "$id" inside enum values.
        [InlineData("""{ "properties": { "$ref": { "type": "string" }, "$id": { "type": "string" } } }""")]
        [InlineData("""{ "enum": [ { "$ref": "https://example.com" }, { "$id": "x" } ] }""")]
        [InlineData("""{ "properties": { "a": { "pattern": "^[a-z]+$" } }, "patternProperties": { "^x-": { "type": "string" } } }""")]
        // ECMA-262 patterns the library accepts, and a "pattern" member inside an unknown keyword, which is not a schema.
        [InlineData("""{ "properties": { "a": { "pattern": "[^]" } } }""")]
        [InlineData("""{ "type": "object", "x-ui": { "pattern": "[" } }""")]
        // An anchor under a property whose name is a keyword elsewhere, and a "$ref" inside an annotation, which the library ignores.
        [InlineData("""{ "properties": { "default": { "$anchor": "d", "type": "string" } }, "$ref": "#d" }""")]
        [InlineData("""{ "type": "object", "x-docs": { "$ref": "https://example.com/doc" } }""")]
        [InlineData("""{ "prefixItems": [ { "$anchor": "first" } ], "items": { "$ref": "#/prefixItems/0" }, "contains": { "$ref": "#first" } }""")]
        // Keywords of earlier drafts are annotations under 2020-12: the library doesn't evaluate a dangling reference inside them.
        [InlineData("""{ "dependencies": { "a": { "$ref": "#/nope" } } }""")]
        [InlineData("""{ "additionalItems": { "$ref": "#/nope" } }""")]
        [InlineData("""{ "definitions": { "x": { "$ref": "#/nope" } } }""")]
        public void Self_contained_schemas_are_accepted(string schema)
        {
            Assert.Empty(Check(schema));
            // Accepted means usable: evaluating data resolves every reference without an error the request would get as a 500.
            var compiled = new DataSchemas().For(Type(schema));
            foreach (var data in new[] { "{}", """{ "a": 1, "default": "x" }""", """[ 1, "x" ]""" })
            {
                Assert.DoesNotContain(DataSchemas.Validate(compiled, JsonElement.Parse(data), "/data"), e => e.Message.Contains("can't be evaluated"));
            }
        }

        [Theory]
        [InlineData("""{ "$ref": "https://example.com/schemas/x.json" }""", "/schema/$ref")]
        [InlineData("""{ "properties": { "a": { "$ref": "other.json#/x" } } }""", "/schema/properties/a/$ref")]
        [InlineData("""{ "properties": { "a": { "$ref": "#/$defs/missing" } } }""", "/schema/properties/a/$ref")]
        [InlineData("""{ "properties": { "a": { "$ref": "#nowhere" } } }""", "/schema/properties/a/$ref")]
        [InlineData("""{ "$id": "https://example.com/x" }""", "/schema/$id")]
        [InlineData("""{ "$defs": { "x": { "$id": "x", "type": "string" } } }""", "/schema/$defs/x/$id")]
        [InlineData("""{ "type": 5 }""", "/schema")]
        [InlineData("""{ "$schema": 2020 }""", "/schema/$schema")]
        [InlineData("""{ "$schema": { } }""", "/schema/$schema")]
        [InlineData("""{ "type": "object", "type": "string" }""", "/schema/type")]
        [InlineData("""{ "properties": { "a": { "pattern": "[" } } }""", "/schema")]
        [InlineData("""{ "patternProperties": { "(": { "type": "string" } } }""", "/schema")]
        [InlineData("""{ "$defs": { "x": { "items": { "pattern": "(?<" } } } }""", "/schema")]
        // References that resolve in the JSON but not to a schema: the library would fail every request at evaluation.
        [InlineData("""{ "x-meta": { "$anchor": "a" }, "$ref": "#a" }""", "/schema/$ref")]
        [InlineData("""{ "$defs": { "x": { "const": { "type": "nope" } } }, "$ref": "#/$defs/x/const" }""", "/schema/$ref")]
        [InlineData("""{ "definitions": { "x": { "type": "string" } }, "$ref": "#/definitions/x" }""", "/schema/$ref")]
        // PostgreSQL can't store a NUL character.
        [InlineData("""{ "type": "object", "description": "a\u0000b" }""", "/schema/description")]
        public void Other_schemas_are_refused_at_the_field(string schema, string pointer)
            => Assert.Contains(Check(schema), e => e.Pointer.StartsWith(pointer, StringComparison.Ordinal));

        private static Data.NotificationType Type(string schema) => new()
        {
            Key = "t.x", Name = "x", Category = "c", DisplayGroup = "general", SchemaHash = "h", Workflow = "[]", DeclarationHash = "d", Schema = schema,
        };

        /// <summary>
        /// A pattern that backtracks without end (5 s for each string before) stops at the short timeout, and the first timeout ends the check:
        /// many strings cost one timeout. Wherever the pattern is: under a reference, under a <c>$schema</c> that switches the library to its own
        /// dialect, and as a property-name pattern.
        /// </summary>
        [Theory]
        [InlineData("""{ "properties": { "a": { "pattern": "^(a+)+$" } }, "additionalProperties": { "pattern": "^(a+)+$" } }""")]
        [InlineData("""{ "$schema": "https://json-schema.org/draft/2020-12/schema", "additionalProperties": { "pattern": "^(a|aa)+$" } }""")]
        [InlineData("""{ "$defs": { "s": { "pattern": "^(a*)*$" } }, "additionalProperties": { "$ref": "#/$defs/s" } }""")]
        [InlineData("""{ "additionalProperties": { "$schema": "http://json-schema.org/draft-07/schema#", "pattern": "^(\\w+\\s?)*$" } }""")]
        [InlineData("""{ "patternProperties": { "^(a+)+$": false } }""")]
        public void A_pattern_that_backtracks_stops_at_the_short_timeout(string schema)
        {
            Assert.Empty(Check(schema));
            var compiled = new DataSchemas().For(Type(schema));
            // 40 strings that each backtrack: as values, and as property names (a different length each, so each is a property).
            var data = JsonElement.Parse(JsonSerializer.Serialize(Enumerable.Range(0, 40).ToDictionary(i => new string('a', 40 + i) + "!", i => new string('a', 40 + i) + "!")));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var errors = DataSchemas.Validate(compiled, data, "/data");
            clock.Stop();

            Assert.Contains(errors, e => e.Message.Contains("took too long"));
            // One timeout of 100 ms; before, 5 s for the first string alone. Room for a busy machine.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
        }

        /// <summary>Only the timeout changes: patterns keep the library's ECMAScript meaning, lookarounds and backreferences included.</summary>
        [Theory]
        [InlineData("^\\d+$", "123", true)]
        [InlineData("^\\d+$", "٣", false)]
        [InlineData("^\\w$", "é", false)]
        [InlineData("^(?!test)[a-z]+$", "prod", true)]
        [InlineData("^(?!test)[a-z]+$", "testing", false)]
        [InlineData("^(a)\\1$", "aa", true)]
        [InlineData("^[^]$", "\n", true)]
        [InlineData("^\\p{L}+$", "été", true)]
        [InlineData("^.{0,5000}$", "short", true)]
        public void Patterns_keep_their_meaning(string pattern, string value, bool valid)
        {
            var schema = $$"""{ "properties": { "a": { "pattern": {{JsonSerializer.Serialize(pattern)}} } } }""";
            Assert.Empty(Check(schema));
            var errors = DataSchemas.Validate(new DataSchemas().For(Type(schema)), JsonElement.Parse(JsonSerializer.Serialize(new { a = value })), "/data");
            Assert.Equal(valid, errors.Count == 0);
        }

        [Fact]
        public void A_regex_keeps_the_librarys_pattern_and_options_with_the_short_timeout()
        {
            var library = new System.Text.RegularExpressions.Regex(@"^\d+$", System.Text.RegularExpressions.RegexOptions.ECMAScript
                | System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(5));
            var shortened = PatternTimeouts.Shorten(library);
            Assert.Equal(library.ToString(), shortened.ToString());
            Assert.Equal(System.Text.RegularExpressions.RegexOptions.ECMAScript, shortened.Options);
            Assert.Equal(PatternTimeouts.Timeout, shortened.MatchTimeout);
        }

        [Fact]
        public void Data_errors_point_into_the_data()
        {
            var schema = new DataSchemas().For(Type(
                """{ "type": "object", "required": ["subject"], "properties": { "subject": { "type": "string" }, "n": { "type": "integer" } } }"""));

            Assert.Empty(DataSchemas.Validate(schema, JsonElement.Parse("""{ "subject": "x", "n": 1 }"""), "/data"));
            var errors = DataSchemas.Validate(schema, JsonElement.Parse("""{ "n": "one" }"""), "/data");
            Assert.Contains(errors, e => e.Pointer == "/data/n");
            Assert.Contains(errors, e => e.Pointer == "/data");
        }
    }
}
