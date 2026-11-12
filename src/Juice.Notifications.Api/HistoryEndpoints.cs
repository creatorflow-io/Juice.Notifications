using System.Security.Claims;
using Juice.MediatR;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.History;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// Delivery history and dead-letter retry (phase-1-plan M8, §7). A tenant admin sees every delivery of the tenant; a producer sees those made
    /// for its own requests, and another's is not found. Only a tenant admin retries.
    /// </summary>
    internal static class HistoryEndpoints
    {
        public static void MapHistoryEndpoints(this IEndpointRouteBuilder app)
        {
            var deliveries = app.MapGroup("/api/v1/tenants/{tenantId:guid}/deliveries").WithTags("Delivery history");
            deliveries.MapGet("", ListAsync);
            deliveries.MapGet("/{id:guid}", GetAsync);
            deliveries.MapPost("/{id:guid}/retry", RetryAsync).AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                var allowed = Guid.TryParse(http.GetRouteValue("tenantId") as string, out var tenantId)
                    && await http.RequestServices.GetRequiredService<ITenantAccess>().CanAdministerAsync(http.User, tenantId, http.RequestAborted);
                return allowed
                    ? await next(context)
                    : ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotTenantAdmin, "Only the tenant's admins retry deliveries.");
            });
        }

        /// <summary>No application filter for a tenant admin; the producer's own application otherwise; or the refusal.</summary>
        private static async Task<(bool Allowed, Guid? ApplicationId, IResult? Refusal)> ViewerAsync(Guid tenantId, ClaimsPrincipal user, ITenantAccess access,
            Producers producers, CancellationToken ct)
        {
            if (await access.CanAdministerAsync(user, tenantId, ct))
            {
                return (true, null, null);
            }
            var (caller, refusal) = await producers.ResolveAsync(user, ct, forWriting: false);
            return caller is null ? (false, null, refusal) : (true, caller.Id, null);
        }

        private static async Task<IResult> ListAsync(Guid tenantId, ClaimsPrincipal user, ITenantAccess access, Producers producers, DeliveryHistory history,
            string? status, string? channel, string? integration, string? type, Guid? request, DateTimeOffset? from, DateTimeOffset? to, Guid? before,
            int? limit, CancellationToken ct)
        {
            var (allowed, applicationId, refusal) = await ViewerAsync(tenantId, user, access, producers, ct);
            if (!allowed)
            {
                return refusal!;
            }
            DeliveryStatus? parsed = null;
            if (status is not null)
            {
                if (!Enum.TryParse<DeliveryStatus>(status, ignoreCase: true, out var s) || !Enum.IsDefined(s) || int.TryParse(status, out _))
                {
                    return ApiResults.Problem(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, $"'{status}' isn't a delivery status.",
                        [new FieldError("/status", "One of " + string.Join(", ", Enum.GetNames<DeliveryStatus>()) + ".")]);
                }
                parsed = s;
            }
            if (limit is < 1 or > DeliveryHistory.MaxLimit)
            {
                return ApiResults.Problem(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, $"limit is 1 to {DeliveryHistory.MaxLimit}.",
                    [new FieldError("/limit", $"1 to {DeliveryHistory.MaxLimit}.")]);
            }
            var page = await history.ListAsync(tenantId, new DeliveryFilter(applicationId, parsed, channel, integration, type, request, from, to, before,
                limit ?? DeliveryHistory.DefaultLimit), ct);
            return Results.Ok(new DeliveryPageResponse(page.Items.Select(ToResponse).ToList(), page.Next));
        }

        private static async Task<IResult> GetAsync(Guid tenantId, Guid id, ClaimsPrincipal user, ITenantAccess access, Producers producers,
            DeliveryHistory history, CancellationToken ct)
        {
            var (allowed, applicationId, refusal) = await ViewerAsync(tenantId, user, access, producers, ct);
            if (!allowed)
            {
                return refusal!;
            }
            return await history.GetAsync(tenantId, id, applicationId, ct) is { } d
                ? Results.Ok(new DeliveryDetailResponse(ToResponse(d.Delivery), d.DestinationSnapshot, d.TemplateVersion, d.Locale, d.RendererVersion,
                    d.ProviderMessageId, d.Items.Select(i => new DeliveryItemResponse(i.RecipientId, i.Kind, i.NamedAs, i.Route, i.Resolver)).ToList(),
                    d.History.Select(a => new DeliveryAttemptResponse(a.Number, a.At, a.Outcome, a.Category, a.Integration, a.StatusCode, a.DurationMs, a.Error,
                        a.ProviderMessageId)).ToList()))
                : ApiResults.NotFound($"There is no delivery {id} you can see in this tenant.");
        }

        private static Task<IResult> RetryAsync(Guid tenantId, Guid id, IMediator mediator, NotificationsDbContext db, Stages.StageNudges nudges,
            CancellationToken ct)
            => ApiResults.SendAsync(mediator, db, new RetryDelivery(tenantId, id),
                outcome =>
                {
                    nudges.Wake(Planning.DeliveryStageNames.Dispatcher);
                    return Results.Ok(new RetryResponse(outcome.Value!.Id, outcome.Value.Status, outcome.Value.Attempts, outcome.Value.Replays));
                },
                conflict => Task.FromResult(conflict.Kind is ConflictKind.RowGone or ConflictKind.Aborted ? ApiResults.ConcurrentChange() : null), ct);

        private static DeliveryHistoryResponse ToResponse(DeliveryRow d) => new(d.Id, d.RequestId, d.Type, d.Channel, d.Integration, d.DestinationKey, d.Status,
            d.Attempts, d.FailureCategory, d.LastError, d.CreatedAt, d.FirstAttemptAt, d.CompletedAt);
    }
}
