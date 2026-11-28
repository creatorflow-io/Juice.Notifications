using Juice.Notifications.Api;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;

namespace Juice.Notifications.Inbox
{
    /// <summary>
    /// The person's inbox under <c>…/me/inbox</c> (design §7.3, D12, phase-2-plan M15), moved here from the service so an embedded host can map it
    /// too (M54). A change is told to the open sessions it concerns after it commits (I18). <c>interactions: false</c> leaves out an item's actions and
    /// its acknowledgement, for a host in which nothing hears them.
    /// </summary>
    internal static class InboxEndpoints
    {
        public static void MapInboxEndpoints(this RouteGroupBuilder me, bool interactions)
        {
            me.MapGet("/inbox", async (Guid tenantId, string? group, bool? unread, bool? actionable, bool? archived, Guid? before, int? limit,
                HttpContext http, PersonInboxReader reader, CancellationToken ct)
                => Results.Ok((await reader.ListAsync(tenantId, Me(http), group, unread ?? false, actionable ?? false, archived ?? false, before,
                    limit ?? 50, ct)).Select(ToResponse)));
            me.MapGet("/inbox/counts", async (Guid tenantId, HttpContext http, PersonInboxReader reader, CancellationToken ct)
                => Results.Ok(ToResponse(await reader.CountsAsync(tenantId, Me(http), ct))));
            me.MapGet("/inbox/{id:guid}", async (Guid tenantId, Guid id, HttpContext http, PersonInboxReader reader, CancellationToken ct)
                => await reader.GetAsync(tenantId, Me(http), id, ct) is { } item ? Results.Ok(ToResponse(item)) : ApiResults.NotFound("There is no such item in your inbox."));
            me.MapPost("/inbox/{id:guid}/read", (Guid tenantId, Guid id, HttpContext http, PersonInboxEditor editor, IInboxEvents events, CancellationToken ct)
                => ChangedAsync(tenantId, http, events, editor.MarkReadAsync(tenantId, Me(http), id, true, ct)));
            me.MapDelete("/inbox/{id:guid}/read", (Guid tenantId, Guid id, HttpContext http, PersonInboxEditor editor, IInboxEvents events, CancellationToken ct)
                => ChangedAsync(tenantId, http, events, editor.MarkReadAsync(tenantId, Me(http), id, false, ct)));
            me.MapPost("/inbox/{id:guid}/archive", (Guid tenantId, Guid id, HttpContext http, PersonInboxEditor editor, IInboxEvents events, CancellationToken ct)
                => ChangedAsync(tenantId, http, events, editor.ArchiveAsync(tenantId, Me(http), id, true, ct)));
            me.MapDelete("/inbox/{id:guid}/archive", (Guid tenantId, Guid id, HttpContext http, PersonInboxEditor editor, IInboxEvents events, CancellationToken ct)
                => ChangedAsync(tenantId, http, events, editor.ArchiveAsync(tenantId, Me(http), id, false, ct)));
            if (interactions)
            {
                // An item's action, or its acknowledgement: interactions the producer hears (phase-3-plan M23, design §19 decision 13).
                me.MapPost("/inbox/{id:guid}/actions/{actionId}", (Guid tenantId, Guid id, string actionId, HttpContext http, PersonInboxEditor editor, IInboxEvents events,
                    CancellationToken ct)
                    => ChangedAsync(tenantId, http, events, editor.ActAsync(tenantId, Me(http), id, actionId, ct)));
                me.MapPost("/inbox/{id:guid}/acknowledge", (Guid tenantId, Guid id, HttpContext http, PersonInboxEditor editor, IInboxEvents events, CancellationToken ct)
                    => ChangedAsync(tenantId, http, events, editor.AcknowledgeAsync(tenantId, Me(http), id, ct)));
            }
            me.MapPost("/inbox/read-all", async (Guid tenantId, string? group, HttpContext http, PersonInboxEditor editor, IInboxEvents events, CancellationToken ct) =>
            {
                var person = Me(http);
                var marked = await editor.MarkAllReadAsync(tenantId, person, group, ct);
                if (marked.Value > 0)
                {
                    await events.PublishAsync(new InboxChange(tenantId, person, InboxChange.Reset, []), CancellationToken.None);
                }
                return Results.Ok(new ReadAllResponse(marked.Value));
            });
        }

        /// <summary>The signed-in person's subscriber id, which the group's filter found.</summary>
        internal static Guid Me(HttpContext http) => (Guid)http.Items[NotificationsInboxRegistration.MeKey]!;

        /// <summary>One of the person's items changed: the answer, and their sessions told once it is committed.</summary>
        private static async Task<IResult> ChangedAsync(Guid tenantId, HttpContext http, IInboxEvents events, Task<Outcome<InboxItemView>> change)
        {
            var outcome = await change;
            if (ApiResults.Failure(outcome) is { } failure)
            {
                return failure;
            }
            await events.PublishAsync(new InboxChange(tenantId, Me(http), InboxChange.Changed, [outcome.Value!.Id]), CancellationToken.None);
            return Results.Ok(ToResponse(outcome.Value));
        }

        internal static InboxItemResponse ToResponse(InboxItemView i)
            => new(i.Id, i.Type, i.Group, i.Title, i.Body, i.Url, i.Actions, i.Actionable, i.Priority, i.CreatedAt, i.ReadAt, i.ArchivedAt,
                i.Correlation is { } c ? new InboxCorrelationResponse(c.State, c.Outcome, c.AcknowledgedBy, c.ResolvedAt) : null,
                i.Interactions?.Select(x => new InboxInteractionResponse(x.Kind, x.ActionId, x.At)).ToList() ?? []);

        private static InboxCountsResponse ToResponse(InboxCounts c)
            => new(c.Groups.Select(g => new InboxGroupCountResponse(g.Key, g.Name, g.Position, g.Open, g.Unread)).ToList(), c.Unread, c.ActionableOpen);
    }
}
