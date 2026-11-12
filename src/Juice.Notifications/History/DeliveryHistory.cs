using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.History
{
    /// <summary>
    /// What a history list may be narrowed to (phase-1-plan M8). <see cref="ApplicationId"/> is set by the API for a producer, never by the caller: a
    /// producer sees only deliveries made for its own requests; a tenant admin sees all of the tenant's.
    /// </summary>
    public sealed record DeliveryFilter(
        Guid? ApplicationId = null, DeliveryStatus? Status = null, string? Channel = null, string? Integration = null, string? Type = null,
        Guid? Request = null, DateTimeOffset? From = null, DateTimeOffset? To = null, Guid? Before = null, int Limit = DeliveryHistory.DefaultLimit);

    public sealed record DeliveryRow(Guid Id, Guid? RequestId, string? Type, string Channel, string Integration, string DestinationKey, DeliveryStatus Status,
        int Attempts, FailureCategory? FailureCategory, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? FirstAttemptAt, DateTimeOffset? CompletedAt);

    public sealed record DeliveryPage(IReadOnlyList<DeliveryRow> Items, Guid? Next);

    public sealed record ItemView(Guid RecipientId, RecipientKind Kind, string NamedAs, string? Route, string Resolver);

    public sealed record AttemptView(int Number, DateTimeOffset At, AttemptOutcome Outcome, FailureCategory? Category, string Integration, int? StatusCode,
        int DurationMs, string? Error, string? ProviderMessageId);

    public sealed record DeliveryDetail(DeliveryRow Delivery, string DestinationSnapshot, int TemplateVersion, string Locale, string RendererVersion,
        string? ProviderMessageId, IReadOnlyList<ItemView> Items, IReadOnlyList<AttemptView> History);

    /// <summary>
    /// Delivery history (design §13, phase-1-plan M8): newest first, one page at a time. A delivery belongs to the requests of the recipients it
    /// carries (one, or several for a digest); it is a producer's when any of them is.
    /// </summary>
    public sealed class DeliveryHistory(NotificationsDbContext db)
    {
        public const int DefaultLimit = 50;
        public const int MaxLimit = 200;

        public async Task<DeliveryPage> ListAsync(Guid tenantId, DeliveryFilter filter, CancellationToken ct)
        {
            var limit = Math.Clamp(filter.Limit, 1, MaxLimit);
            var deliveries = Scoped(tenantId, filter.ApplicationId, filter.Type, filter.Request);
            if (filter.Status is { } status)
            {
                deliveries = deliveries.Where(d => d.Status == status);
            }
            if (filter.Channel is { } channel)
            {
                deliveries = deliveries.Where(d => d.Channel == channel);
            }
            if (filter.Integration is { } key)
            {
                deliveries = deliveries.Where(d => db.Integrations.Any(i => i.TenantId == tenantId && i.Id == d.IntegrationId && i.Key == key));
            }
            if (filter.From is { } from)
            {
                var utcFrom = from.ToUniversalTime(); // the column is timestamptz: Npgsql writes UTC only
                deliveries = deliveries.Where(d => d.CreatedAt >= utcFrom);
            }
            if (filter.To is { } to)
            {
                var utcTo = to.ToUniversalTime();
                deliveries = deliveries.Where(d => d.CreatedAt < utcTo);
            }
            if (filter.Before is { } before)
            {
                // Ids are version 7: in the order they were created. The cursor is the last id of the previous page.
                deliveries = deliveries.Where(d => d.Id.CompareTo(before) < 0);
            }
            var rows = await Rows(tenantId, deliveries.OrderByDescending(d => d.Id).Take(limit + 1)).ToListAsync(ct);
            var page = rows.Take(limit).Select(r => filter.ApplicationId is null ? r : ForProducer(r)).ToList();
            return new DeliveryPage(page, rows.Count > limit ? page[^1].Id : null);
        }

        public async Task<DeliveryDetail?> GetAsync(Guid tenantId, Guid id, Guid? applicationId, CancellationToken ct)
        {
            var delivery = await Scoped(tenantId, applicationId, null, null).Where(d => d.Id == id).SingleOrDefaultAsync(ct);
            if (delivery is null)
            {
                return null;
            }
            var row = await Rows(tenantId, db.Deliveries.AsNoTracking().Where(d => d.TenantId == tenantId && d.Id == id)).SingleAsync(ct);
            var items = await (
                from item in db.DeliveryItems.Where(i => i.TenantId == tenantId && i.DeliveryId == id)
                join recipient in db.Recipients on new { item.TenantId, Id = item.RecipientId } equals new { recipient.TenantId, recipient.Id }
                orderby recipient.Id
                select new ItemView(recipient.Id, recipient.Kind, recipient.NamedAs, recipient.RouteKey, recipient.Resolver)).AsNoTracking().ToListAsync(ct);
            var attempts = await (
                from attempt in db.Attempts.Where(a => a.TenantId == tenantId && a.DeliveryId == id)
                join integration in db.Integrations on new { attempt.TenantId, Id = attempt.IntegrationId } equals new { integration.TenantId, integration.Id }
                orderby attempt.Number
                select new AttemptView(attempt.Number, attempt.At, attempt.Outcome, attempt.Category, integration.Key, attempt.StatusCode, attempt.DurationMs,
                    attempt.Error, attempt.ProviderMessageId)).AsNoTracking().ToListAsync(ct);
            if (applicationId is not null)
            {
                row = ForProducer(row);
                // The provider's message id is its server's reply text (an SMTP 250 can name internal hosts): not the producer's.
                attempts = attempts.Select(a => a with { Error = ProducerError(a.Error, a.Category, a.StatusCode, a.Outcome), ProviderMessageId = null }).ToList();
            }
            // A producer sees which target, not its address: a tenant admin configured it, and a webhook URL can carry a token (D15).
            return new DeliveryDetail(row, applicationId is null ? delivery.DestinationSnapshot : ProducerSnapshot(delivery.DestinationSnapshot), delivery.TemplateVersion, delivery.Locale, delivery.RendererVersion,
                applicationId is null ? delivery.ProviderMessageId : null, items, attempts);
        }

        /// <summary>
        /// What a producer may read of an error. Provider texts quote what the tenant admin configured (an SMTP server echoes the recipient, .NET
        /// names the webhook's host, the policy names an address, a give-up names a secret reference), so a producer gets the category and status
        /// instead. A render failure is about the producer's own template and data, so it is kept.
        /// </summary>
        private static string? ProducerError(string? error, FailureCategory? category, int? status, AttemptOutcome? outcome = null)
        {
            // The service's own texts about the request (its template, its expiry, its type) say nothing the producer doesn't know.
            if (error is null || error.StartsWith("render_failed: ", StringComparison.Ordinal) || error.StartsWith("The request expired", StringComparison.Ordinal)
                || error.StartsWith("The type ", StringComparison.Ordinal))
            {
                return error;
            }
            var what = outcome == AttemptOutcome.Unknown || error.StartsWith("unknown_outcome", StringComparison.Ordinal) ? "unknown_outcome" : "failed";
            return $"{what}: {category?.ToString() ?? "Provider"}{(status is { } s ? $" ({s})" : "")}; details are in the tenant's delivery history";
        }

        private static DeliveryRow ForProducer(DeliveryRow row) => row with { LastError = ProducerError(row.LastError, row.FailureCategory, null) };

        private static string ProducerSnapshot(string snapshot)
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(snapshot);
                return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && json.RootElement.TryGetProperty("target", out var target) && target.ValueKind == System.Text.Json.JsonValueKind.String
                    ? System.Text.Json.JsonSerializer.Serialize(new { target = target.GetString() })
                    : "{}";
            }
            catch (System.Text.Json.JsonException)
            {
                return "{}";
            }
        }

        /// <summary>The tenant's deliveries, narrowed to those carrying a recipient of the given application, type or request.</summary>
        private IQueryable<Delivery> Scoped(Guid tenantId, Guid? applicationId, string? type, Guid? requestId)
        {
            var deliveries = db.Deliveries.AsNoTracking().Where(d => d.TenantId == tenantId);
            if (applicationId is null && type is null && requestId is null)
            {
                return deliveries;
            }
            var requests = db.Requests.Where(r => r.TenantId == tenantId);
            if (applicationId is { } app)
            {
                requests = requests.Where(r => r.ApplicationId == app);
            }
            if (type is not null)
            {
                requests = requests.Where(r => r.TypeKey == type);
            }
            if (requestId is { } rid)
            {
                requests = requests.Where(r => r.Id == rid);
            }
            var carried =
                from item in db.DeliveryItems
                join request in requests on new { item.TenantId, Id = item.RequestId } equals new { request.TenantId, request.Id }
                select item;
            return deliveries.Where(d => carried.Any(i => i.TenantId == d.TenantId && i.DeliveryId == d.Id));
        }

        /// <summary>Each delivery with its integration's key and the first request it carries (the only one, unless it is a digest).</summary>
        private IQueryable<DeliveryRow> Rows(Guid tenantId, IQueryable<Delivery> deliveries)
            => from d in deliveries
               join integration in db.Integrations on new { d.TenantId, Id = d.IntegrationId } equals new { integration.TenantId, integration.Id }
               let request = (
                   from item in db.DeliveryItems.Where(i => i.TenantId == tenantId && i.DeliveryId == d.Id)
                   join r in db.Requests on new { item.TenantId, Id = item.RequestId } equals new { r.TenantId, r.Id }
                   orderby r.Id
                   select new { r.Id, r.TypeKey }).FirstOrDefault()
               orderby d.Id descending
               select new DeliveryRow(d.Id, request == null ? null : request.Id, request == null ? null : request.TypeKey, d.Channel, integration.Key,
                   d.DestinationKey, d.Status, d.Attempts, d.FailureCategory, d.LastError, d.CreatedAt, d.FirstAttemptAt, d.CompletedAt);
    }
}
