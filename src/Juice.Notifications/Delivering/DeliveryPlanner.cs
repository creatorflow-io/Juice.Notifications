using System.Text.Json;
using Juice.Notifications.Data;
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

    public sealed class DeliveryPlannerStage(ILogger<DeliveryPlannerStage> logger, DeliveryPlannerLimits? limits = null) : IStage<NotificationRecipient>
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
            // The runner logged the error; the outcome answers "why didn't I get it?".
            row.Outcome = RecipientOutcome.Failed;
            context.Done(row, null);
            Counted(context, row);
        }

        /// <summary>A recipient's outcome once delivery planning is through with it (Planned: it has deliveries).</summary>
        private static void Counted(StageContext context, NotificationRecipient row)
            => context.AfterCommit.Add(() => Telemetry.Recipients.Add(1, new KeyValuePair<string, object?>("outcome", row.Outcome.ToString())));

        public async Task ExecuteAsync(StageContext context, NotificationRecipient row, CancellationToken ct)
        {
            var db = context.Db;
            if (row.Outcome != RecipientOutcome.Planned)
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
                row.Outcome = RecipientOutcome.Cancelled;
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
            foreach (var destination in await DestinationsAsync(db, row, ct))
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
                    rendered[destination.Channel] = await RenderAsync(db, row.TenantId, type, request, destination.Channel, model,
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
                    TenantId = row.TenantId, DeliveryId = delivery.Id, RecipientId = row.Id, DestinationKey = destination.Key, Channel = destination.Channel,
                });
                created++;
            }
            if (created == 0 && existing.Count == 0)
            {
                // Nothing reaches it, now or from an earlier try: no integration, or a route whose targets are gone, disabled or none, or the
                // route itself deleted. "Why didn't they get it?" is answered here, and in the log.
                row.Outcome = RecipientOutcome.Failed;
            }
            context.Done(row, null);
            Counted(context, row);
            if (created > 0)
            {
                context.Wakes.Add(DeliveryStageNames.Dispatcher);
            }
        }

        private async Task<List<Destination>> DestinationsAsync(NotificationsDbContext db, NotificationRecipient row, CancellationToken ct)
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
                var integration = await db.Integrations.AsNoTracking().Where(i => i.TenantId == row.TenantId && i.Channel == channel && i.Enabled)
                    .OrderBy(i => i.Priority).ThenBy(i => i.Key).FirstOrDefaultAsync(ct);
                return [new Destination(DestinationKeys.External(channel, address), channel, integration, JsonSerializer.Serialize(new { address }, Snapshot),
                    integration is null ? $"The tenant has no enabled {channel} integration." : null)];
            }
            return [];
        }

        /// <summary>One channel's content, or the problem that stopped it and its failure category.</summary>
        private sealed record Rendered(string Content, Template? Template, string? Problem, FailureCategory? Category);

        /// <summary>What a delivery stores when its content couldn't be rendered: nothing to send, so a dead-letter retry refuses it.</summary>
        private const string NoContent = "";

        /// <summary>
        /// The content for one channel: the tenant's template, else the application's, in <c>en</c>; webhook without one is passthrough. A
        /// template that fails is a <c>Content</c> failure, recorded on the delivery. One that ran out of time throws, so the step runs again (the
        /// machine's load can be why), until <paramref name="timeoutIsFinal"/>: then it is recorded like any other failure, so the recipient's
        /// other channels still go out and history says why this one didn't.
        /// </summary>
        private static async Task<Rendered> RenderAsync(NotificationsDbContext db, Guid tenantId, NotificationType type,
            NotificationRequest request, string channel, RenderModel model, bool timeoutIsFinal, CancellationToken ct)
        {
            var template = await db.Templates.AsNoTracking()
                .Where(t => t.TypeId == type.Id && t.Channel == channel && t.Locale == Types.TypeRules.DefaultLocale && (t.TenantId == tenantId || t.TenantId == null))
                .OrderBy(t => t.TenantId == null).ThenByDescending(t => t.Version).FirstOrDefaultAsync(ct);
            if (channel == Channels.Webhook && (template is null || template.Engine == TemplateEngines.Passthrough))
            {
                var envelope = JsonSerializer.Serialize(new
                {
                    id = request.Id, type = request.TypeKey, typeVersion = request.TypeVersion, occurredAt = request.AcceptedAt, tenant = request.TenantId,
                    correlationKey = request.CorrelationKey, data = JsonElement.Parse(request.Data), links = request.Links,
                });
                return new Rendered(ChannelContent.Webhook(envelope, isJson: true), template, null, null);
            }
            if (template is null)
            {
                return new Rendered(NoContent, null, $"The type {type.Key} v{type.Version} has no {channel} template.", FailureCategory.Content);
            }
            try
            {
                var rendered = await TemplateRenderer.RenderAsync(template.Body, model, ct);
                return new Rendered(channel == Channels.Email ? ChannelContent.Email(rendered, type.Name) : ChannelContent.Webhook(rendered, ChannelContent.IsJson(rendered)),
                    template, null, null);
            }
            catch (RenderException ex) when (!ex.Retryable || timeoutIsFinal)
            {
                return new Rendered(NoContent, template, "render_failed: " + ex.Message, FailureCategory.Content);
            }
        }
    }

    public static class DeliveryPlanningRegistration
    {
        public static IServiceCollection AddDeliveryPlanning(this IServiceCollection services, StageOptions options, bool runWorkers = true)
            => services.AddSingleton(new DeliveryPlannerLimits(options.MaxStepFailures)).AddStage<NotificationRecipient, DeliveryPlannerStage>(options, runWorkers);
    }
}
