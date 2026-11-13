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
        /// The type's data, kept as sent: dates stay the text they were, and numbers keep their digits, except that an exponent is written out
        /// (<c>1e2</c> arrives as <c>100</c>) and a negative zero loses its sign.
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
    /// as a date and be written in another format, and a number as a double; here the value's tokens are copied with dates left as strings and
    /// numbers as decimals, and the result is parsed as a <see cref="JsonElement"/>. A repeated property name is kept, so intake reports it.
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
            reader.FloatParseHandling = FloatParseHandling.Decimal;
            try
            {
                using var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
                using (var writer = new JsonTextWriter(text) { DateFormatHandling = DateFormatHandling.IsoDateFormat })
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
