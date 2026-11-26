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
    /// Which commands are the pipeline's (phase-5 waves §5.2, U2): those declared in <see cref="Assemblies"/>. Registered by the pipeline's
    /// hosting (<c>Juice.Notifications.Hosting.NotificationsPipeline</c>), which names its assemblies; never matched by name.
    /// </summary>
    public sealed class NotificationsCommands(IReadOnlyCollection<System.Reflection.Assembly> assemblies)
    {
        public IReadOnlyCollection<System.Reflection.Assembly> Assemblies { get; } = assemblies;

        public bool Owns(Type request) => Assemblies.Contains(request.Assembly);
    }

    /// <summary>
    /// <see cref="NotificationsTransactionBehavior{TRequest, TResponse}"/> around the pipeline's own commands only (<see cref="NotificationsCommands"/>):
    /// any other request in the container (an embedded host's) goes straight on, and no notifications context or transaction is made for it.
    /// Same order as the transaction it wraps. Here, not in the core, so the core's mediator scan doesn't register it a second time.
    /// </summary>
    public sealed class NotificationsPipelineTransaction<TRequest, TResponse>(IServiceProvider services, NotificationsCommands commands)
        : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public int Order => int.MaxValue - 20;

        public ValueTask<TResponse> Handle(TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
            => commands.Owns(typeof(TRequest))
                ? Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetRequiredService<NotificationsTransactionBehavior<TRequest, TResponse>>(services).Handle(request, next, cancellationToken)
                : next.Invoke(request, cancellationToken);
    }

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
