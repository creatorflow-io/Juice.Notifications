using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Planning
{
    /// <summary>
    /// What a resolver is resolving for: the request, in its tenant, with the scope's DbContext, and the tenant's configuration of the resolver
    /// (null for the built-in <c>route</c> and <c>address</c>).
    /// </summary>
    public sealed record ResolveContext(NotificationsDbContext Db, NotificationRequest Request, DateTimeOffset Now, RecipientResolverConfig? Config = null);

    /// <summary>
    /// A destination a resolver produced (design §6.1). <see cref="NamedAs"/> says what was named and is unique within a notification's step:
    /// <c>route:ops-critical</c>, <c>address:email:ops@example.com</c>.
    /// </summary>
    public sealed record ResolvedDestination(RecipientKind Kind, string NamedAs, Guid? RouteId = null, string? RouteKey = null, string? Channel = null,
        string? Address = null);

    /// <summary>
    /// A person a resolver produced, by its source and that source's id for them (design D23): <c>local</c> and a local user's key,
    /// <c>oidc</c> and the provider's subject. Name, locale and time zone refresh the subscriber when the source gives them.
    /// </summary>
    public sealed record ResolvedPerson(string Source, string ExternalId, string? Name = null, string? Locale = null, string? TimeZone = null);

    public sealed record ResolveResult(IReadOnlyList<ResolvedDestination> Destinations, bool Truncated = false)
    {
        public IReadOnlyList<ResolvedPerson> People { get; init; } = [];
    }

    /// <summary>
    /// Turns what a request names into recipients (design §6.1, D20). <c>route</c> and <c>address</c> are built in; the others are
    /// <see cref="Configurable"/>: a tenant enables them in <c>recipient_resolvers</c>, where <see cref="Key"/> is the row's <c>kind</c>.
    /// </summary>
    public interface IRecipientResolver
    {
        string Key { get; }

        IReadOnlySet<string> Kinds { get; }

        /// <summary>Configured per tenant (true), or always on for every tenant (the built-in ones).</summary>
        bool Configurable => true;

        /// <summary>The problem with a tenant's settings for it, or null when they're fine. Called when the configuration is saved.</summary>
        string? CheckSettings(string settingsJson) => null;

        ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct);
    }

    /// <summary><c>route</c>: one route recipient per enabled route of the tenant that matches the request.</summary>
    public sealed class RouteRecipientResolver(ILogger<RouteRecipientResolver> logger) : IRecipientResolver
    {
        public string Key => "route";

        public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { RecipientKinds.Route };

        public bool Configurable => false;

        /// <summary>
        /// Parsed matches by route, valid while the route's match text is the same: parsing builds regexes, which isn't per-notification work.
        /// Keyed on the text itself, not the definition hash, which a writer other than the API (seeding, provisioning) may not keep current.
        /// Held by this (singleton) resolver, and emptied when it outgrows <see cref="MaxCachedRoutes"/>, so deleted routes don't pile up.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (string Text, RouteMatch? Match)> _parsed = new();
        private const int MaxCachedRoutes = 10_000;

        public async ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct)
        {
            var request = context.Request;
            var routes = await context.Db.Routes.AsNoTracking().Where(r => r.TenantId == request.TenantId && r.Enabled).OrderBy(r => r.Key).ToListAsync(ct);
            var matched = new List<ResolvedDestination>();
            foreach (var route in routes)
            {
                if (!_parsed.TryGetValue(route.Id, out var cached) || !string.Equals(cached.Text, route.Match, StringComparison.Ordinal))
                {
                    if (_parsed.Count >= MaxCachedRoutes)
                    {
                        _parsed.Clear();
                    }
                    var (parsed, errors) = RouteMatch.Parse(JsonElement.Parse(route.Match));
                    if (parsed is null)
                    {
                        // Matches are checked when saved; one that no longer parses is skipped, not allowed to stop the others.
                        logger.LogWarning("Route {Route} in tenant {Tenant} has an invalid match and is skipped: {Errors}", route.Key, route.TenantId,
                            string.Join("; ", errors.Select(e => e.Message)));
                    }
                    cached = (route.Match, parsed);
                    _parsed[route.Id] = cached;
                }
                if (cached.Match is not { } match)
                {
                    continue;
                }
                if (match.Matches(request.TypeKey, request.Labels, request.Priority))
                {
                    matched.Add(new ResolvedDestination(RecipientKind.Route, "route:" + route.Key, route.Id, route.Key));
                }
            }
            return new ResolveResult(matched);
        }
    }

    /// <summary><c>address</c>: the external address itself, already checked and normalized at intake.</summary>
    public sealed class StaticAddressResolver : IRecipientResolver
    {
        public string Key => "address";

        public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { RecipientKinds.Address };

        public bool Configurable => false;

        public ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct)
            => ValueTask.FromResult(new ResolveResult(
                [new ResolvedDestination(RecipientKind.External, $"address:{spec.Channel}:{spec.Address}", Channel: spec.Channel, Address: spec.Address)]));
    }

    /// <summary>
    /// The planner (design §4.1 stage 2): claims a <c>Planning</c> notification, resolves what its request named into recipients (each due for the
    /// delivery planner), and marks it <c>Planned</c>, in one save; only the subscribers it maps people to are written before that, by an upsert
    /// that is safe to repeat (<see cref="SubscriberDirectory"/>). A request past its <c>expiresAt</c> becomes <c>Expired</c> with no
    /// recipients. Recipients are unique per notification, step and <see cref="ResolvedDestination.NamedAs"/>, so a second plan can't add a second set.
    /// </summary>
    public sealed class PlannerStage(IEnumerable<IRecipientResolver> resolvers, ILogger<PlannerStage> logger) : IStage<Notification>
    {
        public const string StageName = "planner";

        public string Name => StageName;

        public void GiveUp(StageContext context, Notification row, string error)
        {
            row.State = NotificationState.Failed;
            var message = "Planning failed repeatedly: " + error;
            row.Error = message.Length > 2000 ? message[..2000] : message; // the column's length
            row.UpdatedAt = context.Now;
            context.Done(row, null);
        }

        public async Task ExecuteAsync(StageContext context, Notification row, CancellationToken ct)
        {
            var db = context.Db;
            row.UpdatedAt = context.Now;
            if (row.State != NotificationState.Planning)
            {
                context.Done(row, null);
                return;
            }
            var request = await db.Requests.AsNoTracking().SingleAsync(r => r.TenantId == row.TenantId && r.Id == row.RequestId, ct);
            using var activity = Telemetry.StartStep("notifications.plan", request.TraceParent);
            activity?.SetTag("notifications.notification_id", row.Id);
            if (request.ExpiresAt is { } expires && expires <= context.Now)
            {
                row.State = NotificationState.Expired;
                context.Done(row, null);
                return;
            }
            var specs = JsonSerializer.Deserialize<List<RecipientSpec>>(request.Recipients, IntakeQueries.Json) ?? [];
            var configs = specs.Any(s => s.Resolver is not null)
                ? await db.Resolvers.AsNoTracking().Where(c => c.TenantId == row.TenantId && c.Enabled).ToListAsync(ct)
                : [];
            var planned = new Dictionary<string, (ResolvedDestination Destination, string Resolver)>(StringComparer.Ordinal);
            // A person reached through several names is one recipient: the names, and the first resolver that reached them, by person.
            var people = new Dictionary<(string Source, string ExternalId), (ResolvedPerson Person, SortedSet<string> Via, string Resolver)>();
            foreach (var spec in specs)
            {
                var (resolver, config) = Pick(spec, configs);
                if (resolver is null)
                {
                    logger.LogWarning("No resolver handles recipients of kind {Kind} (resolver {Resolver}) in tenant {Tenant}; notification {Id} skips it",
                        spec.Kind, spec.Resolver, row.TenantId, row.Id);
                    continue;
                }
                var result = await resolver.ResolveAsync(spec, new ResolveContext(db, request, context.Now, config), ct);
                foreach (var destination in result.Destinations)
                {
                    planned.TryAdd(destination.NamedAs, (destination, config?.Key ?? resolver.Key));
                }
                var name = spec.Name;
                foreach (var returned in result.People)
                {
                    if (SubscriberDirectory.Problem(returned) is { } problem)
                    {
                        logger.LogWarning("Resolver {Resolver} returned a person who can't be a subscriber ({Problem}) for {Name} in tenant {Tenant}; notification {Id} skips them",
                            config?.Key ?? resolver.Key, problem, name, row.TenantId, row.Id);
                        continue;
                    }
                    var person = SubscriberDirectory.Fit(returned);
                    if (!people.TryGetValue((person.Source, person.ExternalId), out var entry))
                    {
                        entry = (person, new SortedSet<string>(StringComparer.Ordinal), config?.Key ?? resolver.Key);
                        people[(person.Source, person.ExternalId)] = entry;
                    }
                    entry.Via.Add(name);
                }
            }
            foreach (var (d, resolverKey) in planned.Values)
            {
                db.Recipients.Add(new NotificationRecipient
                {
                    TenantId = row.TenantId, NotificationId = row.Id, Kind = d.Kind, RouteId = d.RouteId, RouteKey = d.RouteKey, Channel = d.Channel,
                    Address = d.Address, Resolver = resolverKey, NamedAs = d.NamedAs, Outcome = RecipientOutcome.Planned, NextAt = context.Now,
                    CreatedAt = context.Now, // was left unset: every recipient read as created in year 1 (found by the M9 load check)
                });
            }
            var recipients = planned.Count;
            if (people.Count > 0)
            {
                var subscribers = await SubscriberDirectory.MapAsync(db, row.TenantId, people.Values.Select(p => p.Person).ToList(), context.Now, ct);
                // Two identities can be one subscriber (an alias): their names join on the one recipient.
                foreach (var group in people.Values.GroupBy(p => subscribers[(p.Person.Source, p.Person.ExternalId)]).OrderBy(g => g.Key))
                {
                    var via = group.SelectMany(p => p.Via).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    db.Recipients.Add(new NotificationRecipient
                    {
                        TenantId = row.TenantId, NotificationId = row.Id, Kind = RecipientKind.Subscriber, SubscriberId = group.Key, Via = via,
                        Resolver = group.First().Resolver, NamedAs = "subscriber:" + group.Key, Outcome = RecipientOutcome.Planned, NextAt = context.Now,
                        CreatedAt = context.Now,
                    });
                    recipients++;
                }
            }
            row.State = NotificationState.Planned;
            context.Done(row, null);
            if (recipients > 0)
            {
                context.Wakes.Add(DeliveryStageNames.DeliveryPlanner);
            }
        }

        /// <summary>
        /// The resolver for a recipient: a built-in one for its kind, or the tenant's configuration intake chose (<see cref="RecipientSpec.Resolver"/>),
        /// if it is still enabled, still handles the kind, and its implementation is registered.
        /// </summary>
        private (IRecipientResolver? Resolver, RecipientResolverConfig? Config) Pick(RecipientSpec spec, List<RecipientResolverConfig> configs)
        {
            if (spec.Kind is null)
            {
                return (null, null);
            }
            if (spec.Resolver is null)
            {
                return (resolvers.FirstOrDefault(r => !r.Configurable && r.Kinds.Contains(spec.Kind)), null);
            }
            var config = configs.FirstOrDefault(c => c.Key == spec.Resolver && c.Handles.Contains(spec.Kind));
            var resolver = config is null ? null : resolvers.FirstOrDefault(r => r.Configurable && r.Key == config.Kind && r.Kinds.Contains(spec.Kind));
            return resolver is null ? (null, null) : (resolver, config);
        }
    }

    /// <summary>Stage names other stages wake.</summary>
    public static class DeliveryStageNames
    {
        public const string DeliveryPlanner = "delivery-planner";
        public const string Dispatcher = "dispatcher";
    }

    public static class PlanningRegistration
    {
        public static IServiceCollection AddPlanning(this IServiceCollection services, StageOptions options, bool runWorkers = true)
        {
            services.AddSingleton<IRecipientResolver, RouteRecipientResolver>();
            services.AddSingleton<IRecipientResolver, StaticAddressResolver>();
            return services.AddStage<Notification, PlannerStage>(options, runWorkers);
        }
    }
}
