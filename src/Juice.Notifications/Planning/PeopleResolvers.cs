using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Planning
{
    using Ef = Microsoft.EntityFrameworkCore.EF;

    /// <summary>
    /// <c>local</c> (design §6.1): people and groups kept in this service, for tenants or embedded hosts with no identity service. A user is the
    /// local user of that id (a subscriber of source <c>local</c>, or a <c>local</c> identity linked as an alias of another subscriber); a group is
    /// a <c>local_groups</c> row, whose members can be any subscribers. One that doesn't exist reaches no one. Read in the planner's scope, never
    /// cached: the rows are this database's own.
    /// </summary>
    public sealed class LocalRecipientResolver : IRecipientResolver
    {
        public const string Source = "local";

        public string Key => "local";

        public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { RecipientKinds.User, RecipientKinds.Group };

        public async ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct)
        {
            var db = context.Db;
            var tenant = context.Request.TenantId;
            IReadOnlyList<ResolvedPerson> people = spec.Kind == RecipientKinds.User
                // An alias needs no subscriber row of its own: the planner's mapping reaches the subscriber it is linked to.
                ? spec.Id is { } id && (await db.Subscribers.AnyAsync(s => s.TenantId == tenant && s.Source == Source && s.ExternalId == id, ct)
                    || await db.SubscriberAliases.AnyAsync(a => a.TenantId == tenant && a.Source == Source && a.ExternalId == id, ct))
                    ? [new ResolvedPerson(Source, id)]
                    : []
                : await (from g in db.LocalGroups
                         join m in db.LocalGroupMembers on new { g.TenantId, GroupId = g.Id } equals new { m.TenantId, m.GroupId }
                         join s in db.Subscribers on new { m.TenantId, Id = m.SubscriberId } equals new { s.TenantId, s.Id }
                         where g.TenantId == tenant && g.Key == spec.Id
                         // Byte order, as the planner keeps the first of an oversized group: one past the bound is all it needs to see.
                         orderby Ef.Functions.Collate(s.Source, "C"), Ef.Functions.Collate(s.ExternalId, "C")
                         select new ResolvedPerson(s.Source, s.ExternalId, null, null, null)).Take(context.MaxPeople + 1).AsNoTracking().ToListAsync(ct);
            return new ResolveResult([]) { People = people };
        }
    }

    /// <summary>
    /// What an embedded host's inbox records and resolves (M54): the roles worth recording (<see cref="Roles"/>, as the host spells them) and how
    /// long a sign-in vouches for one (<see cref="RoleTtl"/>).
    /// </summary>
    public sealed record SignedInOptions(IReadOnlyList<string> Roles, TimeSpan RoleTtl);

    /// <summary>
    /// <c>signed-in</c> (M54, an embedded host's inbox): <c>role:{r}</c> is the people who signed in to this host holding the role <c>r</c> within
    /// <see cref="SignedInOptions.RoleTtl"/>. The host's own sign-in records them (<see cref="People.SubscriberRoleRecorder"/>), so there is no call
    /// to an identity provider and nothing to configure: someone who never signed in isn't reached, and isn't backfilled later. A role nobody
    /// holds reaches no one. The tenant has one row for it (<see cref="EnsureRowAsync"/>), the service's own.
    /// </summary>
    public sealed class SignedInRecipientResolver(SignedInOptions options) : IRecipientResolver
    {
        public const string ResolverKey = "signed-in";

        /// <summary>The source of the subscribers the inbox's sign-in makes.</summary>
        public const string Source = "oidc";

        public string Key => ResolverKey;

        public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { RecipientKinds.Role };

        public async ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct)
        {
            if (spec.Id is not { } role)
            {
                return new ResolveResult([]);
            }
            var db = context.Db;
            var tenant = context.Request.TenantId;
            var since = context.Now - options.RoleTtl;
            var people = await (from r in db.SubscriberRoles
                                join s in db.Subscribers on new { r.TenantId, Id = r.SubscriberId } equals new { s.TenantId, s.Id }
                                where r.TenantId == tenant && r.Role == role && r.SeenAt >= since && s.TenantId == tenant && s.Source == Source
                                // Byte order, as the planner keeps the first of an oversized name: one past the bound is all it needs to see.
                                orderby Ef.Functions.Collate(s.Source, "C"), Ef.Functions.Collate(s.ExternalId, "C")
                                select new ResolvedPerson(s.Source, s.ExternalId, null, null, null)).Take(context.MaxPeople + 1).AsNoTracking().ToListAsync(ct);
            return new ResolveResult([]) { People = people };
        }

        /// <summary>
        /// The tenant's <c>signed-in</c> row, made or brought up to date at start: <see cref="ManagedBy.Service"/> (the admin API and the
        /// provisioning file can't change it), enabled while the host's inbox is on. One statement that is safe when replicas start together. A row of
        /// that key that isn't this one (another kind) is a conflict the host must resolve, not one to overwrite.
        /// </summary>
        public static async Task EnsureRowAsync(NotificationsDbContext db, Guid tenantId, bool enabled, DateTimeOffset now, CancellationToken ct)
        {
            string[] handles = [RecipientKinds.Role];
            const string settings = "{}";
            var hash = Configuration.ConfigurationRules.ResolverHash(ResolverKey, handles, settings, null, enabled);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.recipient_resolvers AS c (tenant_id, id, key, kind, handles, settings, secret_ref, enabled, managed_by, definition_hash, updated_at)
                VALUES ({tenantId}, {Guid.CreateVersion7()}, {ResolverKey}, {ResolverKey}, {handles}, {settings}::jsonb, NULL, {enabled}, {ManagedBy.Service}, {hash}, {now})
                ON CONFLICT (tenant_id, key) DO UPDATE
                    SET enabled = excluded.enabled, handles = excluded.handles, managed_by = excluded.managed_by, definition_hash = excluded.definition_hash,
                        updated_at = excluded.updated_at
                    WHERE c.kind = excluded.kind
                      AND (c.enabled, c.handles, c.managed_by, c.definition_hash) IS DISTINCT FROM (excluded.enabled, excluded.handles, excluded.managed_by, excluded.definition_hash)
                """, ct);
            var row = await db.Resolvers.AsNoTracking().SingleAsync(c => c.TenantId == tenantId && c.Key == ResolverKey, ct);
            if (row.Kind != ResolverKey || row.ManagedBy != ManagedBy.Service)
            {
                throw new InvalidOperationException($"The resolver '{ResolverKey}' of tenant {tenantId} is a {row.Kind} resolver this host didn't make; it is the inbox's own key.");
            }
        }
    }

    /// <summary><c>topic</c> (design §6.1): a topic's subscribers. A topic that doesn't exist, or has none, reaches no one.</summary>
    public sealed class TopicRecipientResolver : IRecipientResolver
    {
        public string Key => "topic";

        public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { RecipientKinds.Topic };

        public async ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct)
        {
            var db = context.Db;
            var tenant = context.Request.TenantId;
            var people = await (from t in db.Topics
                                join sub in db.TopicSubscriptions on new { t.TenantId, TopicId = t.Id } equals new { sub.TenantId, sub.TopicId }
                                join s in db.Subscribers on new { sub.TenantId, Id = sub.SubscriberId } equals new { s.TenantId, s.Id }
                                where t.TenantId == tenant && t.Key == spec.Key
                                orderby Ef.Functions.Collate(s.Source, "C"), Ef.Functions.Collate(s.ExternalId, "C")
                                select new ResolvedPerson(s.Source, s.ExternalId, null, null, null)).Take(context.MaxPeople + 1).AsNoTracking().ToListAsync(ct);
            return new ResolveResult([]) { People = people };
        }
    }
}
