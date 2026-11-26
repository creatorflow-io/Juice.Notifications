using System.Text.Json;
using Juice.Messaging;
using Juice.Messaging.Outbox;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Events
{
    // Events to producers (design §5.4, §19 decision 12; phase-3-plan I37): what happened to their notifications, over RabbitMQ from the outbox.
    // Raised where it happens, as a producer_events row saved with the change (a stage step's save, a transaction of the API), so an event is
    // published exactly when its change commits; a relay moves the rows into the outbox, which publishes and retries.

    /// <summary>Configuration section <c>Events</c>: whether producers get events, and the exchange they come on.</summary>
    public sealed class EventsOptions
    {
        public const string Section = "Events";

        /// <summary>Off by default: no rows are written, nothing is published. Needs the RabbitMQ intake's connection (<c>Intake:RabbitMQ</c>).</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// A topic exchange; routing key <c>{applicationKey}.notification.{event}</c>. An application's broker user may bind only to
        /// <c>{applicationKey}.#</c> (RabbitMQ topic permissions, which the deployment sets: design §19 decision 12).
        /// </summary>
        public string Exchange { get; set; } = "juice.notifications.events";

        /// <summary>How often the relay looks for events to publish.</summary>
        public TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(1);
    }

    public static class EventsRouting
    {
        /// <summary>The RabbitMQ producer and outbox publisher events go through.</summary>
        public const string Publisher = "events";
    }

    public static class ProducerEventNames
    {
        public const string Interaction = "interaction";
        public const string DeliveryFailed = "delivery.failed";
        public const string EscalationExhausted = "escalation.exhausted";
        public const string RecipientUnresolved = "recipient.unresolved";
        public const string CorrelationAcknowledged = "correlation.acknowledged";
        public const string CorrelationResolved = "correlation.resolved";
        public const string CorrelationCancelled = "correlation.cancelled";
    }

    /// <summary>
    /// What a producer hears (design §5.4). <see cref="MessageBase.MessageId"/> is the event id producers deduplicate on (delivery is at least
    /// once). Every event names its application, tenant and correlation key; the rest depends on <see cref="Event"/>. The routing key is
    /// <c>{Application}.notification.{Event}</c>.
    /// </summary>
    [Juice.Messaging.Attributes.Domain(Hosting.NotificationsPipeline.Domain)]
    public sealed record NotificationEvent : IntegrationEvent
    {
        /// <summary><see cref="ProducerEventNames"/>: <c>interaction</c>, <c>delivery.failed</c>, …</summary>
        public string Event { get; init; } = "";
        public string Application { get; init; } = "";
        public Guid NotificationTenantId { get; init; }
        public string? CorrelationKey { get; init; }
        public Guid? NotificationId { get; init; }
        public Guid? RequestId { get; init; }
        public string Type { get; init; } = "";
        public DateTimeOffset OccurredAt { get; init; }

        // An interaction: who did what to which item (kind read, acknowledged or action, and the action's id).
        public Guid? ItemId { get; init; }
        public Guid? SubscriberId { get; init; }
        public string? Kind { get; init; }
        public string? ActionId { get; init; }

        // A recipient every destination of which failed, and the channels they were on.
        public Guid? RecipientId { get; init; }
        public string? NamedAs { get; init; }
        public IReadOnlyList<string>? Channels { get; init; }

        // Names a resolver couldn't resolve (recipient.unresolved), and the resolver.
        public IReadOnlyList<string>? Names { get; init; }
        public string? Resolver { get; init; }

        // A correlation's change: its state, who, and the outcome.
        public string? State { get; init; }
        public string? By { get; init; }
        public string? Outcome { get; init; }

        /// <summary>The escalation's steps run (escalation.exhausted).</summary>
        public int? Steps { get; init; }

        public override string EventName => $"{Application}.notification.{Event}";
    }

    /// <summary>
    /// Raises events: adds a <see cref="ProducerEvent"/> row to the context, saved with the change that raised it. Nothing when events are off or
    /// the application has no broker user (it couldn't receive them: §19 decision 12).
    /// </summary>
    public sealed class ProducerEvents(IOptions<EventsOptions> options)
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public bool Enabled => options.Value.Enabled;

        public async Task RaiseAsync(NotificationsDbContext db, Guid tenantId, Guid applicationId, Func<string, NotificationEvent> build, CancellationToken ct)
        {
            if (!options.Value.Enabled)
            {
                return;
            }
            var app = await db.Applications.AsNoTracking().Where(a => a.Id == applicationId).Select(a => new { a.Key, a.BrokerUser }).SingleOrDefaultAsync(ct);
            if (app?.BrokerUser is null)
            {
                return;
            }
            var evt = build(app.Key) with { MessageId = Guid.CreateVersion7() };
            db.ProducerEvents.Add(new ProducerEvent
            {
                TenantId = tenantId, Id = evt.MessageId, ApplicationId = applicationId, Event = evt.Event, Payload = JsonSerializer.Serialize(evt, Json),
                CreatedAt = evt.OccurredAt,
            });
        }

        internal static NotificationEvent Read(string payload) => JsonSerializer.Deserialize<NotificationEvent>(payload, Json)!;
    }

    /// <summary>
    /// Moves raised events into the outbox, in batches, oldest first: each batch in one transaction that takes its rows (<c>SKIP LOCKED</c>, so
    /// replicas share the work), adds them to the outbox and deletes them. The outbox publishes them to RabbitMQ and retries.
    /// </summary>
    public sealed class ProducerEventRelay(IServiceScopeFactory scopes, IOptions<EventsOptions> options, ILogger<ProducerEventRelay> logger) : BackgroundService
    {
        public const int Batch = 100;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // A full batch means more are waiting: go on at once.
                    if (await RelayOnceAsync(stoppingToken) == Batch)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Producer events weren't moved to the outbox; trying again");
                }
                try
                {
                    await Task.Delay(options.Value.Poll, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>One batch; how many it moved.</summary>
        public async Task<int> RelayOnceAsync(CancellationToken ct)
        {
            using var context = NotificationsMessageContext.Begin("producer-events");
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxService<NotificationsDbContext>>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var rows = await db.ProducerEvents.FromSqlRaw(
                    $"SELECT * FROM notifications.producer_events ORDER BY created_at, id LIMIT {Batch} FOR UPDATE SKIP LOCKED")
                .AsNoTracking().ToListAsync(ct);
            if (rows.Count == 0)
            {
                return 0;
            }
            foreach (var row in rows)
            {
                await outbox.AddEventAsync(ProducerEvents.Read(row.Payload));
            }
            await outbox.SaveEventsAsync(transaction.TransactionId, ct);
            var ids = rows.Select(r => r.Id).ToList();
            await db.ProducerEvents.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
            return rows.Count;
        }
    }

    public static class ProducerEventsRegistration
    {
        public static IServiceCollection AddProducerEvents(this IServiceCollection services, bool runRelay)
        {
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<ProducerEvents>(services);
            if (runRelay)
            {
                services.AddHostedService<ProducerEventRelay>();
            }
            return services;
        }
    }
}
