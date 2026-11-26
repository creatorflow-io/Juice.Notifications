using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Delivering;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;
using Juice.Notifications.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// A route's own digest (phase-5 M46-1, decision G2), driven by hand on a manual clock: what reaches a route with <c>digestSeconds</c> waits
    /// in one digest per route and type for its window and goes as one delivery per target with an item each, as a legacy webhook endpoint
    /// grouped what came within its window. A route without one sends at once; a type's own digest takes its recipients first, never twice.
    /// </summary>
    public sealed class RouteDigestTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 50, Lease = TimeSpan.FromSeconds(30) };
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
        private ServiceProvider? _services;
        private Guid _app;
        private NotificationType _plain = null!;
        private NotificationType _digested = null!;
        private readonly Dictionary<string, Guid> _targets = [];

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = await _database.MigratedAsync();
            var app = Seed.Application("routes");
            NotificationType Type(string key, string workflow) => new()
            {
                ApplicationId = app.Id, Key = key, Version = 1, Name = key, Category = "alerts", DisplayGroup = DisplayGroups.General,
                Priority = NotificationPriority.Normal, Schema = "{}", SchemaHash = "h", Workflow = workflow, DeclarationHash = "d", CreatedAt = Seed.Now,
            };
            _plain = Type("routes.alert", """[{"step":"send"}]""");
            _digested = Type("routes.batched", """[{"by":"type","step":"digest","windowSeconds":300},{"step":"send"}]""");
            var integration = Seed.Integration(Tenant);
            db.AddRange(app, _plain, _digested, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now }, integration);
            foreach (var key in new[] { "grouped", "at-once" })
            {
                var target = new IntegrationTarget
                {
                    TenantId = Tenant, IntegrationId = integration.Id, Key = key, Address = $"https://hooks.example.com/{key}", DefinitionHash = "h", UpdatedAt = Seed.Now,
                };
                var route = new Route
                {
                    TenantId = Tenant, Key = key, Match = """{"type":"routes.*"}""", Targets = [key], DigestSeconds = key == "grouped" ? 60 : 0,
                    DefinitionHash = "h", UpdatedAt = Seed.Now,
                };
                db.AddRange(target, route);
                _targets[key] = target.Id;
                db.RouteTargets.Add(new RouteTarget { TenantId = Tenant, RouteId = route.Id, TargetId = target.Id, Position = 0 });
            }
            await db.SaveChangesAsync();
            _app = app.Id;

            var services = new ServiceCollection().AddLogging(l => l.AddProvider(new ErrorLogProvider()));
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddPostgresClaimStore();
            services.AddPlanning(Options, runWorkers: false);
            services.AddDeliveryPlanning(Options, runWorkers: false);
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

        private async Task DrainAsync<T, TStage>(string stage) where T : Claimable, ITenantOwned where TStage : class, IStage<T>
        {
            var runner = _services!.GetRequiredService<StageRunner<T, TStage>>();
            PassResult pass;
            do
            {
                pass = await runner.RunOnceAsync(stage, Options, Ct);
                Assert.Equal(0, pass.Failed);
            }
            while (pass.Claimed > 0);
        }

        private async Task PlanAsync()
        {
            await DrainAsync<Notification, PlannerStage>("planner");
            await DrainAsync<NotificationRecipient, DeliveryPlannerStage>("delivery-planner");
        }

        /// <summary>A notification of the type to the routes it matches, due now.</summary>
        private async Task NotificationAsync(NotificationType type, int n)
        {
            await using var db = _database.Context();
            var request = Seed.Request(Tenant, _app, type.Id, $"{type.Key}-{n}-{Guid.NewGuid():N}");
            db.Entry(request).Property(r => r.TypeKey).CurrentValue = type.Key;
            db.Entry(request).Property(r => r.Data).CurrentValue = $$"""{ "n": {{n}} }""";
            db.Entry(request).Property(r => r.AcceptedAt).CurrentValue = _clock.GetUtcNow().AddSeconds(n);
            db.Entry(request).Property(r => r.Recipients).CurrentValue = """[{"kind":"route"}]""";
            var notification = Seed.Notification(Tenant, request.Id);
            notification.NextAt = _clock.GetUtcNow();
            db.AddRange(request, notification);
            await db.SaveChangesAsync(Ct);
        }

        private async Task<List<Delivery>> DeliveriesAsync(string target)
        {
            await using var db = _database.Context();
            var key = DestinationKeys.Target(_targets[target]);
            return await db.Deliveries.AsNoTracking().Where(d => d.DestinationKey == key).OrderBy(d => d.CreatedAt).ToListAsync(Ct);
        }

        [Fact]
        public async Task A_routes_digest_gathers_its_window_into_one_delivery_and_another_route_sends_at_once()
        {
            LocalStack.RequireAvailable();
            for (var n = 1; n <= 3; n++)
            {
                await NotificationAsync(_plain, n);
            }
            await PlanAsync();

            // The route without a digest: one delivery each, now. The digested one: nothing yet, one open digest for the route and type.
            Assert.Equal(3, (await DeliveriesAsync("at-once")).Count);
            Assert.Empty(await DeliveriesAsync("grouped"));
            await using (var db = _database.Context())
            {
                var digest = Assert.Single(await db.Digests.AsNoTracking().ToListAsync(Ct));
                Assert.Equal((DigestKeys.Route, "grouped", _plain.Key, _clock.GetUtcNow().AddSeconds(60)), (digest.By, digest.Key, digest.TypeKey, digest.NextAt!.Value));
            }

            _clock.Advance(TimeSpan.FromSeconds(60));
            await DrainAsync<Digest, DigestStage>("digest");

            var grouped = Assert.Single(await DeliveriesAsync("grouped"));
            await using (var db = _database.Context())
            {
                Assert.Equal(3, await db.DeliveryItems.CountAsync(i => i.DeliveryId == grouped.Id, Ct));
                using var content = JsonDocument.Parse(JsonDocument.Parse(grouped.Content).RootElement.GetProperty("Body").GetString()!);
                Assert.Equal([1, 2, 3], content.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("data").GetProperty("n").GetInt32()));
            }

            // The next notification after the close opens the next window.
            await NotificationAsync(_plain, 4);
            await PlanAsync();
            await using (var db = _database.Context())
            {
                Assert.Equal([DigestState.Closed, DigestState.Open], (await db.Digests.AsNoTracking().OrderBy(d => d.OpensAt).ToListAsync(Ct)).Select(d => d.State));
            }
        }

        [Fact]
        public async Task A_types_own_digest_comes_first_and_nothing_is_digested_twice()
        {
            LocalStack.RequireAvailable();
            await NotificationAsync(_digested, 1);
            await NotificationAsync(_digested, 2);
            await PlanAsync();
            await using (var db = _database.Context())
            {
                // The type's digest holds both routes' recipients; the route's digest never opens.
                var digest = Assert.Single(await db.Digests.AsNoTracking().ToListAsync(Ct));
                Assert.Equal(DigestKeys.Type, digest.By);
            }

            _clock.Advance(TimeSpan.FromSeconds(300));
            await DrainAsync<Digest, DigestStage>("digest");
            await DrainAsync<NotificationRecipient, DeliveryPlannerStage>("delivery-planner");

            foreach (var target in new[] { "grouped", "at-once" })
            {
                var delivery = Assert.Single(await DeliveriesAsync(target));
                await using var db = _database.Context();
                Assert.Equal(2, await db.DeliveryItems.CountAsync(i => i.DeliveryId == delivery.Id, Ct));
            }
            await using (var db = _database.Context())
            {
                Assert.Single(await db.Digests.AsNoTracking().ToListAsync(Ct));
            }
        }

        // QA (M46-1).

        /// <summary>
        /// A notification matching a digest route and one that sends at once is counted towards a storm once, by the send step, on the route that
        /// sends at once only (review round 1: the planner counted it before deciding the route digest, and the send step again).
        /// </summary>
        [Fact]
        public async Task A_notification_on_a_digest_route_and_another_is_counted_once_on_the_other_only()
        {
            LocalStack.RequireAvailable();
            await NotificationAsync(_plain, 1);
            await PlanAsync();
            await using var db = _database.Context();
            var minutes = await db.RouteMinutes.AsNoTracking().Where(m => m.TenantId == Tenant).ToDictionaryAsync(m => m.RouteKey, m => m.Count, Ct);
            Assert.Equal(1, minutes.GetValueOrDefault("at-once"));
            Assert.Equal(0, minutes.GetValueOrDefault("grouped"));
        }

        /// <summary>
        /// A storm on a route with its own digest: the route's digest takes what reaches it, so there is one digest (the route's window), not a
        /// storm digest beside it; the route without one takes the storm's digest as before.
        /// </summary>
        [Fact]
        public async Task A_storm_on_a_digest_route_shares_the_routes_one_digest()
        {
            LocalStack.RequireAvailable();
            await using (var db = _database.Context())
            {
                foreach (var key in new[] { "grouped", "at-once" })
                {
                    db.Storms.Add(new Storm { TenantId = Tenant, RouteKey = key, StartedAt = _clock.GetUtcNow(), LastHighAt = _clock.GetUtcNow(), Peak = 500 });
                }
                await db.SaveChangesAsync(Ct);
            }
            for (var n = 1; n <= 4; n++)
            {
                await NotificationAsync(_plain, n);
            }
            await PlanAsync();
            Assert.Empty(await DeliveriesAsync("grouped"));
            await using (var db = _database.Context())
            {
                var grouped = Assert.Single(await db.Digests.AsNoTracking().Where(d => d.Key == "grouped").ToListAsync(Ct));
                Assert.Equal((DigestKeys.Route, _clock.GetUtcNow().AddSeconds(60)), (grouped.By, grouped.NextAt!.Value));
            }

            _clock.Advance(TimeSpan.FromSeconds(60));
            await DrainAsync<Digest, DigestStage>("digest");
            var delivery = Assert.Single(await DeliveriesAsync("grouped"));
            await using (var db = _database.Context())
            {
                Assert.Equal(4, await db.DeliveryItems.CountAsync(i => i.DeliveryId == delivery.Id, Ct));
                Assert.Single(await db.Digests.AsNoTracking().Where(d => d.Key == "grouped").ToListAsync(Ct));
            }
        }

        /// <summary>
        /// A digest route with two targets and a webhook template that reads <c>delivery.id</c>: each target's delivery of the one digest is its
        /// own, and its body carries its own id (M46-0's per-delivery render).
        /// </summary>
        [Fact]
        public async Task Each_target_of_a_route_digest_gets_a_body_with_its_own_delivery_id()
        {
            LocalStack.RequireAvailable();
            await using (var db = _database.Context())
            {
                var integration = await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == Tenant, Ct);
                var second = new IntegrationTarget
                {
                    TenantId = Tenant, IntegrationId = integration.Id, Key = "grouped-2", Address = "https://hooks.example.com/grouped-2", DefinitionHash = "h",
                    UpdatedAt = Seed.Now,
                };
                var route = await db.Routes.SingleAsync(r => r.TenantId == Tenant && r.Key == "grouped", Ct);
                route.Targets = ["grouped", "grouped-2"];
                db.Add(second);
                db.RouteTargets.Add(new RouteTarget { TenantId = Tenant, RouteId = route.Id, TargetId = second.Id, Position = 1 });
                db.Templates.Add(new Template
                {
                    TypeId = _plain.Id, Channel = Channels.Webhook, Locale = "en", Version = 1, Engine = "scriban", CreatedAt = Seed.Now,
                    Body = """{"id": {{ delivery.id | json.encode }}, "n": {{ items | array.size }}}""",
                });
                await db.SaveChangesAsync(Ct);
                _targets["grouped-2"] = second.Id;
            }
            await NotificationAsync(_plain, 1);
            await NotificationAsync(_plain, 2);
            await PlanAsync();
            _clock.Advance(TimeSpan.FromSeconds(60));
            await DrainAsync<Digest, DigestStage>("digest");

            var deliveries = (await DeliveriesAsync("grouped")).Concat(await DeliveriesAsync("grouped-2")).ToList();
            Assert.Equal(2, deliveries.Count);
            Assert.Single(deliveries.Select(d => d.DigestId).Distinct());
            Assert.All(deliveries, d => Assert.Equal($$"""{"id": "{{d.Id}}", "n": 2}""",
                JsonDocument.Parse(d.Content).RootElement.GetProperty("Body").GetString()));
        }

        /// <summary>
        /// An escalation step that names the digest route by key sends at once (the route's digest is for what reaches it at firing): the step's
        /// delivery is no digest's, while the firing's waits in the route's window.
        /// </summary>
        [Fact]
        public async Task An_escalation_step_to_a_digest_route_sends_at_once()
        {
            LocalStack.RequireAvailable();
            var escalating = new NotificationType
            {
                ApplicationId = _app, Key = "routes.paged", Version = 1, Name = "Paged", Category = "alerts", DisplayGroup = DisplayGroups.General,
                Priority = NotificationPriority.High, Schema = "{}", SchemaHash = "h",
                Workflow = """[{"step":"send"},{"policyFrom":"labels.policy","step":"escalate"}]""", DeclarationHash = "d", CreatedAt = Seed.Now,
            };
            Guid notification;
            await using (var db = _database.Context())
            {
                db.Types.Add(escalating);
                db.EscalationPolicies.Add(new EscalationPolicy
                {
                    TenantId = Tenant, Key = "page-now", Steps = """[{"afterMinutes":0,"recipients":[{"kind":"route","key":"grouped"}]}]""", DefinitionHash = "h",
                    UpdatedAt = Seed.Now,
                });
                var now = _clock.GetUtcNow();
                var correlation = new Correlation { TenantId = Tenant, ApplicationId = _app, Key = "incident:paged", CreatedAt = now, LastRequestAt = now, UpdatedAt = now };
                db.Correlations.Add(correlation);
                var request = Seed.Request(Tenant, _app, escalating.Id, "paged-1");
                db.Entry(request).Property(r => r.TypeKey).CurrentValue = escalating.Key;
                db.Entry(request).Property(r => r.CorrelationKey).CurrentValue = correlation.Key;
                db.Entry(request).Property(r => r.CorrelationId).CurrentValue = correlation.Id;
                db.Entry(request).Property(r => r.Labels).CurrentValue = new Dictionary<string, string> { ["policy"] = "page-now" };
                db.Entry(request).Property(r => r.Recipients).CurrentValue = """[{"kind":"route"}]""";
                var row = Seed.Notification(Tenant, request.Id);
                row.NextAt = now;
                db.AddRange(request, row);
                await db.SaveChangesAsync(Ct);
                notification = row.Id;
            }
            await PlanAsync();
            await DrainAsync<Escalation, EscalationStage>("escalation");
            await PlanAsync();

            await using var check = _database.Context();
            var stepped = await (from i in check.DeliveryItems
                                 join r in check.Recipients on new { i.TenantId, Id = i.RecipientId } equals new { r.TenantId, r.Id }
                                 join d in check.Deliveries on new { i.TenantId, Id = i.DeliveryId } equals new { d.TenantId, d.Id }
                                 where r.NotificationId == notification && r.EscalationStep == 1
                                 select new { r.NamedAs, d.DigestId }).AsNoTracking().ToListAsync(Ct);
            var step = Assert.Single(stepped);
            Assert.Equal(("route:grouped", (Guid?)null), (step.NamedAs, step.DigestId));
            // The firing's own recipient of the route waits in its window.
            Assert.Single(await check.Digests.AsNoTracking().Where(d => d.Key == "grouped" && d.TypeKey == escalating.Key && d.State == DigestState.Open).ToListAsync(Ct));
        }
    }
}
