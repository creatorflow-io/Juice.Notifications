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
