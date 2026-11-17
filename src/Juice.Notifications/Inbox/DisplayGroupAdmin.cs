using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.EF.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Inbox
{
    // The display group catalog (design D12, phase-2-plan I24, M15): tenant admins add, rename, reorder and delete groups (never the three
    // built-ins) and move a type to a group. Each change and the re-homing of the open items it affects is one transaction, under the tenant's
    // catalog lock, so it shows at once in every inbox and no item is written meanwhile into a group it leaves.

    /// <summary>A group, and the types the tenant moved to it (a type that declares the group and wasn't moved isn't listed).</summary>
    public sealed record DisplayGroupView(string Key, string Name, int Position, bool BuiltIn, IReadOnlyList<string> Types, DateTimeOffset UpdatedAt);

    /// <summary>Where a type's items go now, whether the tenant chose it, and how many open items moved.</summary>
    public sealed record MovedType(string TypeKey, string GroupKey, bool Chosen, int ItemsMoved);

    public static class DisplayGroupRules
    {
        public const int MaxGroups = 50;
        public const int MaxName = 100;

        public static bool IsName(string? name) => Types.TypeRules.IsLabel(name, MaxName);
    }

    /// <summary>
    /// Changes the tenant's catalog. Not a mediator command: a change and the rewrite of the items it moves must be one transaction that holds the
    /// catalog lock from the start, and a command's transaction opens only after its handler ran. Each method runs its own, saving the
    /// configuration rows through the context so they are audited when it commits. A database conflict (a lost race) is thrown for the API to answer.
    /// </summary>
    public sealed class DisplayGroupEditor(NotificationsDbContext db, DisplayGroupCatalog catalog, TimeProvider clock, Juice.MediatR.IMediator mediator)
    {
        private static Outcome<T> Invalid<T>(params FieldError[] errors) => Outcome<T>.Invalid(ErrorCodes.InvalidConfiguration, "The request has errors.", errors);

        /// <summary>Adds a group, or renames or reorders one; a field left out is kept (a new group's name is required).</summary>
        public async Task<Outcome<DisplayGroupView>> PutAsync(Guid tenantId, string key, string? name, int? position, CancellationToken ct)
        {
            var errors = new List<FieldError>();
            if (!ConfigurationRules.Key().IsMatch(key))
            {
                errors.Add(new FieldError("/key", "A key is 1–63 lowercase letters, digits or hyphens, starting with a letter."));
            }
            if (name is not null && !DisplayGroupRules.IsName(name))
            {
                errors.Add(new FieldError("/name", $"A name is 1–{DisplayGroupRules.MaxName} characters, no control characters or surrounding spaces."));
            }
            if (position is < 0 or > 100_000)
            {
                errors.Add(new FieldError("/position", "A position is 0–100000; groups show in ascending order."));
            }
            if (errors.Count > 0)
            {
                return Outcome<DisplayGroupView>.Invalid(ErrorCodes.InvalidConfiguration, "The request has errors.", errors);
            }
            return await InTransactionAsync(tenantId, async now =>
            {
                var group = await db.DisplayGroups.SingleOrDefaultAsync(g => g.TenantId == tenantId && g.Key == key, ct);
                var added = group is null;
                if (group is null)
                {
                    if (name is null)
                    {
                        return Invalid<DisplayGroupView>(new FieldError("/name", "A new group needs a name."));
                    }
                    // Counted under the lock: two adds at once can't both take the last place.
                    if (await db.DisplayGroups.CountAsync(g => g.TenantId == tenantId, ct) >= DisplayGroupRules.MaxGroups)
                    {
                        return Outcome<DisplayGroupView>.Conflict(ErrorCodes.DisplayGroupLimit, $"A tenant has up to {DisplayGroupRules.MaxGroups} display groups.");
                    }
                    var last = await db.DisplayGroups.Where(g => g.TenantId == tenantId).MaxAsync(g => (int?)g.Position, ct) ?? 0;
                    group = new DisplayGroup { TenantId = tenantId, Key = key, Name = name, Position = position ?? last + 10, UpdatedAt = now };
                    db.DisplayGroups.Add(group);
                }
                else if ((name ?? group.Name, position ?? group.Position) != (group.Name, group.Position))
                {
                    (group.Name, group.Position, group.UpdatedAt) = (name ?? group.Name, position ?? group.Position, now);
                }
                await db.SaveChangesAsync(ct);
                if (added)
                {
                    // Types that declare this key and weren't moved went to general until now; their open items come here too.
                    await DisplayGroupCatalog.RehomeAsync(db, tenantId, DisplayGroups.General, null, ct);
                }
                var types = await db.TypeDisplayGroups.AsNoTracking().Where(t => t.TenantId == tenantId && t.GroupKey == key)
                    .Select(t => t.TypeKey).OrderBy(k => k).ToListAsync(ct);
                return Outcome<DisplayGroupView>.Ok(new DisplayGroupView(group.Key, group.Name, group.Position, group.BuiltIn, types, group.UpdatedAt));
            }, ct);
        }

        /// <summary>
        /// Deletes a group the tenant added (deleting one that isn't there answers false). Types moved to it go back to their declared groups;
        /// its open items follow their types.
        /// </summary>
        public Task<Outcome<bool>> DeleteAsync(Guid tenantId, string key, CancellationToken ct)
            => InTransactionAsync(tenantId, async _ =>
            {
                // Read under the lock: a delete at the same moment has finished, so this one finds it gone.
                var group = await db.DisplayGroups.SingleOrDefaultAsync(g => g.TenantId == tenantId && g.Key == key, ct);
                if (group is null)
                {
                    return Outcome<bool>.Ok(false);
                }
                if (group.BuiltIn)
                {
                    return Outcome<bool>.Conflict(ErrorCodes.DisplayGroupBuiltIn, $"The group {key} is one of the three every tenant has; rename it instead.");
                }
                db.TypeDisplayGroups.RemoveRange(await db.TypeDisplayGroups.Where(t => t.TenantId == tenantId && t.GroupKey == key).ToListAsync(ct));
                db.DisplayGroups.Remove(group);
                await db.SaveChangesAsync(ct);
                await DisplayGroupCatalog.RehomeAsync(db, tenantId, key, null, ct);
                return Outcome<bool>.Ok(true);
            }, ct);

        /// <summary>Moves a type (every version of its key) to a group, or back to the group it declares (<paramref name="groupKey"/> null).</summary>
        public async Task<Outcome<MovedType>> MoveAsync(Guid tenantId, string typeKey, string? groupKey, CancellationToken ct)
        {
            if (groupKey is "")
            {
                return Invalid<MovedType>(new FieldError("/group", "Name the group; deleting the type's display group puts it back in the one it declares."));
            }
            if (!await db.Types.AsNoTracking().AnyAsync(t => t.Key == typeKey, ct))
            {
                return Outcome<MovedType>.NotFound($"There is no type {typeKey}.");
            }
            return await InTransactionAsync(tenantId, async now =>
            {
                if (groupKey is { } wanted && !await db.DisplayGroups.AnyAsync(g => g.TenantId == tenantId && g.Key == wanted, ct))
                {
                    return Invalid<MovedType>(new FieldError("/group", $"There is no display group {wanted}; add it first."));
                }
                var choice = await db.TypeDisplayGroups.SingleOrDefaultAsync(t => t.TenantId == tenantId && t.TypeKey == typeKey, ct);
                if (groupKey is null)
                {
                    if (choice is not null)
                    {
                        db.TypeDisplayGroups.Remove(choice);
                    }
                }
                else if (choice is null)
                {
                    db.TypeDisplayGroups.Add(new TypeDisplayGroup { TenantId = tenantId, TypeKey = typeKey, GroupKey = groupKey, UpdatedAt = now });
                }
                else if (choice.GroupKey != groupKey)
                {
                    (choice.GroupKey, choice.UpdatedAt) = (groupKey, now);
                }
                await db.SaveChangesAsync(ct);
                // Open items move with the type, in this transaction (proof point 6).
                var itemsMoved = await DisplayGroupCatalog.RehomeAsync(db, tenantId, null, typeKey, ct);
                return Outcome<MovedType>.Ok(new MovedType(typeKey, await DisplayGroupCatalog.GroupForAsync(db, tenantId, typeKey, ct), groupKey is not null, itemsMoved));
            }, ct);
        }

        /// <summary>
        /// Runs <paramref name="work"/> in a transaction that holds the tenant's catalog lock exclusive, the catalog seeded first. It commits when
        /// the work succeeds and rolls back otherwise, so a refusal leaves nothing behind.
        /// </summary>
        private async Task<Outcome<T>> InTransactionAsync<T>(Guid tenantId, Func<DateTimeOffset, Task<Outcome<T>>> work, CancellationToken ct)
        {
            var now = clock.GetUtcNow();
            await catalog.SeedAsync(db, tenantId, now, ct);
            // Managed, as Juice's TransactionBehavior manages a command's context: a save then holds its audit events, and they are dispatched
            // with this transaction's id, so they are recorded only if it commits.
            db.BeginManage();
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await DisplayGroupCatalog.LockAsync(db, tenantId, shared: false, ct);
                var outcome = await work(now);
                if (outcome.Kind is OutcomeKind.Ok or OutcomeKind.Created)
                {
                    await mediator.DispatchEventsUntilStableAsync(db, cancellationToken: ct);
                    await db.CommitTransactionAsync(transaction.TransactionId, ct);
                }
                else
                {
                    await transaction.RollbackAsync(ct);
                }
                return outcome;
            }
            finally
            {
                // Dispatched or rolled back: none of them may go out with a later save in this scope.
                db.ClearEvents();
                db.EndManage();
            }
        }
    }

    /// <summary>The tenant's catalog, seeded on first read (phase-2-plan I24).</summary>
    public sealed class DisplayGroupReader(NotificationsDbContext db, DisplayGroupCatalog catalog, TimeProvider clock)
    {
        public async Task<IReadOnlyList<DisplayGroupView>> ListAsync(Guid tenantId, CancellationToken ct)
        {
            await catalog.SeedAsync(db, tenantId, clock.GetUtcNow(), ct);
            var groups = await db.DisplayGroups.AsNoTracking().Where(g => g.TenantId == tenantId).OrderBy(g => g.Position).ThenBy(g => g.Key).ToListAsync(ct);
            var moved = (await db.TypeDisplayGroups.AsNoTracking().Where(t => t.TenantId == tenantId).Select(t => new { t.GroupKey, t.TypeKey }).ToListAsync(ct))
                .ToLookup(t => t.GroupKey, t => t.TypeKey);
            return groups.Select(g => new DisplayGroupView(g.Key, g.Name, g.Position, g.BuiltIn, moved[g.Key].Order(StringComparer.Ordinal).ToList(), g.UpdatedAt)).ToList();
        }
    }
}
