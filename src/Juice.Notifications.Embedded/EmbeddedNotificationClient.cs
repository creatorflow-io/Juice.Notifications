using System.Diagnostics;
using Juice.MediatR;
using Juice.Messaging.Outbox;
using Juice.Notifications.Correlations;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.Embedded
{
    /// <summary>
    /// <see cref="INotificationClient"/> in the host's transaction (phase-5-plan I58, I59). Each call runs in a scope of its own whose
    /// <see cref="NotificationsDbContext"/> is on the host's connection and enlisted in its transaction: the command's handler changes rows, this
    /// saves them (its save dispatches their audit events) and the outbox without committing, and the host's commit makes them durable with its own change, or
    /// its rollback drops them. The transaction behavior sees the open transaction and leaves it to its owner.
    /// </summary>
    internal sealed class EmbeddedNotificationClient(IServiceScopeFactory scopes, NotificationsEmbeddedOptions options,
        INotificationTransaction? transaction = null) : INotificationClient
    {
        public Task<NotificationAccepted> SendAsync(NotificationToSend notification, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(notification);
            return InHostTransactionAsync(async (services, db, application) =>
            {
                var submission = new NotificationSubmission(notification.Type, notification.TypeVersion, notification.IdempotencyKey, notification.CorrelationKey,
                    notification.Recipients?.Select(r => new RecipientSpec(r.Kind, r.Channel, r.Address, r.Id, r.Key, r.Resolver)).ToList(),
                    notification.Actor is { } actor ? new ActorSpec(actor.Kind, actor.Id) : null, notification.Priority, notification.Labels,
                    notification.Data, notification.Links, notification.ExpiresAt);
                var outcome = await services.GetRequiredService<IMediator>().Send(new AcceptNotification(options.TenantId, application.Id, application.Key,
                    submission, Activity.Current?.Id, application.Enabled), cancellationToken);
                var accepted = Value(outcome);
                if (!accepted.Duplicate)
                {
                    // Taken at once when the host commits, if it can say so; else at the planner's next poll.
                    var nudges = services.GetRequiredService<Stages.StageNudges>();
                    transaction!.OnCommitted(() => nudges.Wake(Planning.PlannerStage.StageName));
                }
                return new NotificationAccepted(accepted.Id, accepted.Type, accepted.TypeVersion, accepted.AcceptedAt, accepted.Duplicate);
            }, cancellationToken);
        }

        public Task<CorrelationState> AcknowledgeAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default)
            => CorrelationAsync((lifecycle, application) => lifecycle.AcknowledgeAsync(options.TenantId, application, correlationKey, by, cancellationToken),
                cancellationToken);

        public Task<CorrelationState> ResolveAsync(string correlationKey, string? outcome = null, string? by = null, CancellationToken cancellationToken = default)
            => CorrelationAsync((lifecycle, application) => lifecycle.ResolveAsync(options.TenantId, application, correlationKey, outcome, by, cancellationToken),
                cancellationToken);

        public Task<CorrelationState> CancelAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default)
            => CorrelationAsync((lifecycle, application) => lifecycle.CancelAsync(options.TenantId, application, correlationKey, by, cancellationToken),
                cancellationToken);

        private Task<CorrelationState> CorrelationAsync(Func<CorrelationLifecycle, Guid, Task<Outcome<CorrelationView>>> change, CancellationToken cancellationToken)
            => InHostTransactionAsync(async (services, _, application) =>
                Value(await change(services.GetRequiredService<CorrelationLifecycle>(), application.Id)).State, cancellationToken);

        /// <summary>
        /// Runs <paramref name="work"/> on the host's connection, in its transaction, then saves what it changed and the outbox without
        /// committing. Refused without an open transaction: there would be nothing to commit the request with.
        /// </summary>
        private async Task<T> InHostTransactionAsync<T>(Func<IServiceProvider, NotificationsDbContext, Data.Application, Task<T>> work,
            CancellationToken cancellationToken)
        {
            if (transaction?.Connection is not { } connection || transaction.Transaction is not { } hostTransaction)
            {
                throw new NotificationRefusedException(ErrorCodes.NoTransaction,
                    "No transaction is open: a notification is written in the host's transaction, and commits or rolls back with its change.");
            }
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<HostConnection>().Connection = connection;
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            await db.Database.UseTransactionAsync(hostTransaction, cancellationToken);
            using var context = NotificationsMessageContext.Begin("embedded");
            var application = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.Key == options.ApplicationKey, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"The application {options.ApplicationKey} isn't provisioned yet: the embedded pipeline declares it at start (its hosted service).");
            var result = await work(scope.ServiceProvider, db, application);
            // The context isn't managed by the transaction behavior (the host's transaction was open), so its save dispatches its own audit
            // events, to the host's sink, inside the host's transaction.
            await db.SaveChangesAsync(cancellationToken);
            await scope.ServiceProvider.GetRequiredService<IOutboxService<NotificationsDbContext>>()
                .SaveEventsAsync(db.Database.CurrentTransaction?.TransactionId, cancellationToken);
            return result;
        }

        /// <summary>The outcome's value, or its refusal as the HTTP API would give it.</summary>
        private static T Value<T>(Outcome<T> outcome)
            => outcome.Kind is OutcomeKind.Ok or OutcomeKind.Created && outcome.Value is { } value
                ? value
                : throw new NotificationRefusedException(outcome.Code ?? (outcome.Kind == OutcomeKind.NotFound ? ErrorCodes.NotFound : ErrorCodes.InvalidRequest),
                    outcome.Message ?? outcome.Kind.ToString(), outcome.Errors?.Select(e => new NotificationFieldError(e.Pointer, e.Message)).ToList());
    }
}
