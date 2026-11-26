using System.Text.Json;
using Juice.Messaging;
using Newtonsoft.Json;

namespace Juice.Notifications.Messaging
{
    /// <summary>
    /// A notification request published to RabbitMQ (design §5.2): the same fields as <c>POST /api/v1/tenants/{tenant}/notifications</c>, plus the
    /// tenant. The application is the message's publisher as the broker vouches for it (<c>user_id</c>, which a producer stamps with Juice's
    /// <c>StampUserId</c>), never a field: an application's <c>brokerUser</c> names the broker user it publishes as.
    /// </summary>
    /// <remarks>
    /// Published to the intake exchange (<c>notifications</c> by default) with the routing key <c>NotificationRequested</c>. A producer may declare
    /// its own record of this name and shape instead of referencing this package. There is no answer: a request this service refuses is logged
    /// and counted (<c>notifications.requests</c>, outcome <c>refused</c>); producers that need the answer send over HTTP.
    /// <para>Text fields are text, as over HTTP: a number, boolean, object or array where a string goes (<c>"labels": { "n": 5 }</c>) is refused
    /// as <c>invalid_request</c> with a pointer to it, rather than read as <c>"5"</c> (deferred #20).</para>
    /// </remarks>
    [JsonConverter(typeof(NotificationRequestedConverter))]
    public sealed record NotificationRequested : IntegrationEvent
    {
        /// <summary>The tenant the notification is for. (Juice's own <c>TenantId</c> is the messaging tenant, a string.)</summary>
        public Guid NotificationTenantId { get; init; }

        [JsonConverter(typeof(StrictStringConverter))]
        public string? Type { get; init; }
        public int? TypeVersion { get; init; }
        [JsonConverter(typeof(StrictStringConverter))]
        public string? IdempotencyKey { get; init; }
        [JsonConverter(typeof(StrictStringConverter))]
        public string? CorrelationKey { get; init; }
        public List<RequestedRecipient?>? Recipients { get; init; }
        public RequestedActor? Actor { get; init; }
        [JsonConverter(typeof(StrictStringConverter))]
        public string? Priority { get; init; }
        [JsonProperty(ItemConverterType = typeof(StrictStringConverter))]
        public Dictionary<string, string>? Labels { get; init; }

        /// <summary>
        /// The type's data: strings, dates among them, arrive as the text they were and integers exactly; a number with a fraction or an exponent
        /// arrives as the nearest double (<c>1.10</c> as <c>1.1</c>, <c>1e2</c> as <c>100.0</c>), as most JSON parsers read it, and one beyond a
        /// double (<c>1e400</c>) makes the message unreadable, so it is parked. Over HTTP the data keeps its exact text; a producer that needs more
        /// than double precision sends it as a string.
        /// </summary>
        [JsonConverter(typeof(RawJsonConverter))]
        public JsonElement? Data { get; init; }

        [JsonProperty(ItemConverterType = typeof(StrictStringConverter))]
        public Dictionary<string, string>? Links { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        /// <summary>When what it tells of happened: an escalation's first step is due its wait after it (absent: from when the escalation starts).</summary>
        public DateTimeOffset? OccurredAt { get; init; }
    }

    /// <summary>
    /// <c>{ "kind": "route" }</c>, <c>{ "kind": "address", "channel": "email", "address": "…" }</c>, a person kind by id (<c>{ "kind": "group",
    /// "id": "sre" }</c>) or a topic by key, with an optional <c>resolver</c>.
    /// </summary>
    public sealed record RequestedRecipient(
        [property: JsonConverter(typeof(StrictStringConverter))] string? Kind,
        [property: JsonConverter(typeof(StrictStringConverter))] string? Channel = null,
        [property: JsonConverter(typeof(StrictStringConverter))] string? Address = null,
        [property: JsonConverter(typeof(StrictStringConverter))] string? Id = null,
        [property: JsonConverter(typeof(StrictStringConverter))] string? Key = null,
        [property: JsonConverter(typeof(StrictStringConverter))] string? Resolver = null);

    /// <summary>Who caused it: <c>user</c>, <c>system</c> or <c>service</c>, and an id.</summary>
    public sealed record RequestedActor(
        [property: JsonConverter(typeof(StrictStringConverter))] string? Kind,
        [property: JsonConverter(typeof(StrictStringConverter))] string? Id);

    /// <summary>
    /// Reads the event with dates left as text, then fills it as the serializer would, and keeps the text fields that held something else
    /// (<see cref="TextErrors"/>). Juice reads messages with <see cref="DateParseHandling.DateTimeOffset"/>: a field with a converter
    /// (<see cref="StrictStringConverter"/>) would get a date-like string already parsed, and its text lost. The reader is not buffered, so
    /// <see cref="RawJsonConverter"/> still sees a repeated property name in the data.
    /// </summary>
    /// <remarks>
    /// The errors go from the field converters to this one through a thread-static list, set only for the duration of one event's read (the
    /// read is synchronous): every field read in that time is one of this event's. Outside it, <see cref="StrictStringConverter"/> throws. They
    /// are kept beside the event, not in it, so the record's equality and <c>with</c> copies don't carry them.
    /// </remarks>
    public sealed class NotificationRequestedConverter : JsonConverter<NotificationRequested>
    {
        [ThreadStatic]
        private static List<Juice.Notifications.FieldError>? collecting;

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NotificationRequested, IReadOnlyList<Juice.Notifications.FieldError>> Errors = new();

        /// <summary>The text fields of a message that held something else when it was read; intake refuses them. Empty for one built in code.</summary>
        internal static IReadOnlyList<Juice.Notifications.FieldError> TextErrors(NotificationRequested message)
            => Errors.TryGetValue(message, out var errors) ? errors : [];

        public override bool CanWrite => false;

        public override NotificationRequested? ReadJson(JsonReader reader, Type objectType, NotificationRequested? existingValue, bool hasExistingValue,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }
            if (reader.TokenType != JsonToken.StartObject)
            {
                throw new JsonSerializationException($"A NotificationRequested is a JSON object; got {reader.TokenType}.");
            }
            // Populate fills the object through its contract (all the caller's settings) and doesn't come back to this converter. Dates are
            // switched off on the reader only (from the next token on: the object's start is read already), and restored after: the serializer
            // a converter is handed populates without applying its own settings to the reader, and may be shared, so it isn't changed. A
            // date-typed field (CreatedAt, ExpiresAt) is still read as a date from its text.
            var value = new NotificationRequested();
            var dates = reader.DateParseHandling;
            var outer = collecting;
            var errors = collecting = [];
            reader.DateParseHandling = DateParseHandling.None;
            try
            {
                serializer.Populate(reader, value);
            }
            finally
            {
                reader.DateParseHandling = dates;
                collecting = outer;
            }
            if (errors.Count > 0)
            {
                Errors.AddOrUpdate(value, errors);
            }
            return value;
        }

        public override void WriteJson(JsonWriter writer, NotificationRequested? value, Newtonsoft.Json.JsonSerializer serializer)
            => throw new NotSupportedException();

        /// <summary>Records a text field that held something else, when a message is being read; false outside one.</summary>
        internal static bool Collect(string path, string message)
        {
            if (collecting is null)
            {
                return false;
            }
            collecting.Add(new Juice.Notifications.FieldError(Pointer(path), message));
            return true;
        }

        private static readonly System.Text.RegularExpressions.Regex PathSegment =
            // A dotted name may be empty: Newtonsoft writes an empty label name as "Labels." (it needs no brackets).
            new(@"\['((?:\\.|[^'\\])*)'\]|\[(\d+)\]|(?:^|\.)([^.\[]*)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>The fields a path can name, as the HTTP API spells them; Newtonsoft binds them in any case (<c>LABELS</c>).</summary>
        private static readonly Dictionary<string, string> FieldNames = new[]
        {
            "type", "typeVersion", "idempotencyKey", "correlationKey", "recipients", "actor", "priority", "labels", "data", "links", "expiresAt", "occurredAt",
            "kind", "channel", "address", "id", "key", "resolver",
        }.ToDictionary(n => n, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Newtonsoft's path (<c>Labels.n</c>, <c>Labels['a.b']</c>, <c>Recipients[0].Address</c>) as the HTTP API's pointer
        /// (<c>/labels/n</c>, <c>/labels/a.b</c>, <c>/recipients/0/address</c>): field names as the API spells them, label and link names as sent.
        /// </summary>
        internal static string Pointer(string path)
        {
            var pointer = new System.Text.StringBuilder();
            string? previous = null;
            foreach (System.Text.RegularExpressions.Match match in PathSegment.Matches(path))
            {
                string segment;
                if (match.Groups[2].Success)
                {
                    segment = match.Groups[2].Value;
                }
                else
                {
                    var name = match.Groups[1].Success ? Unescape(match.Groups[1].Value) : match.Groups[3].Value;
                    segment = previous is "labels" or "links" ? name
                        : FieldNames.TryGetValue(name, out var field) ? field
                        : name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
                }
                pointer.Append('/').Append(segment.Replace("~", "~0").Replace("/", "~1"));
                previous = segment;
            }
            return pointer.ToString();
        }

        private static readonly System.Text.RegularExpressions.Regex Escape =
            new(@"\\(u[0-9a-fA-F]{4}|.)", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Singleline);

        /// <summary>A bracketed name as Newtonsoft escaped it (a JavaScript string in single quotes: <c>\'</c>, <c>\t</c>, and four-digit Unicode escapes), unescaped.</summary>
        private static string Unescape(string escaped) => Escape.Replace(escaped, m => m.Groups[1].Value switch
        {
            ['u', _, _, _, _] code => ((char)Convert.ToInt32(code[1..], 16)).ToString(), // a bare "u" is the letter
            "b" => "\b",
            "f" => "\f",
            "n" => "\n",
            "r" => "\r",
            "t" => "\t",
            "0" => "\0",
            var other => other, // \' \" \\ and anything else: the character itself
        });
    }

    /// <summary>
    /// A string or null, as System.Text.Json reads one: Newtonsoft would take a number or a boolean as its text. Anything else is recorded for
    /// intake to refuse (<see cref="NotificationRequestedConverter"/>), or fails the read outside one.
    /// </summary>
    public sealed class StrictStringConverter : JsonConverter<string>
    {
        public override bool CanWrite => false;

        public override string? ReadJson(JsonReader reader, Type objectType, string? existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.Null:
                    return null;
                case JsonToken.String:
                    return (string)reader.Value!;
                // Only from a reader that parsed dates before the event's converter could switch that off (a body buffered into a JToken
                // first). The text as written is gone; this is the date in ISO 8601, close to what was sent.
                case JsonToken.Date:
                    return reader.Value switch
                    {
                        DateTimeOffset offset => offset.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", System.Globalization.CultureInfo.InvariantCulture),
                        DateTime date => date.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                        var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture),
                    };
            }
            var path = reader.Path;
            var kind = reader.TokenType switch
            {
                JsonToken.Integer or JsonToken.Float => "a number",
                JsonToken.Boolean => "a boolean",
                JsonToken.StartObject => "an object",
                JsonToken.StartArray => "an array",
                var token => token.ToString(),
            };
            reader.Skip(); // past an object or array's end, so the read goes on from the next field
            if (!NotificationRequestedConverter.Collect(path, $"This is text; it is {kind}."))
            {
                throw new JsonSerializationException($"{path} is a string; got {kind}.");
            }
            return null;
        }

        public override void WriteJson(JsonWriter writer, string? value, Newtonsoft.Json.JsonSerializer serializer) => throw new NotSupportedException();
    }

    /// <summary>
    /// Carries a JSON value through Juice's (Newtonsoft) message serializer unchanged. Read with Juice's settings, a date-like string would come back
    /// as a date and be written in another format, and a number as a double; here the value's tokens are copied with dates left as strings, and the result is parsed as a <see cref="JsonElement"/>. A repeated property name is kept, so intake reports it.
    /// </summary>
    public sealed class RawJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(JsonElement) || objectType == typeof(JsonElement?);

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, Newtonsoft.Json.JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }
            var dates = reader.DateParseHandling;
            var floats = reader.FloatParseHandling;
            // Takes effect from the next token on: the value's own first token is read already, and is a date only if the value is a lone string.
            reader.DateParseHandling = DateParseHandling.None;
            // Double, not decimal: a decimal turns 1e-30 into 0, and a value past 7.9e28 can't be read at all, which leaves the message unreadable.
            reader.FloatParseHandling = FloatParseHandling.Double;
            try
            {
                using var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
                // A number beyond a double (1e400) reads as infinity. Newtonsoft writes that as the string "Infinity" by default, which would be
                // accepted as data the producer never sent. As a bare symbol it isn't JSON, so the parse below fails and the message is unreadable.
                using (var writer = new JsonTextWriter(text) { DateFormatHandling = DateFormatHandling.IsoDateFormat, FloatFormatHandling = FloatFormatHandling.Symbol })
                {
                    writer.WriteToken(reader, writeChildren: true);
                }
                using var document = JsonDocument.Parse(text.ToString());
                return document.RootElement.Clone();
            }
            finally
            {
                reader.DateParseHandling = dates;
                reader.FloatParseHandling = floats;
            }
        }

        public override void WriteJson(JsonWriter writer, object? value, Newtonsoft.Json.JsonSerializer serializer)
        {
            if (value is JsonElement { ValueKind: not JsonValueKind.Undefined } element)
            {
                writer.WriteRawValue(element.GetRawText());
            }
            else
            {
                writer.WriteNull();
            }
        }
    }
}
