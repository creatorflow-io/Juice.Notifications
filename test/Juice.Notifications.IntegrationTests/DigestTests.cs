using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Delivering;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Digests (phase-3-plan M21, design §7.1, D19), driven by hand on a manual clock: phase 3 proof point 3 (N notifications are one delivery
    /// per destination with N items, none sent twice), a digest closing again plans nothing more, and a notification after the close opens the
    /// next digest.
    /// </summary>
    public sealed class DigestTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 20, Lease = TimeSpan.FromSeconds(30) };
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private const string HookA = "https://hooks.example.com/a";
        private const string HookB = "https://hooks.example.com/b";
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        private ServiceProvider? _services;
        private (Guid App, NotificationType Type) _seed;

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = await _database.MigratedAsync();
            var app = Seed.Application("digest");
            var type = new NotificationType
            {
                ApplicationId = app.Id, Key = "digest.alert", Version = 1, Name = "Alert", Category = "alerts", DisplayGroup = DisplayGroups.General,
                Priority = NotificationPriority.Normal, Schema = "{}", SchemaHash = "h", ExternalRecipients = true,
                Workflow = """[{"by":"type","step":"digest","windowSeconds":60},{"step":"send"}]""", DeclarationHash = "d", CreatedAt = Seed.Now,
            };
            db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now }, Seed.Integration(Tenant));
            await db.SaveChangesAsync();
            _seed = (app.Id, type);

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

        /// <summary>A notification to both webhooks, due now, with its own data.</summary>
        private async Task<Guid> NotificationAsync(int n)
        {
            await using var db = _database.Context();
            var request = Seed.Request(Tenant, _seed.App, _seed.Type.Id, $"k{n}-{Guid.NewGuid():N}");
            db.Entry(request).Property(r => r.TypeKey).CurrentValue = _seed.Type.Key;
            db.Entry(request).Property(r => r.Data).CurrentValue = $$"""{ "n": {{n}} }""";
            db.Entry(request).Property(r => r.AcceptedAt).CurrentValue = _clock.GetUtcNow().AddSeconds(n);
            db.Entry(request).Property(r => r.Recipients).CurrentValue = $$"""
                [{"kind":"address","channel":"webhook","address":"{{HookA}}"},{"kind":"address","channel":"webhook","address":"{{HookB}}"}]
                """;
            var notification = Seed.Notification(Tenant, request.Id);
            notification.NextAt = _clock.GetUtcNow();
            db.AddRange(request, notification);
            await db.SaveChangesAsync(Ct);
            return notification.Id;
        }

        [Fact]
        public async Task A_digest_of_N_is_one_delivery_per_destination_with_N_items_and_none_twice()
        {
            LocalStack.RequireAvailable();
            const int N = 5;
            for (var n = 1; n <= N; n++)
            {
                await NotificationAsync(n);
            }
            await DrainAsync<Notification, PlannerStage>("planner");

            await using (var db = _database.Context())
            {
                // Planned, waiting in one digest (grouped by type: both destinations' recipients of every notification).
                Assert.All(await db.Notifications.AsNoTracking().ToListAsync(Ct), n => Assert.Equal(NotificationState.Planned, n.State));
                var digest = Assert.Single(await db.Digests.AsNoTracking().ToListAsync(Ct));
                Assert.Equal((DigestState.Open, _clock.GetUtcNow().AddSeconds(60)), (digest.State, digest.NextAt!.Value));
                var recipients = await db.Recipients.AsNoTracking().ToListAsync(Ct);
                Assert.Equal(2 * N, recipients.Count);
                Assert.All(recipients, r => Assert.Equal((RecipientOutcome.Pending, digest.Id, (DateTimeOffset?)null), (r.Outcome, r.DigestId!.Value, r.NextAt)));
                Assert.Empty(await db.Deliveries.ToListAsync(Ct));
            }

            // Before it closes: nothing.
            _clock.Advance(TimeSpan.FromSeconds(59));
            await DrainAsync<Digest, DigestStage>("digest");
            await using (var db = _database.Context())
            {
                Assert.Empty(await db.Deliveries.ToListAsync(Ct));
            }

            _clock.Advance(TimeSpan.FromSeconds(1));
            await DrainAsync<Digest, DigestStage>("digest");
            await using (var db = _database.Context())
            {
                var digest = Assert.Single(await db.Digests.AsNoTracking().ToListAsync(Ct));
                Assert.Equal((DigestState.Closed, 2 * N, (DateTimeOffset?)null), (digest.State, digest.Members, digest.NextAt));
                var deliveries = await db.Deliveries.AsNoTracking().OrderBy(d => d.DestinationKey).ToListAsync(Ct);
                Assert.Equal([DestinationKeys.External(Channels.Webhook, HookA), DestinationKeys.External(Channels.Webhook, HookB)],
                    deliveries.Select(d => d.DestinationKey));
                foreach (var delivery in deliveries)
                {
                    Assert.Equal((digest.Id, DeliveryStatus.Pending), (delivery.DigestId!.Value, delivery.Status));
                    Assert.Equal(N, await db.DeliveryItems.CountAsync(i => i.DeliveryId == delivery.Id, Ct));
                    // The webhook's envelope lists the notifications in the order they were accepted.
                    using var content = JsonDocument.Parse(JsonDocument.Parse(delivery.Content).RootElement.GetProperty("Body").GetString()!);
                    Assert.Equal(Enumerable.Range(1, N), content.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("data").GetProperty("n").GetInt32()));
                    Assert.Equal(0, content.RootElement.GetProperty("more").GetInt32());
                }
                Assert.All(await db.Recipients.AsNoTracking().ToListAsync(Ct), r => Assert.Equal((RecipientOutcome.Planned, (DateTimeOffset?)null), (r.Outcome, r.NextAt)));
            }

            // Again (a second claim, a nudge): nothing more is planned.
            await using (var db = _database.Context())
            {
                await db.Digests.ExecuteUpdateAsync(d => d.SetProperty(x => x.NextAt, _clock.GetUtcNow()), Ct);
            }
            await DrainAsync<Digest, DigestStage>("digest");
            await using (var db = _database.Context())
            {
                Assert.Equal(2, await db.Deliveries.CountAsync(Ct));
                Assert.Equal(2 * N, await db.DeliveryItems.CountAsync(Ct));
            }

            // After the close, the next notification opens the next digest.
            await NotificationAsync(N + 1);
            await DrainAsync<Notification, PlannerStage>("planner");
            await using (var db = _database.Context())
            {
                Assert.Equal([DigestState.Closed, DigestState.Open], (await db.Digests.AsNoTracking().OrderBy(d => d.OpensAt).ThenBy(d => d.Id).ToListAsync(Ct)).Select(d => d.State));
            }
        }

        /// <summary>
        /// A digest given up while open closes under the lock members join under before it reads its members (phases 3–4 review, round 3): a
        /// notification joining at that moment either is among the members it fails, or finds the digest closed and opens the next one; none is
        /// left waiting in a digest that never sends.
        /// </summary>
        [Fact]
        public async Task A_digest_given_up_while_open_takes_the_joining_lock_and_fails_every_member()
        {
            LocalStack.RequireAvailable();
            await NotificationAsync(1);
            await DrainAsync<Notification, PlannerStage>("planner");
            await using (var joining = _database.Context())
            {
                await using var transaction = await joining.Database.BeginTransactionAsync(Ct);
                var open = await joining.Digests.AsNoTracking().SingleAsync(Ct);
                var key = Workflows.WorkflowRunner.DigestLock(Tenant, open.TypeKey, open.By, open.Key);
                await joining.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({Workflows.WorkflowRunner.DigestLockSpace}, hashtext({key}))", Ct);

                await using var scope = _services!.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var digest = await db.Digests.SingleAsync(Ct);
                var context = new StageContext(db, new Claim(digest.TenantId, digest.Id, digest.Version), _clock.GetUtcNow(), scope.ServiceProvider);
                var givingUp = Task.Run(() => scope.ServiceProvider.GetRequiredService<DigestStage>().GiveUp(context, digest, "test"), Ct);
                await Task.Delay(500, Ct);
                Assert.False(givingUp.IsCompleted); // waits for the joining notification
                await transaction.CommitAsync(Ct);
                await givingUp;
                await db.SaveChangesAsync(Ct);
            }
            await using (var db = _database.Context())
            {
                Assert.Equal(DigestState.Cancelled, (await db.Digests.AsNoTracking().SingleAsync(Ct)).State);
                Assert.All(await db.Recipients.AsNoTracking().ToListAsync(Ct), r => Assert.Equal(RecipientOutcome.Failed, r.Outcome));
            }
        }
    }
}
