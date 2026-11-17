using System.Globalization;
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
        public static string Write(JsonElement element) => Write(element, normalizeNumbers: false);

        /// <summary>
        /// As <see cref="Write(JsonElement)"/>, with every number in one form as well: the same value however it is spelled (<c>1e2</c>, <c>100</c>,
        /// <c>100.0</c> are all <c>100</c>). For hashing what was stored: jsonb rewrites numbers (<c>1e2</c> comes back as <c>100</c>), so a hash
        /// of numbers as written would see a change that isn't one (deferred #9). Not for type declarations, whose hashes are already stored.
        /// </summary>
        public static string WriteNormalized(JsonElement element) => Write(element, normalizeNumbers: true);

        private static string Write(JsonElement element, bool normalizeNumbers)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteElement(writer, element, normalizeNumbers);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>
        /// A JSON number in one exact form, its significant digits and a power of ten: <c>1e2</c>, <c>100</c> and <c>100.0</c> are all
        /// <c>1E2</c>; <c>0.001</c> is <c>1E-3</c>; every zero is <c>0</c>. Worked on the text, so nothing is rounded: two numbers get the same
        /// form only when they are the same value, however small, large or long (a decimal or a double would round some together).
        /// </summary>
        internal static string NormalizeNumber(string raw)
        {
            var negative = raw.StartsWith('-');
            var body = negative ? raw[1..] : raw;
            var e = body.IndexOfAny(['e', 'E']);
            var exponent = e < 0 ? System.Numerics.BigInteger.Zero : System.Numerics.BigInteger.Parse(body[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var mantissa = e < 0 ? body : body[..e];
            var point = mantissa.IndexOf('.');
            var digits = point < 0 ? mantissa : mantissa[..point] + mantissa[(point + 1)..];
            if (point >= 0)
            {
                exponent -= mantissa.Length - point - 1;
            }
            digits = digits.TrimStart('0');
            if (digits.Length == 0)
            {
                return "0";
            }
            var significant = digits.TrimEnd('0');
            exponent += digits.Length - significant.Length;
            return (negative ? "-" : "") + significant + "E" + exponent.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The JSON pointer of the first string or property name holding a NUL character (<c>\u0000</c>), or null. PostgreSQL stores neither
        /// in text nor in jsonb, so it is refused as a field error instead of failing the insert.
        /// </summary>
        public static string? FirstNul(JsonElement element, string at)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString()!.Contains('\0') ? at : null;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        var here = at + "/" + property.Name.Replace("~", "~0").Replace("/", "~1");
                        if (property.Name.Contains('\0'))
                        {
                            return here;
                        }
                        if (FirstNul(property.Value, here) is { } inner)
                        {
                            return inner;
                        }
                    }
                    return null;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (FirstNul(item, $"{at}/{i++}") is { } inner)
                        {
                            return inner;
                        }
                    }
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// The first string or property name PostgreSQL can't store (half a surrogate pair on its own, then a NUL), as a field error, or null.
        /// Run before anything else reads the strings: reading half a pair as a string throws.
        /// </summary>
        public static FieldError? Unstorable(JsonElement element, string at)
            => FirstMalformed(element, at) is { } malformed ? new FieldError(malformed, Intake.RequestRules.MalformedMessage)
                : FirstNul(element, at) is { } nul ? new FieldError(nul, Intake.RequestRules.NulMessage)
                : null;

        /// <summary>
        /// The JSON pointer of the first string or property name holding half a surrogate pair on its own (<c>\ud800</c>), or null. Such text
        /// has no UTF-8 form, so jsonb refuses it: refused as a field error instead of failing the insert.
        /// </summary>
        public static string? FirstMalformed(JsonElement element, string at)
        {
            static bool Bad(Func<string> read)
            {
                try
                {
                    return !Intake.RequestRules.IsWellFormed(read());
                }
                catch (InvalidOperationException)
                {
                    return true; // the reader refuses to make a string of it
                }
            }
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return Bad(() => element.GetString()!) ? at : null;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (Bad(() => property.Name))
                        {
                            return at;
                        }
                        var here = at + "/" + property.Name.Replace("~", "~0").Replace("/", "~1");
                        if (FirstMalformed(property.Value, here) is { } inner)
                        {
                            return inner;
                        }
                    }
                    return null;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        if (FirstMalformed(item, $"{at}/{i++}") is { } inner)
                        {
                            return inner;
                        }
                    }
                    return null;
                default:
                    return null;
            }
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

        private static void WriteElement(Utf8JsonWriter writer, JsonElement element, bool normalizeNumbers)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteElement(writer, property.Value, normalizeNumbers);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteElement(writer, item, normalizeNumbers);
                    }
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.Number when normalizeNumbers:
                    writer.WriteRawValue(NormalizeNumber(element.GetRawText()), skipInputValidation: true);
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
