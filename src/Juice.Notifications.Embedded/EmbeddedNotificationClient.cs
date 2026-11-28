using System.Diagnostics;
using Juice.MediatR;
using Juice.Notifications.Correlations;
using Juice.Notifications.Data;
using Juice.Notifications.Hosting;
using Juice.Notifications.Intake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.Embedded
{
    /// <summary>
    /// <see cref="INotificationClient"/> in the host's transaction (phase-5-plan I58, I59). Each call runs in a scope of its own whose
    /// <see cref="NotificationsDbContext"/> is on the host's connection and enlisted in its transaction: the command's handler changes rows, this
    /// saves them (its save dispatches their audit events) and the outbox without committing, and the host's commit makes them durable with its own change, or
    /// its rollback drops them (<see cref="HostScope"/>, which <see cref="INotificationsHostScope"/> also is). The transaction behavior sees the
    /// open transaction and leaves it to its owner.
    /// </summary>
    internal sealed class EmbeddedNotificationClient(HostScope hostScope, NotificationsEmbeddedOptions options,
        INotificationTransaction? transaction = null) : INotificationClient
    {
        public Task<NotificationAccepted> SendAsync(NotificationToSend notification, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(notification);
            // A request on a correlation touches it (its row version): a hold, acknowledge or resolve committed at the same moment in another
            // transaction makes this save lose, refused as concurrent_change like the correlation calls (deferred #53).
            return ConcurrentAsync(() => InHostTransactionAsync(async (services, db, application) =>
            {
                var submission = new NotificationSubmission(notification.Type, notification.TypeVersion, notification.IdempotencyKey, notification.CorrelationKey,
                    notification.Recipients?.Select(r => new RecipientSpec(r.Kind, r.Channel, r.Address, r.Id, r.Key, r.Resolver)).ToList(),
                    notification.Actor is { } actor ? new ActorSpec(actor.Kind, actor.Id) : null, notification.Priority, notification.Labels,
                    notification.Data, notification.Links, notification.ExpiresAt, notification.OccurredAt, notification.DryRun);
                var outcome = await services.GetRequiredService<IMediator>().Send(new AcceptNotification(options.TenantId, application.Id, application.Key,
                    submission, Activity.Current?.Id, application.Enabled), cancellationToken);
                var accepted = Value(outcome);
                if (!accepted.Duplicate)
                {
                    // Taken at once when the host commits, if it can say so; else at the planner's next poll.
                    var nudges = services.GetRequiredService<Stages.StageNudges>();
                    transaction!.OnCommitted(() => nudges.Wake(Planning.PlannerStage.StageName));
                }
                return new NotificationAccepted(accepted.Id, accepted.Type, accepted.TypeVersion, accepted.AcceptedAt, accepted.Duplicate) { DryRun = accepted.DryRun };
            }, cancellationToken));
        }

        public Task<CorrelationState> AcknowledgeAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default)
            => CorrelationAsync(correlationKey, (lifecycle, application) => lifecycle.AcknowledgeAsync(options.TenantId, application, correlationKey, by, cancellationToken),
                cancellationToken);

        public Task<CorrelationState> ResolveAsync(string correlationKey, string? outcome = null, string? by = null, CancellationToken cancellationToken = default)
            => CorrelationAsync(correlationKey, (lifecycle, application) => lifecycle.ResolveAsync(options.TenantId, application, correlationKey, outcome, by, cancellationToken),
                cancellationToken);

        public Task<CorrelationState> CancelAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default)
            => CorrelationAsync(correlationKey, (lifecycle, application) => lifecycle.CancelAsync(options.TenantId, application, correlationKey, by, cancellationToken),
                cancellationToken);

        public Task<CorrelationHold> HoldAsync(string correlationKey, string reason, string? by = null, CancellationToken cancellationToken = default)
            => HoldChangeAsync((lifecycle, application) => lifecycle.HoldAsync(options.TenantId, application, correlationKey, reason, by, cancellationToken),
                cancellationToken);

        public Task<CorrelationHold> ReleaseAsync(string correlationKey, string? by = null, CancellationToken cancellationToken = default)
            => HoldChangeAsync((lifecycle, application) => lifecycle.ReleaseAsync(options.TenantId, application, correlationKey, by, cancellationToken),
                cancellationToken);

        /// <summary>
        /// An acknowledge, resolve or cancel in the host's transaction. With the host's inbox (M54), the people with an item of the correlation are told
        /// once the host commits (never before: they would read the old state), as the service tells them after its own commit; a host that can't say
        /// when leaves them to reload.
        /// </summary>
        private Task<CorrelationState> CorrelationAsync(string correlationKey, Func<CorrelationLifecycle, Guid, Task<Outcome<CorrelationView>>> change,
            CancellationToken cancellationToken)
            => ConcurrentAsync(() => InHostTransactionAsync(async (services, db, application) =>
            {
                var state = Value(await change(services.GetRequiredService<CorrelationLifecycle>(), application.Id)).State;
                if (services.GetRequiredService<NotificationsHosting>().Inbox)
                {
                    var items = await db.InboxItems.AsNoTracking()
                        .Where(i => i.TenantId == options.TenantId && db.Correlations.Any(c => c.TenantId == i.TenantId && c.Id == i.CorrelationId
                            && c.ApplicationId == application.Id && c.Key == correlationKey))
                        .Select(i => new { i.SubscriberId, i.Id }).ToListAsync(cancellationToken);
                    if (items.Count > 0)
                    {
                        var inbox = services.GetRequiredService<Inbox.IInboxEvents>();
                        transaction!.OnCommitted(() =>
                        {
                            // Told, not waited for: the host's commit isn't held up, and the hub's own failure is its own (it never throws).
                            foreach (var person in items.GroupBy(i => i.SubscriberId))
                            {
                                _ = inbox.PublishAsync(new Inbox.InboxChange(options.TenantId, person.Key, Inbox.InboxChange.Changed, [.. person.Select(i => i.Id)]),
                                    CancellationToken.None);
                            }
                        });
                    }
                }
                return state;
            }, cancellationToken));

        /// <summary>
        /// A hold or release in the host's transaction. A release that resumed parked escalations wakes the escalation stage, and counts them,
        /// once the host commits (never before: the stage can't see them until then); a host that can't say when leaves them to the poll.
        /// </summary>
        private Task<CorrelationHold> HoldChangeAsync(Func<CorrelationLifecycle, Guid, Task<Outcome<CorrelationHoldResult>>> change,
            CancellationToken cancellationToken)
            => ConcurrentAsync(() => InHostTransactionAsync(async (services, _, application) =>
            {
                var result = Value(await change(services.GetRequiredService<CorrelationLifecycle>(), application.Id));
                if (result.Resumed > 0)
                {
                    var (nudges, resumed) = (services.GetRequiredService<Stages.StageNudges>(), result.Resumed);
                    transaction!.OnCommitted(() =>
                    {
                        Telemetry.EscalationsReleased.Add(resumed);
                        nudges.Wake(Workflows.EscalationStage.StageName);
                    });
                }
                var v = result.View;
                return new CorrelationHold(v.State, v.HeldAt is not null, v.HeldAt, v.HoldReason, v.HeldBy, result.Changed, result.Resumed);
            }, cancellationToken));

        /// <summary>
        /// A correlation call that met another change of the same correlation (a request, a resolve, a hold at the same moment) is refused as the
        /// HTTP API refuses it (<c>concurrent_change</c>). The lost update doesn't abort the host's PostgreSQL transaction, so the host may call
        /// again in it; a unique violation or a serialization failure does abort it, and goes up as it is (the host retries its transaction).
        /// </summary>
        private static async Task<T> ConcurrentAsync<T>(Func<Task<T>> call)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (DbConflicts.Classify(ex) is { Kind: ConflictKind.RowGone })
            {
                throw new NotificationRefusedException(ErrorCodes.ConcurrentChange,
                    "The correlation changed at the same moment (a request, a resolve or a hold); asking again answers on what it is now.");
            }
        }

        /// <summary>
        /// Runs <paramref name="work"/> in the host's transaction (<see cref="HostScope"/>), with the host's application. Refused without an
        /// open transaction: there would be nothing to commit the request with.
        /// </summary>
        private Task<T> InHostTransactionAsync<T>(Func<IServiceProvider, NotificationsDbContext, Data.Application, Task<T>> work,
            CancellationToken cancellationToken)
            => hostScope.InHostTransactionAsync("embedded", async (services, db) =>
            {
                var application = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.Key == options.ApplicationKey, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"The application {options.ApplicationKey} isn't provisioned yet: the embedded pipeline declares it at start (its hosted service).");
                return await work(services, db, application);
            }, cancellationToken);

        /// <summary>The outcome's value, or its refusal as the HTTP API would give it.</summary>
        private static T Value<T>(Outcome<T> outcome)
            => outcome.Kind is OutcomeKind.Ok or OutcomeKind.Created && outcome.Value is { } value
                ? value
                : throw new NotificationRefusedException(outcome.Code ?? (outcome.Kind == OutcomeKind.NotFound ? ErrorCodes.NotFound : ErrorCodes.InvalidRequest),
                    outcome.Message ?? outcome.Kind.ToString(), outcome.Errors?.Select(e => new NotificationFieldError(e.Pointer, e.Message)).ToList());
    }
}
