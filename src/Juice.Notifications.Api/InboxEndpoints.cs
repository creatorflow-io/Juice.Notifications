using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Inbox;

namespace Juice.Notifications.Api
{
    /// <summary>
    /// The person's inbox under <c>…/me/inbox</c>, and the tenant's display group catalog (design §7.3, D12, phase-2-plan M15). A change is told
    /// to the open sessions it concerns after it commits (I18).
    /// </summary>
    internal static class InboxEndpoints
    {
        public static void MapInboxEndpoints(this RouteGroupBuilder me)
        {
            me.MapGet("/inbox", async (Guid tenantId, string? group, bool? unread, bool? actionable, bool? archived, Guid? before, int? limit,
                HttpContext http, PersonInboxReader reader, CancellationToken ct)
                => Results.Ok((await reader.ListAsync(tenantId, MeEndpoints.Me(http), group, unread ?? false, actionable ?? false, archived ?? false, before,
                    limit ?? 50, ct)).Select(ToResponse)));
            me.MapGet("/inbox/counts", async (Guid tenantId, HttpContext http, PersonInboxReader reader, CancellationToken ct)
                => Results.Ok(ToResponse(await reader.CountsAsync(tenantId, MeEndpoints.Me(http), ct))));
            me.MapGet("/inbox/{id:guid}", async (Guid tenantId, Guid id, HttpContext http, PersonInboxReader reader, CancellationToken ct)
                => await reader.GetAsync(tenantId, MeEndpoints.Me(http), id, ct) is { } item ? Results.Ok(ToResponse(item)) : ApiResults.NotFound("There is no such item in your inbox."));
            me.MapPost("/inbox/{id:guid}/read", (Guid tenantId, Guid id, HttpContext http, PersonInboxEditor editor, IInboxEvents events, CancellationToken ct)
                => ChangedAsync(tenantId, http, events, editor.MarkReadAsync(tenantId, MeEndpoints.Me(http), id, true, ct)));
            me.MapDelete("/inbox/{id:guid}/read", (Guid tenantId, Guid id, HttpContext http, PersonInboxEditor editor, IInboxEvents events, CancellationToken ct)
                => ChangedAsync(tenantId, http, events, editor.MarkReadAsync(tenantId, MeEndpoints.Me(http), id, false, ct)));
            me.MapPost("/inbox/{id:guid}/archive", (Guid tenantId, Guid id, HttpContext http, PersonInboxEditor editor, IInboxEvents events, CancellationToken ct)
                => ChangedAsync(tenantId, http, events, editor.ArchiveAsync(tenantId, MeEndpoints.Me(http), id, true, ct)));
            me.MapDelete("/inbox/{id:guid}/archive", (Guid tenantId, Guid id, HttpContext http, PersonInboxEditor editor, IInboxEvents events, CancellationToken ct)
                => ChangedAsync(tenantId, http, events, editor.ArchiveAsync(tenantId, MeEndpoints.Me(http), id, false, ct)));
            // An item's action, or its acknowledgement: interactions the producer hears (phase-3-plan M23, design §19 decision 13).
            me.MapPost("/inbox/{id:guid}/actions/{actionId}", (Guid tenantId, Guid id, string actionId, HttpContext http, PersonInboxEditor editor, IInboxEvents events,
                CancellationToken ct)
                => ChangedAsync(tenantId, http, events, editor.ActAsync(tenantId, MeEndpoints.Me(http), id, actionId, ct)));
            me.MapPost("/inbox/{id:guid}/acknowledge", (Guid tenantId, Guid id, HttpContext http, PersonInboxEditor editor, IInboxEvents events, CancellationToken ct)
                => ChangedAsync(tenantId, http, events, editor.AcknowledgeAsync(tenantId, MeEndpoints.Me(http), id, ct)));
            me.MapPost("/inbox/read-all", async (Guid tenantId, string? group, HttpContext http, PersonInboxEditor editor, IInboxEvents events, CancellationToken ct) =>
            {
                var person = MeEndpoints.Me(http);
                var marked = await editor.MarkAllReadAsync(tenantId, person, group, ct);
                if (marked.Value > 0)
                {
                    await events.PublishAsync(new InboxChange(tenantId, person, InboxChange.Reset, []), CancellationToken.None);
                }
                return Results.Ok(new ReadAllResponse(marked.Value));
            });
        }

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

        /// <summary>One of the person's items changed: the answer, and their sessions told once it is committed.</summary>
        private static async Task<IResult> ChangedAsync(Guid tenantId, HttpContext http, IInboxEvents events, Task<Outcome<InboxItemView>> change)
        {
            var outcome = await change;
            if (ApiResults.Failure(outcome) is { } failure)
            {
                return failure;
            }
            await events.PublishAsync(new InboxChange(tenantId, MeEndpoints.Me(http), InboxChange.Changed, [outcome.Value!.Id]), CancellationToken.None);
            return Results.Ok(ToResponse(outcome.Value));
        }

        internal static InboxItemResponse ToResponse(InboxItemView i)
            => new(i.Id, i.Type, i.Group, i.Title, i.Body, i.Url, i.Actions, i.Actionable, i.Priority, i.CreatedAt, i.ReadAt, i.ArchivedAt,
                i.Correlation is { } c ? new InboxCorrelationResponse(c.State, c.Outcome, c.AcknowledgedBy, c.ResolvedAt) : null,
                i.Interactions?.Select(x => new InboxInteractionResponse(x.Kind, x.ActionId, x.At)).ToList() ?? []);

        private static InboxCountsResponse ToResponse(InboxCounts c)
            => new(c.Groups.Select(g => new InboxGroupCountResponse(g.Key, g.Name, g.Position, g.Open, g.Unread)).ToList(), c.Unread, c.ActionableOpen);

        private static DisplayGroupResponse ToResponse(DisplayGroupView g) => new(g.Key, g.Name, g.Position, g.BuiltIn, g.Types, g.UpdatedAt);

        private static IResult ToResponse(MovedType m) => Results.Ok(new TypeDisplayGroupResponse(m.TypeKey, m.GroupKey, m.Chosen, m.ItemsMoved));
    }
}
