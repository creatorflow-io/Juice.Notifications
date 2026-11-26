using Juice.MediatR;
using Juice.Messaging;
using Juice.Messaging.Extensions;
using Juice.Messaging.Policies;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Embedded;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Juice.Notifications.IntegrationTests.EmbeddedHostTests;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The pipeline beside a host's own messaging in one container (phase-5 waves §5.2, U2): a host command opens no notifications transaction,
    /// a host message gets no notifications route, and the pipeline's own commands and events are handled as before. The host here has its own
    /// <c>DbContext</c>, command, event and publishing policy, as Observability's platform does.
    /// </summary>
    public sealed class CoexistenceTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000c0e01");
        private readonly ScratchDatabase _database = new();
        private ServiceProvider? _services;

        /// <summary>The host's own command: its handler reports whether a notifications transaction is open around it.</summary>
        public sealed record HostCommand : MessageBase, IRequest<bool>;

        internal sealed class HostCommandHandler(NotificationsDbContext notifications) : IRequestHandler<HostCommand, bool>
        {
            public ValueTask<bool> Handle(HostCommand request, CancellationToken cancellationToken)
                => ValueTask.FromResult(notifications.Database.CurrentTransaction is not null);
        }

        /// <summary>The host's own event, which its policy routes to its own route.</summary>
        public sealed record HostEvent : IntegrationEvent;

        internal sealed class HostRoutes : IMessagePublishingPolicy
        {
            public const string Route = "host";

            public ValueTask<IReadOnlyCollection<PublishRoute>> ResolveAsync(PolicyResolveContext context)
                => ValueTask.FromResult<IReadOnlyCollection<PublishRoute>>(context.EventType == nameof(HostEvent) ? [new PublishRoute(Route, string.Empty)] : []);
        }

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using (await _database.MigratedAsync())
            {
            }
            var services = new ServiceCollection().AddLogging();
            // The host's side: its context, command and policy, registered as a host registers them.
            services.AddDbContext<HostDbContext>(o => o.UseNpgsql(_database.ConnectionString));
            services.AddScoped<IRequestHandler<HostCommand, bool>, HostCommandHandler>();
            services.AddJuiceNotificationsEmbedded(new ConfigurationBuilder().Build(), o =>
            {
                EmbeddedHost.Configure(_database.ConnectionString)(o);
                o.TenantId = Tenant;
            });
            services.AddMessaging().AddPublishingPolicies<HostRoutes>();
            _services = services.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync()
        {
            if (_services is not null)
            {
                await _services.DisposeAsync();
            }
            await _database.DisposeAsync();
        }

        [Fact]
        public async Task A_host_command_opens_no_notifications_transaction_and_the_pipelines_own_command_still_commits()
        {
            LocalStack.RequireAvailable();
            using var context = NotificationsMessageContext.Begin("test");
            await using (var scope = _services!.CreateAsyncScope())
            {
                Assert.False(await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new HostCommand(), Ct));
            }

            // The pipeline's own command: its handler only changes entities; the transaction behavior saves and commits them.
            await using (var scope = _services!.CreateAsyncScope())
            {
                // One transaction around it, not two: the gate, which runs the transaction itself.
                var behaviors = scope.ServiceProvider.GetServices<IPipelineBehavior<PutAllowlistEntry, Outcome<AllowlistView>>>().ToList();
                Assert.Single(behaviors, b => b is NotificationsPipelineTransaction<PutAllowlistEntry, Outcome<AllowlistView>>);
                Assert.DoesNotContain(behaviors, b => b is NotificationsTransactionBehavior<PutAllowlistEntry, Outcome<AllowlistView>>);
                var outcome = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new PutAllowlistEntry(Tenant, "10.20.0.0/16", [8443]), Ct);
                Assert.Equal(OutcomeKind.Ok, outcome.Kind);
            }
            await using var db = _database.Context();
            Assert.True(await db.Allowlist.AnyAsync(a => a.TenantId == Tenant && a.Destination == "10.20.0.0/16", Ct));
        }

        [Fact]
        public async Task A_host_event_gets_only_the_hosts_route_and_the_pipelines_event_keeps_its_own()
        {
            LocalStack.RequireAvailable();
            var policy = _services!.GetRequiredService<IMessagePublishingPolicy>();
            var host = await policy.ResolveAsync(new PolicyResolveContext { EventType = nameof(HostEvent), Domain = typeof(HostEvent).GetDomainName() });
            Assert.Equal([HostRoutes.Route], host.Select(r => r.PublisherKey));

            var pipeline = await policy.ResolveAsync(new PolicyResolveContext
            {
                EventType = nameof(Events.NotificationEvent), Domain = typeof(Events.NotificationEvent).GetDomainName(),
            });
            Assert.Equal(["local"], pipeline.Select(r => r.PublisherKey));
        }
    }
}
