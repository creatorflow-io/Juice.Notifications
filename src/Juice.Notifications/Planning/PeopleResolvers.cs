using Juice.Notifications.Intake;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.Planning
{
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
                         orderby s.Source, s.ExternalId
                         select new ResolvedPerson(s.Source, s.ExternalId, null, null, null)).AsNoTracking().ToListAsync(ct);
            return new ResolveResult([]) { People = people };
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
                                orderby s.Source, s.ExternalId
                                select new ResolvedPerson(s.Source, s.ExternalId, null, null, null)).AsNoTracking().ToListAsync(ct);
            return new ResolveResult([]) { People = people };
        }
    }
}
