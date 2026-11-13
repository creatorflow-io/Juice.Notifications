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
    /// </remarks>
    public sealed record NotificationRequested : IntegrationEvent
    {
        /// <summary>The tenant the notification is for. (Juice's own <c>TenantId</c> is the messaging tenant, a string.)</summary>
        public Guid NotificationTenantId { get; init; }

        public string? Type { get; init; }
        public int? TypeVersion { get; init; }
        public string? IdempotencyKey { get; init; }
        public string? CorrelationKey { get; init; }
        public List<RequestedRecipient?>? Recipients { get; init; }
        public RequestedActor? Actor { get; init; }
        public string? Priority { get; init; }
        public Dictionary<string, string>? Labels { get; init; }

        /// <summary>
        /// The type's data: strings, dates among them, arrive as the text they were and integers exactly; a number with a fraction or an exponent
        /// arrives as the nearest double (<c>1.10</c> as <c>1.1</c>, <c>1e2</c> as <c>100.0</c>), as most JSON parsers read it, and one beyond a
        /// double (<c>1e400</c>) makes the message unreadable, so it is parked. Over HTTP the data keeps its exact text; a producer that needs more
        /// than double precision sends it as a string.
        /// </summary>
        [JsonConverter(typeof(RawJsonConverter))]
        public JsonElement? Data { get; init; }

        public Dictionary<string, string>? Links { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
    }

    /// <summary><c>{ "kind": "route" }</c>, or <c>{ "kind": "address", "channel": "email", "address": "…" }</c>.</summary>
    public sealed record RequestedRecipient(string? Kind, string? Channel = null, string? Address = null);

    /// <summary>Who caused it: <c>user</c>, <c>system</c> or <c>service</c>, and an id.</summary>
    public sealed record RequestedActor(string? Kind, string? Id);

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
