using Juice.MediatR;
using Juice.MediatR.Behaviors;
using Juice.Messaging;
using Juice.Messaging.Outbox;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.Data
{
    /// <summary>
    /// Every command's transaction: the handler changes entities, and this saves them, dispatches domain events, saves the outbox and commits, in
    /// that order (Juice <see cref="TransactionBehavior{TRequest, TResponse, TContext}"/>). Handlers never begin, save or commit.
    /// </summary>
    public sealed class NotificationsTransactionBehavior<TRequest, TResponse>(NotificationsDbContext db, IOutboxService<NotificationsDbContext> outbox,
        IMediator mediator, ILogger<NotificationsTransactionBehavior<TRequest, TResponse>> logger, IPostCommitActions? postCommitActions = null)
        : TransactionBehavior<TRequest, TResponse, NotificationsDbContext>(db, outbox, mediator, logger, postCommitActions)
        where TRequest : IRequest<TResponse>;

    /// <summary>
    /// The ambient <see cref="MessageContext"/> every entry point needs before sending a command that may publish (a worker's pass, a request).
    /// Starts one when none is active and clears it after; inside an active one it does nothing.
    /// </summary>
    public readonly struct NotificationsMessageContext : IDisposable
    {
        private readonly bool _started;

        private NotificationsMessageContext(bool started) => _started = started;

        /// <param name="source">What started the work: a worker's name, or <c>api</c>.</param>
        /// <param name="correlationId">The caller's correlation id; null for a new one.</param>
        public static NotificationsMessageContext Begin(string source, string? correlationId = null)
        {
            if (MessageContext.IsInitialized)
            {
                return new NotificationsMessageContext(false);
            }
            MessageContext.Initialize(correlationId ?? Guid.NewGuid().ToString(), causationId: null, executionId: Guid.NewGuid().ToString(), source: source);
            return new NotificationsMessageContext(true);
        }

        public void Dispose()
        {
            if (_started)
            {
                MessageContext.Clear();
            }
        }
    }
}
