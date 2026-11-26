using Juice.MediatR;
using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Delivering;
using Juice.Notifications.History;
using Juice.Notifications.Providers;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Dry run (phase-5-plan I67; m44-architecture §3.6, §5.3): a delivery planned while <c>Delivery:DryRun</c> is on is marked so at planning,
    /// and the dispatcher takes it through every check a send makes, then records <c>WouldSend</c> with its rendered body, never calling the
    /// provider and making no attempt. A check that fails takes the path a real send takes. The mark is fixed at planning, so switching the option
    /// at dispatch changes nothing already planned.
    /// </summary>
    public sealed class DryRunTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 10, Lease = TimeSpan.FromSeconds(60) };
        private const string Mail = """{"address":"a@example.com"}""";

        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
        private readonly ScriptedProvider _email = new(Channels.Email);
        private readonly RecordingHub _hub = new();

        /// <summary>What the inbox hub would push to people.</summary>
        private sealed class RecordingHub : Inbox.IInboxEvents
        {
            public System.Collections.Concurrent.ConcurrentQueue<Inbox.InboxChange> Changes { get; } = new();

            public Task PublishAsync(Inbox.InboxChange change, CancellationToken ct)
            {
                Changes.Enqueue(change);
                return Task.CompletedTask;
            }
        }
        private readonly Dictionary<bool, ServiceProvider> _dispatchers = [];
        private ServiceProvider? _planner;

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
            // The delivery planner in dry run.
            var services = new ServiceCollection().AddLogging(l => l.AddProvider(new ErrorLogProvider()));
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddPostgresClaimStore();
            services.Configure<DeliveryOptions>(o => o.DryRun = true);
            services.AddDeliveryPlanning(Options, runWorkers: false);
            _planner = services.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var dispatcher in _dispatchers.Values)
            {
                await dispatcher.DisposeAsync();
            }
            if (_planner is not null)
            {
                await _planner.DisposeAsync();
            }
            await _database.DisposeAsync();
        }

        /// <summary>A dispatcher whose own <c>Delivery:DryRun</c> is <paramref name="dryRun"/>: it decides nothing, the delivery's mark does.</summary>
        private ServiceProvider Dispatcher(bool dryRun)
        {
            if (!_dispatchers.TryGetValue(dryRun, out var provider))
            {
                var services = new ServiceCollection().AddLogging();
                services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
                services.AddSingleton<TimeProvider>(_clock);
                services.AddPostgresClaimStore();
                services.Configure<DeliveryOptions>(o => (o.DryRun, o.RetryBase) = (dryRun, TimeSpan.FromSeconds(1)));
                services.AddSingleton<INotificationProvider>(_email);
                // The inbox as the service writes it, with what the hub would push recorded.
                services.AddSingleton<Inbox.IInboxEvents>(_hub);
                Inbox.InboxRegistration.AddInboxProvider(services);
                services.AddBuiltInChannels();
                People.ContactEncryptionRegistration.AddContactEncryption(services);
                services.AddStage<Delivery, DispatcherStage>(Options, runWorkers: false);
                services.AddMediatR(mediator => mediator.RegisterServicesFromAssemblyContaining<RetryDelivery>(includeNonPublicTypes: true));
                _dispatchers[dryRun] = provider = services.BuildServiceProvider();
            }
            return provider;
        }

        private Task<PassResult> DispatchAsync(bool dryRunOption = false)
            => Dispatcher(dryRunOption).GetRequiredService<StageRunner<Delivery, DispatcherStage>>().RunOnceAsync("dispatcher", Options, Ct);

        private sealed record Scene(Guid Tenant, Guid App, NotificationType Type, NotificationRequest Request, Notification Notification, Integration Integration);

        /// <summary>A tenant of its own: a type with an email template, a request and its notification, and a fake email integration.</summary>
        private async Task<Scene> SceneAsync(DateTimeOffset? expiresAt = null, bool enabled = true, string? secretRef = null)
        {
            await using var db = _database.Context();
            var tenant = Guid.CreateVersion7();
            var app = Seed.Application("dry" + Guid.NewGuid().ToString("N")[..8]);
            var type = Seed.Type(app.Id, app.Key + ".thing");
            var request = Seed.Request(tenant, app.Id, type.Id, Guid.NewGuid().ToString("N"));
            db.Entry(request).Property(r => r.TypeKey).CurrentValue = type.Key;
            db.Entry(request).Property(r => r.Data).CurrentValue = """{ "host": "db-1" }""";
            db.Entry(request).Property(r => r.ExpiresAt).CurrentValue = expiresAt;
            var notification = Seed.Notification(tenant, request.Id);
            (notification.State, notification.NextAt) = (NotificationState.Planned, null);
            var integration = new Integration
            {
                TenantId = tenant, Key = "fake-mail", Channel = Channels.Email, Kind = "fake", Settings = "{}", Enabled = enabled, SecretRef = secretRef,
                DefinitionHash = "h", UpdatedAt = Seed.Now,
            };
            db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = tenant, LinkedAt = Seed.Now }, request, notification, integration);
            db.Templates.Add(new Template
            {
                TypeId = type.Id, Channel = Channels.Email, Locale = "en", Version = 1, Engine = "scriban", Body = "Subject: Disk\n\nHost {{ data.host }}",
                CreatedAt = Seed.Now,
            });
            await db.SaveChangesAsync(Ct);
            return new Scene(tenant, app.Id, type, request, notification, integration);
        }

        /// <summary>Plans an address recipient through the dry-run delivery planner; the delivery it made.</summary>
        private async Task<Delivery> PlannedAsync(Scene scene)
        {
            await using (var db = _database.Context())
            {
                db.Recipients.Add(new NotificationRecipient
                {
                    TenantId = scene.Tenant, NotificationId = scene.Notification.Id, Kind = RecipientKind.External, Channel = Channels.Email,
                    Address = "ops@example.com", Resolver = "address", NamedAs = "address:email:ops@example.com", Outcome = RecipientOutcome.Planned,
                    NextAt = _clock.GetUtcNow(), CreatedAt = Seed.Now,
                });
                await db.SaveChangesAsync(Ct);
            }
            var pass = await _planner!.GetRequiredService<StageRunner<NotificationRecipient, DeliveryPlannerStage>>().RunOnceAsync("delivery-planner", Options, Ct);
            Assert.Equal(1, pass.Completed);
            await using var read = _database.Context();
            return await read.Deliveries.AsNoTracking().SingleAsync(d => d.TenantId == scene.Tenant, Ct);
        }

        /// <summary>A delivery made by hand, as the planner would leave it, dry or not.</summary>
        private async Task<Delivery> HandMadeAsync(Scene scene, bool dryRun, string snapshot = Mail)
        {
            await using var db = _database.Context();
            var recipient = Seed.Recipient(scene.Tenant, scene.Notification.Id);
            var delivery = new Delivery
            {
                TenantId = scene.Tenant, Channel = Channels.Email, IntegrationId = scene.Integration.Id, DestinationKey = "ext:email:a@example.com",
                DestinationSnapshot = snapshot, Locale = "en", RendererVersion = "test", Content = """{"subject":"s","text":"t"}""", CreatedAt = _clock.GetUtcNow(),
                Status = DeliveryStatus.Pending, NextAt = _clock.GetUtcNow(), DryRun = dryRun,
            };
            db.AddRange(recipient, delivery, new DeliveryItem
            {
                TenantId = scene.Tenant, DeliveryId = delivery.Id, RecipientId = recipient.Id, RequestId = scene.Request.Id, DestinationKey = delivery.DestinationKey,
                Channel = Channels.Email,
            });
            await db.SaveChangesAsync(Ct);
            return delivery;
        }

        private async Task<(Delivery Row, int Attempts)> ReadAsync(Delivery delivery)
        {
            await using var db = _database.Context();
            return (await db.Deliveries.AsNoTracking().SingleAsync(d => d.TenantId == delivery.TenantId && d.Id == delivery.Id, Ct),
                await db.Attempts.CountAsync(a => a.TenantId == delivery.TenantId && a.DeliveryId == delivery.Id, Ct));
        }

        [Fact]
        public async Task A_delivery_planned_in_dry_run_is_recorded_as_would_send_with_its_body_and_never_sent()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            var planned = await PlannedAsync(scene);
            Assert.True(planned.DryRun);
            Assert.Equal(DeliveryStatus.Pending, planned.Status);
            var sends = _email.Sends;

            // The dispatcher's own option is off (the switch to the new pipeline happened meanwhile): the mark decides.
            Assert.Equal(1, (await DispatchAsync(dryRunOption: false)).Completed);

            var (row, attempts) = await ReadAsync(planned);
            Assert.Equal((DeliveryStatus.WouldSend, false, (DateTimeOffset?)null, (FailureCategory?)null, (string?)null),
                (row.Status, row.FailureCheckDue, row.NextAt, row.FailureCategory, row.LastError));
            Assert.Equal((_clock.GetUtcNow(), _clock.GetUtcNow()), (row.CompletedAt!.Value, row.FirstAttemptAt!.Value));
            Assert.Contains("db-1", row.Content);
            Assert.Equal((0, 0), (attempts, row.Attempts));
            Assert.Equal(sends, _email.Sends);
            // Ended: nothing more claims it.
            _clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(0, (await DispatchAsync()).Claimed);
        }

        [Fact]
        public async Task A_delivery_planned_for_real_is_sent_even_by_a_dispatcher_in_dry_run()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            var delivery = await HandMadeAsync(scene, dryRun: false);
            _email.Then(SendResult.Delivered());
            var sends = _email.Sends;
            Assert.Equal(1, (await DispatchAsync(dryRunOption: true)).Completed);
            var (row, attempts) = await ReadAsync(delivery);
            Assert.Equal((DeliveryStatus.Delivered, 1), (row.Status, attempts));
            Assert.Equal(sends + 1, _email.Sends);
        }

        [Fact]
        public async Task A_dry_delivery_whose_request_expired_is_expired_not_would_send()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync(expiresAt: _clock.GetUtcNow().AddMinutes(1));
            var delivery = await HandMadeAsync(scene, dryRun: true);
            _clock.Advance(TimeSpan.FromMinutes(2));
            await DispatchAsync();
            Assert.Equal(DeliveryStatus.Expired, (await ReadAsync(delivery)).Row.Status);
        }

        [Fact]
        public async Task A_dry_delivery_that_fails_a_check_takes_the_real_path()
        {
            LocalStack.RequireAvailable();
            var sends = _email.Sends;
            // No address to send to: failed, as a real send would be.
            var scene = await SceneAsync();
            var noAddress = await HandMadeAsync(scene, dryRun: true, snapshot: "{}");
            await DispatchAsync();
            var (failed, _) = await ReadAsync(noAddress);
            Assert.Equal((DeliveryStatus.Failed, (FailureCategory?)FailureCategory.Destination, true), (failed.Status, failed.FailureCategory, failed.FailureCheckDue));

            // Its integration disabled, and no other for the channel: failed on the provider's side.
            var disabled = await HandMadeAsync(await SceneAsync(enabled: false), dryRun: true);
            await DispatchAsync();
            Assert.Equal((DeliveryStatus.Failed, (FailureCategory?)FailureCategory.Provider), ((await ReadAsync(disabled)).Row.Status, (await ReadAsync(disabled)).Row.FailureCategory));

            Assert.Equal(sends, _email.Sends);
        }

        /// <summary>
        /// An integration secret (env: or file:) that isn't set fails the step, dry or not, as on release/0: the delivery stays Pending with the
        /// runner's backoff (no attempt, nothing sent), and is given up as failed after the step-failure limit. Never would-send.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task An_unreadable_secret_fails_the_step_as_a_real_send_does(bool dryRun)
        {
            LocalStack.RequireAvailable();
            var sends = _email.Sends;
            var scene = await SceneAsync(secretRef: "env:NOTIFICATIONS_SECRET_DRY_RUN_UNSET_" + Guid.NewGuid().ToString("N"));
            var delivery = await HandMadeAsync(scene, dryRun);
            var pass = await DispatchAsync();
            Assert.Equal((1, 1), (pass.Claimed, pass.Failed));
            var (row, attempts) = await ReadAsync(delivery);
            Assert.Equal((DeliveryStatus.Pending, 1, 0), (row.Status, row.StepFailures, attempts));
            Assert.Equal(sends, _email.Sends);
        }

        /// <summary>
        /// Shadow mode runs beside legacy, so a dry delivery must never reach a person: a dry inbox delivery writes no item and the hub tells no
        /// one, while the same delivery planned for real does both (the control).
        /// </summary>
        [Fact]
        public async Task A_dry_inbox_delivery_writes_no_item_and_tells_no_one()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            Delivery Inbox(Guid integration, Subscriber person, bool dryRun) => new()
            {
                TenantId = scene.Tenant, Channel = Channels.Inbox, IntegrationId = integration, DestinationKey = $"inbox:{person.Id}",
                DestinationSnapshot = $$"""{"subscriber":"{{person.Id}}"}""", Locale = "en", RendererVersion = "test",
                Content = $$"""{"type":"{{scene.Type.Key}}","title":"Disk full","body":"db-1"}""", CreatedAt = _clock.GetUtcNow(), Status = DeliveryStatus.Pending,
                NextAt = _clock.GetUtcNow(), DryRun = dryRun,
            };
            Delivery dry, real;
            await using (var db = _database.Context())
            {
                var inbox = new Integration
                {
                    TenantId = scene.Tenant, Key = Notifications.Inbox.InboxIntegrations.Key, Channel = Channels.Inbox, Kind = Notifications.Inbox.InboxIntegrations.Kind,
                    Settings = "{}", ManagedBy = ManagedBy.Service, DefinitionHash = "h", UpdatedAt = Seed.Now,
                };
                var (ann, ben) = (new Subscriber { TenantId = scene.Tenant, Source = "local", ExternalId = "ann", CreatedAt = Seed.Now },
                    new Subscriber { TenantId = scene.Tenant, Source = "local", ExternalId = "ben", CreatedAt = Seed.Now });
                (dry, real) = (Inbox(inbox.Id, ann, dryRun: true), Inbox(inbox.Id, ben, dryRun: false));
                // One recipient (a group, say) reaching both people.
                var recipient = Seed.Recipient(scene.Tenant, scene.Notification.Id);
                db.AddRange(inbox, ann, ben, recipient, dry, real,
                    new DeliveryItem { TenantId = scene.Tenant, DeliveryId = dry.Id, RecipientId = recipient.Id, RequestId = scene.Request.Id, DestinationKey = dry.DestinationKey, Channel = Channels.Inbox },
                    new DeliveryItem { TenantId = scene.Tenant, DeliveryId = real.Id, RecipientId = recipient.Id, RequestId = scene.Request.Id, DestinationKey = real.DestinationKey, Channel = Channels.Inbox });
                await db.SaveChangesAsync(Ct);
            }

            await DispatchAsync();

            Assert.Equal(DeliveryStatus.WouldSend, (await ReadAsync(dry)).Row.Status);
            Assert.Equal(DeliveryStatus.Delivered, (await ReadAsync(real)).Row.Status);
            await using var check = _database.Context();
            Assert.False(await check.InboxItems.AnyAsync(i => i.TenantId == scene.Tenant && i.DeliveryId == dry.Id, Ct));
            var item = await check.InboxItems.AsNoTracking().SingleAsync(i => i.TenantId == scene.Tenant && i.DeliveryId == real.Id, Ct);
            var told = _hub.Changes.Where(c => c.TenantId == scene.Tenant).ToList();
            Assert.Equal([item.Id], told.SelectMany(c => c.ItemIds));
            Assert.DoesNotContain(told, c => c.SubscriberId == Guid.Parse(dry.DestinationKey["inbox:".Length..]));
        }

        [Fact]
        public async Task A_would_send_delivery_is_not_retried()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            var delivery = await HandMadeAsync(scene, dryRun: true);
            await DispatchAsync();
            Assert.Equal(DeliveryStatus.WouldSend, (await ReadAsync(delivery)).Row.Status);

            await using var scope = Dispatcher(false).CreateAsyncScope();
            var retried = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new RetryDelivery(scene.Tenant, delivery.Id), Ct);
            Assert.Equal((OutcomeKind.Conflict, ErrorCodes.NotRetryable), (retried.Kind, retried.Code));
        }
    }
}
