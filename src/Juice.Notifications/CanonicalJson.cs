using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Juice.Notifications
{
    /// <summary>
    /// JSON in one canonical form: object properties sorted by name (ordinal), no whitespace, strings and numbers as written. Two documents that mean
    /// the same give the same text, so their hash identifies the content (type declarations, schemas).
    /// </summary>
    public static class CanonicalJson
    {
        public static string Write(JsonElement element)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteElement(writer, element);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// The JSON pointer of the first object that repeats a property name, or null. Such JSON means different things to different readers (jsonb
        /// keeps the last; JsonNode refuses it), so it is refused wherever its meaning matters.
        /// </summary>
        public static string? FirstDuplicate(JsonElement element, string at)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        var here = at + "/" + property.Name.Replace("~", "~0").Replace("/", "~1");
                        if (!names.Add(property.Name))
                        {
                            return here;
                        }
                        if (FirstDuplicate(property.Value, here) is { } inner)
                        {
                            return inner;
                        }
                    }
                    return null;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (FirstDuplicate(item, $"{at}/{i++}") is { } inner)
                        {
                            return inner;
                        }
                    }
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>SHA-256 of the canonical form, lowercase hex.</summary>
        public static string Hash(JsonElement element) => Sha256(Write(element));

        public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteElement(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteElement(writer, item);
                    }
                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
