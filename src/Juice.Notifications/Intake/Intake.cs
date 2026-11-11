using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Data;
using Juice.Notifications.Types;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Intake
{
    /// <summary>A recipient as sent: <c>{ "kind": "route" }</c>, or <c>{ "kind": "address", "channel": "email", "address": "…" }</c>.</summary>
    public sealed record RecipientSpec(string? Kind, string? Channel = null, string? Address = null);

    /// <summary>Who caused it: <c>user</c>, <c>system</c> or <c>service</c>, and an id.</summary>
    public sealed record ActorSpec(string? Kind, string? Id);

    /// <summary>A notification request as sent (design §5.2). Fields are loose, so each problem is reported against its field.</summary>
    public sealed record NotificationSubmission(
        string? Type, int? TypeVersion, string? IdempotencyKey, string? CorrelationKey, IReadOnlyList<RecipientSpec>? Recipients, ActorSpec? Actor,
        string? Priority, IReadOnlyDictionary<string, string>? Labels, JsonElement? Data, IReadOnlyDictionary<string, string>? Links,
        DateTimeOffset? ExpiresAt);

    /// <summary>
    /// Accepts a notification for a tenant from an application: the request and its notification (<c>Planning</c>, due now) are stored in one
    /// transaction (phase-1-plan I3). The same idempotency key again returns the original request.
    /// </summary>
    public sealed record AcceptNotification(Guid NotificationTenantId, Guid ApplicationId, string ApplicationKey, NotificationSubmission Submission,
        string? TraceParent) : MessageBase, IRequest<Outcome<AcceptedView>>;

    /// <summary>An accepted request; <see cref="Duplicate"/> when the idempotency key was seen before and this is the original.</summary>
    public sealed record AcceptedView(Guid Id, string Type, int TypeVersion, DateTimeOffset AcceptedAt, bool Duplicate);

    /// <summary>What a notification request may contain (design §5.2, phase-1-plan M2).</summary>
    public static partial class RequestRules
    {
        public const int MaxDataBytes = 256 * 1024;
        public const int MaxRecipients = 100;
        public const int MaxLabels = 50;
        public const int MaxLinks = 20;

        /// <summary>Recipient kinds of later phases, refused for now with a clear message rather than as unknown.</summary>
        private static readonly string[] LaterKinds = ["user", "group", "role", "topic", "subscriber"];

        [GeneratedRegex(@"^[\x21-\x7E]{1,200}\z")]
        private static partial Regex Key();

        [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.:-]{0,62}\z")]
        private static partial Regex LabelName();

        [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]{0,62}\z")]
        private static partial Regex LinkName();

        public static bool IsKey(string? value) => value is not null && Key().IsMatch(value);

        /// <summary>Normalized recipients, or null with the reason; the default is <c>[{ "kind": "route" }]</c>.</summary>
        internal static IReadOnlyList<RecipientSpec>? Recipients(IReadOnlyList<RecipientSpec>? sent, NotificationType type, IChannelRegistry channels,
            List<FieldError> invalid, List<FieldError> refused)
        {
            if (sent is null || sent.Count == 0)
            {
                return [new RecipientSpec(RecipientKinds.Route)];
            }
            if (sent.Count > MaxRecipients)
            {
                invalid.Add(new FieldError("/recipients", $"Up to {MaxRecipients} recipients."));
                return null;
            }
            var result = new List<RecipientSpec>();
            for (var i = 0; i < sent.Count; i++)
            {
                var at = $"/recipients/{i}";
                var r = sent[i];
                switch (r?.Kind)
                {
                    case RecipientKinds.Route:
                        if (!result.Any(x => x.Kind == RecipientKinds.Route))
                        {
                            result.Add(new RecipientSpec(RecipientKinds.Route));
                        }
                        break;
                    case RecipientKinds.Address:
                        if (!type.ExternalRecipients)
                        {
                            refused.Add(new FieldError(at, $"The type {type.Key} doesn't allow external recipients (externalRecipients: false)."));
                            break;
                        }
                        if (r.Channel is null || channels.Find(r.Channel) is not { } channel)
                        {
                            invalid.Add(new FieldError(at + "/channel", "The channel is one of the registered channels."));
                            break;
                        }
                        if (NormalizeAddress(channel, r.Address) is not { } address)
                        {
                            invalid.Add(new FieldError(at + "/address", channel.Address == AddressKind.None
                                ? $"The {channel.Key} channel takes no addresses in a request; use a route."
                                : $"The address isn't a valid {channel.Address.ToString().ToLowerInvariant()} address."));
                            break;
                        }
                        if (!result.Any(x => x.Kind == RecipientKinds.Address && x.Channel == channel.Key && x.Address == address))
                        {
                            result.Add(new RecipientSpec(RecipientKinds.Address, channel.Key, address));
                        }
                        break;
                    case { } kind when LaterKinds.Contains(kind):
                        refused.Add(new FieldError(at + "/kind", $"Recipients of kind '{kind}' come with people and the inbox (phase 2). Use route or address."));
                        break;
                    default:
                        invalid.Add(new FieldError(at + "/kind", "The kind is route or address."));
                        break;
                }
            }
            return result;
        }

        /// <summary>The address in canonical form for its channel, or null if it isn't one (design §3: email lowercased).</summary>
        public static string? NormalizeAddress(ChannelDefinition channel, string? address)
        {
            if (string.IsNullOrWhiteSpace(address) || address.Length > 320)
            {
                return null;
            }
            switch (channel.Address)
            {
                case AddressKind.Email:
                    var trimmed = address.Trim();
                    // A bare address only: no display name, no list, nothing MailAddress would quietly drop.
                    if (!System.Net.Mail.MailAddress.TryCreate(trimmed, out var mail) || mail.Address != trimmed || !string.IsNullOrEmpty(mail.DisplayName))
                    {
                        return null;
                    }
                    return mail.Address.ToLowerInvariant();
                default:
                    return null;
            }
        }

        internal static void Labels(IReadOnlyDictionary<string, string>? labels, List<FieldError> invalid)
        {
            if (labels is null)
            {
                return;
            }
            if (labels.Count > MaxLabels)
            {
                invalid.Add(new FieldError("/labels", $"Up to {MaxLabels} labels."));
            }
            foreach (var (name, value) in labels)
            {
                if (!LabelName().IsMatch(name))
                {
                    invalid.Add(new FieldError("/labels/" + TypeRules.Pointer(name), "A label name is 1–63 letters, digits, _ . : or -, starting with a letter."));
                }
                else if (value is null || value.Length > 500)
                {
                    invalid.Add(new FieldError("/labels/" + TypeRules.Pointer(name), "A label value is a string of up to 500 characters."));
                }
            }
        }

        internal static void Links(IReadOnlyDictionary<string, string>? links, List<FieldError> invalid)
        {
            if (links is null)
            {
                return;
            }
            if (links.Count > MaxLinks)
            {
                invalid.Add(new FieldError("/links", $"Up to {MaxLinks} links."));
            }
            foreach (var (name, value) in links)
            {
                var at = "/links/" + TypeRules.Pointer(name);
                if (!LinkName().IsMatch(name))
                {
                    invalid.Add(new FieldError(at, "A link name is 1–63 letters, digits, _ . or -, starting with a letter."));
                }
                else if (value is not { Length: <= 2000 } || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                {
                    invalid.Add(new FieldError(at, "A link is an absolute http or https URL of up to 2000 characters."));
                }
            }
        }

        internal static string? Actor(ActorSpec? actor, List<FieldError> invalid)
        {
            if (actor is null)
            {
                return null;
            }
            if (actor.Kind is not ("user" or "system" or "service"))
            {
                invalid.Add(new FieldError("/actor/kind", "The actor's kind is user, system or service."));
                return null;
            }
            if (actor.Id is not { Length: > 0 and <= 200 } || actor.Id.Any(char.IsControl))
            {
                invalid.Add(new FieldError("/actor/id", "The actor's id is 1–200 characters."));
                return null;
            }
            return actor.Kind + ":" + actor.Id;
        }
    }

    internal sealed class IntakeHandler(NotificationsDbContext db, IChannelRegistry channels, DataSchemas schemas, TimeProvider clock)
        : IRequestHandler<AcceptNotification, Outcome<AcceptedView>>
    {
        public async ValueTask<Outcome<AcceptedView>> Handle(AcceptNotification request, CancellationToken cancellationToken)
        {
            var s = request.Submission;
            var tenant = request.NotificationTenantId;
            if (!RequestRules.IsKey(s.IdempotencyKey))
            {
                return Outcome<AcceptedView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.",
                    [new FieldError("/idempotencyKey", "The idempotency key is 1–200 printable characters without spaces. It is required.")]);
            }
            if (!await db.ApplicationTenants.AnyAsync(t => t.ApplicationId == request.ApplicationId && t.TenantId == tenant, cancellationToken))
            {
                return Outcome<AcceptedView>.Forbidden(ErrorCodes.TenantNotLinked, "The application isn't linked to this tenant. A platform admin links it.");
            }
            // The same key again is the same request: answer with the original, whatever this one says (design §5.2).
            if (await IntakeQueries.FindAsync(db, tenant, request.ApplicationId, s.IdempotencyKey!, cancellationToken) is { } original)
            {
                return Outcome<AcceptedView>.Ok(original with { Duplicate = true });
            }
            if (string.IsNullOrEmpty(s.Type))
            {
                return Outcome<AcceptedView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.", [new FieldError("/type", "The type is required.")]);
            }
            if (!s.Type.StartsWith(request.ApplicationKey + ".", StringComparison.Ordinal))
            {
                return Outcome<AcceptedView>.Forbidden(ErrorCodes.TypeNotOwned, $"An application sends only its own types: {request.ApplicationKey}.…");
            }
            var types = db.Types.AsNoTracking().Where(t => t.ApplicationId == request.ApplicationId && t.Key == s.Type);
            var type = s.TypeVersion is { } version
                ? await types.SingleOrDefaultAsync(t => t.Version == version, cancellationToken)
                : await types.OrderByDescending(t => t.Version).FirstOrDefaultAsync(cancellationToken);
            if (type is null)
            {
                return Outcome<AcceptedView>.Unprocessable(ErrorCodes.UnknownType, s.TypeVersion is null
                    ? $"There is no type {s.Type}. Declare it first: PUT /api/v1/applications/{request.ApplicationKey}/types."
                    : $"The type {s.Type} has no version {s.TypeVersion}.");
            }

            var now = clock.UtcNow();
            var invalid = new List<FieldError>();
            var refused = new List<FieldError>();
            if (s.CorrelationKey is not null && !RequestRules.IsKey(s.CorrelationKey))
            {
                invalid.Add(new FieldError("/correlationKey", "The correlation key is 1–200 printable characters without spaces."));
            }
            // A well-formed request the type's rules refuse: 422, like a recipient the type doesn't allow.
            else if (s.CorrelationKey is null && type.Correlation == CorrelationRules.Required)
            {
                refused.Add(new FieldError("/correlationKey", $"The type {type.Key} requires a correlation key."));
            }
            else if (s.CorrelationKey is not null && type.Correlation == CorrelationRules.None)
            {
                refused.Add(new FieldError("/correlationKey", $"The type {type.Key} takes no correlation key."));
            }
            var priority = type.Priority;
            if (s.Priority is not null && !TypeRules.TryPriority(s.Priority, out priority))
            {
                invalid.Add(new FieldError("/priority", "The priority is low, normal, high or critical."));
            }
            if (s.ExpiresAt is { } expires && expires <= now)
            {
                invalid.Add(new FieldError("/expiresAt", "The request expires in the past."));
            }
            RequestRules.Labels(s.Labels, invalid);
            RequestRules.Links(s.Links, invalid);
            var actor = RequestRules.Actor(s.Actor, invalid);
            var recipients = RequestRules.Recipients(s.Recipients, type, channels, invalid, refused);
            var data = s.Data is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } d ? d : JsonDocument.Parse("{}").RootElement;
            if (data.ValueKind != JsonValueKind.Object)
            {
                invalid.Add(new FieldError("/data", "The data is a JSON object."));
            }
            else if (CanonicalJson.FirstDuplicate(data, "/data") is { } duplicate)
            {
                invalid.Add(new FieldError(duplicate, "This object repeats a property name; each name appears once."));
            }
            else if (System.Text.Encoding.UTF8.GetByteCount(data.GetRawText()) > RequestRules.MaxDataBytes)
            {
                invalid.Add(new FieldError("/data", $"The data is at most {RequestRules.MaxDataBytes / 1024} KB."));
            }
            if (invalid.Count > 0)
            {
                return Outcome<AcceptedView>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.", invalid);
            }
            if (refused.Count > 0)
            {
                return Outcome<AcceptedView>.Unprocessable(refused.Any(e => e.Pointer.StartsWith("/recipients", StringComparison.Ordinal))
                    ? ErrorCodes.RecipientNotAllowed : ErrorCodes.InvalidRequest, "The type's rules refuse this request.", refused);
            }
            var dataErrors = DataSchemas.Validate(schemas.For(type), data, "/data");
            if (dataErrors.Count > 0)
            {
                return Outcome<AcceptedView>.Unprocessable(ErrorCodes.InvalidData, $"The data doesn't match version {type.Version} of {type.Key}.", dataErrors);
            }

            // Two requests with one key at once both get here; the second commit hits the key, and the API answers it with the original.
            var stored = new NotificationRequest
            {
                TenantId = tenant, ApplicationId = request.ApplicationId, IdempotencyKey = s.IdempotencyKey!, TypeId = type.Id, TypeKey = type.Key,
                TypeVersion = type.Version, SchemaHash = type.SchemaHash, CorrelationKey = s.CorrelationKey, Priority = priority,
                Labels = s.Labels is null ? [] : new Dictionary<string, string>(s.Labels), Data = data.GetRawText(),
                Recipients = JsonSerializer.Serialize(recipients, IntakeQueries.Json), Actor = actor,
                Links = s.Links is null ? [] : new Dictionary<string, string>(s.Links), ExpiresAt = s.ExpiresAt, AcceptedAt = now,
                TraceParent = request.TraceParent is { Length: <= 55 } trace ? trace : null,
            };
            db.Requests.Add(stored);
            db.Notifications.Add(new Notification
            {
                TenantId = tenant, RequestId = stored.Id, State = NotificationState.Planning, NextAt = now, CreatedAt = now, UpdatedAt = now,
            });
            return Outcome<AcceptedView>.Created(new AcceptedView(stored.Id, type.Key, type.Version, now, Duplicate: false));
        }
    }

    public static class IntakeQueries
    {
        internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

        /// <summary>The request already accepted under this key, if any.</summary>
        public static async Task<AcceptedView?> FindAsync(NotificationsDbContext db, Guid tenantId, Guid applicationId, string idempotencyKey, CancellationToken ct)
            => await db.Requests.AsNoTracking()
                .Where(r => r.TenantId == tenantId && r.ApplicationId == applicationId && r.IdempotencyKey == idempotencyKey)
                .Select(r => new AcceptedView(r.Id, r.TypeKey, r.TypeVersion, r.AcceptedAt, true))
                .SingleOrDefaultAsync(ct);
    }

    /// <summary>A notification as its producer sees it: the request, its processing state and its deliveries so far.</summary>
    public sealed record NotificationDetail(
        Guid Id, string Type, int TypeVersion, string IdempotencyKey, string? CorrelationKey, NotificationPriority Priority,
        IReadOnlyDictionary<string, string> Labels, IReadOnlyDictionary<string, string> Links, string? Actor, DateTimeOffset AcceptedAt,
        DateTimeOffset? ExpiresAt, NotificationState State, string? Error, IReadOnlyList<DeliverySummary> Deliveries);

    public sealed record DeliverySummary(Guid Id, string Channel, string DestinationKey, DeliveryStatus Status, int Attempts, FailureCategory? FailureCategory,
        DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

    /// <summary>Reads notifications for their producer, outside any command.</summary>
    public sealed class NotificationReader(NotificationsDbContext db)
    {
        /// <summary>One of the application's own notifications in a tenant; another application's is not found.</summary>
        public async Task<NotificationDetail?> GetAsync(Guid tenantId, Guid applicationId, Guid id, CancellationToken ct)
        {
            var request = await db.Requests.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id && r.ApplicationId == applicationId, ct);
            if (request is null)
            {
                return null;
            }
            var notification = await db.Notifications.AsNoTracking().SingleAsync(n => n.TenantId == tenantId && n.RequestId == id, ct);
            var deliveries = await (
                from recipient in db.Recipients.Where(r => r.TenantId == tenantId && r.NotificationId == notification.Id)
                join item in db.DeliveryItems on new { recipient.TenantId, RecipientId = recipient.Id } equals new { item.TenantId, item.RecipientId }
                join delivery in db.Deliveries on new { item.TenantId, Id = item.DeliveryId } equals new { delivery.TenantId, delivery.Id }
                select delivery).AsNoTracking().Distinct().OrderBy(d => d.CreatedAt).ToListAsync(ct);
            return new NotificationDetail(request.Id, request.TypeKey, request.TypeVersion, request.IdempotencyKey, request.CorrelationKey, request.Priority,
                request.Labels, request.Links, request.Actor, request.AcceptedAt, request.ExpiresAt, notification.State, notification.Error,
                deliveries.Select(d => new DeliverySummary(d.Id, d.Channel, d.DestinationKey, d.Status, d.Attempts, d.FailureCategory, d.CreatedAt, d.CompletedAt)).ToList());
        }
    }
}
