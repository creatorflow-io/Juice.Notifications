using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;
using Juice.Notifications.Data;

namespace Juice.Notifications.Types
{
    /// <summary>
    /// The data schemas of type versions: checked when declared, compiled once per version and cached, and used to validate each request's
    /// <c>data</c> (phase-1-plan I7). JSON Schema 2020-12 only.
    /// <list type="bullet">
    /// <item>Each schema is built with its own registry, never the library's global one.</item>
    /// <item>A schema is one self-contained document: <c>$ref</c> and <c>$dynamicRef</c> point inside it (<c>#/$defs/…</c> or <c>#anchor</c>), and
    /// <c>$id</c> isn't allowed. Every reference is checked when the type is declared, because the library only resolves one when it evaluates it:
    /// a dangling reference would otherwise fail a request later (500), or reach for another document.</item>
    /// </list>
    /// </summary>
    public sealed class DataSchemas
    {
        public const string Dialect202012 = "https://json-schema.org/draft/2020-12/schema";
        public const int MaxSchemaBytes = 64 * 1024;
        public const int MaxErrors = 50;

        private readonly ConcurrentDictionary<Guid, JsonSchema> _compiled = new();

        /// <summary>Checks a declared schema; the errors are empty when it can be used.</summary>
        public static IReadOnlyList<FieldError> Check(JsonElement schema, string pointer)
        {
            if (schema.ValueKind != JsonValueKind.Object)
            {
                return [new FieldError(pointer, "The schema is a JSON Schema 2020-12 object.")];
            }
            if (System.Text.Encoding.UTF8.GetByteCount(schema.GetRawText()) > MaxSchemaBytes)
            {
                return [new FieldError(pointer, $"The schema is larger than {MaxSchemaBytes / 1024} KB.")];
            }
            if (CanonicalJson.FirstDuplicate(schema, pointer) is { } duplicate)
            {
                return [new FieldError(duplicate, "This object repeats a property name; each name appears once.")];
            }
            if (schema.TryGetProperty("$schema", out var dialect) && (dialect.ValueKind != JsonValueKind.String || dialect.GetString() != Dialect202012))
            {
                return [new FieldError(pointer + "/$schema", $"Only JSON Schema 2020-12 is supported: omit $schema or use {Dialect202012}.")];
            }
            var meta = MetaSchemas.Draft202012.Evaluate(schema, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!meta.IsValid)
            {
                return Errors(meta, pointer);
            }
            if (CanonicalJson.FirstNul(schema, pointer) is { } nul)
            {
                return [new FieldError(nul, Intake.RequestRules.NulMessage)];
            }
            var references = CheckReferences(schema, pointer);
            if (references.Count > 0)
            {
                return references;
            }
            try
            {
                Build(schema);
                return [];
            }
            catch (Exception ex) when (ex is JsonSchemaException or RefResolutionException or JsonException or ArgumentException)
            {
                return [new FieldError(pointer, "The schema can't be used: " + ex.Message)];
            }
        }

        // The 2020-12 keywords whose values are subschemas. Only these are schemas to the library: anything else (an annotation, an unknown
        // keyword, enum or const values) is data, so a "$ref" or "$anchor" in it means nothing and a reference into it isn't a schema.
        private static readonly HashSet<string> OneSubschema = ["additionalProperties", "propertyNames", "items", "contains", "not", "if", "then",
            "else", "unevaluatedItems", "unevaluatedProperties", "contentSchema"];
        private static readonly HashSet<string> SubschemaArrays = ["allOf", "anyOf", "oneOf", "prefixItems"];
        private static readonly HashSet<string> SubschemaMaps = ["properties", "patternProperties", "$defs", "dependentSchemas"];

        /// <summary>
        /// Every <c>$ref</c>/<c>$dynamicRef</c> in a subschema must land on a subschema of this document (<c>#</c>, <c>#/$defs/…</c>, or a
        /// <c>#anchor</c> declared in a subschema); no <c>$id</c>. A reference that lands elsewhere would pass a build and fail every request.
        /// </summary>
        private static List<FieldError> CheckReferences(JsonElement root, string pointer)
        {
            var locations = new HashSet<string>(StringComparer.Ordinal);
            var anchors = new HashSet<string>(StringComparer.Ordinal);
            var references = new List<(string At, string Reference)>();
            var errors = new List<FieldError>();
            Walk(root, "", pointer, locations, anchors, references, errors);
            foreach (var (at, reference) in references)
            {
                if (!Resolves(reference, locations, anchors))
                {
                    errors.Add(new FieldError(at, $"The reference '{reference}' doesn't point to a subschema of this schema (#, #/$defs/… or a #anchor)."));
                }
            }
            return errors;
        }

        /// <summary>Records the subschema at <paramref name="location"/> (a pointer from the root), its anchors and references, and walks its subschemas.</summary>
        private static void Walk(JsonElement schema, string location, string pointer, HashSet<string> locations, HashSet<string> anchors,
            List<(string, string)> references, List<FieldError> errors)
        {
            locations.Add(location);
            if (schema.ValueKind != JsonValueKind.Object)
            {
                return; // true or false
            }
            foreach (var property in schema.EnumerateObject())
            {
                var segment = "/" + TypeRules.Pointer(property.Name);
                var here = pointer + location + segment;
                var value = property.Value;
                switch (property.Name)
                {
                    case "$id":
                        errors.Add(new FieldError(here, "A schema is one document without $id; use $defs and #/$defs/… references."));
                        break;
                    case "$ref" or "$dynamicRef" when value.ValueKind == JsonValueKind.String:
                        references.Add((here, value.GetString()!));
                        break;
                    case "$anchor" or "$dynamicAnchor" when value.ValueKind == JsonValueKind.String:
                        anchors.Add(value.GetString()!);
                        break;
                    case var name when OneSubschema.Contains(name):
                        Walk(value, location + segment, pointer, locations, anchors, references, errors);
                        break;
                    case var name when SubschemaArrays.Contains(name) && value.ValueKind == JsonValueKind.Array:
                        var i = 0;
                        foreach (var item in value.EnumerateArray())
                        {
                            Walk(item, $"{location}{segment}/{i++}", pointer, locations, anchors, references, errors);
                        }
                        break;
                    case var name when SubschemaMaps.Contains(name) && value.ValueKind == JsonValueKind.Object:
                        foreach (var sub in value.EnumerateObject())
                        {
                            Walk(sub.Value, location + segment + "/" + TypeRules.Pointer(sub.Name), pointer, locations, anchors, references, errors);
                        }
                        break;
                }
            }
        }

        private static bool Resolves(string reference, HashSet<string> locations, HashSet<string> anchors)
        {
            if (!reference.StartsWith('#'))
            {
                return false;
            }
            var fragment = Uri.UnescapeDataString(reference[1..]);
            return fragment.Length == 0 || (fragment.StartsWith('/') ? locations.Contains(fragment) : anchors.Contains(fragment));
        }

        /// <summary>The compiled schema of a type version, built on first use.</summary>
        public JsonSchema For(NotificationType type)
            // The compiled schema keeps reading the elements it was built from, so they must outlive it: a clone owns its memory.
            => _compiled.GetOrAdd(type.Id, _ => Build(JsonElement.Parse(type.Schema)));

        /// <summary>
        /// Validates a request's data against its type version. Compiling the schema is inside the same guard as evaluating it: a stored schema
        /// the library can't build or resolve refuses the request (422), not a 500.
        /// </summary>
        public IReadOnlyList<FieldError> Validate(NotificationType type, JsonElement data, string pointer)
        {
            JsonSchema schema;
            try
            {
                schema = For(type);
            }
            catch (Exception ex) when (ex is RefResolutionException or JsonSchemaException or JsonException or ArgumentException)
            {
                return [new FieldError(pointer, "The type's schema can't be evaluated: " + ex.Message)];
            }
            return Validate(schema, data, pointer);
        }

        /// <summary>Validates a request's data; the errors point into what was sent, under <paramref name="pointer"/>.</summary>
        public static IReadOnlyList<FieldError> Validate(JsonSchema schema, JsonElement data, string pointer)
        {
            try
            {
                var results = schema.Evaluate(data, new EvaluationOptions { OutputFormat = OutputFormat.List });
                return results.IsValid ? [] : Errors(results, pointer);
            }
            catch (RegexMatchTimeoutException)
            {
                // JsonSchema.Net matches each pattern with its own 5 s timeout (no hook to change it): a pattern that backtracks without end
                // stops there, and the data is refused (docs/deferred.md #6).
                return [new FieldError(pointer, "A pattern in the schema took too long to check against this data.")];
            }
            catch (Exception ex) when (ex is RefResolutionException or JsonSchemaException)
            {
                // The declaration check should have refused such a schema; if one gets through, the request is refused, not a 500.
                return [new FieldError(pointer, "The type's schema can't be evaluated: " + ex.Message)];
            }
        }

        private static JsonSchema Build(JsonElement schema)
            => JsonSchema.Build(schema, new BuildOptions { SchemaRegistry = new SchemaRegistry(), Dialect = Dialect.Draft202012 });

        private static IReadOnlyList<FieldError> Errors(EvaluationResults results, string pointer)
        {
            var errors = new List<FieldError>();
            foreach (var node in (results.Details ?? []).Prepend(results))
            {
                if (node.Errors is null)
                {
                    continue;
                }
                foreach (var (_, message) in node.Errors)
                {
                    errors.Add(new FieldError(pointer + node.InstanceLocation, message));
                    if (errors.Count == MaxErrors)
                    {
                        return errors;
                    }
                }
            }
            return errors.Count > 0 ? errors : [new FieldError(pointer, "The value doesn't match the schema.")];
        }
    }
}
