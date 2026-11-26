using Juice.MediatR;
using Juice.Notifications.Admin;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.People;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// The tenant's mutes (design §6.4, phase-2-plan M14), moved out of the people endpoints (M44-3) so a host without people has them: one
    /// naming a mandatory type exactly holds it too, and says so.
    /// </summary>
    internal static class MuteEndpoints
    {
        public static void MapMuteEndpoints(this IEndpointRouteBuilder tenant)
        {
            var mutes = tenant.MapGroup("").WithTags("People").WithMetadata(new AdminPageMetadata(AdminPages.Mutes));
            mutes.MapGet("/mutes", async (Guid tenantId, bool? all, Guid? before, int? limit, PreferenceReader reader, CancellationToken ct)
                => Results.Ok((await reader.MutesAsync(tenantId, null, all ?? false, before, limit ?? 50, ct)).Select(ToResponse)));
            mutes.MapPost("/mutes", (Guid tenantId, AddMuteBody body, HttpContext http, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new AddMute(tenantId, null, body.Match, body.StartsAt, body.EndsAt, body.Reason, AdminCaller.Name(http.User)),
                    // Where it answers: under the host's prefix (and path base), as the request came.
                    o => Results.Created($"{http.Request.PathBase}{http.Request.Path.Value!.TrimEnd('/')}/{o.Value!.Id}", ToResponse(o.Value)), PreferenceConflict, ct));
            mutes.MapDelete("/mutes/{id:guid}", (Guid tenantId, Guid id, IMediator mediator, NotificationsDbContext db, CancellationToken ct)
                => ApiResults.SendAsync(mediator, db, new CancelMute(tenantId, null, id), _ => Results.NoContent(), PreferenceConflict, ct));
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
    }
}
