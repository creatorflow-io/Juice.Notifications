using System.Diagnostics;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Messaging
{
    /// <summary>
    /// Accepts a <see cref="NotificationRequested"/> with the same <see cref="AcceptNotification"/> command as the HTTP API, for the application whose
    /// <c>brokerUser</c> published it. A duplicate idempotency key is the same request, as over HTTP.
    /// </summary>
    /// <remarks>
    /// <para>A request this service refuses is logged and counted, and taken off the queue: it would be refused the same way every time. Any
    /// other failure throws, and the message comes back after a delay (the queue dead-letters it to a delay queue that returns it,
    /// <see cref="RabbitMQIntake"/>): the database unreachable, a transaction PostgreSQL aborted, or a bug, which keeps the message until it is
    /// fixed rather than losing it.</para>
    /// <para>The publisher is read from the message context, which Juice sets from the message's <c>user_id</c>. The copies Juice's own retry
    /// tiers republish have none, so the intake queue doesn't use them.</para>
    /// </remarks>
    internal sealed class NotificationRequestedHandler(IMediator mediator, NotificationsDbContext db, StageNudges nudges,
        ILogger<NotificationRequestedHandler> logger) : IIntegrationEventHandler<NotificationRequested>
    {
        public async Task HandleAsync(NotificationRequested message)
        {
            var publisher = MessageContext.IsInitialized ? MessageContext.Current.PublisherId : null;
            if (publisher is null)
            {
                Refuse(message, null, ErrorCodes.UnverifiedPublisher,
                    "The message has no user_id, so its publisher is unknown. Publish with the producer's StampUserId, as the application's broker user.");
                return;
            }
            var app = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.BrokerUser == publisher);
            if (app is null)
            {
                Refuse(message, publisher, ErrorCodes.NotAProducer,
                    $"No application publishes as the broker user {publisher}. A platform admin sets an application's brokerUser.");
                return;
            }

            var submission = new NotificationSubmission(message.Type, message.TypeVersion, message.IdempotencyKey, message.CorrelationKey,
                message.Recipients?.Select(r => new RecipientSpec(r?.Kind, r?.Channel, r?.Address)).ToList(),
                message.Actor is null ? null : new ActorSpec(message.Actor.Kind, message.Actor.Id),
                message.Priority, message.Labels, message.Data, message.Links, message.ExpiresAt);
            // The consumer's span continues the producer's trace (the message's traceparent), so the stages carry it on as for HTTP.
            var traceParent = Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.Id : null;
            var command = new AcceptNotification(message.NotificationTenantId, app.Id, app.Key, submission, traceParent, app.Enabled);

            Outcome<AcceptedView> outcome;
            try
            {
                outcome = await mediator.Send(command);
            }
            catch (Exception ex) when (DbConflicts.IsConnectionFailure(ex) || DbConflicts.Classify(ex) is { Kind: ConflictKind.Aborted })
            {
                logger.LogWarning(ex, "NotificationRequested {MessageId} from {Application} wasn't stored, and comes back after the retry delay.",
                    message.MessageId, app.Key);
                throw;
            }
            catch (Exception ex) when (DbConflicts.Classify(ex) is { } conflict)
            {
                db.ChangeTracker.Clear();
                switch (conflict)
                {
                    // The same idempotency key at once (a redelivery racing its first delivery, or the same request over HTTP): one request.
                    case { Kind: ConflictKind.Unique, Constraint: Constraints.RequestIdempotencyKey }:
                        Count(null, "duplicate"); // the original's type, not known here
                        return;
                    // The link was removed while the request was accepted.
                    case { Kind: ConflictKind.ForeignKey, Constraint: Constraints.RequestApplicationTenant }:
                        Refuse(message, app.Key, ErrorCodes.TenantNotLinked, "The application isn't linked to this tenant.");
                        return;
                    default:
                        throw;
                }
            }

            if (outcome.Kind is not (OutcomeKind.Created or OutcomeKind.Ok))
            {
                Refuse(message, app.Key, outcome.Code ?? "refused", outcome.Message ?? "", outcome.Errors);
                return;
            }
            var view = outcome.Value!;
            if (!view.Duplicate)
            {
                nudges.Wake(PlannerStage.StageName); // committed: the planner can take it now rather than at its next poll
            }
            Count(view.Type, view.Duplicate ? "duplicate" : "accepted");
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("NotificationRequested {MessageId} from {Application} is request {RequestId} ({Outcome}).",
                    message.MessageId, app.Key, view.Id, view.Duplicate ? "duplicate" : "accepted");
            }
        }

        private static void Count(string? type, string outcome)
            => Telemetry.Requests.Add(1, new KeyValuePair<string, object?>("type", type),
                new KeyValuePair<string, object?>("outcome", outcome), new KeyValuePair<string, object?>("intake", "rabbitmq"));

        /// <summary>
        /// Logged with what the producer needs to find and fix it, and counted by code. The type isn't a tag here: an unknown one is whatever the
        /// message said.
        /// </summary>
        private void Refuse(NotificationRequested message, string? application, string code, string reason, IReadOnlyList<FieldError>? errors = null)
        {
            Telemetry.Requests.Add(1, new KeyValuePair<string, object?>("outcome", "refused"), new KeyValuePair<string, object?>("code", code),
                new KeyValuePair<string, object?>("intake", "rabbitmq"));
            logger.LogWarning(
                "NotificationRequested {MessageId} refused ({Code}): {Reason} Application {Application}, tenant {TenantId}, type {Type}, idempotency key {IdempotencyKey}.{Errors}",
                message.MessageId, code, reason, application ?? "(unknown)", message.NotificationTenantId, message.Type, message.IdempotencyKey,
                errors is { Count: > 0 } ? " " + string.Join("; ", errors.Select(e => $"{e.Pointer}: {e.Message}")) : "");
        }
    }
}
