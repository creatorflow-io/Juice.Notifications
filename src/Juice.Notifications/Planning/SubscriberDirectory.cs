using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Planning
{
    /// <summary>
    /// Maps the people resolvers return to subscribers (design §6.1 step 2, D23): an identity linked as an alias reaches its subscriber; any other
    /// is the subscriber of that source and id, created the first time (phase-2-plan I15).
    /// </summary>
    /// <remarks>
    /// The upsert is its own statement, committed before the planner's step saves: a stage step holds no transaction while it runs. It is safe
    /// to repeat, so a step that fails or loses its lease leaves at most subscribers that a later plan would have made anyway. Two planners
    /// creating the same person at once make one row (the unique identity); a name, locale or time zone the source changed is written only
    /// when it differs, so planning a large group doesn't rewrite every row it already knows.
    /// </remarks>
    public static class SubscriberDirectory
    {
        /// <summary>
        /// Why a person a resolver returned can't be a subscriber (no source or id, or longer than the columns hold), or null when it can. A
        /// source's data isn't ours to trust: one bad person must not fail the whole notification at the upsert.
        /// </summary>
        public static string? Problem(ResolvedPerson person)
            => person.Source is null || !Configuration.ConfigurationRules.Key().IsMatch(person.Source)
                ? "its source is 1–63 lowercase letters, digits or hyphens, starting with a letter"
                : !Intake.RequestRules.IsRecipientId(person.ExternalId)
                    ? $"its external id is 1–{Intake.RequestRules.MaxRecipientIdLength} characters, no control characters or lone surrogates"
                    : null;

        /// <summary>
        /// The person without a name, locale or time zone the columns can't hold, too long or not well-formed text (the subscriber keeps what it has).
        /// </summary>
        public static ResolvedPerson Fit(ResolvedPerson person) => person with
        {
            Name = Holds(person.Name, MaxName),
            Locale = Holds(person.Locale, MaxLocale),
            TimeZone = Holds(person.TimeZone, MaxTimeZone),
        };

        private static string? Holds(string? value, int max) => value is null || value.Length > max || !Intake.RequestRules.IsWellFormed(value) ? null : value;

        // The subscribers table's column lengths (NotificationsDbContext).
        internal const int MaxName = 200;
        internal const int MaxLocale = 35;
        internal const int MaxTimeZone = 64;

        public static Task<IReadOnlyDictionary<(string Source, string ExternalId), Guid>> MapAsync(NotificationsDbContext db, Guid tenantId,
            IReadOnlyList<ResolvedPerson> people, DateTimeOffset now, CancellationToken ct)
            => MapAsync(db, tenantId, people, now, null, ct);

        /// <summary>
        /// As above, and stores the contact points the people's sources gave (<see cref="ResolvedPerson.Contacts"/>) on the subscribers they map to,
        /// when <paramref name="contacts"/> is given.
        /// </summary>
        public static async Task<IReadOnlyDictionary<(string Source, string ExternalId), Guid>> MapAsync(NotificationsDbContext db, Guid tenantId,
            IReadOnlyList<ResolvedPerson> people, DateTimeOffset now, SourceContacts? contacts, CancellationToken ct)
        {
            var map = await MapPeopleAsync(db, tenantId, people, now, ct);
            if (contacts is not null && people.Any(p => p.Contacts is { Count: > 0 }))
            {
                // A side effect of planning, not needed for it: one that fails is logged, and the people are planned with the contact points
                // they had. The next plan stores them.
                try
                {
                    await contacts.StoreAsync(db, tenantId, people, map, now, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    contacts.Failed(ex, tenantId);
                }
            }
            return map;
        }

        private static async Task<IReadOnlyDictionary<(string Source, string ExternalId), Guid>> MapPeopleAsync(NotificationsDbContext db, Guid tenantId,
            IReadOnlyList<ResolvedPerson> people, DateTimeOffset now, CancellationToken ct)
        {
            var distinct = people.DistinctBy(p => (p.Source, p.ExternalId)).OrderBy(p => p.Source, StringComparer.Ordinal)
                .ThenBy(p => p.ExternalId, StringComparer.Ordinal).ToList();
            var externalIds = distinct.Select(p => p.ExternalId).Distinct(StringComparer.Ordinal).ToArray();
            // Both columns of the (tenant, source, external id) indexes, so a lookup doesn't scan every identity the tenant has.
            var allSources = distinct.Select(p => p.Source).Distinct(StringComparer.Ordinal).ToArray();
            var map = new Dictionary<(string, string), Guid>();

            var aliases = await db.SubscriberAliases.AsNoTracking()
                .Where(a => a.TenantId == tenantId && allSources.Contains(a.Source) && externalIds.Contains(a.ExternalId))
                .Select(a => new { a.Source, a.ExternalId, a.SubscriberId }).ToListAsync(ct);
            foreach (var alias in aliases)
            {
                map[(alias.Source, alias.ExternalId)] = alias.SubscriberId;
            }
            var rest = distinct.Where(p => !map.ContainsKey((p.Source, p.ExternalId))).ToList();
            if (rest.Count == 0)
            {
                return map;
            }

            var ids = rest.Select(_ => Guid.CreateVersion7()).ToArray();
            var sources = rest.Select(p => p.Source).ToArray();
            var external = rest.Select(p => p.ExternalId).ToArray();
            var names = rest.Select(p => p.Name).ToArray();
            var locales = rest.Select(p => p.Locale).ToArray();
            var zones = rest.Select(p => p.TimeZone).ToArray();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.subscribers AS s (tenant_id, id, source, external_id, name, locale, time_zone, created_at, version)
                SELECT {tenantId}, u.id, u.source, u.external_id, u.name, u.locale, u.time_zone, {now}, 0
                FROM unnest({ids}::uuid[], {sources}::text[], {external}::text[], {names}::text[], {locales}::text[], {zones}::text[])
                    AS u(id, source, external_id, name, locale, time_zone)
                ON CONFLICT (tenant_id, source, external_id) DO UPDATE
                    SET name = COALESCE(excluded.name, s.name), locale = COALESCE(excluded.locale, s.locale),
                        time_zone = COALESCE(excluded.time_zone, s.time_zone)
                    WHERE (excluded.name IS NOT NULL AND excluded.name IS DISTINCT FROM s.name)
                       OR (excluded.locale IS NOT NULL AND excluded.locale IS DISTINCT FROM s.locale)
                       OR (excluded.time_zone IS NOT NULL AND excluded.time_zone IS DISTINCT FROM s.time_zone)
                """, ct);

            var restIds = external.Distinct(StringComparer.Ordinal).ToArray();
            var restSources = sources.Distinct(StringComparer.Ordinal).ToArray();
            // Read again after the upsert: an identity linked as an alias between the first read and the upsert (whose subscriber row the upsert
            // then made, unlinked) still reaches the subscriber it was linked to. The row made stays unused: the alias is read first from now on.
            var linkedMeanwhile = await db.SubscriberAliases.AsNoTracking()
                .Where(a => a.TenantId == tenantId && restSources.Contains(a.Source) && restIds.Contains(a.ExternalId))
                .Select(a => new { a.Source, a.ExternalId, a.SubscriberId }).ToListAsync(ct);
            foreach (var alias in linkedMeanwhile)
            {
                map[(alias.Source, alias.ExternalId)] = alias.SubscriberId;
            }
            var subscribers = await db.Subscribers.AsNoTracking()
                .Where(s => s.TenantId == tenantId && restSources.Contains(s.Source) && restIds.Contains(s.ExternalId))
                .Select(s => new { s.Source, s.ExternalId, s.Id, s.MergedInto }).ToListAsync(ct);
            foreach (var s in subscribers)
            {
                // Merged into another while its alias wasn't seen above (linked in between): the one it was linked to.
                map.TryAdd((s.Source, s.ExternalId), s.MergedInto ?? s.Id);
            }
            foreach (var p in rest)
            {
                if (!map.ContainsKey((p.Source, p.ExternalId)))
                {
                    // Inserted above or by a concurrent plan; a row deleted in between is a fault the stage retries.
                    throw new InvalidOperationException($"Subscriber {p.Source}/{p.ExternalId} in tenant {tenantId} vanished while it was planned.");
                }
            }
            return map;
        }
    }

    /// <summary>
    /// Stores the addresses sources give for people as their contact points (phase-2-plan I15): encrypted, unique per person, channel and address,
    /// with one upsert that is safe to repeat like the subscribers' own. A new one is verified when its source says so; an existing one only
    /// becomes verified (by the source) when it wasn't, so a source never un-verifies an address or overwrites the person's own verification. An
    /// address on a channel that takes none, or that isn't valid, is skipped with a warning, as an invalid person is.
    /// </summary>
    public sealed class SourceContacts(People.ContactCrypto crypto, IChannelRegistry channels, ILogger logger)
    {
        internal void Failed(Exception ex, Guid tenantId)
            => logger.LogWarning(ex, "Storing the contact points sources gave for people in tenant {Tenant} failed; they are planned without them", tenantId);

        internal async Task StoreAsync(NotificationsDbContext db, Guid tenantId, IReadOnlyList<ResolvedPerson> people,
            IReadOnlyDictionary<(string Source, string ExternalId), Guid> map, DateTimeOffset now, CancellationToken ct)
        {
            var rows = new Dictionary<(Guid Subscriber, string Channel, string Hash), (string Address, bool Verified, string Source)>();
            foreach (var person in people.Where(p => p.Contacts is { Count: > 0 }))
            {
                if (!map.TryGetValue((person.Source, person.ExternalId), out var subscriberId))
                {
                    continue;
                }
                // No more than a person may have in all (the statement below holds the total to it).
                foreach (var contact in person.Contacts!.Take(People.ContactRules.MaxPerPerson))
                {
                    var channel = contact.Channel is null ? null : channels.Find(contact.Channel);
                    if (!People.ContactRules.TakesContactPoints(channel) || Intake.RequestRules.NormalizeAddress(channel!, contact.Address) is not { } address)
                    {
                        logger.LogWarning("Source {Source} gave person {ExternalId} in tenant {Tenant} a {Channel} address that can't be a contact point; it is skipped",
                            person.Source, person.ExternalId, tenantId, contact.Channel);
                        continue;
                    }
                    var key = (subscriberId, channel!.Key, crypto.ContactHash(tenantId, channel.Key, address));
                    // The same address twice (two names reaching the person): verified if either says so.
                    rows[key] = rows.TryGetValue(key, out var seen) && seen.Verified ? seen : (address, contact.Verified, person.Source);
                }
            }
            if (rows.Count == 0)
            {
                return;
            }
            var ordered = rows.OrderBy(r => r.Key.Subscriber).ThenBy(r => r.Key.Channel, StringComparer.Ordinal).ThenBy(r => r.Key.Hash, StringComparer.Ordinal).ToList();
            var ids = ordered.Select(_ => Guid.CreateVersion7()).ToArray();
            var subscribers = ordered.Select(r => r.Key.Subscriber).ToArray();
            var channelKeys = ordered.Select(r => r.Key.Channel).ToArray();
            var hashes = ordered.Select(r => r.Key.Hash).ToArray();
            var addresses = ordered.Select(r => crypto.ProtectAddress(tenantId, r.Key.Subscriber, r.Key.Channel, r.Value.Address)).ToArray();
            var masks = ordered.Select(r => People.ContactCrypto.Mask(r.Value.Address)).ToArray();
            var verified = ordered.Select(r => r.Value.Verified).ToArray();
            var sources = ordered.Select(r => r.Value.Source).ToArray();
            var keyId = crypto.KeyId;
            var ok = nameof(ContactHealth.Ok);
            // In the order of the unique key, so two plans storing the same people lock rows in the same order.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO notifications.contact_points AS c (tenant_id, id, subscriber_id, channel, address, address_hash, key_id, masked, verified_at, verified_by, health, created_at)
                SELECT {tenantId}, u.id, u.subscriber_id, u.channel, u.address, u.address_hash, {keyId}, u.masked,
                    CASE WHEN u.verified THEN {now} END, CASE WHEN u.verified THEN u.source END, {ok}, {now}
                FROM (
                    -- Each new address ranked among the person's new ones only: those they have already count once, in count(*) below.
                    SELECT k.*, row_number() OVER (PARTITION BY k.subscriber_id, k.known ORDER BY k.channel, k.address_hash) AS n
                    FROM (
                        SELECT u.*, EXISTS (SELECT 1 FROM notifications.contact_points e WHERE e.tenant_id = {tenantId} AND e.subscriber_id = u.subscriber_id
                            AND e.channel = u.channel AND e.address_hash = u.address_hash) AS known
                        FROM unnest({ids}::uuid[], {subscribers}::uuid[], {channelKeys}::text[], {addresses}::text[], {hashes}::text[], {verified}::boolean[], {sources}::text[], {masks}::text[])
                            AS u(id, subscriber_id, channel, address, address_hash, verified, source, masked)) AS k) AS u
                -- One the person has already is updated; a new one only while the person stays within the limit in all, counting the new ones
                -- before it in this statement, so a source can't grow a person's list plan after plan.
                WHERE u.known
                   OR (SELECT count(*) FROM notifications.contact_points e WHERE e.tenant_id = {tenantId} AND e.subscriber_id = u.subscriber_id) + u.n
                        <= {People.ContactRules.MaxPerPerson}
                ON CONFLICT (tenant_id, subscriber_id, channel, address_hash) DO UPDATE
                    SET verified_at = excluded.verified_at, verified_by = excluded.verified_by
                    WHERE c.verified_at IS NULL AND excluded.verified_at IS NOT NULL
                """, ct);
        }
    }
}
