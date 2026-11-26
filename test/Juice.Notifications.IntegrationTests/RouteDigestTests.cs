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
    }
}
