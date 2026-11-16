using System.Text.RegularExpressions;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.People
{
    // What the local and topic resolvers read (phase-2-plan M11): local users (subscribers of source local), local groups of any subscribers,
    // and topics with their subscribers. Tenant admins manage them; a person subscribes themselves to a self-service topic.

    public sealed record GroupView(string Key, string Name, int Members, DateTimeOffset UpdatedAt);

    public sealed record TopicView(string Key, string Name, bool SelfService, int Subscribers, DateTimeOffset UpdatedAt);

    /// <summary>Creates or changes a local user: the subscriber <c>local:{id}</c>. A field left out keeps its value.</summary>
    public sealed record PutLocalUser(Guid NotificationTenantId, string Id, string? Name, string? Locale, string? TimeZone)
        : MessageBase, IRequest<Outcome<SubscriberView>>;

    public sealed record PutGroup(Guid NotificationTenantId, string Key, string? Name) : MessageBase, IRequest<Outcome<GroupView>>;

    /// <summary>Deletes a local group and its memberships. Requests naming it and not yet planned reach no one through it.</summary>
    public sealed record DeleteGroup(Guid NotificationTenantId, string Key) : MessageBase, IRequest<Outcome<bool>>;

    public sealed record AddGroupMember(Guid NotificationTenantId, string GroupKey, Guid SubscriberId) : MessageBase, IRequest<Outcome<bool>>;

    public sealed record RemoveGroupMember(Guid NotificationTenantId, string GroupKey, Guid SubscriberId) : MessageBase, IRequest<Outcome<bool>>;

    public sealed record PutTopic(Guid NotificationTenantId, string Key, string? Name, bool? SelfService) : MessageBase, IRequest<Outcome<TopicView>>;

    public sealed record DeleteTopic(Guid NotificationTenantId, string Key) : MessageBase, IRequest<Outcome<bool>>;

    /// <summary>Subscribes a subscriber to a topic: by an admin, or by the person themselves (<see cref="BySelf"/>), only to a self-service topic.</summary>
    public sealed record Subscribe(Guid NotificationTenantId, string TopicKey, Guid SubscriberId, bool BySelf) : MessageBase, IRequest<Outcome<bool>>;

    /// <summary>Unsubscribes from a topic: by an admin, or by the person themselves (<see cref="BySelf"/>), only from a self-service topic.</summary>
    public sealed record Unsubscribe(Guid NotificationTenantId, string TopicKey, Guid SubscriberId, bool BySelf = false) : MessageBase, IRequest<Outcome<bool>>;

    public static partial class DirectoryRules
    {
        /// <summary>A local user's id: path-safe, as requests name it (<c>{ "kind": "user", "id": "adam" }</c>).</summary>
        [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._@+-]{0,199}\z")]
        public static partial Regex LocalUserId();

        /// <summary>A topic's key, as requests name it: <c>observability.service:billing</c>.</summary>
        [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,199}\z")]
        public static partial Regex TopicKey();

        /// <summary>A BCP 47 tag, loosely: letters, digits and hyphens.</summary>
        [GeneratedRegex(@"^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*\z")]
        public static partial Regex Locale();

        /// <summary>The subscribers table's name column, which groups and topics share.</summary>
        public const int MaxName = SubscriberDirectory.MaxName;

        internal static readonly FieldError NameError = new("/name", $"A name is 1–{MaxName} characters, no control characters.");

        internal static bool IsName(string? name) => name is { Length: > 0 and <= MaxName } && !name.Any(char.IsControl) && Intake.RequestRules.IsWellFormed(name);

        internal static bool IsLocale(string locale) => locale.Length <= SubscriberDirectory.MaxLocale && Locale().IsMatch(locale);

        /// <summary>An IANA time zone id this host knows.</summary>
        internal static bool IsTimeZone(string zone)
            => zone.Length <= SubscriberDirectory.MaxTimeZone &&TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var found) && found.HasIanaId;
    }

    internal sealed class DirectoryHandlers(NotificationsDbContext db, TimeProvider clock) :
        IRequestHandler<PutLocalUser, Outcome<SubscriberView>>,
        IRequestHandler<PutGroup, Outcome<GroupView>>, IRequestHandler<DeleteGroup, Outcome<bool>>,
        IRequestHandler<AddGroupMember, Outcome<bool>>, IRequestHandler<RemoveGroupMember, Outcome<bool>>,
        IRequestHandler<PutTopic, Outcome<TopicView>>, IRequestHandler<DeleteTopic, Outcome<bool>>,
        IRequestHandler<Subscribe, Outcome<bool>>, IRequestHandler<Unsubscribe, Outcome<bool>>
    {
        private static Outcome<T> Invalid<T>(params FieldError[] errors) => Outcome<T>.Invalid(ErrorCodes.InvalidRequest, "The request has errors.", errors);

        public async ValueTask<Outcome<SubscriberView>> Handle(PutLocalUser r, CancellationToken ct)
        {
            var errors = new List<FieldError>();
            if (!DirectoryRules.LocalUserId().IsMatch(r.Id))
            {
                errors.Add(new FieldError("/id", "A local user's id is 1–200 letters, digits, '.', '_', '@', '+' or '-', starting with a letter or digit."));
            }
            if (r.Name is not null && !DirectoryRules.IsName(r.Name))
            {
                errors.Add(DirectoryRules.NameError);
            }
            if (r.Locale is not null && !DirectoryRules.IsLocale(r.Locale))
            {
                errors.Add(new FieldError("/locale", "A locale is a language tag such as en or vi-VN."));
            }
            if (r.TimeZone is not null && !DirectoryRules.IsTimeZone(r.TimeZone))
            {
                errors.Add(new FieldError("/timeZone", "A time zone is an IANA id such as Asia/Ho_Chi_Minh."));
            }
            if (errors.Count > 0)
            {
                return Invalid<SubscriberView>([.. errors]);
            }
            var user = await db.Subscribers.SingleOrDefaultAsync(s => s.TenantId == r.NotificationTenantId && s.Source == LocalRecipientResolver.Source
                && s.ExternalId == r.Id, ct);
            if (user is null)
            {
                // The identity linked as an alias reaches the subscriber it is linked to (SubscriberDirectory reads aliases first): a row made
                // here would never be read, and what it was given would never apply.
                if (await db.SubscriberAliases.AsNoTracking().Where(a => a.TenantId == r.NotificationTenantId && a.Source == LocalRecipientResolver.Source
                    && a.ExternalId == r.Id).Select(a => (Guid?)a.SubscriberId).FirstOrDefaultAsync(ct) is { } linked)
                {
                    return Outcome<SubscriberView>.Conflict(ErrorCodes.AliasInUse,
                        $"This local user is linked to subscriber {linked}; change that subscriber, or unlink the identity first.");
                }
                user = new Subscriber
                {
                    TenantId = r.NotificationTenantId, Source = LocalRecipientResolver.Source, ExternalId = r.Id, Name = r.Name, Locale = r.Locale,
                    TimeZone = r.TimeZone, CreatedAt = clock.UtcNow(),
                };
                db.Subscribers.Add(user);
                // A new subscriber has no aliases yet.
                return Outcome<SubscriberView>.Ok(SubscriberQueries.View(user, []));
            }
            else
            {
                user.Name = r.Name ?? user.Name;
                user.Locale = r.Locale ?? user.Locale;
                user.TimeZone = r.TimeZone ?? user.TimeZone;
            }
            return Outcome<SubscriberView>.Ok(await SubscriberQueries.ViewAsync(db, user, ct));
        }

        public async ValueTask<Outcome<GroupView>> Handle(PutGroup r, CancellationToken ct)
        {
            if (!ConfigurationRules.Key().IsMatch(r.Key))
            {
                return Invalid<GroupView>(new FieldError("/key", "A key is 1–63 lowercase letters, digits or hyphens, starting with a letter."));
            }
            var group = await db.LocalGroups.SingleOrDefaultAsync(g => g.TenantId == r.NotificationTenantId && g.Key == r.Key, ct);
            var name = r.Name ?? group?.Name ?? r.Key;
            if (!DirectoryRules.IsName(name))
            {
                return Invalid<GroupView>(DirectoryRules.NameError);
            }
            if (group is null)
            {
                group = new LocalGroup { TenantId = r.NotificationTenantId, Key = r.Key, Name = name, UpdatedAt = clock.UtcNow() };
                db.LocalGroups.Add(group);
                return Outcome<GroupView>.Ok(new GroupView(group.Key, group.Name, 0, group.UpdatedAt));
            }
            if (group.Name != name)
            {
                group.Name = name;
                group.UpdatedAt = clock.UtcNow();
            }
            var members = await db.LocalGroupMembers.CountAsync(m => m.TenantId == r.NotificationTenantId && m.GroupId == group.Id, ct);
            return Outcome<GroupView>.Ok(new GroupView(group.Key, group.Name, members, group.UpdatedAt));
        }

        public async ValueTask<Outcome<bool>> Handle(DeleteGroup r, CancellationToken ct)
        {
            var group = await db.LocalGroups.SingleOrDefaultAsync(g => g.TenantId == r.NotificationTenantId && g.Key == r.Key, ct);
            if (group is null)
            {
                return Outcome<bool>.Ok(false);
            }
            // Removed one by one rather than by the cascade, so each membership's end is audited.
            db.LocalGroupMembers.RemoveRange(await db.LocalGroupMembers.Where(m => m.TenantId == r.NotificationTenantId && m.GroupId == group.Id).ToListAsync(ct));
            db.LocalGroups.Remove(group);
            return Outcome<bool>.Ok(true);
        }

        public async ValueTask<Outcome<bool>> Handle(AddGroupMember r, CancellationToken ct)
        {
            var group = await db.LocalGroups.AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == r.NotificationTenantId && g.Key == r.GroupKey, ct);
            if (group is null)
            {
                return Outcome<bool>.NotFound("No such group.");
            }
            if (!await db.Subscribers.AnyAsync(s => s.TenantId == r.NotificationTenantId && s.Id == r.SubscriberId, ct))
            {
                return Outcome<bool>.NotFound("No such subscriber.");
            }
            if (await db.LocalGroupMembers.AnyAsync(m => m.TenantId == r.NotificationTenantId && m.GroupId == group.Id && m.SubscriberId == r.SubscriberId, ct))
            {
                return Outcome<bool>.Ok(false);
            }
            db.LocalGroupMembers.Add(new LocalGroupMember { TenantId = r.NotificationTenantId, GroupId = group.Id, SubscriberId = r.SubscriberId, AddedAt = clock.UtcNow() });
            return Outcome<bool>.Ok(true);
        }

        public async ValueTask<Outcome<bool>> Handle(RemoveGroupMember r, CancellationToken ct)
        {
            var member = await (from m in db.LocalGroupMembers
                                join g in db.LocalGroups on new { m.TenantId, Id = m.GroupId } equals new { g.TenantId, g.Id }
                                where m.TenantId == r.NotificationTenantId && g.Key == r.GroupKey && m.SubscriberId == r.SubscriberId
                                select m).SingleOrDefaultAsync(ct);
            if (member is null)
            {
                return Outcome<bool>.Ok(false);
            }
            db.LocalGroupMembers.Remove(member);
            return Outcome<bool>.Ok(true);
        }

        public async ValueTask<Outcome<TopicView>> Handle(PutTopic r, CancellationToken ct)
        {
            if (!DirectoryRules.TopicKey().IsMatch(r.Key))
            {
                return Invalid<TopicView>(new FieldError("/key", "A topic key is 1–200 letters, digits, '.', '_', ':' or '-', starting with a letter or digit."));
            }
            var topic = await db.Topics.SingleOrDefaultAsync(t => t.TenantId == r.NotificationTenantId && t.Key == r.Key, ct);
            var name = r.Name ?? topic?.Name ?? r.Key;
            if (!DirectoryRules.IsName(name))
            {
                return Invalid<TopicView>(DirectoryRules.NameError);
            }
            var selfService = r.SelfService ?? topic?.SelfService ?? true;
            if (topic is null)
            {
                topic = new Topic { TenantId = r.NotificationTenantId, Key = r.Key, Name = name, SelfService = selfService, UpdatedAt = clock.UtcNow() };
                db.Topics.Add(topic);
                return Outcome<TopicView>.Ok(new TopicView(topic.Key, topic.Name, topic.SelfService, 0, topic.UpdatedAt));
            }
            if ((topic.Name, topic.SelfService) != (name, selfService))
            {
                topic.Name = name;
                topic.SelfService = selfService;
                topic.UpdatedAt = clock.UtcNow();
            }
            var subscribers = await db.TopicSubscriptions.CountAsync(s => s.TenantId == r.NotificationTenantId && s.TopicId == topic.Id, ct);
            return Outcome<TopicView>.Ok(new TopicView(topic.Key, topic.Name, topic.SelfService, subscribers, topic.UpdatedAt));
        }

        public async ValueTask<Outcome<bool>> Handle(DeleteTopic r, CancellationToken ct)
        {
            var topic = await db.Topics.SingleOrDefaultAsync(t => t.TenantId == r.NotificationTenantId && t.Key == r.Key, ct);
            if (topic is null)
            {
                return Outcome<bool>.Ok(false);
            }
            db.TopicSubscriptions.RemoveRange(await db.TopicSubscriptions.Where(s => s.TenantId == r.NotificationTenantId && s.TopicId == topic.Id).ToListAsync(ct));
            db.Topics.Remove(topic);
            return Outcome<bool>.Ok(true);
        }

        public async ValueTask<Outcome<bool>> Handle(Subscribe r, CancellationToken ct)
        {
            var topic = await db.Topics.AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == r.NotificationTenantId && t.Key == r.TopicKey, ct);
            if (topic is null)
            {
                return Outcome<bool>.NotFound("No such topic.");
            }
            if (r.BySelf && !topic.SelfService)
            {
                return Outcome<bool>.Forbidden(ErrorCodes.TopicNotSelfService, "This topic's subscribers are chosen by an admin.");
            }
            if (!await db.Subscribers.AnyAsync(s => s.TenantId == r.NotificationTenantId && s.Id == r.SubscriberId, ct))
            {
                return Outcome<bool>.NotFound("No such subscriber.");
            }
            // A person who follows it through an identity linked into them follows it already (as Unsubscribe and TopicsForAsync see it).
            var ids = r.BySelf ? await DirectoryReader.SelfAsync(db, r.NotificationTenantId, r.SubscriberId, ct) : [r.SubscriberId];
            if (await db.TopicSubscriptions.AnyAsync(s => s.TenantId == r.NotificationTenantId && s.TopicId == topic.Id && ids.Contains(s.SubscriberId), ct))
            {
                return Outcome<bool>.Ok(false);
            }
            db.TopicSubscriptions.Add(new TopicSubscription { TenantId = r.NotificationTenantId, TopicId = topic.Id, SubscriberId = r.SubscriberId, SubscribedAt = clock.UtcNow() });
            return Outcome<bool>.Ok(true);
        }

        public async ValueTask<Outcome<bool>> Handle(Unsubscribe r, CancellationToken ct)
        {
            var topic = await db.Topics.AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == r.NotificationTenantId && t.Key == r.TopicKey, ct);
            if (topic is null)
            {
                return Outcome<bool>.Ok(false);
            }
            // An admin chose who follows a topic that isn't self-service; a person opts out of what reaches them through preferences instead.
            if (r.BySelf && !topic.SelfService)
            {
                return Outcome<bool>.Forbidden(ErrorCodes.TopicNotSelfService, "This topic's subscribers are chosen by an admin.");
            }
            // The person and any subscriber linked into them: the topic reaches them through either.
            var ids = r.BySelf ? await DirectoryReader.SelfAsync(db, r.NotificationTenantId, r.SubscriberId, ct) : [r.SubscriberId];
            var subscriptions = await db.TopicSubscriptions
                .Where(s => s.TenantId == r.NotificationTenantId && s.TopicId == topic.Id && ids.Contains(s.SubscriberId)).ToListAsync(ct);
            db.TopicSubscriptions.RemoveRange(subscriptions);
            return Outcome<bool>.Ok(subscriptions.Count > 0);
        }
    }

    /// <summary>Reads a tenant's groups and topics, outside any command.</summary>
    public sealed class DirectoryReader(NotificationsDbContext db)
    {
        public async Task<IReadOnlyList<GroupView>> GroupsAsync(Guid tenantId, CancellationToken ct)
            => await db.LocalGroups.AsNoTracking().Where(g => g.TenantId == tenantId).OrderBy(g => g.Key)
                .Select(g => new GroupView(g.Key, g.Name, db.LocalGroupMembers.Count(m => m.TenantId == g.TenantId && m.GroupId == g.Id), g.UpdatedAt))
                .ToListAsync(ct);

        /// <summary>The group's members, or null when there is no such group.</summary>
        public async Task<IReadOnlyList<SubscriberView>?> MembersAsync(Guid tenantId, string key, CancellationToken ct)
        {
            var found = await db.LocalGroups.AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == tenantId && g.Key == key, ct);
            if (found is null)
            {
                return null;
            }
            var members = await (from m in db.LocalGroupMembers
                                 join s in db.Subscribers on new { m.TenantId, Id = m.SubscriberId } equals new { s.TenantId, s.Id }
                                 where m.TenantId == tenantId && m.GroupId == found.Id
                                 orderby s.Id
                                 select s).AsNoTracking().ToListAsync(ct);
            var ids = members.Select(s => s.Id).ToArray();
            var aliases = (await db.SubscriberAliases.AsNoTracking().Where(a => a.TenantId == tenantId && ids.Contains(a.SubscriberId)).ToListAsync(ct))
                .ToLookup(a => a.SubscriberId);
            return members.Select(s => SubscriberQueries.View(s, aliases[s.Id])).ToList();
        }

        public async Task<IReadOnlyList<TopicView>> TopicsAsync(Guid tenantId, CancellationToken ct)
            => await db.Topics.AsNoTracking().Where(t => t.TenantId == tenantId).OrderBy(t => t.Key)
                .Select(t => new TopicView(t.Key, t.Name, t.SelfService, db.TopicSubscriptions.Count(s => s.TenantId == t.TenantId && s.TopicId == t.Id), t.UpdatedAt))
                .ToListAsync(ct);

        /// <summary>A subscriber and those linked into it (<see cref="Subscriber.MergedInto"/>): who "me" is, for what each of them follows.</summary>
        internal static async Task<List<Guid>> SelfAsync(NotificationsDbContext db, Guid tenantId, Guid subscriberId, CancellationToken ct)
            => [subscriberId, .. await db.Subscribers.AsNoTracking().Where(s => s.TenantId == tenantId && s.MergedInto == subscriberId).Select(s => s.Id).ToListAsync(ct)];

        /// <summary>The topics a person follows (as themselves or through an identity linked into them), and the self-service ones they could.</summary>
        public async Task<IReadOnlyList<(TopicView Topic, bool Subscribed)>> TopicsForAsync(Guid tenantId, Guid subscriberId, CancellationToken ct)
        {
            var self = await SelfAsync(db, tenantId, subscriberId, ct);
            var rows = await db.Topics.AsNoTracking().Where(t => t.TenantId == tenantId)
                .Select(t => new
                {
                    t.Key, t.Name, t.SelfService, t.UpdatedAt,
                    Subscribed = db.TopicSubscriptions.Any(s => s.TenantId == t.TenantId && s.TopicId == t.Id && self.Contains(s.SubscriberId)),
                })
                .Where(t => t.SelfService || t.Subscribed).OrderBy(t => t.Key).ToListAsync(ct);
            // A person isn't told how many others follow a topic.
            return rows.Select(t => (new TopicView(t.Key, t.Name, t.SelfService, 0, t.UpdatedAt), t.Subscribed)).ToList();
        }
    }
}
