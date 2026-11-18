using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Planning
{
    /// <summary>
    /// What a resolver is resolving for: the request, in its tenant, with the scope's DbContext, and the tenant's configuration of the resolver
    /// (null for the built-in <c>route</c> and <c>address</c>).
    /// </summary>
    /// <remarks>
    /// <see cref="MaxPeople"/> is the most people one name may reach (design §6.1 bounds). A resolver may return one more than that, and needn't
    /// read more: the planner then keeps the first <see cref="MaxPeople"/> and records the name as <c>Truncated</c>.
    /// </remarks>
    /// <remarks>
    /// A resolver returns each person once. It may stop at one past <see cref="MaxPeople"/> when there are more; past that many, the name is
    /// recorded as truncated.
    /// </remarks>
    public sealed record ResolveContext(NotificationsDbContext Db, NotificationRequest Request, DateTimeOffset Now, RecipientResolverConfig? Config,
        int MaxPeople)
    {
        /// <summary>
        /// Cancelled when the planner step's time for resolving is up (two thirds of its lease, from the step's start), whichever name is being
        /// resolved: a remote resolver stops there and reports itself unavailable (or what it read, truncated), so the step finishes before
        /// another worker can take it over. Not the worker's own token: the step still records its result.
        /// </summary>
        public CancellationToken StepDeadline { get; init; }
    }

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
    /// <remarks>
    /// <see cref="Contacts"/> are addresses the source has for the person, each verified or not by the source (phase-2-plan I15): stored as their
    /// contact points, a verified one marked verified by the source. A source never un-verifies one, and a person may remove one (it comes back
    /// the next time the source returns it).
    /// </remarks>
    public sealed record ResolvedPerson(string Source, string ExternalId, string? Name = null, string? Locale = null, string? TimeZone = null,
        IReadOnlyList<ResolvedContact>? Contacts = null);

    /// <summary>An address a source has for a person, on a registered channel (<c>email</c>), and whether the source verified it.</summary>
    public sealed record ResolvedContact(string Channel, string Address, bool Verified);

    /// <summary>
    /// What a name resolved to. <see cref="Truncated"/>: the source itself cut the list short (a remote directory's page limit), so the name is
    /// recorded as <c>Truncated</c> even when fewer than the bound came back.
    /// </summary>
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
    /// <c>contact-point</c>: the person's contact point the service's own request named (a verification code). The delivery planner reads the point;
    /// its address is never in the request or the recipient.
    /// </summary>
    public sealed class StaticContactPointResolver : IRecipientResolver
    {
        public string Key => "contact-point";

        public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { RecipientKinds.ContactPoint };

        public bool Configurable => false;

        public ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct)
            => ValueTask.FromResult(new ResolveResult([new ResolvedDestination(RecipientKind.ContactPoint, $"contact-point:{spec.Id}")]));
    }

    /// <summary>
    /// The planner (design §4.1 stage 2): claims a <c>Planning</c> notification, resolves what its request named into recipients (each due for the
    /// delivery planner), and marks it <c>Planned</c>, in one save; only the subscribers it maps people to are written before that, by an upsert
    /// that is safe to repeat (<see cref="SubscriberDirectory"/>). A request past its <c>expiresAt</c> becomes <c>Expired</c> with no
    /// recipients. Recipients are unique per notification, step and <see cref="ResolvedDestination.NamedAs"/>, so a second plan can't add a second set.
    /// </summary>
    /// <remarks>
    /// A resolver that throws <see cref="ResolverUnavailableException"/> doesn't fail the step (phase-2-plan I22): the notification stays in
    /// <c>Planning</c> with nothing written but when to try again, until <see cref="ResolverOptions.WaitLimit"/> after the first failure. Then it is
    /// planned with what resolved, and each name that didn't is a recipient with outcome <c>ResolverUnavailable</c>. A name reaching more than
    /// <see cref="ResolverOptions.MaxRecipients"/> people keeps the first of them, by source and external id, so a retry keeps the same ones, and
    /// is recorded as <c>Truncated</c>. Both records name what the request named (<c>group:sre</c>), have no subscriber, and are never due.
    /// </remarks>
    public sealed class PlannerStage(IEnumerable<IRecipientResolver> resolvers, IOptions<ResolverOptions> resolverOptions, ILogger<PlannerStage> logger,
        IChannelRegistry channels, People.ContactCrypto crypto, People.MuteMatches mutes, Workflows.WorkflowRunner workflows, Events.ProducerEvents events,
        Storms.StormGuard? storms = null)
        : IStage<Notification>
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
            if (row.State is not (NotificationState.Planning or NotificationState.Running))
            {
                context.Done(row, null);
                return;
            }
            var request = await db.Requests.AsNoTracking().SingleAsync(r => r.TenantId == row.TenantId && r.Id == row.RequestId, ct);
            if (row.State == NotificationState.Running)
            {
                // Planned already: the type's workflow goes on from its step (phase-3-plan I27).
                await workflows.RunAsync(context, row, request, ct);
                return;
            }
            using var activity = Telemetry.StartStep("notifications.plan", request.TraceParent);
            activity?.SetTag("notifications.notification_id", row.Id);
            if (request.ExpiresAt is { } expires && expires <= context.Now)
            {
                row.State = NotificationState.Expired;
                context.Done(row, null);
                return;
            }
            // An escalation step (phase-3-plan I34): its policy's names, not the request's, sent at once whatever the workflow.
            var escalating = row.EscalationStep > 0 && row.EscalationSpecs is not null;
            var specs = JsonSerializer.Deserialize<List<RecipientSpec>>(escalating ? row.EscalationSpecs! : request.Recipients, IntakeQueries.Json) ?? [];
            var configs = specs.Any(s => s.Resolver is not null)
                ? await db.Resolvers.AsNoTracking().Where(c => c.TenantId == row.TenantId && c.Enabled).ToListAsync(ct)
                : [];
            var bounds = resolverOptions.Value;
            // Resolving may take two thirds of the lease; the rest is for mapping people and saving (the lease runs from the step's start).
            using var stepDeadline = new CancellationTokenSource(context.Lease * 2 / 3);
            var planned = new Dictionary<string, (ResolvedDestination Destination, string Resolver)>(StringComparer.Ordinal);
            // Names that couldn't be resolved now, and names that reached more people than the bound, each with its resolver.
            var unavailable = new Dictionary<string, string>(StringComparer.Ordinal);
            var truncated = new Dictionary<string, string>(StringComparer.Ordinal);
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
                var resolverKey = config?.Key ?? resolver.Key;
                var name = spec.Name;
                ResolveResult result;
                try
                {
                    result = await resolver.ResolveAsync(spec, new ResolveContext(db, request, context.Now, config, bounds.MaxRecipients) { StepDeadline = stepDeadline.Token }, ct);
                }
                catch (ResolverUnavailableException ex) when (!ct.IsCancellationRequested)
                {
                    if (context.Now - (row.ResolverWaitSince ?? context.Now) < bounds.WaitLimit)
                    {
                        // Still waiting: nothing this try resolved will be kept, so the other names needn't be resolved either.
                        Wait(context, row, bounds, resolverKey, name, ex);
                        return;
                    }
                    logger.LogWarning(ex, "Resolver {Resolver} is still unavailable for {Name} in tenant {Tenant} after the wait limit; notification {Id} is planned without it",
                        resolverKey, name, row.TenantId, row.Id);
                    unavailable.TryAdd(name, resolverKey);
                    continue;
                }
                foreach (var destination in result.Destinations)
                {
                    planned.TryAdd(destination.NamedAs, (destination, resolverKey));
                }
                // People who can't be subscribers are dropped before the bound, so they don't take the places of people who can.
                var returnedPeople = new List<ResolvedPerson>(result.People.Count);
                string? firstProblem = null;
                foreach (var returned in result.People)
                {
                    if (SubscriberDirectory.Problem(returned) is { } problem)
                    {
                        firstProblem ??= problem;
                        continue;
                    }
                    returnedPeople.Add(returned);
                }
                if (firstProblem is not null)
                {
                    logger.LogWarning("Resolver {Resolver} returned {Count} people who can't be subscribers (the first: {Problem}) for {Name} in tenant {Tenant}; notification {Id} skips them",
                        resolverKey, result.People.Count - returnedPeople.Count, firstProblem, name, row.TenantId, row.Id);
                }
                // Past the bound by what the resolver returned, before any were dropped: it may have stopped at one past the bound, so a person
                // dropped above doesn't show there were no more.
                if (result.Truncated || result.People.Count > bounds.MaxRecipients)
                {
                    // The first in code point order (the byte order of COLLATE "C"): the same people on a retry, whatever order the source gave.
                    returnedPeople = returnedPeople.DistinctBy(p => (p.Source, p.ExternalId)).Order(CodePointOrder.People).Take(bounds.MaxRecipients).ToList();
                    truncated.TryAdd(name, resolverKey);
                }
                foreach (var returned in returnedPeople)
                {
                    var person = SubscriberDirectory.Fit(returned);
                    if (!people.TryGetValue((person.Source, person.ExternalId), out var entry))
                    {
                        entry = (person, new SortedSet<string>(StringComparer.Ordinal), resolverKey);
                        people[(person.Source, person.ExternalId)] = entry;
                    }
                    entry.Via.Add(name);
                }
            }
            foreach (var (named, resolverKey) in unavailable)
            {
                db.Recipients.Add(Unreached(row, named, resolverKey, RecipientOutcome.ResolverUnavailable, context.Now));
                // One record per name: one named twice, through a resolver that failed and another that truncated, reads as unavailable.
                truncated.Remove(named);
            }
            if (unavailable.Count > 0)
            {
                // The producer hears who wasn't reached (design §5.4, phase-3-plan I37), saved with this plan.
                var names = unavailable.Keys.Order(StringComparer.Ordinal).ToList();
                await events.RaiseAsync(db, row.TenantId, request.ApplicationId, app => new Events.NotificationEvent
                {
                    Event = Events.ProducerEventNames.RecipientUnresolved, Application = app, NotificationTenantId = row.TenantId,
                    CorrelationKey = request.CorrelationKey, NotificationId = row.Id, RequestId = request.Id, Type = request.TypeKey, Names = names,
                    Resolver = string.Join(",", unavailable.Values.Distinct().Order(StringComparer.Ordinal)), OccurredAt = context.Now,
                }, ct);
            }
            foreach (var (named, resolverKey) in truncated)
            {
                logger.LogWarning("{Name} reached more than {Max} people in tenant {Tenant}; notification {Id} keeps the first of them",
                    named, bounds.MaxRecipients, row.TenantId, row.Id);
                db.Recipients.Add(Unreached(row, named, resolverKey, RecipientOutcome.Truncated, context.Now));
            }
            if (unavailable.Count > 0 || truncated.Count > 0)
            {
                var (unresolvedCount, truncatedCount) = (unavailable.Count, truncated.Count);
                context.AfterCommit.Add(() =>
                {
                    if (unresolvedCount > 0)
                    {
                        Telemetry.RecipientsUnresolved.Add(unresolvedCount);
                    }
                    if (truncatedCount > 0)
                    {
                        Telemetry.RecipientsTruncated.Add(truncatedCount);
                    }
                });
            }
            // Who is to hear: mutes and preferences decide among these (design §6.1 step 4) once all are known.
            var due = new List<NotificationRecipient>();
            foreach (var (d, resolverKey) in planned.Values)
            {
                due.Add(new NotificationRecipient
                {
                    TenantId = row.TenantId, NotificationId = row.Id, Kind = d.Kind, RouteId = d.RouteId, RouteKey = d.RouteKey, Channel = d.Channel,
                    Address = d.Address, Resolver = resolverKey, NamedAs = d.NamedAs, Outcome = RecipientOutcome.Planned, NextAt = context.Now,
                    CreatedAt = context.Now, // was left unset: every recipient read as created in year 1 (found by the M9 load check)
                });
            }
            if (people.Count > 0)
            {
                var subscribers = await SubscriberDirectory.MapAsync(db, row.TenantId, people.Values.Select(p => p.Person).ToList(), context.Now,
                    new SourceContacts(crypto, channels, logger), ct);
                // Two identities can be one subscriber (an alias): their names join on the one recipient.
                foreach (var group in people.Values.GroupBy(p => subscribers[(p.Person.Source, p.Person.ExternalId)]).OrderBy(g => g.Key))
                {
                    var via = group.SelectMany(p => p.Via).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    due.Add(new NotificationRecipient
                    {
                        TenantId = row.TenantId, NotificationId = row.Id, Kind = RecipientKind.Subscriber, SubscriberId = group.Key, Via = via,
                        Resolver = group.First().Resolver, NamedAs = "subscriber:" + group.Key, Outcome = RecipientOutcome.Planned, NextAt = context.Now,
                        CreatedAt = context.Now,
                    });
                }
            }
            foreach (var recipient in due)
            {
                recipient.EscalationStep = row.EscalationStep;
            }
            if (escalating)
            {
                // Nobody twice: anyone the notification reached already (the request's plan, an earlier step) isn't added again.
                var reached = (await db.Recipients.AsNoTracking().Where(r => r.TenantId == row.TenantId && r.NotificationId == row.Id && r.Kind != RecipientKind.Name)
                    .Select(r => r.NamedAs).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
                due.RemoveAll(r => reached.Contains(r.NamedAs));
                if (!due.Any(r => r.Kind != RecipientKind.Name))
                {
                    // Nobody new: the next step runs at once, not after its wait (design §7.1).
                    await db.Escalations.Where(e => e.TenantId == row.TenantId && e.NotificationId == row.Id && e.State == EscalationState.Running)
                        .ExecuteUpdateAsync(s => s.SetProperty(e => e.NextAt, context.Now), ct);
                    logger.LogInformation("Escalation step {Step} of notification {Id} in tenant {Tenant} reached nobody new", row.EscalationStep, row.Id, row.TenantId);
                }
            }
            var type = await db.Types.AsNoTracking().SingleAsync(t => t.Id == request.TypeId, ct);
            // Storm protection (phase-4-plan I43): a route storming now takes its recipients as a digest, which the workflow's send step does
            // (and counts them there). Otherwise this notification counts on its routes here, and may start a storm for the ones after it.
            var stormy = false;
            if (!escalating && storms is not null && due.Any(r => r.RouteKey is not null))
            {
                var routes = due.Where(r => r.RouteKey is not null).GroupBy(r => r.RouteKey!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                stormy = await storms.AnyOpenAsync(db, row.TenantId, routes.Keys, ct);
                if (!stormy && workflows.For(type).IsSendOnly)
                {
                    await storms.CountAsync(db, row.TenantId, routes, context.Now, context.AfterCommit, ct);
                }
            }
            if (!escalating && (!workflows.For(type).IsSendOnly || stormy))
            {
                // A workflow: the people wait (Pending, not due) until its send step, which applies mutes and preferences as they are then. The
                // notification runs its workflow on the next claim, at once.
                foreach (var recipient in due)
                {
                    (recipient.Outcome, recipient.NextAt) = (RecipientOutcome.Pending, null);
                }
                db.Recipients.AddRange(due);
                row.State = NotificationState.Running;
                row.WorkflowStep = 0;
                row.ResolverWaitSince = null;
                context.Done(row, context.Now);
                context.Wakes.Add(StageName);
                return;
            }
            if (due.Count > 0)
            {
                await People.PreferencePolicy.ApplyAsync(db, request, type, due, mutes, context.Now, ct);
                db.Recipients.AddRange(due);
                // Those that won't hear are done here; the delivery planner counts the rest when it is through with them.
                var held = due.Where(r => r.Outcome != RecipientOutcome.Planned).GroupBy(r => r.Outcome).Select(g => (Outcome: g.Key.ToString(), Count: g.Count())).ToList();
                if (held.Count > 0)
                {
                    context.AfterCommit.Add(() =>
                    {
                        foreach (var (outcome, count) in held)
                        {
                            Telemetry.Recipients.Add(count, new KeyValuePair<string, object?>("outcome", outcome));
                        }
                    });
                }
            }
            row.State = NotificationState.Planned;
            // The wait belonged to this plan: a later one (an escalation step) waits afresh. The markers keep what it left out.
            row.ResolverWaitSince = null;
            context.Done(row, null);
            if (due.Any(r => r.Outcome == RecipientOutcome.Planned))
            {
                context.Wakes.Add(DeliveryStageNames.DeliveryPlanner);
            }
        }

        /// <summary>
        /// Puts the notification off until the resolver may be back, writing nothing else: the next try resolves every name again, in one step,
        /// so a partial plan is never stored. Each wait is as long as the wait so far (so it doubles), within the bounds, and the last try falls on
        /// the limit itself. The step's failure count is kept, so a fault elsewhere still adds up toward giving the row up.
        /// </summary>
        private void Wait(StageContext context, Notification row, ResolverOptions bounds, string resolver, string name, Exception ex)
        {
            if (row.ResolverWaitSince is null)
            {
                logger.LogWarning(ex, "Resolver {Resolver} is unavailable for {Name} in tenant {Tenant}; notification {Id} waits for it up to {Limit}",
                    resolver, name, row.TenantId, row.Id, bounds.WaitLimit);
            }
            else
            {
                logger.LogInformation("Resolver {Resolver} is still unavailable for {Name} in tenant {Tenant}; notification {Id} waits: {Error}",
                    resolver, name, row.TenantId, row.Id, ex.Message);
            }
            var since = row.ResolverWaitSince ??= context.Now;
            var waited = context.Now - since;
            var next = context.Now + TimeSpan.FromTicks(Math.Clamp(waited.Ticks, bounds.RetryMin.Ticks, bounds.RetryMax.Ticks));
            var failures = row.StepFailures;
            context.Done(row, next < since + bounds.WaitLimit ? next : since + bounds.WaitLimit);
            row.StepFailures = failures;
        }

        /// <summary>
        /// A record of a name that reached no one, or not everyone it named: it names what the request named, has no subscriber, and is never
        /// due, so the delivery planner never takes it.
        /// </summary>
        private static NotificationRecipient Unreached(Notification row, string named, string resolver, RecipientOutcome outcome, DateTimeOffset now) => new()
        {
            EscalationStep = row.EscalationStep,
            TenantId = row.TenantId, NotificationId = row.Id, Kind = RecipientKind.Name, Resolver = resolver, NamedAs = named, Via = [named],
            Outcome = outcome, NextAt = null, CreatedAt = now,
        };

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
            services.AddSingleton<IRecipientResolver, StaticContactPointResolver>();
            services.AddSingleton<IRecipientResolver, LocalRecipientResolver>();
            services.AddSingleton<IRecipientResolver, TopicRecipientResolver>();
            // The identity resolver reads the deployment's directories (Identity:Directories; phase-2-plan M16).
            services.AddSingleton<IRecipientResolver, IdentityRecipientResolver>();
            services.TryAddSingleton<IIdentityDirectory, KeycloakIdentityDirectory>();
            // Says which directory is wrong and how (IdentityOptions.Problem).
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<IdentityOptions>, IdentityOptionsValidation>());
            services.AddOptions<IdentityOptions>().ValidateOnStart();
            services.AddOptions<ResolverOptions>()
                .Validate(o => o.MaxRecipients is > 0 and <= 1_000_000 && o.WaitLimit >= TimeSpan.Zero && o.RetryMin > TimeSpan.Zero
                    && o.RetryMax >= o.RetryMin && o.CacheSeconds >= 0,
                    "Resolvers: MaxRecipients is 1–1,000,000, WaitLimit and CacheSeconds aren't negative, and 0 < RetryMin ≤ RetryMax.")
                .ValidateOnStart();
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<ResolverCache>();
            services.AddSingleton<Workflows.WorkflowRunner>();
            services.AddOptions<Storms.StormOptions>();
            services.AddSingleton<Storms.StormGuard>();
            // Events to producers (phase-3-plan I37): off unless Events:Enabled; the relay is the host's to run.
            services.AddOptions<Events.EventsOptions>();
            services.TryAddSingleton<Events.ProducerEvents>();
            // Escalations run on their own claims (phase-3-plan I34), and hand their steps to this stage.
            services.AddStage<Escalation, Workflows.EscalationStage>(options, runWorkers);
            services.TryAddSingleton<People.MuteMatches>();
            // Contact points a source vouches for are stored encrypted, on registered channels.
            services.AddBuiltInChannels();
            People.ContactEncryptionRegistration.AddContactEncryption(services);
            return services.AddStage<Notification, PlannerStage>(options, runWorkers);
        }
    }
}
