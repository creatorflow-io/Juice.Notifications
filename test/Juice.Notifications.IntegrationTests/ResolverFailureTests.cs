using System.Collections.Concurrent;
using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Intake;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// A resolver for tests that can be taken down (phase-2-plan M12): while <see cref="Down"/>, it throws <see cref="ResolverUnavailableException"/>,
    /// as a remote directory that times out would. Groups are whoever the test put in them, returned in a different order on every call, as a
    /// source with no stable order would. Registered as the implementation <c>flaky</c>.
    /// </summary>
    public sealed class FlakyResolver : IRecipientResolver
    {
        public const string Source = "flaky";

        public string Key => "flaky";

        public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { RecipientKinds.User, RecipientKinds.Group };

        public volatile bool Down;

        public int Calls;

        public ConcurrentDictionary<string, ResolvedPerson[]> Groups { get; } = new(StringComparer.Ordinal);

        public ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref Calls);
            if (Down)
            {
                throw new ResolverUnavailableException("The directory timed out.");
            }
            var members = Groups.GetValueOrDefault(spec.Id!) ?? [];
            // A different rotation each call: what the planner keeps mustn't depend on the order it was given.
            var rotated = members.Skip(call % Math.Max(1, members.Length)).Concat(members.Take(call % Math.Max(1, members.Length))).ToList();
            return ValueTask.FromResult(new ResolveResult([]) { People = rotated });
        }
    }

    /// <summary>
    /// Resolver failure and bounds in planning (phase-2-plan M12, I22, I23; phase 2 proof point 4): an outage shorter than the wait limit plans
    /// everyone; a longer one plans the rest and records the name as <c>ResolverUnavailable</c>; a name past the bound keeps the same first people
    /// however often it is planned, and is recorded as <c>Truncated</c>.
    /// </summary>
    public sealed class ResolverFailureTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 10, Lease = TimeSpan.FromSeconds(30) };
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private const int Max = 3;
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
        private readonly FakePeopleResolver _staff = new();
        private readonly FlakyResolver _remote = new();
        private ServiceProvider? _services;
        private (Guid App, Guid Type) _seed;

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = await _database.MigratedAsync();
            var app = Seed.Application("people");
            var type = Seed.Type(app.Id, "people.thing");
            db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now });
            db.Resolvers.AddRange(
                Config("staff", "fake"),
                Config("remote", "flaky"),
                Config("local", "local"));
            await db.SaveChangesAsync();
            _seed = (app.Id, type.Id);

            var services = new ServiceCollection().AddLogging(l => l.AddProvider(new ErrorLogProvider()));
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddPostgresClaimStore();
            services.AddPlanning(Options, runWorkers: false);
            services.Configure<ResolverOptions>(o => o.MaxRecipients = Max);
            services.AddSingleton<IRecipientResolver>(_staff);
            services.AddSingleton<IRecipientResolver>(_remote);
            _services = services.BuildServiceProvider();
        }

        private static RecipientResolverConfig Config(string key, string kind) => new()
        {
            TenantId = Tenant, Key = key, Kind = kind, Handles = ["group", "user"], Settings = "{}", DefinitionHash = "h", UpdatedAt = Seed.Now,
        };

        public async ValueTask DisposeAsync()
        {
            if (_services is not null)
            {
                await _services.DisposeAsync();
            }
            await _database.DisposeAsync();
        }

        private StageRunner<Notification, PlannerStage> Runner => _services!.GetRequiredService<StageRunner<Notification, PlannerStage>>();

        private async Task<Guid> PlanningAsync(string recipients)
        {
            await using var db = _database.Context();
            var request = Seed.Request(Tenant, _seed.App, _seed.Type, Guid.NewGuid().ToString("N"));
            db.Entry(request).Property(r => r.TypeKey).CurrentValue = "people.thing";
            db.Entry(request).Property(r => r.Recipients).CurrentValue = recipients;
            var notification = Seed.Notification(Tenant, request.Id);
            notification.NextAt = _clock.GetUtcNow();
            db.AddRange(request, notification);
            await db.SaveChangesAsync(Ct);
            return notification.Id;
        }

        /// <summary>Runs the planner until nothing is due now; every step must complete, a resolver outage included.</summary>
        private async Task DrainAsync()
        {
            PassResult pass;
            var completed = 0;
            do
            {
                pass = await Runner.RunOnceAsync("planner", Options, Ct);
                Assert.Equal(0, pass.Failed);
                completed += pass.Completed;
            }
            while (pass.Claimed > 0);
            Assert.True(completed > 0, "The planner completed nothing.");
        }

        private async Task<Notification> NotificationAsync(Guid id)
        {
            await using var db = _database.Context();
            return await db.Notifications.SingleAsync(n => n.Id == id, Ct);
        }

        private async Task<List<NotificationRecipient>> RecipientsAsync(Guid id)
        {
            await using var db = _database.Context();
            return await db.Recipients.Where(r => r.NotificationId == id).ToListAsync(Ct);
        }

        private const string Both = """[{"kind":"group","id":"sre","resolver":"staff"},{"kind":"group","id":"ops","resolver":"remote"}]""";

        private static ResolvedPerson Person(string source, string id) => new(source, id, "Person " + id);

        [Fact]
        public async Task An_outage_shorter_than_the_wait_limit_stores_nothing_meanwhile_then_plans_everyone()
        {
            LocalStack.RequireAvailable();
            _staff.Groups["sre"] = [Person(FakePeopleResolver.Source, "adam")];
            _remote.Groups["ops"] = [Person(FlakyResolver.Source, "bea"), Person(FlakyResolver.Source, "cai")];
            _remote.Down = true;
            var start = _clock.GetUtcNow();
            var id = await PlanningAsync(Both);

            await DrainAsync();

            var waiting = await NotificationAsync(id);
            Assert.Equal(NotificationState.Planning, waiting.State);
            Assert.Equal(start, waiting.ResolverWaitSince);
            Assert.Equal(start.AddSeconds(1), waiting.NextAt);
            Assert.Equal(0, waiting.StepFailures);
            // Not even the people who did resolve: each try is the whole plan, or nothing.
            Assert.Empty(await RecipientsAsync(id));
            await using (var db = _database.Context())
            {
                Assert.Empty(await db.Subscribers.Where(s => s.TenantId == Tenant).ToListAsync(Ct));
            }

            // Each wait is as long as the wait so far: 1, 2, 4, 8 s after the first second.
            foreach (var expected in new[] { 2, 4, 8, 16 })
            {
                _clock.Advance((await NotificationAsync(id)).NextAt!.Value - _clock.GetUtcNow());
                await DrainAsync();
                Assert.Equal(start.AddSeconds(expected), (await NotificationAsync(id)).NextAt);
            }

            _remote.Down = false;
            _clock.Advance((await NotificationAsync(id)).NextAt!.Value - _clock.GetUtcNow());
            await DrainAsync();

            Assert.Equal(NotificationState.Planned, (await NotificationAsync(id)).State);
            var recipients = await RecipientsAsync(id);
            Assert.Equal(3, recipients.Count);
            Assert.All(recipients, r => Assert.Equal((RecipientKind.Subscriber, RecipientOutcome.Planned), (r.Kind, r.Outcome)));
            Assert.All(recipients, r => Assert.NotNull(r.SubscriberId));
        }

        [Fact]
        public async Task An_outage_past_the_wait_limit_plans_the_rest_and_records_the_name_as_unavailable()
        {
            LocalStack.RequireAvailable();
            _staff.Groups["sre"] = [Person(FakePeopleResolver.Source, "adam")];
            _remote.Down = true;
            var start = _clock.GetUtcNow();
            var id = await PlanningAsync(Both);

            var tries = new List<DateTimeOffset>();
            for (var i = 0; i < 100 && (await NotificationAsync(id)).State == NotificationState.Planning; i++)
            {
                var due = (await NotificationAsync(id)).NextAt!.Value;
                _clock.Advance(due - _clock.GetUtcNow());
                tries.Add(_clock.GetUtcNow());
                await DrainAsync();
            }

            var planned = await NotificationAsync(id);
            Assert.Equal(NotificationState.Planned, planned.State);
            // 1 s doubling to 30 s, and the last try on the limit itself.
            Assert.Equal(new[] { 0, 1, 2, 4, 8, 16, 32, 62, 92, 122, 152, 182, 212, 242, 272, 300 },
                tries.Select(t => (int)(t - start).TotalSeconds).ToArray());

            var recipients = await RecipientsAsync(id);
            Assert.Equal(2, recipients.Count);
            var adam = Assert.Single(recipients, r => r.SubscriberId is not null);
            Assert.Equal((RecipientOutcome.Planned, "staff"), (adam.Outcome, adam.Resolver));
            var unresolved = Assert.Single(recipients, r => r.SubscriberId is null);
            Assert.Equal((RecipientKind.Name, "group:ops", "remote", RecipientOutcome.ResolverUnavailable), (unresolved.Kind, unresolved.NamedAs, unresolved.Resolver, unresolved.Outcome));
            Assert.Null(unresolved.NextAt);
        }

        [Fact]
        public async Task A_name_past_the_bound_keeps_the_same_first_people_on_every_plan_and_is_recorded_as_truncated()
        {
            LocalStack.RequireAvailable();
            _remote.Groups["ops"] = [.. new[] { "e", "b", "d", "a", "c" }.Select(x => Person(FlakyResolver.Source, x))];
            var first = await PlanningAsync("""[{"kind":"group","id":"ops","resolver":"remote"}]""");
            var second = await PlanningAsync("""[{"kind":"group","id":"ops","resolver":"remote"}]""");

            await DrainAsync();

            await using var db = _database.Context();
            var subscribers = await db.Subscribers.Where(s => s.TenantId == Tenant).ToDictionaryAsync(s => s.Id, s => s.ExternalId, Ct);
            foreach (var id in new[] { first, second })
            {
                var recipients = await RecipientsAsync(id);
                Assert.Equal("a,b,c", string.Join(",", recipients.Where(r => r.SubscriberId is not null).Select(r => subscribers[r.SubscriberId!.Value]).Order()));
                var marker = Assert.Single(recipients, r => r.SubscriberId is null);
                Assert.Equal(("group:ops", RecipientOutcome.Truncated), (marker.NamedAs, marker.Outcome));
            }
            Assert.Equal(3, subscribers.Count);
        }

        [Fact]
        public async Task A_name_past_the_bound_is_truncated_even_when_people_it_returned_are_dropped()
        {
            LocalStack.RequireAvailable();
            // One past the bound, as a resolver that stops there returns; one of them can't be a subscriber.
            _remote.Groups["ops"] = [Person(FlakyResolver.Source, "a"), Person("Not A Source", "b"), Person(FlakyResolver.Source, "c"),
                Person(FlakyResolver.Source, "d")];
            var id = await PlanningAsync("""[{"kind":"group","id":"ops","resolver":"remote"}]""");

            await DrainAsync();

            var recipients = await RecipientsAsync(id);
            Assert.Equal(3, recipients.Count(r => r.Kind == RecipientKind.Subscriber));
            Assert.Equal(RecipientOutcome.Truncated, Assert.Single(recipients, r => r.Kind == RecipientKind.Name).Outcome);
        }

        [Fact]
        public async Task A_person_whose_id_or_name_has_half_a_surrogate_pair_is_skipped_or_unnamed_not_the_whole_plan_failed()
        {
            LocalStack.RequireAvailable();
            _remote.Groups["ops"] = [new ResolvedPerson(FlakyResolver.Source, "ab\uD800"), new ResolvedPerson(FlakyResolver.Source, "ok", "Bad \uDC00 name")];
            var id = await PlanningAsync("""[{"kind":"group","id":"ops","resolver":"remote"}]""");

            await DrainAsync();

            Assert.Equal(NotificationState.Planned, (await NotificationAsync(id)).State);
            Assert.Single(await RecipientsAsync(id));
            await using var db = _database.Context();
            var subscriber = Assert.Single(await db.Subscribers.Where(s => s.TenantId == Tenant).ToListAsync(Ct));
            Assert.Equal(("ok", (string?)null), (subscriber.ExternalId, subscriber.Name));
        }

        [Fact]
        public async Task A_name_both_truncated_and_unavailable_through_two_resolvers_is_one_unavailable_record()
        {
            LocalStack.RequireAvailable();
            _staff.Groups["ops"] = [.. new[] { "e", "b", "d", "a", "c" }.Select(x => Person(FakePeopleResolver.Source, x))];
            _remote.Down = true;
            var id = await PlanningAsync("""[{"kind":"group","id":"ops","resolver":"staff"},{"kind":"group","id":"ops","resolver":"remote"}]""");

            for (var i = 0; i < 100 && (await NotificationAsync(id)).State == NotificationState.Planning; i++)
            {
                _clock.Advance((await NotificationAsync(id)).NextAt!.Value - _clock.GetUtcNow());
                await DrainAsync();
            }

            Assert.Equal(NotificationState.Planned, (await NotificationAsync(id)).State);
            var recipients = await RecipientsAsync(id);
            Assert.Equal(Max, recipients.Count(r => r.SubscriberId is not null));
            var marker = Assert.Single(recipients, r => r.SubscriberId is null);
            Assert.Equal(("group:ops", "remote", RecipientOutcome.ResolverUnavailable), (marker.NamedAs, marker.Resolver, marker.Outcome));
        }

        [Fact]
        public async Task A_local_group_past_the_bound_keeps_its_first_members_by_byte_order()
        {
            LocalStack.RequireAvailable();
            await using (var db = _database.Context())
            {
                var group = new LocalGroup { TenantId = Tenant, Key = "all", Name = "All", UpdatedAt = Seed.Now };
                db.LocalGroups.Add(group);
                // Upper case sorts before lower case in byte order, after it in most linguistic collations.
                foreach (var name in new[] { "b", "B", "a", "A", "c" })
                {
                    var subscriber = new Subscriber { TenantId = Tenant, Source = LocalRecipientResolver.Source, ExternalId = name, CreatedAt = Seed.Now };
                    db.Subscribers.Add(subscriber);
                    db.LocalGroupMembers.Add(new LocalGroupMember { TenantId = Tenant, GroupId = group.Id, SubscriberId = subscriber.Id, AddedAt = Seed.Now });
                }
                await db.SaveChangesAsync(Ct);
            }
            var id = await PlanningAsync("""[{"kind":"group","id":"all","resolver":"local"}]""");

            await DrainAsync();

            await using var check = _database.Context();
            var names = await check.Subscribers.Where(s => s.TenantId == Tenant).ToDictionaryAsync(s => s.Id, s => s.ExternalId, Ct);
            var recipients = await RecipientsAsync(id);
            Assert.Equal("A,B,a", string.Join(",", recipients.Where(r => r.SubscriberId is not null).Select(r => names[r.SubscriberId!.Value])
                .Order(StringComparer.Ordinal)));
            Assert.Equal(RecipientOutcome.Truncated, Assert.Single(recipients, r => r.SubscriberId is null).Outcome);
        }
    }
}
