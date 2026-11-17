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

            // The tenant's mutes (design §6.4, phase-2-plan M14): one naming a mandatory type exactly holds it too, and says so.
            tenant.MapGet("/mutes", async (Guid tenantId, bool? all, Guid? before, int? limit, PreferenceReader reader, CancellationToken ct)
                => Results.Ok((await reader.MutesAsync(tenantId, null, all ?? false, before, limit ?? 50, ct)).Select(ToResponse)));
            tenant.MapPost("/mutes", (Guid tenantId, AddMuteBody body, HttpContext http, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new AddMute(tenantId, null, body.Match, body.StartsAt, body.EndsAt, body.Reason, NotificationsAudit.Caller(http.User)),
                    o => Results.Created($"/api/v1/tenants/{tenantId}/mutes/{o.Value!.Id}", ToResponse(o.Value)), PreferenceConflict, ct));
            tenant.MapDelete("/mutes/{id:guid}", (Guid tenantId, Guid id, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new CancelMute(tenantId, null, id), _ => Results.NoContent(), PreferenceConflict, ct));

            // Local users, groups and topics: what the local and topic resolvers read (phase-2-plan M11).
            tenant.MapPut("/local-users/{id}", (Guid tenantId, string id, PutLocalUserBody body, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new PutLocalUser(tenantId, id, body.Name, body.Locale, body.TimeZone),
                    outcome => Results.Ok(ToResponse(outcome.Value!)), DirectoryConflict, ct));

            tenant.MapGet("/groups", async (Guid tenantId, DirectoryReader reader, CancellationToken ct)
                => Results.Ok((await reader.GroupsAsync(tenantId, ct)).Select(ToResponse)));
            tenant.MapPut("/groups/{key}", (Guid tenantId, string key, PutGroupBody body, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new PutGroup(tenantId, key, body.Name),
                    outcome => Results.Ok(ToResponse(outcome.Value!)), DirectoryConflict, ct));
            tenant.MapDelete("/groups/{key}", (Guid tenantId, string key, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new DeleteGroup(tenantId, key), _ => Results.NoContent(), DirectoryConflict, ct));
            tenant.MapGet("/groups/{key}/members", async (Guid tenantId, string key, DirectoryReader reader, CancellationToken ct)
                => await reader.MembersAsync(tenantId, key, ct) is { } members ? Results.Ok(members.Select(ToResponse)) : ApiResults.NotFound("No such group."));
            tenant.MapPut("/groups/{key}/members/{subscriberId:guid}", (Guid tenantId, string key, Guid subscriberId, IMediator mediator, NotificationsDbContext db,
                CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new AddGroupMember(tenantId, key, subscriberId), _ => Results.NoContent(), DirectoryConflict, ct));
            tenant.MapDelete("/groups/{key}/members/{subscriberId:guid}", (Guid tenantId, string key, Guid subscriberId, IMediator mediator,
                NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new RemoveGroupMember(tenantId, key, subscriberId), _ => Results.NoContent(), RemoveConflict, ct));

            tenant.MapGet("/topics", async (Guid tenantId, DirectoryReader reader, CancellationToken ct)
                => Results.Ok((await reader.TopicsAsync(tenantId, ct)).Select(ToResponse)));
            tenant.MapPut("/topics/{key}", (Guid tenantId, string key, PutTopicBody body, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new PutTopic(tenantId, key, body.Name, body.SelfService), outcome => Results.Ok(ToResponse(outcome.Value!)),
                    DirectoryConflict, ct));
            tenant.MapDelete("/topics/{key}", (Guid tenantId, string key, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new DeleteTopic(tenantId, key), _ => Results.NoContent(), DirectoryConflict, ct));
            tenant.MapPut("/topics/{key}/subscribers/{subscriberId:guid}", (Guid tenantId, string key, Guid subscriberId, IMediator mediator,
                NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new Subscribe(tenantId, key, subscriberId, BySelf: false), _ => Results.NoContent(), DirectoryConflict, ct));
            tenant.MapDelete("/topics/{key}/subscribers/{subscriberId:guid}", (Guid tenantId, string key, Guid subscriberId, IMediator mediator,
                NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new Unsubscribe(tenantId, key, subscriberId), _ => Results.NoContent(), RemoveConflict, ct));
        }

        /// <summary>
        /// A preference, quiet hours or mute change that raced another: the same preference saved twice at once, or the person linked elsewhere
        /// meanwhile.
        /// </summary>
        internal static Task<IResult?> PreferenceConflict(DbConflict conflict) => Task.FromResult<IResult?>(conflict switch
        {
            { Kind: ConflictKind.Unique, Constraint: Constraints.PreferenceKey } => ApiResults.ConcurrentChange(),
            { Kind: ConflictKind.ForeignKey, Constraint: Constraints.PreferenceSubscriber or Constraints.MuteSubscriber } => ApiResults.ConcurrentChange(),
            { Kind: ConflictKind.Aborted or ConflictKind.RowGone } => ApiResults.ConcurrentChange(),
            _ => null,
        });

        internal static MuteResponse ToResponse(MuteView m)
            => new(m.Id, m.Match, m.StartsAt, m.EndsAt, m.Reason, m.CreatedBy, m.CreatedAt, m.CancelledAt, m.AppliesToMandatory);

        /// <summary>
        /// A removal that raced: the same membership or subscription removed by another call first is already what was asked (204), not a conflict.
        /// </summary>
        internal static Task<IResult?> RemoveConflict(DbConflict conflict)
            => conflict.Kind == ConflictKind.RowGone ? Task.FromResult<IResult?>(Results.NoContent()) : DirectoryConflict(conflict);

        /// <summary>
        /// A directory change that raced another: the same local user, group, topic, membership or subscription created twice at once, or a member
        /// or subscription added to a group or topic deleted at the same moment.
        /// </summary>
        internal static Task<IResult?> DirectoryConflict(DbConflict conflict) => Task.FromResult<IResult?>(conflict switch
        {
            { Kind: ConflictKind.Unique, Constraint: Constraints.SubscriberIdentity or Constraints.LocalGroupKey or Constraints.LocalGroupMember
                or Constraints.TopicKey or Constraints.TopicSubscription } => ApiResults.ConcurrentChange(),
            { Kind: ConflictKind.ForeignKey, Constraint: Constraints.LocalGroupMemberGroup or Constraints.TopicSubscriptionTopic } => ApiResults.ConcurrentChange(),
            { Kind: ConflictKind.Aborted or ConflictKind.RowGone } => ApiResults.ConcurrentChange(),
            _ => null,
        });

        private static GroupResponse ToResponse(GroupView g) => new(g.Key, g.Name, g.Members, g.UpdatedAt);

        private static TopicResponse ToResponse(TopicView t) => new(t.Key, t.Name, t.SelfService, t.Subscribers, t.UpdatedAt);

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
