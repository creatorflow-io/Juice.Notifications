using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

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
                    ? $"its external id is 1–{Intake.RequestRules.MaxRecipientIdLength} characters, no control characters"
                    : null;

        /// <summary>The person without a name, locale or time zone the columns can't hold (the subscriber keeps what it has).</summary>
        public static ResolvedPerson Fit(ResolvedPerson person) => person with
        {
            Name = person.Name is { Length: > MaxName } ? null : person.Name,
            Locale = person.Locale is { Length: > MaxLocale } ? null : person.Locale,
            TimeZone = person.TimeZone is { Length: > MaxTimeZone } ? null : person.TimeZone,
        };

        // The subscribers table's column lengths (NotificationsDbContext).
        internal const int MaxName = 200;
        internal const int MaxLocale = 35;
        internal const int MaxTimeZone = 64;

        public static async Task<IReadOnlyDictionary<(string Source, string ExternalId), Guid>> MapAsync(NotificationsDbContext db, Guid tenantId,
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
}
