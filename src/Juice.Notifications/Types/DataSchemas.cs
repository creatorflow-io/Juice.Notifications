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
            var references = new List<FieldError>();
            CheckReferences(schema, schema, pointer, references);
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

        /// <summary>Every <c>$ref</c>/<c>$dynamicRef</c> under <paramref name="node"/> must resolve inside <paramref name="root"/>; no <c>$id</c>.</summary>
        private static void CheckReferences(JsonElement root, JsonElement node, string at, List<FieldError> errors)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in node.EnumerateArray())
                {
                    CheckReferences(root, item, $"{at}/{i++}", errors);
                }
                return;
            }
            if (node.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            foreach (var property in node.EnumerateObject())
            {
                var here = at + "/" + TypeRules.Pointer(property.Name);
                switch (property.Name)
                {
                    case "$id":
                        errors.Add(new FieldError(here, "A schema is one document without $id; use $defs and #/$defs/… references."));
                        break;
                    case "$ref" or "$dynamicRef" when property.Value.ValueKind == JsonValueKind.String:
                        if (!Resolves(root, property.Value.GetString()!))
                        {
                            errors.Add(new FieldError(here, $"The reference '{property.Value.GetString()}' doesn't point inside this schema (#/… or #anchor)."));
                        }
                        break;
                    // Property names and enum or const values are data, not subschemas: a property called "$ref" is fine.
                    case "properties" or "patternProperties" or "$defs" or "definitions" or "dependentSchemas" when property.Value.ValueKind == JsonValueKind.Object:
                        foreach (var sub in property.Value.EnumerateObject())
                        {
                            CheckReferences(root, sub.Value, here + "/" + TypeRules.Pointer(sub.Name), errors);
                        }
                        break;
                    case "enum" or "const" or "examples" or "default":
                        break;
                    default:
                        CheckReferences(root, property.Value, here, errors);
                        break;
                }
            }
        }

        /// <summary>A same-document reference: <c>#</c>, a JSON pointer (<c>#/$defs/x</c>), or an anchor (<c>#name</c>) declared in the document.</summary>
        private static bool Resolves(JsonElement root, string reference)
        {
            if (!reference.StartsWith('#'))
            {
                return false;
            }
            var fragment = Uri.UnescapeDataString(reference[1..]);
            if (fragment.Length == 0)
            {
                return true;
            }
            if (!fragment.StartsWith('/'))
            {
                return HasAnchor(root, fragment);
            }
            var current = root;
            foreach (var token in fragment[1..].Split('/').Select(t => t.Replace("~1", "/").Replace("~0", "~")))
            {
                if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var next))
                {
                    current = next;
                }
                else if (current.ValueKind == JsonValueKind.Array && int.TryParse(token, out var index) && index >= 0 && index < current.GetArrayLength())
                {
                    current = current[index];
                }
                else
                {
                    return false;
                }
            }
            return current.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False;
        }

        private static bool HasAnchor(JsonElement node, string anchor) => node.ValueKind switch
        {
            JsonValueKind.Object => node.EnumerateObject().Any(p =>
                (p.Name is "$anchor" or "$dynamicAnchor" && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == anchor)
                || (p.Name is not ("enum" or "const" or "examples" or "default") && HasAnchor(p.Value, anchor))),
            JsonValueKind.Array => node.EnumerateArray().Any(item => HasAnchor(item, anchor)),
            _ => false,
        };

        /// <summary>The compiled schema of a type version, built on first use.</summary>
        public JsonSchema For(NotificationType type)
            // The compiled schema keeps reading the elements it was built from, so they must outlive it: a clone owns its memory.
            => _compiled.GetOrAdd(type.Id, _ => Build(JsonElement.Parse(type.Schema)));

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
