using System.Collections.Concurrent;
using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Inbox
{
    // The in-app inbox (design §7.3, §9.1, phase-2-plan M15, I17, I18, I24): a channel the core hosts. A person recipient always has an inbox
    // destination; the inbox provider writes one item per delivery; the person reads, archives and counts their items through `me/inbox`, and
    // a SignalR hub tells their open sessions what changed, after it is committed.

    /// <summary>
    /// The tenant's own inbox integration (phase-2-plan I17): every delivery names an integration, so the inbox has one per tenant, created on first
    /// use and owned by the service (<see cref="ManagedBy.Service"/>): the admin API shows it but neither changes nor deletes it. Its key has a
    /// dot, which no key the API accepts has, so it can't collide with a tenant's own integration. Read each time (one indexed row), so a row
    /// removed outside the API (a restore) is simply made again.
    /// </summary>
    public sealed class InboxIntegrations(TimeProvider? clock = null)
    {
        public const string Key = "notifications.inbox";
        public const string Kind = "inbox";

        private const string NoSettings = "{}";

        /// <summary>The tenant's inbox integration, inserted if it isn't there (safe under concurrency: one row per tenant and key).</summary>
        public async Task<Integration> EnsureAsync(NotificationsDbContext db, Guid tenantId, CancellationToken ct)
        {
            if (await db.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Key == Key, ct) is { } found)
            {
                return found;
            }
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.integrations (tenant_id, id, key, channel, kind, settings, secret_ref, priority, enabled, managed_by, definition_hash, updated_at)
                VALUES ({tenantId}, {Guid.CreateVersion7()}, {Key}, {Channels.Inbox}, {Kind}, {NoSettings}::jsonb, NULL, 0, TRUE, {ManagedBy.Service}, {Kind}, {now})
                ON CONFLICT (tenant_id, key) DO NOTHING
                """, ct);
            return await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == tenantId && i.Key == Key, ct);
        }
    }

    /// <summary>
    /// A tenant's display group catalog (design D12, phase-2-plan I24): seeded with the three built-ins on first use (remembered per tenant in this
    /// process: built-ins are never deleted), and where a type's items go, by the database's <c>notifications.inbox_group</c> (<see cref="InboxSql"/>).
    /// Writing or restoring an item takes the tenant's catalog lock shared; adding a group, moving a type or deleting a group takes it exclusive
    /// (<see cref="DisplayGroupEditor"/>), so an item is never written into a group a change has just left or removed.
    /// </summary>
    public sealed class DisplayGroupCatalog
    {
        private readonly ConcurrentDictionary<Guid, bool> _seeded = new();

        /// <summary>The advisory lock's first key: the catalog's, apart from any other lock the service may take.</summary>
        private const int LockSpace = 0x1B0C;

        /// <summary>The tenant's catalog lock, until the transaction ends (one must be open).</summary>
        public static async Task LockAsync(NotificationsDbContext db, Guid tenantId, bool shared, CancellationToken ct)
        {
            if (db.Database.CurrentTransaction is null)
            {
                throw new InvalidOperationException("The catalog lock lasts until its transaction ends: begin one first.");
            }
            var key = tenantId.ToString();
            await (shared
                ? db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock_shared({LockSpace}, hashtext({key}))", ct)
                : db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockSpace}, hashtext({key}))", ct));
        }

        /// <summary>Inserts the built-in groups the tenant lacks, once per tenant in this process. Call it outside a transaction.</summary>
        public async Task SeedAsync(NotificationsDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken ct)
        {
            if (_seeded.ContainsKey(tenantId))
            {
                return;
            }
            var keys = DisplayGroups.BuiltIn.Select(g => g.Key).ToArray();
            var names = DisplayGroups.BuiltIn.Select(g => g.Name).ToArray();
            var ids = keys.Select(_ => Guid.CreateVersion7()).ToArray();
            var positions = Enumerable.Range(0, keys.Length).Select(i => (i + 1) * 10).ToArray();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.display_groups (tenant_id, id, key, name, position, built_in, updated_at)
                SELECT {tenantId}, u.id, u.key, u.name, u.position, TRUE, {now}
                FROM unnest({ids}::uuid[], {keys}::text[], {names}::text[], {positions}::int[]) AS u(id, key, name, position)
                ON CONFLICT (tenant_id, key) DO NOTHING
                """, ct);
            // Remembered only once committed: a seed inside a transaction that rolls back is made again next time.
            if (db.Database.CurrentTransaction is null)
            {
                _seeded[tenantId] = true;
            }
        }

        /// <summary>Where a type's items go in the tenant now.</summary>
        public static Task<string> GroupForAsync(NotificationsDbContext db, Guid tenantId, string typeKey, CancellationToken ct)
            => db.Database.SqlQuery<string>($"SELECT notifications.inbox_group({tenantId}, {typeKey}) AS \"Value\"").SingleAsync(ct);

        /// <summary>
        /// Puts the tenant's open items in <paramref name="groupKey"/>, or of <paramref name="typeKey"/>, where their types go now, in one statement;
        /// the answer is how many moved.
        /// </summary>
        public static Task<int> RehomeAsync(NotificationsDbContext db, Guid tenantId, string? groupKey, string? typeKey, CancellationToken ct)
            => db.Database.ExecuteSqlAsync($"""
                UPDATE notifications.inbox_items AS i SET group_key = notifications.inbox_group(i.tenant_id, i.type_key)
                WHERE i.tenant_id = {tenantId} AND i.archived_at IS NULL
                  AND ({groupKey}::text IS NULL OR i.group_key = {groupKey}) AND ({typeKey}::text IS NULL OR i.type_key = {typeKey})
                  AND i.group_key IS DISTINCT FROM notifications.inbox_group(i.tenant_id, i.type_key)
                """, ct);
    }

    /// <summary>
    /// What changed in a person's inbox, for their open sessions (phase-2-plan I18): <c>added</c>, <c>changed</c> (read, unread, archived, moved)
    /// or <c>reset</c> (many at once: reload), and the items it was.
    /// </summary>
    public sealed record InboxChange(Guid TenantId, Guid SubscriberId, string Kind, IReadOnlyList<Guid> ItemIds)
    {
        public const string Added = "added";
        public const string Changed = "changed";
        public const string Reset = "reset";
    }

    /// <summary>
    /// Tells a person's open sessions what changed in their inbox, after it is committed: a client reloads on <c>reset</c> and on reconnect, so a
    /// lost message costs only a reload (phase-2-plan I18). The host's SignalR hub implements it; without one, nothing is told.
    /// </summary>
    public interface IInboxEvents
    {
        Task PublishAsync(InboxChange change, CancellationToken ct);
    }

    internal sealed class NoInboxEvents : IInboxEvents
    {
        public Task PublishAsync(InboxChange change, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// The inbox provider (phase-2-plan I17): "sending" writes the person's item, in its own transaction as any provider's send is its own, once
    /// per delivery (a repeated send finds it there and writes nothing), so it repeats safely. The group is resolved now (I24). A person removed
    /// since planning is a <see cref="FailureCategory.Destination"/> failure; nothing else fails but the database, which the dispatcher retries.
    /// </summary>
    public sealed class InboxProvider(IServiceScopeFactory scopes, IInboxEvents events, DisplayGroupCatalog catalog, ILogger<InboxProvider> logger,
        TimeProvider? clock = null)
        : INotificationProvider
    {
        public string Channel => Channels.Inbox;

        public string Kind => InboxIntegrations.Kind;

        public bool Idempotent => true;

        public bool RequiresSecret => false;

        public string? CheckSettings(string settings) => null;

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            if (!Guid.TryParse(delivery.Destination, out var subscriberId))
            {
                return SendResult.Permanent(FailureCategory.Destination, "The delivery names no person's inbox.");
            }
            InboxContent? content;
            try
            {
                content = JsonSerializer.Deserialize<InboxContent>(delivery.Content, Json);
            }
            catch (JsonException)
            {
                content = null;
            }
            if (content is null || string.IsNullOrEmpty(content.Type) || content.Title is null)
            {
                return SendResult.Permanent(FailureCategory.Content, "The delivery's content isn't an inbox item.");
            }
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                await catalog.SeedAsync(db, delivery.TenantId, now, ct);
                var actions = content.Actions is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0 ? a.GetRawText() : null;
                var itemId = Guid.CreateVersion7();
                var priority = delivery.Priority.ToString();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                // Shared: items are written side by side; a move or a group's delete waits for them, and they for it (phase-2-plan I24).
                await DisplayGroupCatalog.LockAsync(db, delivery.TenantId, shared: true, ct);
                var written = await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO notifications.inbox_items (tenant_id, id, subscriber_id, delivery_id, type_key, group_key, title, body, url, actions, actionable,
                        priority, created_at, read_at, archived_at, correlation_id)
                    VALUES ({delivery.TenantId}, {itemId}, {subscriberId}, {delivery.DeliveryId}, {content.Type},
                        notifications.inbox_group({delivery.TenantId}, {content.Type}),
                        {content.Title}, {content.Body ?? ""}, {content.Url}, {actions}::jsonb, {actions != null}, {priority}, {now}, NULL, NULL,
                        (SELECT r.correlation_id FROM notifications.delivery_items i
                         JOIN notifications.requests r ON r.tenant_id = i.tenant_id AND r.id = i.request_id
                         WHERE i.tenant_id = {delivery.TenantId} AND i.delivery_id = {delivery.DeliveryId} LIMIT 1))
                    ON CONFLICT (tenant_id, delivery_id) DO NOTHING
                    """, ct);
                await transaction.CommitAsync(ct);
                if (written > 0)
                {
                    await TellAsync(new InboxChange(delivery.TenantId, subscriberId, InboxChange.Added, [itemId]));
                }
                return SendResult.Delivered(id: written > 0 ? itemId.ToString() : null);
            }
            catch (Exception ex) when (DbConflicts.Classify(ex) is { Kind: ConflictKind.ForeignKey })
            {
                return SendResult.Permanent(FailureCategory.Destination, "The person was removed before the item was written.");
            }
        }

        private async Task TellAsync(InboxChange change)
        {
            try
            {
                await events.PublishAsync(change, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The item is written; a session that missed this reloads on reconnect.
                logger.LogWarning(ex, "Couldn't tell person {Subscriber}'s sessions about a new inbox item", change.SubscriberId);
            }
        }

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    }

    public static class InboxRegistration
    {
        /// <summary>The tenants' inbox integrations and display group catalog (one per host: it remembers which tenants are seeded).</summary>
        public static IServiceCollection AddInboxIntegrations(this IServiceCollection services)
        {
            services.TryAddSingleton<InboxIntegrations>();
            services.TryAddSingleton<DisplayGroupCatalog>();
            return services;
        }

        /// <summary>The inbox provider, and no events until a host registers its hub's.</summary>
        public static IServiceCollection AddInboxProvider(this IServiceCollection services)
        {
            AddInboxIntegrations(services);
            services.TryAddSingleton<IInboxEvents, NoInboxEvents>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationProvider, InboxProvider>());
            return services;
        }
    }
}
