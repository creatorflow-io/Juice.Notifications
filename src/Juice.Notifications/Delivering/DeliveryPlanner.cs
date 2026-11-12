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
    public sealed class DeliveryPlannerStage(ILogger<DeliveryPlannerStage> logger) : IStage<NotificationRecipient>
    {
        public string Name => DeliveryStageNames.DeliveryPlanner;

        private sealed record Destination(string Key, string Channel, Integration? Integration, string Snapshot, string? Problem);

        public void GiveUp(StageContext context, NotificationRecipient row, string error)
        {
            // The runner logged the error; the outcome answers "why didn't I get it?".
            row.Outcome = RecipientOutcome.Failed;
            context.Done(row, null);
        }

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
            if (request.ExpiresAt is { } expires && expires <= context.Now)
            {
                row.Outcome = RecipientOutcome.Cancelled;
                context.Done(row, null);
                return;
            }
            var type = await db.Types.AsNoTracking().SingleAsync(t => t.Id == request.TypeId, ct);
            var model = new RenderModel(type.Key, type.Version, type.Name, request.Priority, JsonElement.Parse(request.Data), request.Labels, request.Links);

            var created = 0;
            foreach (var destination in await DestinationsAsync(db, row, ct))
            {
                if (await db.DeliveryItems.AnyAsync(i => i.TenantId == row.TenantId && i.RecipientId == row.Id && i.DestinationKey == destination.Key
                    && i.Channel == destination.Channel, ct))
                {
                    continue;
                }
                var (content, template, problem) = destination.Problem is null
                    ? await RenderAsync(db, row.TenantId, type, request, destination.Channel, model, ct)
                    : ("{}", null, destination.Problem);
                if (destination.Integration is null)
                {
                    logger.LogWarning("Recipient {Recipient} can't reach {Destination}: {Problem}", row.Id, destination.Key, problem);
                    continue; // no integration to record the delivery against; the recipient's outcome says why below
                }
                var delivery = new Delivery
                {
                    TenantId = row.TenantId, Channel = destination.Channel, IntegrationId = destination.Integration.Id, DestinationKey = destination.Key,
                    DestinationSnapshot = destination.Snapshot, TemplateVersion = template?.Version ?? 0, Locale = template?.Locale ?? Types.TypeRules.DefaultLocale,
                    RendererVersion = TemplateRenderer.Version, Content = content, Priority = request.Priority, Mandatory = type.Mandatory, CreatedAt = context.Now,
                    Status = problem is null ? DeliveryStatus.Pending : DeliveryStatus.Failed,
                    FailureCategory = problem is null ? null : FailureCategory.Content,
                    LastError = problem is { Length: > 2000 } ? problem[..2000] : problem, NextAt = problem is null ? context.Now : null, CompletedAt = problem is null ? null : context.Now,
                };
                db.Deliveries.Add(delivery);
                db.DeliveryItems.Add(new DeliveryItem
                {
                    TenantId = row.TenantId, DeliveryId = delivery.Id, RecipientId = row.Id, DestinationKey = destination.Key, Channel = destination.Channel,
                });
                created++;
            }
            context.Done(row, null);
            if (created > 0)
            {
                context.Wakes.Add(DeliveryStageNames.Dispatcher);
            }
        }

        private static async Task<List<Destination>> DestinationsAsync(NotificationsDbContext db, NotificationRecipient row, CancellationToken ct)
        {
            if (row.Kind == RecipientKind.Route)
            {
                var route = row.RouteId is { } routeId ? await db.Routes.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == row.TenantId && r.Id == routeId, ct) : null;
                if (route is null)
                {
                    return []; // deleted since planning: nothing to send to
                }
                var targets = await (
                    from target in db.IntegrationTargets.Where(t => t.TenantId == row.TenantId && route.Targets.Contains(t.Key) && t.Enabled)
                    join integration in db.Integrations on new { target.TenantId, Id = target.IntegrationId } equals new { integration.TenantId, integration.Id }
                    select new { target, integration }).AsNoTracking().ToListAsync(ct);
                return targets.OrderBy(t => t.target.Key).Select(t => new Destination(DestinationKeys.Target(t.target.Id), t.integration.Channel, t.integration,
                    JsonSerializer.Serialize(new { target = t.target.Key, address = t.target.Address, addressRef = t.target.AddressRef }),
                    t.integration.Enabled ? null : $"The integration {t.integration.Key} is disabled.")).ToList();
            }
            if (row.Kind == RecipientKind.External && row.Channel is { } channel && row.Address is { } address)
            {
                var integration = await db.Integrations.AsNoTracking().Where(i => i.TenantId == row.TenantId && i.Channel == channel && i.Enabled)
                    .OrderBy(i => i.Priority).ThenBy(i => i.Key).FirstOrDefaultAsync(ct);
                return [new Destination(DestinationKeys.External(channel, address), channel, integration, JsonSerializer.Serialize(new { address }),
                    integration is null ? $"The tenant has no enabled {channel} integration." : null)];
            }
            return [];
        }

        /// <summary>The content for one channel: the tenant's template, else the application's, in <c>en</c>; webhook without one is passthrough.</summary>
        private static async Task<(string Content, Template? Template, string? Problem)> RenderAsync(NotificationsDbContext db, Guid tenantId, NotificationType type,
            NotificationRequest request, string channel, RenderModel model, CancellationToken ct)
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
                return (ChannelContent.Webhook(envelope, isJson: true), template, null);
            }
            if (template is null)
            {
                return ("{}", null, $"The type {type.Key} v{type.Version} has no {channel} template.");
            }
            try
            {
                var rendered = await TemplateRenderer.RenderAsync(template.Body, model, ct);
                return (channel == Channels.Email ? ChannelContent.Email(rendered, type.Name) : ChannelContent.Webhook(rendered, ChannelContent.IsJson(rendered)),
                    template, null);
            }
            catch (RenderException ex)
            {
                return ("{}", template, "render_failed: " + ex.Message);
            }
        }
    }

    public static class DeliveryPlanningRegistration
    {
        public static IServiceCollection AddDeliveryPlanning(this IServiceCollection services, StageOptions options, bool runWorkers = true)
            => services.AddStage<NotificationRecipient, DeliveryPlannerStage>(options, runWorkers);
    }
}
