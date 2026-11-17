using System.Text.Json;

using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Inbox
{
    // The signed-in person's inbox (design §7.3, phase-2-plan M15, I25): their items by group, newest first; unread and open counts; read,
    // unread, read all and archive. Every change is the person's own items only.

    public sealed record InboxItemView(Guid Id, string Type, string Group, string Title, string Body, string? Url, JsonElement? Actions, bool Actionable,
        NotificationPriority Priority, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, DateTimeOffset? ArchivedAt);

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
    public sealed class PersonInboxEditor(NotificationsDbContext db, TimeProvider clock)
    {
        private static Outcome<InboxItemView> Missing() => Outcome<InboxItemView>.NotFound("There is no such item in your inbox.");

        /// <summary>Marks the item read (its time kept when it was read already) or unread.</summary>
        public async Task<Outcome<InboxItemView>> MarkReadAsync(Guid tenantId, Guid subscriberId, Guid itemId, bool read, CancellationToken ct)
        {
            var self = await People.DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            var now = clock.GetUtcNow();
            var changed = await db.InboxItems.Where(i => i.TenantId == tenantId && self.Contains(i.SubscriberId) && i.Id == itemId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.ReadAt, i => read ? i.ReadAt ?? now : null), ct);
            return changed == 0 ? Missing() : await ViewAsync(tenantId, itemId, ct);
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
            => Outcome<InboxItemView>.Ok(PersonInboxReader.View(await db.InboxItems.AsNoTracking().SingleAsync(i => i.TenantId == tenantId && i.Id == itemId, ct)));
    }

    public sealed class PersonInboxReader(NotificationsDbContext db, DisplayGroupCatalog catalog, TimeProvider clock)
    {
        internal static InboxItemView View(InboxItem i)
            => new(i.Id, i.TypeKey, i.GroupKey, i.Title, i.Body, i.Url, i.Actions is null ? null : JsonElement.Parse(i.Actions), i.Actionable, i.Priority,
                i.CreatedAt, i.ReadAt, i.ArchivedAt);

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
                items = items.Where(i => i.Actionable);
            }
            if (before is { } id)
            {
                items = items.Where(i => i.Id < id);
            }
            var page = await items.OrderByDescending(i => i.Id).Take(Math.Clamp(limit, 1, InboxRules.MaxPage)).ToListAsync(ct);
            return page.Select(View).ToList();
        }

        public async Task<InboxItemView?> GetAsync(Guid tenantId, Guid subscriberId, Guid itemId, CancellationToken ct)
        {
            var self = await People.DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            return await db.InboxItems.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == tenantId && self.Contains(i.SubscriberId) && i.Id == itemId, ct)
                is { } item ? View(item) : null;
        }

        public async Task<InboxCounts> CountsAsync(Guid tenantId, Guid subscriberId, CancellationToken ct)
        {
            await catalog.SeedAsync(db, tenantId, clock.GetUtcNow(), ct);
            var groups = await db.DisplayGroups.AsNoTracking().Where(g => g.TenantId == tenantId).OrderBy(g => g.Position).ThenBy(g => g.Key)
                .Select(g => new { g.Key, g.Name, g.Position }).ToListAsync(ct);
            var self = await People.DirectoryReader.SelfAsync(db, tenantId, subscriberId, ct);
            var counts = await db.InboxItems.AsNoTracking().Where(i => i.TenantId == tenantId && self.Contains(i.SubscriberId) && i.ArchivedAt == null)
                .GroupBy(i => i.GroupKey)
                .Select(g => new { Key = g.Key, Open = g.Count(), Unread = g.Count(i => i.ReadAt == null), Actionable = g.Count(i => i.Actionable) })
                .ToListAsync(ct);
            var byKey = counts.ToDictionary(c => c.Key, StringComparer.Ordinal);
            return new InboxCounts(
                groups.Select(g => byKey.TryGetValue(g.Key, out var c) ? new InboxGroupCount(g.Key, g.Name, g.Position, c.Open, c.Unread)
                    : new InboxGroupCount(g.Key, g.Name, g.Position, 0, 0)).ToList(),
                counts.Sum(c => c.Unread), counts.Sum(c => c.Actionable));
        }
    }
}
