using Juice.MediatR;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.People;

namespace Juice.Notifications.Api
{
    /// <summary>A tenant's subscribers and the identities linked to them (design D23). For platform admins and the tenant's own admins.</summary>
    internal static class PeopleEndpoints
    {
        public static void MapPeopleEndpoints(this IEndpointRouteBuilder app)
        {
            var tenant = app.MapGroup("/api/v1/tenants/{tenantId:guid}").RequireTenantAdmin().WithTags("People");

            tenant.MapGet("/subscribers", async (Guid tenantId, string? source, string? externalId, Guid? after, int? limit, SubscriberReader reader,
                CancellationToken ct)
                => Results.Ok((await reader.ListAsync(tenantId, source, externalId, after, limit ?? 50, ct)).Select(ToResponse)));
            tenant.MapGet("/subscribers/{id:guid}", async (Guid tenantId, Guid id, SubscriberReader reader, CancellationToken ct)
                => await reader.GetAsync(tenantId, id, ct) is { } view ? Results.Ok(ToResponse(view)) : ApiResults.NotFound("No such subscriber."));
            tenant.MapPost("/subscribers/{id:guid}/aliases", (Guid tenantId, Guid id, LinkAliasBody body, HttpContext http, IMediator mediator,
                NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new LinkAlias(tenantId, id, body.Source, body.ExternalId, NotificationsAudit.Caller(http.User)),
                    outcome => Results.Ok(ToResponse(outcome.Value!)), Conflict, ct));
            // An external id can hold anything a source uses (slashes included), so the identity travels in the query.
            tenant.MapDelete("/subscribers/{id:guid}/aliases", (Guid tenantId, Guid id, string source, string externalId, IMediator mediator,
                NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new UnlinkAlias(tenantId, id, source, externalId), _ => Results.NoContent(), Conflict, ct));
        }

        /// <summary>
        /// A link that raced: the same identity linked to two subscribers at once, the subscriber linked or unlinked elsewhere meanwhile, or it
        /// merged and deleted.
        /// </summary>
        private static Task<IResult?> Conflict(DbConflict conflict) => Task.FromResult<IResult?>(conflict switch
        {
            { Kind: ConflictKind.Unique, Constraint: Constraints.AliasIdentity } => ApiResults.ConcurrentChange(),
            { Kind: ConflictKind.ForeignKey, Constraint: Constraints.AliasSubscriber } => ApiResults.ConcurrentChange(),
            { Kind: ConflictKind.Aborted or ConflictKind.RowGone } => ApiResults.ConcurrentChange(),
            _ => null,
        });

        private static SubscriberResponse ToResponse(SubscriberView v)
            => new(v.Id, v.Source, v.ExternalId, v.Name, v.Locale, v.TimeZone, v.MergedInto,
                v.Aliases.Select(a => new AliasResponse(a.Source, a.ExternalId, a.LinkedBy, a.LinkedAt)).ToList(), v.CreatedAt);
    }
}
