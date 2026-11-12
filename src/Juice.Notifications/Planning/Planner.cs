using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Planning
{
    /// <summary>What a resolver is resolving for: the request, in its tenant, with the scope's DbContext.</summary>
    public sealed record ResolveContext(NotificationsDbContext Db, NotificationRequest Request, DateTimeOffset Now);

    /// <summary>
    /// A destination a resolver produced (design §6.1). <see cref="NamedAs"/> says what was named and is unique within a notification's step:
    /// <c>route:ops-critical</c>, <c>address:email:ops@example.com</c>. People (subscribers) join in phase 2.
    /// </summary>
    public sealed record ResolvedDestination(RecipientKind Kind, string NamedAs, Guid? RouteId = null, string? RouteKey = null, string? Channel = null,
        string? Address = null);

    public sealed record ResolveResult(IReadOnlyList<ResolvedDestination> Destinations, bool Truncated = false);

    /// <summary>Turns what a request names into recipients (design §6.1, D20). Phase 1: <c>route</c> and <c>address</c>.</summary>
    public interface IRecipientResolver
    {
        string Key { get; }

        IReadOnlySet<string> Kinds { get; }

        ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct);
    }

    /// <summary><c>route</c>: one route recipient per enabled route of the tenant that matches the request.</summary>
    public sealed class RouteRecipientResolver(ILogger<RouteRecipientResolver> logger) : IRecipientResolver
    {
        public string Key => "route";

        public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { RecipientKinds.Route };

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

        public ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct)
            => ValueTask.FromResult(new ResolveResult(
                [new ResolvedDestination(RecipientKind.External, $"address:{spec.Channel}:{spec.Address}", Channel: spec.Channel, Address: spec.Address)]));
    }

    /// <summary>
    /// The planner (design §4.1 stage 2): claims a <c>Planning</c> notification, resolves what its request named into recipients (each due for the
    /// delivery planner), and marks it <c>Planned</c>, all in one transaction. A request past its <c>expiresAt</c> becomes <c>Expired</c> with no
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
            var resolve = new ResolveContext(db, request, context.Now);
            var planned = new Dictionary<string, (ResolvedDestination Destination, string Resolver)>(StringComparer.Ordinal);
            foreach (var spec in specs)
            {
                var resolver = resolvers.FirstOrDefault(r => spec.Kind is not null && r.Kinds.Contains(spec.Kind));
                if (resolver is null)
                {
                    logger.LogWarning("No resolver handles recipients of kind {Kind}; notification {Id} skips it", spec.Kind, row.Id);
                    continue;
                }
                foreach (var destination in (await resolver.ResolveAsync(spec, resolve, ct)).Destinations)
                {
                    planned.TryAdd(destination.NamedAs, (destination, resolver.Key));
                }
            }
            foreach (var (d, resolverKey) in planned.Values)
            {
                db.Recipients.Add(new NotificationRecipient
                {
                    TenantId = row.TenantId, NotificationId = row.Id, Kind = d.Kind, RouteId = d.RouteId, RouteKey = d.RouteKey, Channel = d.Channel,
                    Address = d.Address, Resolver = resolverKey, NamedAs = d.NamedAs, Outcome = RecipientOutcome.Planned, NextAt = context.Now,
                });
            }
            row.State = NotificationState.Planned;
            context.Done(row, null);
            if (planned.Count > 0)
            {
                context.Wakes.Add(DeliveryStageNames.DeliveryPlanner);
            }
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
