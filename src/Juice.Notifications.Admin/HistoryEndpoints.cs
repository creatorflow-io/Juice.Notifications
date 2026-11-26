using System.Security.Claims;
using Juice.MediatR;
using Juice.Notifications.Admin;
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
        public static void MapHistoryEndpoints(this IEndpointRouteBuilder tenant)
        {
            var deliveries = tenant.MapGroup("/deliveries").WithTags("Delivery history").WithMetadata(new AdminPageMetadata(AdminPages.Deliveries));
            deliveries.MapGet("", ListAsync).ProducerToo();
            deliveries.MapGet("/{id:guid}", GetAsync).ProducerToo();
            // Retry and replay are operating, not configuring (p5-d6: an Operator retries).
            deliveries.MapPost("/replay", ReplayAsync).Needs(AdminRights.Operate);
            deliveries.MapPost("/{id:guid}/retry", RetryAsync).Needs(AdminRights.Operate);
        }

        /// <summary>Bulk replay (phase-4-plan I44): the tenant's failed deliveries matching the filter, each as a single retry.</summary>
        private static async Task<IResult> ReplayAsync(Guid tenantId, ReplayBody body, IMediator mediator, NotificationsDbContext db, Stages.StageNudges nudges,
            CancellationToken ct)
        {
            FailureCategory? category = null;
            if (body.Category is { } named)
            {
                if (Enum.GetNames<FailureCategory>().FirstOrDefault(n => n.Equals(named, StringComparison.OrdinalIgnoreCase)) is not { } name)
                {
                    return ApiResults.Problem(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, $"'{named}' isn't a failure category.",
                        [new FieldError("/category", "One of " + string.Join(", ", Enum.GetNames<FailureCategory>()) + ".")]);
                }
                category = Enum.Parse<FailureCategory>(name);
            }
            return await ApiResults.SendAsync(mediator, db, new ReplayDeliveries(tenantId, new ReplayFilter(body.Channel, body.Integration, body.Type, category,
                    body.From, body.To, body.Limit)),
                outcome =>
                {
                    if (outcome.Value!.Replayed > 0)
                    {
                        nudges.Wake(Planning.DeliveryStageNames.Dispatcher);
                    }
                    return Results.Ok(new ReplayResponse(outcome.Value.Replayed, outcome.Value.Skipped, outcome.Value.More));
                }, _ => Task.FromResult<IResult?>(null), ct);
        }

        /// <summary>No application filter for a tenant admin; the producer's own application otherwise; or the refusal.</summary>
        private static async Task<(bool Allowed, Guid? ApplicationId, IResult? Refusal)> ViewerAsync(Guid tenantId, ClaimsPrincipal user, INotificationsAdminAccess access,
            IServiceProvider services, CancellationToken ct)
        {
            if (await access.RightsAsync(user, tenantId, ct) != AdminRights.None)
            {
                return (true, null, null);
            }
            // Not an admin: the service's producer, for its own deliveries (the rights filter let it this far only where the hook is).
            if (services.GetService<IAdminFallbackCaller>() is not { } fallback)
            {
                return (false, null, ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotTenantAdmin, "The caller doesn't administer this tenant."));
            }
            var (application, refusal) = await fallback.ResolveAsync(user, ct);
            return application is null ? (false, null, refusal) : (true, application, null);
        }

        private static async Task<IResult> ListAsync(Guid tenantId, ClaimsPrincipal user, INotificationsAdminAccess access, IServiceProvider services, DeliveryHistory history,
            string? status, string? channel, string? integration, string? type, Guid? request, DateTimeOffset? from, DateTimeOffset? to, Guid? before,
            int? limit, CancellationToken ct)
        {
            var (allowed, applicationId, refusal) = await ViewerAsync(tenantId, user, access, services, ct);
            if (!allowed)
            {
                return refusal!;
            }
            DeliveryStatus? parsed = null;
            if (status is not null)
            {
                // One status by name: Enum.TryParse would also take "1" and "Sending,Retrying" (their flags OR-ed into another status).
                if (Enum.GetNames<DeliveryStatus>().FirstOrDefault(n => n.Equals(status, StringComparison.OrdinalIgnoreCase)) is not { } name
                    || !Enum.TryParse<DeliveryStatus>(name, out var s))
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

        private static async Task<IResult> GetAsync(Guid tenantId, Guid id, ClaimsPrincipal user, INotificationsAdminAccess access, IServiceProvider services,
            DeliveryHistory history, CancellationToken ct)
        {
            var (allowed, applicationId, refusal) = await ViewerAsync(tenantId, user, access, services, ct);
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
            d.Attempts, d.FailureCategory, d.LastError, d.CreatedAt, d.FirstAttemptAt, d.CompletedAt, d.Failovers);
    }
}
