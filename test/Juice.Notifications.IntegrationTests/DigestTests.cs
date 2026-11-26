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
        private readonly RenderCounter _renders = new();
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
            services.AddSingleton(_renders);
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

        /// <summary>
        /// A worker that loaded a digest open, whose close then finds it closed by another (its claim lost), stops there: nothing of it is
        /// saved, so it can't plan the members alongside the owner or cancel a digest the owner is sending (phases 3–4 review, round 5).
        /// </summary>
        [Fact]
        public async Task A_stale_worker_whose_close_finds_the_digest_closed_saves_nothing()
        {
            LocalStack.RequireAvailable();
            await NotificationAsync(1);
            await DrainAsync<Notification, PlannerStage>("planner");
            await using var scope = _services!.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var digest = await db.Digests.SingleAsync(Ct);
            var context = new StageContext(db, new Claim(digest.TenantId, digest.Id, digest.Version), _clock.GetUtcNow(), scope.ServiceProvider);
            await using (var owner = _database.Context())
            {
                // Another worker claimed it since, and closed it (the claim moves the version).
                await owner.Digests.ExecuteUpdateAsync(s => s.SetProperty(d => d.State, DigestState.Closed).SetProperty(d => d.Version, d => d.Version + 1), Ct);
            }
            var stage = scope.ServiceProvider.GetRequiredService<DigestStage>();

            await Assert.ThrowsAsync<InvalidOperationException>(() => stage.ExecuteAsync(context, digest, Ct));
            Assert.Throws<InvalidOperationException>(() => stage.GiveUp(context, digest, "test"));
            await using var check = _database.Context();
            Assert.Equal(DigestState.Closed, (await check.Digests.AsNoTracking().SingleAsync(Ct)).State);
            Assert.All(await check.Recipients.AsNoTracking().ToListAsync(Ct), r => Assert.Equal(RecipientOutcome.Pending, r.Outcome));
            Assert.Equal(0, await check.Deliveries.CountAsync(Ct));
        }

        /// <summary>
        /// A stale worker's close lands on nothing once another has claimed the digest (the close applies only at the version it loaded), so
        /// the owner's close still applies and its step plans the members (phases 3–4 review, round 6).
        /// </summary>
        [Fact]
        public async Task A_stale_close_lands_on_nothing_and_the_owners_close_applies()
        {
            LocalStack.RequireAvailable();
            await NotificationAsync(1);
            await DrainAsync<Notification, PlannerStage>("planner");
            await using var stale = _services!.CreateAsyncScope();
            var staleDb = stale.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var staleRow = await staleDb.Digests.SingleAsync(Ct);
            var staleContext = new StageContext(staleDb, new Claim(staleRow.TenantId, staleRow.Id, staleRow.Version), _clock.GetUtcNow(), stale.ServiceProvider);
            await using (var claim = _database.Context())
            {
                // Another worker claims it (the claim moves the version) but hasn't closed it yet.
                await claim.Digests.ExecuteUpdateAsync(s => s.SetProperty(d => d.Version, d => d.Version + 1), Ct);
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => stale.ServiceProvider.GetRequiredService<DigestStage>().ExecuteAsync(staleContext, staleRow, Ct));
            await using (var check = _database.Context())
            {
                Assert.Equal(DigestState.Open, (await check.Digests.AsNoTracking().SingleAsync(Ct)).State);
            }

            await using var owner = _services!.CreateAsyncScope();
            var db = owner.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var row = await db.Digests.SingleAsync(Ct);
            var context = new StageContext(db, new Claim(row.TenantId, row.Id, row.Version), _clock.GetUtcNow(), owner.ServiceProvider);
            await owner.ServiceProvider.GetRequiredService<DigestStage>().ExecuteAsync(context, row, Ct);
            await db.SaveChangesAsync(Ct);
            await using var after = _database.Context();
            Assert.Equal(DigestState.Closed, (await after.Digests.AsNoTracking().SingleAsync(Ct)).State);
            Assert.All(await after.Recipients.AsNoTracking().ToListAsync(Ct), r => Assert.Equal(RecipientOutcome.Planned, r.Outcome));
        }

        /// <summary>
        /// A digest's content is rendered once for its destinations carrying the same notifications (phase-5 M46-0), unless the template reads
        /// <c>delivery</c>: then once per delivery, each with its own id.
        /// </summary>
        [Theory]
        [InlineData("{\"n\": {{ items | array.size }}}", 1)]
        [InlineData("{\"id\": {{ delivery.id | json.encode }}}", 2)]
        public async Task A_digest_is_rendered_once_unless_its_template_reads_the_delivery(string body, int renders)
        {
            LocalStack.RequireAvailable();
            await using (var db = _database.Context())
            {
                db.Templates.Add(new Template
                {
                    TypeId = _seed.Type.Id, Channel = Channels.Webhook, Locale = "en", Version = 1, Engine = "scriban", Body = body, CreatedAt = Seed.Now,
                });
                await db.SaveChangesAsync(Ct);
            }
            await NotificationAsync(1);
            await NotificationAsync(2);
            await DrainAsync<Notification, PlannerStage>("planner");
            _clock.Advance(TimeSpan.FromSeconds(60));
            await DrainAsync<Digest, DigestStage>("digest");

            await using (var db = _database.Context())
            {
                var deliveries = await db.Deliveries.AsNoTracking().ToListAsync(Ct);
                Assert.Equal(2, deliveries.Count);
                Assert.Equal(renders, _renders.Count);
                foreach (var delivery in deliveries)
                {
                    var sent = JsonDocument.Parse(delivery.Content).RootElement.GetProperty("Body").GetString()!;
                    Assert.Equal(renders == 1 ? """{"n": 2}""" : $$"""{"id": "{{delivery.Id}}"}""", sent);
                }
            }
        }
    }
}
