using Juice.Notifications.Api.Contracts;
using Juice.Notifications.History;
using Microsoft.Extensions.Caching.Memory;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// What a tenant's admins read to investigate (phase-4c-plan I52): the notifications list, a notification's recipients and why (deferred
    /// #36), the correlations list and the overview. Tenant admins only (<see cref="ITenantAccess"/>): a producer reads its own notifications
    /// and correlations one at a time, by id and key.
    /// </summary>
    internal static class InvestigationEndpoints
    {
        /// <summary>How long an overview is kept per tenant: its counts are a glance, not a ledger.</summary>
        internal static readonly TimeSpan OverviewFor = TimeSpan.FromSeconds(30);

        public static void MapInvestigationEndpoints(this IEndpointRouteBuilder app)
        {
            var tenant = app.MapGroup("/api/v1/tenants/{tenantId:guid}").RequireTenantAdmin().WithTags("Investigation");
            tenant.MapGet("/notifications", NotificationsAsync);
            tenant.MapGet("/notifications/{id:guid}/recipients", RecipientsAsync);
            tenant.MapGet("/correlations", CorrelationsAsync);
            tenant.MapGet("/overview", OverviewAsync);
        }

        private static async Task<IResult> NotificationsAsync(Guid tenantId, string? type, string? application, string? correlation, string? state,
            DateTimeOffset? from, DateTimeOffset? to, Guid? before, int? limit, Investigation reader, CancellationToken ct)
        {
            if (Named<NotificationState>(state, "state", "a notification state", out var parsed) is { } bad)
            {
                return bad;
            }
            if (Limit(limit) is { } wrong)
            {
                return wrong;
            }
            var page = await reader.NotificationsAsync(tenantId, new NotificationFilter(type, application, correlation, parsed, from, to, before,
                limit ?? Investigation.DefaultLimit), ct);
            return Results.Ok(new NotificationPageResponse(page.Items.Select(n => new NotificationListItemResponse(n.Id, n.Type, n.TypeVersion,
                n.Application, n.CorrelationKey, n.Priority, n.State, n.Error, n.Actor, n.AcceptedAt)).ToList(), page.Next));
        }

        private static async Task<IResult> RecipientsAsync(Guid tenantId, Guid id, Guid? after, int? limit, Investigation reader, CancellationToken ct)
        {
            if (Limit(limit) is { } wrong)
            {
                return wrong;
            }
            return await reader.RecipientsAsync(tenantId, id, after, limit ?? Investigation.DefaultLimit, ct) is { } page
                ? Results.Ok(new RecipientPageResponse(page.Items.Select(r => new RecipientResponse(r.Id, r.Kind, r.NamedAs, r.Via, r.SubscriberId, r.Route,
                    r.Channel, r.Address, r.Resolver, r.Outcome, r.Bypassed, r.EscalationStep,
                    r.Digest is { } d ? new RecipientDigestResponse(d.Id, d.State, d.ClosesAt, d.ClosedAt) : null, r.CreatedAt,
                    r.Deliveries.Select(x => new RecipientDeliveryResponse(x.Id, x.Channel, x.DestinationKey, x.Status, x.FailureCategory)).ToList())).ToList(),
                    page.Next))
                : ApiResults.NotFound($"There is no notification {id} in this tenant.");
        }

        private static async Task<IResult> CorrelationsAsync(Guid tenantId, string? application, string? key, string? state, Guid? before, int? limit,
            Investigation reader, CancellationToken ct)
        {
            if (Named<CorrelationState>(state, "state", "a correlation state", out var parsed) is { } bad)
            {
                return bad;
            }
            if (Limit(limit) is { } wrong)
            {
                return wrong;
            }
            var page = await reader.CorrelationsAsync(tenantId, new CorrelationFilter(application, key, parsed, before, limit ?? Investigation.DefaultLimit), ct);
            return Results.Ok(new CorrelationPageResponse(page.Items.Select(c => new CorrelationListItemResponse(c.Id, c.Application, c.Key, c.State,
                c.AcknowledgedBy, c.AcknowledgedAt, c.ResolvedAt, c.Outcome, c.CreatedAt, c.LastRequestAt)).ToList(), page.Next));
        }

        private static async Task<IResult> OverviewAsync(Guid tenantId, Investigation reader, IMemoryCache cache, CancellationToken ct)
        {
            var overview = await cache.GetOrCreateAsync(("notifications-overview", tenantId), entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = OverviewFor;
                entry.Size = 1;
                return reader.OverviewAsync(tenantId, ct);
            });
            return Results.Ok(new OverviewResponse(overview!.At, overview.Since,
                overview.Deliveries.Select(d => new OverviewDeliveriesResponse(d.Channel, d.Status, d.Count)).ToList(),
                overview.FailingIntegrations.Select(i => new OverviewIntegrationResponse(i.Integration, i.Failed, i.LastFailedAt, i.LastError)).ToList(),
                overview.OpenStorms, overview.Unfinished));
        }

        /// <summary>
        /// One value of <typeparamref name="T"/> by name, any case; a number or a list of flags is refused (Enum.TryParse would take them).
        /// The refusal, or null with <paramref name="parsed"/> set (null when <paramref name="value"/> is).
        /// </summary>
        internal static IResult? Named<T>(string? value, string field, string what, out T? parsed) where T : struct, Enum
        {
            parsed = null;
            if (value is null)
            {
                return null;
            }
            if (Enum.GetNames<T>().FirstOrDefault(n => n.Equals(value, StringComparison.OrdinalIgnoreCase)) is not { } name)
            {
                return ApiResults.Problem(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, $"'{value}' isn't {what}.",
                    [new FieldError($"/{field}", "One of " + string.Join(", ", Enum.GetNames<T>()) + ".")]);
            }
            parsed = Enum.Parse<T>(name);
            return null;
        }

        private static IResult? Limit(int? limit) => limit is < 1 or > Investigation.MaxLimit
            ? ApiResults.Problem(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, $"limit is 1 to {Investigation.MaxLimit}.",
                [new FieldError("/limit", $"1 to {Investigation.MaxLimit}.")])
            : null;
    }
}
