using System.Diagnostics;
using System.Security.Claims;
using Juice.MediatR;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Juice.Notifications.Types;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Api
{
    /// <summary>What producers call: declaring their types (design §5.1) and sending notifications (§5.2). The caller is the token's application.</summary>
    internal static class ProducerEndpoints
    {
        public static void MapProducerEndpoints(this IEndpointRouteBuilder app)
        {
            var types = app.MapGroup("/api/v1/applications/{appKey}/types").WithTags("Types");
            types.MapPut("", DeclareAsync);
            types.MapGet("/{typeKey}/versions", VersionsAsync);
            types.MapGet("/{typeKey}/versions/{version:int}", VersionAsync);

            var notifications = app.MapGroup("/api/v1/tenants/{tenantId:guid}/notifications").WithTags("Notifications");
            notifications.MapPost("", SendAsync);
            notifications.MapGet("/{id:guid}", GetAsync);
        }

        private static async Task<IResult> DeclareAsync(string appKey, DeclareTypeBody body, ClaimsPrincipal user, Producers producers, IMediator mediator,
            NotificationsDbContext db, TypeReader reader, IChannelRegistry channels, CancellationToken ct)
        {
            var (caller, refusal) = await producers.ResolveAsync(user, ct);
            if (caller is null)
            {
                return refusal!;
            }
            if (caller.Key != appKey)
            {
                return ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotYourApplication, $"An application declares only its own types; you are {caller.Key}.");
            }
            var declaration = new TypeDeclaration(body.Key, body.Name, body.Category, body.DisplayGroup, body.Priority, body.Mandatory, body.ExternalRecipients,
                body.Schema, body.Correlation, body.Redact, body.Workflow, body.Templates);
            return await ApiResults.SendAsync(mediator, db, new DeclareType(caller.Id, caller.Key, declaration),
                outcome => outcome.Value!.IsNew
                    ? Results.Created($"/api/v1/applications/{appKey}/types/{outcome.Value.Key}/versions/{outcome.Value.Version}", ToResponse(outcome.Value))
                    : Results.Ok(ToResponse(outcome.Value)),
                async conflict =>
                {
                    // Another declaration took the next version first. The same declaration gets that version; a different one asks again.
                    if (conflict is { Kind: ConflictKind.Unique, Constraint: Constraints.TypeVersion })
                    {
                        var latest = await reader.LatestAsync(caller.Id, body.Key!, ct);
                        return latest is not null && latest.DeclarationHash == TypeReader.HashOf(caller.Key, declaration, channels)
                            ? Results.Ok(new TypeVersionResponse(latest.Key, latest.Version, latest.Name, latest.SchemaHash, latest.CreatedAt))
                            : ApiResults.ConcurrentChange();
                    }
                    return conflict.Kind == ConflictKind.Aborted ? ApiResults.ConcurrentChange() : null;
                }, ct);
        }

        private static async Task<IResult> VersionsAsync(string appKey, string typeKey, ClaimsPrincipal user, Producers producers, NotificationsDbContext db,
            TypeReader reader, CancellationToken ct)
        {
            var (app, refusal) = await ReaderOfAsync(appKey, user, producers, db, ct);
            if (app is null)
            {
                return refusal!;
            }
            var versions = await reader.VersionsAsync(app.Id, typeKey, ct);
            return versions.Count == 0 ? ApiResults.NotFound($"The application {appKey} has no type {typeKey}.") : Results.Ok(versions.Select(ToResponse));
        }

        private static async Task<IResult> VersionAsync(string appKey, string typeKey, int version, ClaimsPrincipal user, Producers producers,
            NotificationsDbContext db, TypeReader reader, CancellationToken ct)
        {
            var (app, refusal) = await ReaderOfAsync(appKey, user, producers, db, ct);
            if (app is null)
            {
                return refusal!;
            }
            return await reader.VersionAsync(app.Id, typeKey, version, ct) is { } d
                ? Results.Ok(new TypeVersionDetailResponse(d.Key, d.Version, d.Name, d.Category, d.DisplayGroup, d.Priority, d.Mandatory, d.ExternalRecipients,
                    d.Correlation, d.Redact, d.Schema, d.Workflow, d.Templates, d.SchemaHash, d.CreatedAt))
                : ApiResults.NotFound($"The type {typeKey} has no version {version}.");
        }

        /// <summary>Types are read by their own application, or by a platform admin.</summary>
        private static async Task<(Application? App, IResult? Refusal)> ReaderOfAsync(string appKey, ClaimsPrincipal user, Producers producers,
            NotificationsDbContext db, CancellationToken ct)
        {
            if (user.IsInRole(Roles.PlatformAdmin))
            {
                var app = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.Key == appKey, ct);
                return app is null ? (null, ApiResults.NotFound($"There is no application '{appKey}'.")) : (app, null);
            }
            var (caller, refusal) = await producers.ResolveAsync(user, ct, forWriting: false);
            if (caller is null)
            {
                return (null, refusal);
            }
            return caller.Key == appKey
                ? (caller, null)
                : (null, ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.NotYourApplication, $"An application reads only its own types; you are {caller.Key}."));
        }

        private static async Task<IResult> SendAsync(Guid tenantId, SendNotificationBody body, ClaimsPrincipal user, Producers producers, IMediator mediator,
            NotificationsDbContext db, Stages.StageNudges nudges, CancellationToken ct)
        {
            // A disabled application is refused by intake, after the idempotency key: a retry of an accepted request still gets the original.
            var (caller, refusal) = await producers.ResolveAsync(user, ct, forWriting: false);
            if (caller is null)
            {
                return refusal!;
            }
            var submission = new NotificationSubmission(body.Type, body.TypeVersion, body.IdempotencyKey, body.CorrelationKey,
                body.Recipients?.Select(r => new RecipientSpec(r?.Kind, r?.Channel, r?.Address)).ToList(), body.Actor is null ? null : new ActorSpec(body.Actor.Kind, body.Actor.Id),
                body.Priority, body.Labels, body.Data, body.Links, body.ExpiresAt);
            var traceParent = Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.Id : null;
            return await ApiResults.SendAsync(mediator, db, new AcceptNotification(tenantId, caller.Id, caller.Key, submission, traceParent, caller.Enabled),
                outcome =>
                {
                    if (!outcome.Value!.Duplicate)
                    {
                        nudges.Wake(Planning.PlannerStage.StageName); // committed: the planner can take it now rather than at its next poll
                    }
                    return Accepted(tenantId, outcome.Value);
                },
                async conflict => conflict switch
                {
                    // The same idempotency key at once: the original answers both.
                    { Kind: ConflictKind.Unique, Constraint: Constraints.RequestIdempotencyKey }
                        => await IntakeQueries.FindAsync(db, tenantId, caller.Id, body.IdempotencyKey!, ct) is { } original
                            ? Accepted(tenantId, original)
                            : ApiResults.ConcurrentChange(),
                    // The link was removed while the request was accepted.
                    { Kind: ConflictKind.ForeignKey, Constraint: Constraints.RequestApplicationTenant }
                        => ApiResults.Problem(StatusCodes.Status403Forbidden, ErrorCodes.TenantNotLinked, "The application isn't linked to this tenant."),
                    { Kind: ConflictKind.Aborted } => ApiResults.ConcurrentChange(),
                    _ => null,
                }, ct);
        }

        private static IResult Accepted(Guid tenantId, AcceptedView view)
        {
            var response = new AcceptedResponse(view.Id, "accepted", view.Type, view.TypeVersion, view.AcceptedAt);
            return view.Duplicate
                ? Results.Ok(response)
                : Results.Accepted($"/api/v1/tenants/{tenantId}/notifications/{view.Id}", response);
        }

        private static async Task<IResult> GetAsync(Guid tenantId, Guid id, ClaimsPrincipal user, Producers producers, NotificationReader reader, CancellationToken ct)
        {
            var (caller, refusal) = await producers.ResolveAsync(user, ct, forWriting: false);
            if (caller is null)
            {
                return refusal!;
            }
            return await reader.GetAsync(tenantId, caller.Id, id, ct) is { } n
                ? Results.Ok(new NotificationResponse(n.Id, n.Type, n.TypeVersion, n.IdempotencyKey, n.CorrelationKey, n.Priority, n.Labels, n.Links, n.Actor,
                    n.AcceptedAt, n.ExpiresAt, n.State, n.Error,
                    n.Deliveries.Select(d => new DeliveryResponse(d.Id, d.Channel, d.DestinationKey, d.Status, d.Attempts, d.FailureCategory, d.CreatedAt, d.CompletedAt)).ToList()))
                : ApiResults.NotFound($"There is no notification {id} from {caller.Key} in this tenant.");
        }

        private static TypeVersionResponse ToResponse(TypeVersionView v) => new(v.Key, v.Version, v.Name, v.SchemaHash, v.CreatedAt);
    }
}
