using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.People;
using Juice.Notifications.Planning;
using Juice.Notifications.Rendering;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Delivering
{
    /// <summary>Destination keys (design §3): canonical, so uniqueness doesn't depend on which table a destination lives in.</summary>
    public static class DestinationKeys
    {
        public static string Target(Guid targetId) => $"it:{targetId}";

        public static string External(string channel, string address) => $"ext:{channel}:{address}";

        /// <summary>A person's contact point (design §3): its id, never its address, which is encrypted (phase-2-plan I19).</summary>
        public static string ContactPoint(Guid contactPointId) => $"cp:{contactPointId}";

        /// <summary>A person's inbox (design §3, phase-2-plan I17).</summary>
        public static string Inbox(Guid subscriberId) => $"inbox:{subscriberId}";
    }

    /// <summary>
    /// The delivery planner (design §4.1 stage 4): claims a planned recipient and turns it into deliveries, one per destination and channel, each
    /// with its integration, template version, locale and content rendered once and stored before any attempt (D10). Route recipients reach
    /// their route's enabled targets; an external address goes through the tenant's first enabled integration for its channel. A destination
    /// that can't be served (no integration, no template, a render failure) still gets a delivery, failed with its reason, so history shows it.
    /// <c>UNIQUE (RecipientId, DestinationKey, Channel)</c> on items stops a recipient reaching a destination twice.
    /// </summary>
    /// <summary>The stage's step-failure limit, which the planner's timeout retries have to stay under.</summary>
    public sealed record DeliveryPlannerLimits(int MaxStepFailures);

    public sealed class DeliveryPlannerStage(ILogger<DeliveryPlannerStage> logger, IChannelRegistry channels, Inbox.InboxIntegrations inboxes,
        DeliveryPlannerLimits? limits = null) : IStage<NotificationRecipient>
    {
        public string Name => DeliveryStageNames.DeliveryPlanner;

        private sealed record Destination(string Key, string Channel, Integration? Integration, string Snapshot, string? Problem);

        /// <summary>
        /// Render timeouts tolerated on one recipient before the next is recorded as a failed delivery: a timeout can be the machine's load,
        /// so it is retried; one that keeps happening is the template's, and shows in history rather than only in the log.
        /// </summary>
        private const int TimeoutsBeforeFailing = 2;

        /// <summary>Fewer when the stage gives a row up sooner: the last try records the timeout, so other channels still go out.</summary>
        private int TimeoutsAllowed => Math.Max(0, Math.Min(TimeoutsBeforeFailing, (limits?.MaxStepFailures ?? int.MaxValue) - 1));

        /// <summary>Snapshots keep an address as written (no 0026 for each &amp;), so history reads it and it stays its own length.</summary>
        private static readonly JsonSerializerOptions Snapshot = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public void GiveUp(StageContext context, NotificationRecipient row, string error)
        {
            // The runner logged the error; the outcome answers "why didn't I get it?". One deferred by quiet hours may already have a delivery
            // (its inbox item) from an earlier step: it stays planned, and history shows how that went. Only a recipient still being planned
            // is changed: one held or finished keeps its outcome. Synchronous because the runner's hook is; it runs once per row given up.
            if (row.Outcome is RecipientOutcome.Planned or RecipientOutcome.Deferred)
            {
                row.Outcome = Deliveries(context.Db, row).Any() ? RecipientOutcome.Planned : RecipientOutcome.Failed;
            }
            context.Done(row, null);
            Counted(context, row);
        }

        /// <summary>
        /// The recipient's deliveries on record, however each ended. A recipient with any is <c>Planned</c> (as in phase 1): history, not the
        /// outcome, says whether each went out, failed or expired, and a retried one changes only its own row.
        /// </summary>
        private static IQueryable<DeliveryItem> Deliveries(NotificationsDbContext db, NotificationRecipient row)
            => db.DeliveryItems.AsNoTracking().Where(i => i.TenantId == row.TenantId && i.RecipientId == row.Id);

        /// <summary>A recipient's outcome once delivery planning is through with it (Planned: it has deliveries).</summary>
        private static void Counted(StageContext context, NotificationRecipient row)
            => context.AfterCommit.Add(() => Telemetry.Recipients.Add(1, new KeyValuePair<string, object?>("outcome", row.Outcome.ToString())));

        public async Task ExecuteAsync(StageContext context, NotificationRecipient row, CancellationToken ct)
        {
            var db = context.Db;
            // Deferred: a person's personal channels held by quiet hours, due again when they end (phase-2-plan I21).
            if (row.Outcome is not (RecipientOutcome.Planned or RecipientOutcome.Deferred))
            {
                context.Done(row, null);
                return;
            }
            var notification = await db.Notifications.AsNoTracking().SingleAsync(n => n.TenantId == row.TenantId && n.Id == row.NotificationId, ct);
            var request = await db.Requests.AsNoTracking().SingleAsync(r => r.TenantId == row.TenantId && r.Id == notification.RequestId, ct);
            using var activity = Telemetry.StartStep("notifications.deliveries", request.TraceParent);
            activity?.SetTag("notifications.recipient_id", row.Id);
            if (request.ExpiresAt is { } expires && expires <= context.Now)
            {
                // A deferred one already has its inbox item, if any: it stays planned and history shows how that went. Its held channels won't go now.
                row.Outcome = row.Outcome == RecipientOutcome.Deferred && await Deliveries(db, row).AnyAsync(ct) ? RecipientOutcome.Planned : RecipientOutcome.Cancelled;
                context.Done(row, null);
                Counted(context, row);
                return;
            }
            var type = await db.Types.AsNoTracking().SingleAsync(t => t.Id == request.TypeId, ct);
            var model = new RenderModel(type.Key, type.Version, type.Name, request.Priority, JsonElement.Parse(request.Data), request.Labels, request.Links);

            var created = 0;
            // What this recipient already has (a retried step), read once; and each channel's content, rendered once for all its destinations.
            var existing = (await db.DeliveryItems.AsNoTracking().Where(i => i.TenantId == row.TenantId && i.RecipientId == row.Id)
                .Select(i => new { i.DestinationKey, i.Channel }).ToListAsync(ct)).Select(i => (i.DestinationKey, i.Channel)).ToHashSet();
            var rendered = new Dictionary<string, Rendered>(StringComparer.Ordinal);
            var destinations = await DestinationsAsync(db, row, type, ct);
            var (turnedOff, deferUntil) = (false, (DateTimeOffset?)null);
            if (row.Kind == RecipientKind.Subscriber && row.SubscriberId is { } person)
            {
                (destinations, turnedOff, deferUntil) = await PersonalRulesAsync(db, row, person, type, destinations, context.Now, ct);
            }
            foreach (var destination in destinations)
            {
                if (existing.Contains((destination.Key, destination.Channel)))
                {
                    continue;
                }
                if (destination.Integration is null)
                {
                    // No integration to record a delivery against: logged, and the recipient's outcome says it below.
                    logger.LogWarning("Recipient {Recipient} can't reach {Destination}: {Problem}", row.Id, destination.Key, destination.Problem);
                    continue;
                }
                // Rendered even for a destination with a problem (its integration disabled), so a dead-letter retry once it is fixed has content to send.
                if (!rendered.ContainsKey(destination.Channel))
                {
                    rendered[destination.Channel] = await RenderAsync(db, row.TenantId, type, request, destination.Channel, channels.Find(destination.Channel), model,
                        timeoutIsFinal: row.StepFailures >= TimeoutsAllowed, ct);
                }
                var (content, template, problem, category) = rendered[destination.Channel];
                if (problem is null && destination.Problem is not null)
                {
                    // A destination-level problem (an integration disabled) is the provider's side, not the content.
                    (problem, category) = (destination.Problem, FailureCategory.Provider);
                }
                var delivery = new Delivery
                {
                    TenantId = row.TenantId, Channel = destination.Channel, IntegrationId = destination.Integration.Id, DestinationKey = destination.Key,
                    DestinationSnapshot = destination.Snapshot, TemplateVersion = template?.Version ?? 0, Locale = template?.Locale ?? Types.TypeRules.DefaultLocale,
                    RendererVersion = TemplateRenderer.Version, Content = content, Priority = request.Priority, Mandatory = type.Mandatory, CreatedAt = context.Now,
                    Status = problem is null ? DeliveryStatus.Pending : DeliveryStatus.Failed,
                    FailureCategory = problem is null ? null : category,
                    LastError = ChannelContent.Cut(problem, 2000), NextAt = problem is null ? context.Now : null, CompletedAt = problem is null ? null : context.Now,
                };
                db.Deliveries.Add(delivery);
                if (problem is not null)
                {
                    // Failed here, never by the dispatcher: counted with the deliveries it finishes (by channel and status).
                    var channel = delivery.Channel;
                    context.AfterCommit.Add(() => Telemetry.Deliveries.Add(1, new KeyValuePair<string, object?>("channel", channel),
                        new KeyValuePair<string, object?>("status", nameof(DeliveryStatus.Failed))));
                }
                db.DeliveryItems.Add(new DeliveryItem
                {
                    TenantId = row.TenantId, DeliveryId = delivery.Id, RecipientId = row.Id, RequestId = request.Id, DestinationKey = destination.Key,
                    Channel = destination.Channel,
                });
                created++;
            }
            if (deferUntil is { } until)
            {
                // Quiet hours: what could go now has gone (the inbox); the person's channels are planned when they end. Counted then.
                row.Outcome = RecipientOutcome.Deferred;
                context.Done(row, until);
                if (created > 0)
                {
                    context.Wakes.Add(DeliveryStageNames.Dispatcher);
                }
                return;
            }
            if (created == 0 && existing.Count == 0)
            {
                // Nothing on record, now or from an earlier try: the person turned off every channel they could be reached on, or no
                // integration, or a route whose targets are gone, disabled or none, or the route itself deleted. "Why didn't they get it?" is
                // answered here, and in the log. A delivery failed as it was planned (no template, a render failure) is on record: history lists it.
                row.Outcome = turnedOff ? RecipientOutcome.OptedOut : RecipientOutcome.Failed;
            }
            else
            {
                row.Outcome = RecipientOutcome.Planned; // a deferred one, now planned
            }
            context.Done(row, null);
            Counted(context, row);
            if (created > 0)
            {
                context.Wakes.Add(DeliveryStageNames.Dispatcher);
            }
        }

        private async Task<List<Destination>> DestinationsAsync(NotificationsDbContext db, NotificationRecipient row, NotificationType type, CancellationToken ct)
        {
            if (row.Kind == RecipientKind.Route)
            {
                var route = row.RouteId is { } routeId ? await db.Routes.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == row.TenantId && r.Id == routeId, ct) : null;
                if (route is null)
                {
                    return []; // deleted since planning: nothing to send to
                }
                // A route can name a target that no longer exists (docs/deferred.md #8): say so, rather than reach nobody without a trace.
                var existing = await db.IntegrationTargets.AsNoTracking().Where(t => t.TenantId == row.TenantId && route.Targets.Contains(t.Key)).Select(t => t.Key).ToListAsync(ct);
                if (route.Targets.Except(existing, StringComparer.Ordinal).ToList() is { Count: > 0 } missing)
                {
                    logger.LogWarning("Route {Route} in tenant {Tenant} names targets that don't exist: {Missing}; recipient {Recipient} doesn't reach them",
                        route.Key, row.TenantId, string.Join(", ", missing), row.Id);
                }
                var targets = await (
                    from target in db.IntegrationTargets.Where(t => t.TenantId == row.TenantId && route.Targets.Contains(t.Key) && t.Enabled)
                    join integration in db.Integrations on new { target.TenantId, Id = target.IntegrationId } equals new { integration.TenantId, integration.Id }
                    select new { target, integration }).AsNoTracking().ToListAsync(ct);
                return targets.OrderBy(t => t.target.Key).Select(t => new Destination(DestinationKeys.Target(t.target.Id), t.integration.Channel, t.integration,
                    JsonSerializer.Serialize(new { target = t.target.Key, address = t.target.Address, addressRef = t.target.AddressRef }, Snapshot),
                    t.integration.Enabled ? null : $"The integration {t.integration.Key} is disabled.")).ToList();
            }
            if (row.Kind == RecipientKind.External && row.Channel is { } channel && row.Address is { } address)
            {
                var integration = await FirstIntegrationAsync(db, row.TenantId, channel, ct);
                return [new Destination(DestinationKeys.External(channel, address), channel, integration, JsonSerializer.Serialize(new { address }, Snapshot),
                    integration is null ? $"The tenant has no enabled {channel} integration." : null)];
            }
            if (row.Kind == RecipientKind.Subscriber && row.SubscriberId is { } subscriberId)
            {
                return await ContactDestinationsAsync(db, row, subscriberId, type, ct);
            }
            if (row.Kind == RecipientKind.ContactPoint && Guid.TryParse(row.NamedAs.AsSpan("contact-point:".Length), out var pointId)
                && await db.ContactPoints.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == row.TenantId && c.Id == pointId, ct) is { } named)
            {
                // A verification code: to the point the person is verifying, verified or not (removed since: nothing to send to).
                return [PointDestination(named, await FirstIntegrationAsync(db, row.TenantId, named.Channel, ct), verification: true)];
            }
            return [];
        }

        /// <summary>
        /// How a person hears (design §6.4, phase-2-plan I21): without the channels they turned off for the type's category (never the inbox of a
        /// mandatory type), and during their quiet hours with only the inbox now, the rest when the hours end (<c>deferUntil</c>). A mandatory
        /// type goes past quiet hours, recorded on the recipient. <c>allOff</c>: the person turned off every channel that would have reached them.
        /// </summary>
        private static async Task<(List<Destination> Destinations, bool AllOff, DateTimeOffset? DeferUntil)> PersonalRulesAsync(NotificationsDbContext db,
            NotificationRecipient row, Guid subscriberId, NotificationType type, List<Destination> destinations, DateTimeOffset now, CancellationToken ct)
        {
            if (destinations.Count == 0)
            {
                return (destinations, false, null);
            }
            // One read: the person's quiet hours and their preferences for the category.
            var person = await db.Subscribers.AsNoTracking().Where(s => s.TenantId == row.TenantId && s.Id == subscriberId)
                .Select(s => new
                {
                    s.QuietHours, s.TimeZone,
                    Preferences = db.Preferences.Where(p => p.TenantId == s.TenantId && p.SubscriberId == s.Id
                        && (p.Category == type.Category || p.Category == PreferenceRules.Any)).ToList(),
                })
                .SingleOrDefaultAsync(ct);
            var preferences = person?.Preferences ?? [];
            // A normal type that reached here wasn't opted out: each channel takes its most specific choice. A mandatory one went past the
            // category's choice for every channel, so only choices naming the channel count, and never against its inbox.
            var kept = destinations.Where(d => (type.Mandatory && d.Channel == Channels.Inbox)
                || PreferencePolicy.Mode(preferences, type.Category, d.Channel, channelOnly: type.Mandatory) != PreferenceModes.Off).ToList();
            if (kept.Count == 0)
            {
                return (kept, true, null);
            }
            if (QuietHours.Parse(person?.QuietHours)?.EndIfQuiet(now, person!.TimeZone) is not { } end || !kept.Any(d => d.Channel != Channels.Inbox))
            {
                return (kept, false, null);
            }
            if (type.Mandatory)
            {
                PreferencePolicy.Bypass(row, BypassedRules.QuietHours);
                return (kept, false, null);
            }
            return (kept.Where(d => d.Channel == Channels.Inbox).ToList(), false, end);
        }

        /// <summary>The tenant's integration a destination on <paramref name="channel"/> goes through: its first enabled one, by priority then key.</summary>
        private static Task<Integration?> FirstIntegrationAsync(NotificationsDbContext db, Guid tenantId, string channel, CancellationToken ct)
            => db.Integrations.AsNoTracking().Where(i => i.TenantId == tenantId && i.Channel == channel && i.Enabled)
                .OrderBy(i => i.Priority).ThenBy(i => i.Key).FirstOrDefaultAsync(ct);

        /// <summary>
        /// A person's destinations (design §6.2, §6.4): each of their contact points (and those of an identity linked into them) that is verified,
        /// not failed, and on a channel the type has a template for, through the tenant's first enabled integration for that channel. None of
        /// these rules bends for a mandatory type. The same address on two linked identities is one destination. First, always, the person's inbox
        /// (phase-2-plan I17), through the tenant's own inbox integration, with or without an inbox template. Push devices come in phase 4.
        /// </summary>
        private async Task<List<Destination>> ContactDestinationsAsync(NotificationsDbContext db, NotificationRecipient row, Guid subscriberId,
            NotificationType type, CancellationToken ct)
        {
            var self = await People.DirectoryReader.SelfAsync(db, row.TenantId, subscriberId, ct);
            var declared = await db.Templates.AsNoTracking().Where(t => t.TypeId == type.Id && (t.TenantId == null || t.TenantId == row.TenantId))
                .Select(t => t.Channel).Distinct().ToListAsync(ct);
            var inbox = await inboxes.EnsureAsync(db, row.TenantId, ct);
            List<Destination> destinations = [new Destination(DestinationKeys.Inbox(subscriberId), Channels.Inbox, inbox,
                JsonSerializer.Serialize(new { subscriber = subscriberId }, Snapshot), null)];
            var points = (await db.ContactPoints.AsNoTracking()
                .Where(c => c.TenantId == row.TenantId && self.Contains(c.SubscriberId) && c.VerifiedAt != null && c.Health != ContactHealth.Failed
                    && declared.Contains(c.Channel))
                .OrderBy(c => c.Channel).ThenBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct))
                // The hash is of the tenant, channel and address, not the person: one address on two linked identities is sent once.
                .DistinctBy(c => (c.Channel, c.AddressHash)).ToList();
            var integrations = new Dictionary<string, Integration?>(StringComparer.Ordinal);
            foreach (var point in points)
            {
                if (!People.ContactRules.TakesContactPoints(channels.Find(point.Channel)))
                {
                    continue; // its channel's package was removed, or no longer reaches people
                }
                if (!integrations.TryGetValue(point.Channel, out var integration))
                {
                    integrations[point.Channel] = integration = await FirstIntegrationAsync(db, row.TenantId, point.Channel, ct);
                }
                destinations.Add(PointDestination(point, integration, verification: false));
            }
            return destinations;
        }

        /// <summary>
        /// A contact point as a destination. The snapshot names the point and a masked address, never the address: the dispatcher reads and decrypts
        /// it when sending, so a point removed in between isn't sent to. <paramref name="verification"/> marks a verification code, which goes to a
        /// point that isn't verified yet. One whose key is gone waits at the dispatcher until the key is back.
        /// </summary>
        private static Destination PointDestination(ContactPoint point, Integration? integration, bool verification)
        {
            // Never decrypted here: the stored mask is what history shows, and the dispatcher reads the address (and a missing key retries there).
            var masked = point.Masked;
            var snapshot = verification
                ? JsonSerializer.Serialize(new { contactPoint = point.Id, masked, verification }, Snapshot)
                : JsonSerializer.Serialize(new { contactPoint = point.Id, masked }, Snapshot);
            return new Destination(DestinationKeys.ContactPoint(point.Id), point.Channel, integration, snapshot,
                integration is null ? $"The tenant has no enabled {point.Channel} integration." : null);
        }

        /// <summary>One channel's content, or the problem that stopped it and its failure category.</summary>
        private sealed record Rendered(string Content, Template? Template, string? Problem, FailureCategory? Category);

        /// <summary>What a delivery stores when its content couldn't be rendered: nothing to send, so a dead-letter retry refuses it.</summary>
        private const string NoContent = "";

        /// <summary>
        /// The content for one channel, shaped as its definition says: the tenant's template, else the application's, in <c>en</c>; a passthrough
        /// channel without one sends the request's envelope. A template that fails is a <c>Content</c> failure, recorded on the delivery. One that
        /// ran out of time throws, so the step runs again (the machine's load can be why), until <paramref name="timeoutIsFinal"/>: then it is
        /// recorded like any other failure, so the recipient's other channels still go out and history says why this one didn't. An integration on a
        /// channel no longer registered (its package removed) fails as <c>Content</c> too: nothing says what its content would be.
        /// </summary>
        private async Task<Rendered> RenderAsync(NotificationsDbContext db, Guid tenantId, NotificationType type,
            NotificationRequest request, string channel, ChannelDefinition? definition, RenderModel model, bool timeoutIsFinal, CancellationToken ct)
        {
            if (definition is null)
            {
                return new Rendered(NoContent, null, $"The channel {channel} isn't registered.", FailureCategory.Content);
            }
            var template = await db.Templates.AsNoTracking()
                .Where(t => t.TypeId == type.Id && t.Channel == channel && t.Locale == Types.TypeRules.DefaultLocale && (t.TenantId == tenantId || t.TenantId == null))
                .OrderBy(t => t.TenantId == null).ThenByDescending(t => t.Version).FirstOrDefaultAsync(ct);
            if (definition.Passthrough && (template is null || template.Engine == TemplateEngines.Passthrough))
            {
                var envelope = JsonSerializer.Serialize(new
                {
                    id = request.Id, type = request.TypeKey, typeVersion = request.TypeVersion, occurredAt = request.AcceptedAt, tenant = request.TenantId,
                    correlationKey = request.CorrelationKey, data = JsonElement.Parse(request.Data), links = request.Links,
                });
                return new Rendered(ChannelContent.Webhook(envelope, isJson: true), template, null, null);
            }
            if (template is null && definition.Content == ContentShape.Item)
            {
                // The inbox always has something to show, so a mandatory type always reaches the person (phase-2-plan I17).
                return new Rendered(ChannelContent.Inbox(null, type.Key, type.Name, request.Links, type.Actions), null, null, null);
            }
            if (template is null)
            {
                return new Rendered(NoContent, null, $"The type {type.Key} v{type.Version} has no {channel} template.", FailureCategory.Content);
            }
            try
            {
                var rendered = await TemplateRenderer.RenderAsync(template.Body, model, ct);
                return new Rendered(definition.Content switch
                {
                    ContentShape.Message => ChannelContent.Email(rendered, type.Name),
                    ContentShape.Item => ChannelContent.Inbox(rendered, type.Key, type.Name, request.Links, type.Actions),
                    _ => ChannelContent.Webhook(rendered, ChannelContent.IsJson(rendered)),
                }, template, null, null);
            }
            catch (RenderException ex) when (definition.Content == ContentShape.Item && (!ex.Retryable || timeoutIsFinal))
            {
                // The inbox always has something to show (phase-2-plan I17), so a mandatory type still reaches the person: the default item, and
                // the template's failure in the log for whoever owns it.
                logger.LogWarning("The inbox template of {Type} v{Version} failed for tenant {Tenant}, so its item shows the default: {Error}",
                    type.Key, type.Version, tenantId, ex.Message);
                return new Rendered(ChannelContent.Inbox(null, type.Key, type.Name, request.Links, type.Actions), template, null, null);
            }
            catch (RenderException ex) when (!ex.Retryable || timeoutIsFinal)
            {
                // A tenant's own template (an override its admin wrote) is marked so: its error quotes the template, which only admins see.
                return new Rendered(NoContent, template, (template.TenantId is null ? "render_failed: " : "render_failed (tenant template): ") + ex.Message,
                    FailureCategory.Content);
            }
        }
    }

    public static class DeliveryPlanningRegistration
    {
        public static IServiceCollection AddDeliveryPlanning(this IServiceCollection services, StageOptions options, bool runWorkers = true)
            => Inbox.InboxRegistration.AddInboxIntegrations(services.AddBuiltInChannels()).AddSingleton(new DeliveryPlannerLimits(options.MaxStepFailures))
                .AddStage<NotificationRecipient, DeliveryPlannerStage>(options, runWorkers);
    }
}
