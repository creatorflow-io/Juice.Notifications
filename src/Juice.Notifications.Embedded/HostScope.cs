using Juice.MediatR;
using Juice.Messaging.Outbox;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.Embedded
{
    /// <summary>
    /// The pipeline's commands in the host's open transaction (phase-5 M46-2, decision on Q-A): a host that changes its own rows and the
    /// pipeline's setup together (Observability's webhooks alias writes a legacy endpoint and its integration, target and route) commits or
    /// rolls back both as one. The work runs on a <see cref="NotificationsDbContext"/> on the host's connection, enlisted in its transaction
    /// (<see cref="INotificationTransaction"/>, which the host registers); each command's changes are saved as it goes, so the next one sees them,
    /// and the outbox is saved at the end. Nothing is committed here: the host's commit makes it durable, its rollback drops it.
    /// </summary>
    public interface INotificationsHostScope
    {
        /// <summary>
        /// Runs <paramref name="work"/> in the host's transaction. Refused (<see cref="ErrorCodes.NoTransaction"/>) when none is open: there
        /// would be nothing to commit the changes with.
        /// </summary>
        Task<T> RunAsync<T>(Func<NotificationsHostWork, Task<T>> work, CancellationToken cancellationToken = default);
    }

    /// <summary>What a host's work in <see cref="INotificationsHostScope"/> has: the tenant, the scope's services and context, and its commands.</summary>
    public sealed class NotificationsHostWork(Guid tenantId, IServiceProvider services, NotificationsDbContext db)
    {
        /// <summary>The host's tenant: every command names it.</summary>
        public Guid TenantId => tenantId;

        /// <summary>The scope the work runs in, on the host's connection.</summary>
        public IServiceProvider Services => services;

        /// <summary>The pipeline's context in the host's transaction, for reads; the commands write.</summary>
        public NotificationsDbContext Db => db;

        private int _saves;

        /// <summary>
        /// Sends a command and, when it succeeds, saves what it changed (in the host's transaction), so the commands after it see it.
        /// <para>
        /// The command runs in a savepoint of the host's transaction, taken before it runs (a handler may write with its own statements, as a
        /// delete releases stale rows): a refused command, or one that fails, is undone to the savepoint, so the host's transaction is neither
        /// changed nor left aborted (25P02), and the host decides whether to roll back. One that loses a race in the database (another
        /// transaction created the same key or id at the same moment, a row changed under it) is answered as a conflict, <c>id_in_use</c> or
        /// <c>concurrent_change</c>.
        /// </para>
        /// <para>
        /// Fail closed: only a command on one tenant's setup (<see cref="Configuration.ITenantCommand"/>), for the host's tenant, runs here; a
        /// command for another tenant is <c>tenant_forbidden</c>, and any other command (platform-wide: applications, types, tenant links; a
        /// request, which goes through <see cref="INotificationClient"/>; people) is <c>not_allowed</c>.
        /// </para>
        /// </summary>
        public async Task<Outcome<TResult>> SendAsync<TResult>(IRequest<Outcome<TResult>> command, CancellationToken cancellationToken = default)
        {
            if (command is not Configuration.ITenantCommand { NotificationTenantId: var tenant })
            {
                return Outcome<TResult>.Forbidden(ErrorCodes.NotAllowed,
                    $"{command.GetType().Name} isn't one of the tenant setup commands a host sends here (a request goes through the notification client).");
            }
            if (tenant != tenantId)
            {
                return Outcome<TResult>.Forbidden(ErrorCodes.TenantForbidden, $"The host's notifications are its tenant's ({tenantId}) only, not {tenant}'s.");
            }
            var transaction = db.Database.CurrentTransaction
                ?? throw new InvalidOperationException("The host scope's context is enlisted in the host's transaction.");
            var savepoint = $"notifications_host_work_{++_saves}";
            await transaction.CreateSavepointAsync(savepoint, cancellationToken);
            Outcome<TResult> outcome;
            try
            {
                outcome = await services.GetRequiredService<IMediator>().Send(command, cancellationToken);
                if (outcome.Kind is OutcomeKind.Ok or OutcomeKind.Created)
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await UndoAsync(transaction, savepoint);
                if (DbConflicts.Classify(ex) is { } conflict && conflict.Kind is ConflictKind.Unique or ConflictKind.RowGone or ConflictKind.Aborted)
                {
                    return conflict is { Kind: ConflictKind.Unique, Constraint: Constraints.IntegrationId }
                        ? Outcome<TResult>.Conflict(ErrorCodes.IdInUse, "Another transaction created an integration with this id at the same moment.")
                        : Outcome<TResult>.Conflict(ErrorCodes.ConcurrentChange,
                            "Another transaction changed the same setup at the same moment; asking again answers on what it is now.");
                }
                throw;
            }
            if (outcome.Kind is not (OutcomeKind.Ok or OutcomeKind.Created))
            {
                // Refused: whatever the handler wrote with its own statements is undone too, and its unsaved changes dropped.
                await UndoAsync(transaction, savepoint);
                return outcome;
            }
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return outcome;
        }

        /// <summary>Back to before the command: its statements undone in the host's transaction, and nothing of it left to save.</summary>
        private async Task UndoAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, string savepoint)
        {
            await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>The host's transaction for the pipeline's work, for <see cref="INotificationsHostScope"/> and the client alike.</summary>
    internal sealed class HostScope(IServiceScopeFactory scopes, NotificationsEmbeddedOptions options, INotificationTransaction? transaction = null)
        : INotificationsHostScope
    {
        public Task<T> RunAsync<T>(Func<NotificationsHostWork, Task<T>> work, CancellationToken cancellationToken = default)
            => InHostTransactionAsync("embedded-host", (services, db) => work(new NotificationsHostWork(options.TenantId, services, db)), cancellationToken);

        /// <summary>
        /// Runs <paramref name="work"/> on the host's connection, in its transaction, then saves what it changed and the outbox without
        /// committing. Refused without an open transaction.
        /// </summary>
        internal async Task<T> InHostTransactionAsync<T>(string source, Func<IServiceProvider, NotificationsDbContext, Task<T>> work, CancellationToken cancellationToken)
        {
            if (transaction?.Connection is not { } connection || transaction.Transaction is not { } hostTransaction)
            {
                throw new NotificationRefusedException(ErrorCodes.NoTransaction,
                    "No transaction is open: the pipeline writes in the host's transaction, and commits or rolls back with its change.");
            }
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<HostConnection>().Connection = connection;
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            await db.Database.UseTransactionAsync(hostTransaction, cancellationToken);
            using var context = NotificationsMessageContext.Begin(source);
            var result = await work(scope.ServiceProvider, db);
            // The context isn't managed by the transaction behavior (the host's transaction was open), so its save dispatches its own audit
            // events, to the host's sink, inside the host's transaction.
            await db.SaveChangesAsync(cancellationToken);
            await scope.ServiceProvider.GetRequiredService<IOutboxService<NotificationsDbContext>>()
                .SaveEventsAsync(db.Database.CurrentTransaction?.TransactionId, cancellationToken);
            return result;
        }
    }
}
