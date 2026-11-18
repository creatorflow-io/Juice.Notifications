using System.Collections.Concurrent;
using Juice.Notifications.Data;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Planning
{
    /// <summary>How long planning waits for an unavailable resolver, how many people one name may reach, and how long expansions are cached (design §6.1).</summary>
    public sealed class ResolverOptions
    {
        public const string Section = "Resolvers";

        /// <summary>
        /// How long after a notification's first resolver failure planning keeps waiting for it (phase-2-plan I22). After that it plans what did
        /// resolve and records each name that didn't as <c>ResolverUnavailable</c>.
        /// </summary>
        public TimeSpan WaitLimit { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>The first wait after a resolver failure; each wait is as long as the notification has waited so far, up to <see cref="RetryMax"/>.</summary>
        public TimeSpan RetryMin { get; set; } = TimeSpan.FromSeconds(1);

        public TimeSpan RetryMax { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The most people one named recipient reaches (I23). Beyond it the first this many, by source and external id, are kept and the name is
        /// recorded as <c>Truncated</c>. Service-wide for now; a per-tenant override waits for tenant settings (deferred #28).
        /// </summary>
        public int MaxRecipients { get; set; } = 10_000;

        /// <summary>How long a remote resolver's group and role expansions are kept (<see cref="ResolverCache"/>); 0 turns caching off.</summary>
        public int CacheSeconds { get; set; } = 300;
    }

    /// <summary>
    /// Code point order, which is the byte order of UTF-8 and so of PostgreSQL's <c>COLLATE "C"</c>: ordinal comparison of UTF-16 differs from it
    /// for characters past U+FFFF against U+E000–U+FFFF. Truncation keeps the first people in this order, whichever resolver gave them.
    /// </summary>
    public static class CodePointOrder
    {
        public static int Compare(string? a, string? b)
        {
            if (a is null || b is null)
            {
                return a is null ? b is null ? 0 : -1 : 1;
            }
            var x = a.EnumerateRunes();
            var y = b.EnumerateRunes();
            while (true)
            {
                var more = x.MoveNext();
                if (more != y.MoveNext())
                {
                    return more ? 1 : -1;
                }
                if (!more)
                {
                    // Equal as runes, which read a lone surrogate as U+FFFD: tell such ids apart, so the order is total.
                    return string.CompareOrdinal(a, b);
                }
                if (x.Current.Value != y.Current.Value)
                {
                    return x.Current.Value < y.Current.Value ? -1 : 1;
                }
            }
        }

        /// <summary>People by source, then external id.</summary>
        public static IComparer<ResolvedPerson> People { get; } = Comparer<ResolvedPerson>.Create((p, q) =>
            Compare(p.Source, q.Source) is var bySource and not 0 ? bySource : Compare(p.ExternalId, q.ExternalId));
    }

    /// <summary>
    /// A resolver couldn't reach what it resolves from (a timeout, a 5xx, a refused connection): not the request's fault, and worth waiting for
    /// (phase-2-plan I22). Anything else a resolver throws is a fault of the step, retried and given up like any other.
    /// </summary>
    public sealed class ResolverUnavailableException : Exception
    {
        public ResolverUnavailableException(string message) : base(message)
        {
        }

        public ResolverUnavailableException(string message, Exception inner) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// Expansions a remote resolver may reuse for <see cref="ResolverOptions.CacheSeconds"/> (design §6.1, phase-2-plan I23): by tenant, resolver
    /// configuration (its key, kind and settings, so a changed configuration starts afresh) and what was named. In memory, per process. A load
    /// that throws is not cached, so an outage isn't remembered past its end. The local and topic resolvers don't use it: their rows are this
    /// database's own. On-call results must never go through it. Its first user is the identity resolver (phase-2-plan M16).
    /// </summary>
    /// <remarks>
    /// Bounded by people held as well as by entries, since one entry can hold up to <see cref="ResolverOptions.MaxRecipients"/> people: past either
    /// bound, expired entries go first, then the oldest, until the new one fits. Concurrent misses on one name each load it; there are as many as
    /// planner workers at most, and the next plan finds it cached.
    /// </remarks>
    public sealed class ResolverCache(IOptions<ResolverOptions> options, TimeProvider clock)
    {
        internal const int MaxEntries = 10_000;

        /// <summary>People held across all entries: about 1,000,000 short records, some hundreds of MB at worst.</summary>
        internal const int MaxPeople = 1_000_000;

        private readonly ConcurrentDictionary<(Guid Tenant, string Resolver, string Definition, string Name), (DateTimeOffset Until, ResolveResult Result)>
            _entries = new();

        private readonly Lock _trim = new();
        private int _count;
        private long _people;

        public async ValueTask<ResolveResult> GetOrLoadAsync(RecipientResolverConfig config, string name, Func<CancellationToken, ValueTask<ResolveResult>> load,
            CancellationToken ct)
        {
            var seconds = options.Value.CacheSeconds;
            if (seconds <= 0)
            {
                return await load(ct);
            }
            // The settings themselves, not the definition hash, which a writer other than the API (seeding) may not keep current.
            var key = (config.TenantId, config.Key, config.Kind + "\n" + config.Settings, name);
            if (_entries.TryGetValue(key, out var cached) && cached.Until > clock.GetUtcNow())
            {
                return cached.Result;
            }
            var result = await load(ct);
            if (result.Partial)
            {
                // Cut short by time (the step's deadline, the name's budget): a slow directory, so the next read may be whole. Kept, the part
                // would stand for the whole until the entry expired. One truncated by the scan bound is the same every time, and kept.
                return result;
            }
            var size = Size(result);
            if (size > MaxPeople)
            {
                return result; // too large to keep at all
            }
            lock (_trim)
            {
                var now = clock.GetUtcNow();
                if (_entries.TryRemove(key, out var old))
                {
                    _count--;
                    _people -= Size(old.Result);
                }
                if (_count + 1 > MaxEntries || _people + size > MaxPeople)
                {
                    foreach (var (k, entry) in _entries)
                    {
                        if (entry.Until <= now && _entries.TryRemove(k, out _))
                        {
                            _count--;
                            _people -= Size(entry.Result);
                        }
                    }
                    if (_count + 1 > MaxEntries || _people + size > MaxPeople)
                    {
                        // Then the oldest (the soonest to expire), only until the new entry fits.
                        foreach (var (k, entry) in _entries.OrderBy(e => e.Value.Until).ToList())
                        {
                            if (_count + 1 <= MaxEntries && _people + size <= MaxPeople)
                            {
                                break;
                            }
                            if (_entries.TryRemove(k, out _))
                            {
                                _count--;
                                _people -= Size(entry.Result);
                            }
                        }
                    }
                }
                _entries[key] = (now.AddSeconds(seconds), result);
                _count++;
                _people += size;
            }
            return result;
        }

        private static long Size(ResolveResult result) => result.People.Count + result.Destinations.Count;
    }
}
