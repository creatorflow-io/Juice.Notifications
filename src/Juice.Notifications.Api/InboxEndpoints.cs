using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Inbox;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// The tenant's display group catalog (design §7.3, D12, phase-2-plan M15). The person's inbox under <c>…/me/inbox</c> is in
    /// Juice.Notifications.Inbox (M54). A change to the catalog is told to the tenant's open sessions after it commits (I18).
    /// </summary>
    internal static class InboxEndpoints
    {
        public static void MapDisplayGroupEndpoints(this IEndpointRouteBuilder app)
        {
            var tenant = app.MapGroup("/api/v1/tenants/{tenantId:guid}").RequireTenantAdmin().WithTags("Display groups");
            tenant.MapGet("/display-groups", async (Guid tenantId, DisplayGroupReader reader, CancellationToken ct)
                => Results.Ok((await reader.ListAsync(tenantId, ct)).Select(ToResponse)));
            tenant.MapPut("/display-groups/{key}", (Guid tenantId, string key, PutDisplayGroupBody body, DisplayGroupEditor editor, NotificationsDbContext db,
                IInboxEvents events, CancellationToken ct)
                => CatalogAsync(db, events, tenantId, () => editor.PutAsync(tenantId, key, body.Name, body.Position, ct), group => Results.Ok(ToResponse(group)),
                    // A new group may take in items of types that declare it.
                    _ => true));
            tenant.MapDelete("/display-groups/{key}", (Guid tenantId, string key, DisplayGroupEditor editor, NotificationsDbContext db, IInboxEvents events,
                CancellationToken ct)
                => CatalogAsync(db, events, tenantId, () => editor.DeleteAsync(tenantId, key, ct), _ => Results.NoContent(), deleted => deleted));
            tenant.MapPut("/types/{typeKey}/display-group", (Guid tenantId, string typeKey, MoveTypeBody body, DisplayGroupEditor editor, NotificationsDbContext db,
                IInboxEvents events, CancellationToken ct)
                => CatalogAsync(db, events, tenantId, () => editor.MoveAsync(tenantId, typeKey, body.Group ?? "", ct), ToResponse, moved => moved.ItemsMoved > 0));
            tenant.MapDelete("/types/{typeKey}/display-group", (Guid tenantId, string typeKey, DisplayGroupEditor editor, NotificationsDbContext db,
                IInboxEvents events, CancellationToken ct)
                => CatalogAsync(db, events, tenantId, () => editor.MoveAsync(tenantId, typeKey, null, ct), ToResponse, moved => moved.ItemsMoved > 0));
        }

        /// <summary>
        /// A catalog change: its answer, or a lost race's (asking again gives the current state). When <paramref name="moved"/> says items may
        /// have moved, every session of the tenant reloads its counts: many people's items may be among them.
        /// </summary>
        private static async Task<IResult> CatalogAsync<T>(NotificationsDbContext db, IInboxEvents events, Guid tenantId, Func<Task<Outcome<T>>> change,
            Func<T, IResult> onSuccess, Func<T, bool> moved)
        {
            Outcome<T> outcome;
            try
            {
                outcome = await change();
            }
            catch (Exception ex) when (DbConflicts.Classify(ex) is { } conflict && conflict switch
            {
                { Kind: ConflictKind.Unique, Constraint: Constraints.DisplayGroupKey or Constraints.TypeDisplayGroupKey } => true,
                { Kind: ConflictKind.ForeignKey, Constraint: Constraints.TypeDisplayGroupGroup } => true,
                { Kind: ConflictKind.Aborted or ConflictKind.RowGone } => true,
                _ => false,
            })
            {
                db.ChangeTracker.Clear();
                return ApiResults.ConcurrentChange();
            }
            if (ApiResults.Failure(outcome) is { } failure)
            {
                return failure;
            }
            if (moved(outcome.Value!))
            {
                await events.PublishAsync(new InboxChange(tenantId, Guid.Empty, InboxChange.Reset, []), CancellationToken.None);
            }
            return onSuccess(outcome.Value!);
        }

        private static DisplayGroupResponse ToResponse(DisplayGroupView g) => new(g.Key, g.Name, g.Position, g.BuiltIn, g.Types, g.UpdatedAt);

        private static IResult ToResponse(MovedType m) => Results.Ok(new TypeDisplayGroupResponse(m.TypeKey, m.GroupKey, m.Chosen, m.ItemsMoved));
    }
}
