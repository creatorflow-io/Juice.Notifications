using System.Text.Json;

using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Inbox
{
    // The signed-in person's inbox (design §7.3, phase-2-plan M15, I25): their items by group, newest first; unread and open counts; read,
    // unread, read all and archive. Every change is the person's own items only.

    public sealed record InboxItemView(Guid Id, string Type, string Group, string Title, string Body, string? Url, JsonElement? Actions, bool Actionable,
        NotificationPriority Priority, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, DateTimeOffset? ArchivedAt, InboxCorrelationView? Correlation = null,
        IReadOnlyList<InboxInteractionView>? Interactions = null);

    /// <summary>What the person did with the item (phase-3-plan I36): <c>read</c>, <c>acknowledged</c>, or <c>action</c> with its id.</summary>
    public sealed record InboxInteractionView(string Kind, string? ActionId, DateTimeOffset At);

    public static class InteractionKinds
    {
        public const string Read = "read";
        public const string Acknowledged = "acknowledged";
        public const string Action = "action";
    }

    /// <summary>
    /// The item's correlation as every recipient sees it (design §7.3, phase-3-plan I35): open items wait for someone; a resolved one shows its
    /// outcome wherever the decision was made.
    /// </summary>
    public sealed record InboxCorrelationView(CorrelationState State, string? Outcome, string? AcknowledgedBy, DateTimeOffset? ResolvedAt);

    /// <summary>A group with the person's open (not archived) and unread items in it.</summary>
    public sealed record InboxGroupCount(string Key, string Name, int Position, int Open, int Unread);

    /// <summary>
    /// The person's counts: per group of the tenant's catalog, in its order; unread in all; and actionable and open (§7.3: what the default
    /// <c>approval</c> group shows, wherever the tenant moved those types).
    /// </summary>
    public sealed record InboxCounts(IReadOnlyList<InboxGroupCount> Groups, int Unread, int ActionableOpen);

    public static class InboxRules
    {
        public const int MaxPage = 100;
    }

    /// <summary>
    /// Changes the person's own items (theirs and those of identities linked into them): read, unread, read all, archive and restore. Not mediator
    /// commands: items aren't configuration (nothing to audit), each change is one statement, and a restore takes the catalog lock in its own
    /// transaction, which a command's handler can't (its transaction opens after it runs).
    /// </summary>
    public sealed class PersonInboxEditor(NotificationsDbContext db, TimeProvider clock, Events.ProducerEvents? events = null)
    {
        private static Outcome<InboxItemView> Missing() => Outcome<InboxItemView>.NotFound("There is no such item in your inbox.");

        /// <summary>Marks the item read (its time kept when it was read already) or unread. The first read is an interaction the producer hears.</summary>
        public async Task<Outcome<InboxItemView>> MarkReadAsync(Guid tenantId, Guid subscriberId, Guid itemId, bool read, CancellationToken ct)
        {
            var self = await People.DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            var now = clock.GetUtcNow();
            var changed = await db.InboxItems.Where(i => i.TenantId == tenantId && self.Contains(i.SubscriberId) && i.Id == itemId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.ReadAt, i => read ? i.ReadAt ?? now : null), ct);
            if (changed == 0)
            {
                return Missing();
            }
            if (read)
            {
                await RecordAsync(tenantId, await db.InboxItems.AsNoTracking().SingleAsync(i => i.TenantId == tenantId && i.Id == itemId, ct),
                    InteractionKinds.Read, "", now, ct);
            }
            return await ViewAsync(tenantId, itemId, ct);
        }

        /// <summary>
        /// The person chose one of the item's actions (design §7.3, §19 decision 13): an interaction, and <c>notification.interaction</c> for the
        /// producer, who decides; the inbox decides nothing. Once per person, item and action; it reads the item. Refused once the correlation
        /// is resolved or cancelled.
        /// </summary>
        public async Task<Outcome<InboxItemView>> ActAsync(Guid tenantId, Guid subscriberId, Guid itemId, string actionId, CancellationToken ct)
            => await InteractAsync(tenantId, subscriberId, itemId, InteractionKinds.Action, actionId, ct);

        /// <summary>The person acknowledged the item: an interaction the producer hears (it may acknowledge the correlation in turn).</summary>
        public async Task<Outcome<InboxItemView>> AcknowledgeAsync(Guid tenantId, Guid subscriberId, Guid itemId, CancellationToken ct)
            => await InteractAsync(tenantId, subscriberId, itemId, InteractionKinds.Acknowledged, "", ct);

        private async Task<Outcome<InboxItemView>> InteractAsync(Guid tenantId, Guid subscriberId, Guid itemId, string kind, string actionId, CancellationToken ct)
        {
            var self = await People.DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            var item = await db.InboxItems.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == tenantId && self.Contains(i.SubscriberId) && i.Id == itemId, ct);
            if (item is null)
            {
                return Missing();
            }
            if (kind == InteractionKinds.Action && !ActionIds(item.Actions).Contains(actionId))
            {
                return Outcome<InboxItemView>.NotFound($"The item has no action {actionId}.");
            }
            if (item.CorrelationId is { } correlationId
                && await db.Correlations.AsNoTracking().Where(c => c.TenantId == tenantId && c.Id == correlationId).Select(c => c.State).SingleAsync(ct)
                    is CorrelationState.Resolved or CorrelationState.Cancelled)
            {
                return Outcome<InboxItemView>.Conflict(ErrorCodes.CorrelationClosed, "It has been decided already: the item's correlation is closed.");
            }
            var now = clock.GetUtcNow();
            await RecordAsync(tenantId, item, kind, actionId, now, ct);
            // Acting on it reads it, and that is its first read if nothing read it before.
            if (await db.InboxItems.Where(i => i.TenantId == tenantId && i.Id == itemId && i.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(i => i.ReadAt, now), ct) > 0)
            {
                await RecordAsync(tenantId, item, InteractionKinds.Read, "", now, ct);
            }
            return await ViewAsync(tenantId, itemId, ct);
        }

        private static HashSet<string> ActionIds(string? actions)
        {
            if (actions is null)
            {
                return [];
            }
            using var document = JsonDocument.Parse(actions);
            return document.RootElement.EnumerateArray().Where(a => a.TryGetProperty("id", out _)).Select(a => a.GetProperty("id").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>
        /// Records the interaction once (a repeat writes nothing) and, the first time, raises <c>notification.interaction</c> in the same transaction
        /// (phase-3-plan I36, I37).
        /// </summary>
        private async Task RecordAsync(Guid tenantId, InboxItem item, string kind, string actionId, DateTimeOffset now, CancellationToken ct)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var id = Guid.CreateVersion7();
            var written = await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.interactions (tenant_id, id, inbox_item_id, delivery_id, correlation_id, subscriber_id, kind, action_id, at)
                VALUES ({tenantId}, {id}, {item.Id}, {item.DeliveryId}, {item.CorrelationId}, {item.SubscriberId}, {kind}, {actionId}, {now})
                ON CONFLICT (tenant_id, inbox_item_id, kind, action_id) DO NOTHING
                """, ct);
            if (written > 0 && events is { Enabled: true })
            {
                var request = await (from i in db.DeliveryItems
                                     join r in db.Requests on new { i.TenantId, Id = i.RequestId } equals new { r.TenantId, r.Id }
                                     join x in db.Recipients on new { i.TenantId, Id = i.RecipientId } equals new { x.TenantId, Id = x.Id }
                                     where i.TenantId == tenantId && i.DeliveryId == item.DeliveryId
                                     select new { Request = r, x.NotificationId }).AsNoTracking().FirstAsync(ct);
                await events.RaiseAsync(db, tenantId, request.Request.ApplicationId, app => new Events.NotificationEvent
                {
                    Event = Events.ProducerEventNames.Interaction, Application = app, NotificationTenantId = tenantId, CorrelationKey = request.Request.CorrelationKey,
                    NotificationId = request.NotificationId, RequestId = request.Request.Id, Type = request.Request.TypeKey, ItemId = item.Id,
                    SubscriberId = item.SubscriberId, Kind = kind, ActionId = actionId.Length > 0 ? actionId : null, OccurredAt = now,
                }, ct);
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }

        /// <summary>Marks every unread open item read, in one group or all; the answer is how many.</summary>
        public async Task<Outcome<int>> MarkAllReadAsync(Guid tenantId, Guid subscriberId, string? group, CancellationToken ct)
        {
            var now = clock.GetUtcNow();
            var self = await People.DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            return Outcome<int>.Ok(await db.InboxItems
                .Where(i => i.TenantId == tenantId && self.Contains(i.SubscriberId) && i.ReadAt == null && i.ArchivedAt == null && (group == null || i.GroupKey == group))
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.ReadAt, now), ct));
        }

        /// <summary>
        /// Archives the item (which reads it), or restores it into the group its type goes to now (it may have moved meanwhile), under the
        /// catalog lock shared as an item's write is, so a move or a group's delete can't run between choosing the group and saving it.
        /// </summary>
        public async Task<Outcome<InboxItemView>> ArchiveAsync(Guid tenantId, Guid subscriberId, Guid itemId, bool archived, CancellationToken ct)
        {
            var self = await People.DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            var now = clock.GetUtcNow();
            int changed;
            if (archived)
            {
                changed = await db.InboxItems.Where(i => i.TenantId == tenantId && self.Contains(i.SubscriberId) && i.Id == itemId)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.ArchivedAt, i => i.ArchivedAt ?? now).SetProperty(i => i.ReadAt, i => i.ReadAt ?? now), ct);
            }
            else
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await DisplayGroupCatalog.LockAsync(db, tenantId, shared: true, ct);
                var selfIds = self.ToArray();
                changed = await db.Database.ExecuteSqlAsync($"""
                    UPDATE notifications.inbox_items AS i
                    SET group_key = CASE WHEN i.archived_at IS NULL THEN i.group_key ELSE notifications.inbox_group(i.tenant_id, i.type_key) END,
                        archived_at = NULL
                    WHERE i.tenant_id = {tenantId} AND i.subscriber_id = ANY({selfIds}) AND i.id = {itemId}
                    """, ct);
                await transaction.CommitAsync(ct);
            }
            return changed == 0 ? Missing() : await ViewAsync(tenantId, itemId, ct);
        }

        private async Task<Outcome<InboxItemView>> ViewAsync(Guid tenantId, Guid itemId, CancellationToken ct)
            => Outcome<InboxItemView>.Ok((await PersonInboxReader.ViewsAsync(db, db.InboxItems.Where(i => i.TenantId == tenantId && i.Id == itemId), ct)).Single());
    }

    public sealed class PersonInboxReader(NotificationsDbContext db, DisplayGroupCatalog catalog, TimeProvider clock)
    {
        internal static InboxItemView View(InboxItem i, Correlation? c, IReadOnlyList<InboxInteractionView>? interactions = null)
            => new(i.Id, i.TypeKey, i.GroupKey, i.Title, i.Body, i.Url, i.Actions is null ? null : JsonElement.Parse(i.Actions), i.Actionable, i.Priority,
                i.CreatedAt, i.ReadAt, i.ArchivedAt, c is null ? null : new InboxCorrelationView(c.State, c.Outcome, c.AcknowledgedBy, c.ResolvedAt),
                interactions ?? []);

        /// <summary>The items, with their correlations, newest first.</summary>
        internal static async Task<List<InboxItemView>> ViewsAsync(NotificationsDbContext db, IQueryable<InboxItem> items, CancellationToken ct)
        {
            var rows = await (from i in items
                              join c in db.Correlations on new { i.TenantId, Id = i.CorrelationId } equals new { c.TenantId, Id = (Guid?)c.Id } into correlations
                              from c in correlations.DefaultIfEmpty()
                              orderby i.Id descending
                              select new { Item = i, Correlation = c }).AsNoTracking().ToListAsync(ct);
            if (rows.Count == 0)
            {
                return [];
            }
            var tenant = rows[0].Item.TenantId;
            var ids = rows.Select(r => r.Item.Id).ToList();
            var interactions = (await db.Interactions.AsNoTracking().Where(x => x.TenantId == tenant && ids.Contains(x.InboxItemId)).OrderBy(x => x.At)
                    .Select(x => new { x.InboxItemId, x.Kind, x.ActionId, x.At }).ToListAsync(ct))
                .ToLookup(x => x.InboxItemId, x => new InboxInteractionView(x.Kind, x.ActionId.Length > 0 ? x.ActionId : null, x.At));
            return rows.Select(x => View(x.Item, x.Correlation, interactions[x.Item.Id].ToList())).ToList();
        }

        /// <summary>Actionable and open (design §7.3): actionable, and its correlation (if any) still open or acknowledged.</summary>
        private IQueryable<InboxItem> ActionableOpen(IQueryable<InboxItem> items)
            => items.Where(i => i.Actionable && (i.CorrelationId == null || db.Correlations.Any(c => c.TenantId == i.TenantId && c.Id == i.CorrelationId
                && (c.State == CorrelationState.Open || c.State == CorrelationState.Acknowledged))));

        /// <summary>
        /// The person's items, newest first: open ones (or archived ones), in one group or all, only unread or only actionable if asked; a page of
        /// <paramref name="limit"/> before the item <paramref name="before"/>.
        /// </summary>
        public async Task<IReadOnlyList<InboxItemView>> ListAsync(Guid tenantId, Guid subscriberId, string? group, bool unreadOnly, bool actionableOnly,
            bool archived, Guid? before, int limit, CancellationToken ct)
        {
            var self = await People.DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            var items = db.InboxItems.AsNoTracking().Where(i => i.TenantId == tenantId && self.Contains(i.SubscriberId)
                && (archived ? i.ArchivedAt != null : i.ArchivedAt == null));
            if (group is not null)
            {
                items = items.Where(i => i.GroupKey == group);
            }
            if (unreadOnly)
            {
                items = items.Where(i => i.ReadAt == null);
            }
            if (actionableOnly)
            {
                items = ActionableOpen(items);
            }
            if (before is { } id)
            {
                items = items.Where(i => i.Id < id);
            }
            return await ViewsAsync(db, items.OrderByDescending(i => i.Id).Take(Math.Clamp(limit, 1, InboxRules.MaxPage)), ct);
        }

        public async Task<InboxItemView?> GetAsync(Guid tenantId, Guid subscriberId, Guid itemId, CancellationToken ct)
        {
            var self = await People.DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            return (await ViewsAsync(db, db.InboxItems.Where(i => i.TenantId == tenantId && self.Contains(i.SubscriberId) && i.Id == itemId), ct)).SingleOrDefault();
        }

        public async Task<InboxCounts> CountsAsync(Guid tenantId, Guid subscriberId, CancellationToken ct)
        {
            await catalog.SeedAsync(db, tenantId, clock.GetUtcNow(), ct);
            var groups = await db.DisplayGroups.AsNoTracking().Where(g => g.TenantId == tenantId).OrderBy(g => g.Position).ThenBy(g => g.Key)
                .Select(g => new { g.Key, g.Name, g.Position }).ToListAsync(ct);
            var self = await People.DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            var counts = await db.InboxItems.AsNoTracking().Where(i => i.TenantId == tenantId && self.Contains(i.SubscriberId) && i.ArchivedAt == null)
                .GroupBy(i => i.GroupKey)
                .Select(g => new { Key = g.Key, Open = g.Count(), Unread = g.Count(i => i.ReadAt == null) })
                .ToListAsync(ct);
            var actionableOpen = await ActionableOpen(db.InboxItems.AsNoTracking().Where(i => i.TenantId == tenantId && self.Contains(i.SubscriberId) && i.ArchivedAt == null))
                .CountAsync(ct);
            var byKey = counts.ToDictionary(c => c.Key, StringComparer.Ordinal);
            return new InboxCounts(
                groups.Select(g => byKey.TryGetValue(g.Key, out var c) ? new InboxGroupCount(g.Key, g.Name, g.Position, c.Open, c.Unread)
                    : new InboxGroupCount(g.Key, g.Name, g.Position, 0, 0)).ToList(),
                counts.Sum(c => c.Unread), actionableOpen);
        }
    }
}
