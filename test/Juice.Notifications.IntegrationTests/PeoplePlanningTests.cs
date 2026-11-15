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
    /// A resolver for tests (phase-2-plan M10): users are themselves, groups are whoever the test put in them, each person from the source the test
    /// gave. Registered as the implementation <c>fake</c>.
    /// </summary>
    public sealed class FakePeopleResolver : IRecipientResolver
    {
        public const string Source = "fake";

        public string Key => "fake";

        public IReadOnlySet<string> Kinds { get; } = new HashSet<string> { RecipientKinds.User, RecipientKinds.Group, RecipientKinds.Role };

        /// <summary>Group members by group id.</summary>
        public ConcurrentDictionary<string, ResolvedPerson[]> Groups { get; } = new(StringComparer.Ordinal);

        public string? CheckSettings(string settingsJson) => settingsJson.Contains("\"invalid\"", StringComparison.Ordinal) ? "These settings are refused." : null;

        public ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct)
            => ValueTask.FromResult(new ResolveResult([])
            {
                People = spec.Kind == RecipientKinds.User ? [new ResolvedPerson(Source, spec.Id!, "User " + spec.Id)] : Groups.GetValueOrDefault(spec.Id!) ?? [],
            });
    }

    /// <summary>
    /// People in planning (phase-2-plan M10): what resolvers return becomes subscribers, one recipient per person however many names reach them
    /// (phase 2 proof point 3), and planning stays safe with several workers creating the same new people at once.
    /// </summary>
    public sealed class PeoplePlanningTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 10, Lease = TimeSpan.FromSeconds(30) };
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
        private readonly FakePeopleResolver _people = new();
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
            db.Resolvers.Add(new RecipientResolverConfig
            {
                TenantId = Tenant, Key = "staff", Kind = "fake", Handles = ["group", "user"], Settings = "{}", DefinitionHash = "h", UpdatedAt = Seed.Now,
            });
            await db.SaveChangesAsync();
            _seed = (app.Id, type.Id);

            var services = new ServiceCollection().AddLogging(l => l.AddProvider(new ErrorLogProvider()));
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddPostgresClaimStore();
            services.AddPlanning(Options, runWorkers: false);
            services.AddSingleton<IRecipientResolver>(_people);
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

        private StageRunner<Notification, PlannerStage> Runner => _services!.GetRequiredService<StageRunner<Notification, PlannerStage>>();

        /// <summary>Notifications due for planning, each naming <paramref name="recipients"/> (as intake stores them, with the resolver chosen).</summary>
        private async Task<List<Guid>> PlanningAsync(int count, string recipients)
        {
            await using var db = _database.Context();
            var ids = new List<Guid>();
            for (var i = 0; i < count; i++)
            {
                var request = Seed.Request(Tenant, _seed.App, _seed.Type, Guid.NewGuid().ToString("N"));
                db.Entry(request).Property(r => r.TypeKey).CurrentValue = "people.thing";
                db.Entry(request).Property(r => r.Recipients).CurrentValue = recipients;
                var notification = Seed.Notification(Tenant, request.Id);
                notification.NextAt = _clock.GetUtcNow();
                db.AddRange(request, notification);
                ids.Add(notification.Id);
            }
            await db.SaveChangesAsync(Ct);
            return ids;
        }

        /// <summary>Runs the planner until nothing is due; every step it took must have completed.</summary>
        private async Task DrainAsync(int workers = 1)
        {
            var passes = new System.Collections.Concurrent.ConcurrentBag<PassResult>();
            await Task.WhenAll(Enumerable.Range(1, workers).Select(async w =>
            {
                PassResult pass;
                do
                {
                    pass = await Runner.RunOnceAsync($"planner-{w}", Options, Ct);
                    passes.Add(pass);
                }
                while (pass.Claimed > 0);
            }));
            Assert.True(passes.Sum(p => p.Completed) > 0, "The planner completed nothing: " + string.Join("; ", passes));
            Assert.Equal(0, passes.Sum(p => p.Failed));
        }

        private static ResolvedPerson Person(string id, string source = FakePeopleResolver.Source) => new(source, id, "Person " + id);

        [Fact]
        public async Task A_person_named_directly_and_through_two_groups_gets_one_recipient_naming_all_three()
        {
            LocalStack.RequireAvailable();
            _people.Groups["sre"] = [Person("adam"), Person("bea")];
            _people.Groups["oncall"] = [Person("adam"), Person("cai")];
            var ids = await PlanningAsync(1, """
                [{"kind":"group","id":"sre","resolver":"staff"},{"kind":"user","id":"adam","resolver":"staff"},{"kind":"group","id":"oncall","resolver":"staff"}]
                """);

            await DrainAsync();

            await using var db = _database.Context();
            var subscribers = await db.Subscribers.Where(s => s.TenantId == Tenant).ToDictionaryAsync(s => s.ExternalId, Ct);
            Assert.Equal(["adam", "bea", "cai"], subscribers.Keys.Order());
            Assert.All(subscribers.Values, s => Assert.Equal(FakePeopleResolver.Source, s.Source));
            // The user's own name from the resolver wins over nothing; a group gives a name too.
            Assert.Equal("Person adam", subscribers["adam"].Name);

            var recipients = await db.Recipients.Where(r => r.NotificationId == ids[0]).ToListAsync(Ct);
            Assert.Equal(3, recipients.Count);
            Assert.All(recipients, r => Assert.Equal((RecipientKind.Subscriber, "staff", RecipientOutcome.Planned), (r.Kind, r.Resolver, r.Outcome)));
            var adam = Assert.Single(recipients, r => r.SubscriberId == subscribers["adam"].Id);
            Assert.Equal("subscriber:" + subscribers["adam"].Id, adam.NamedAs);
            Assert.Equal(["group:oncall", "group:sre", "user:adam"], adam.Via);
            Assert.Equal(["group:sre"], Assert.Single(recipients, r => r.SubscriberId == subscribers["bea"].Id).Via);
        }

        [Fact]
        public async Task Workers_planning_at_once_make_each_new_person_one_subscriber_and_each_notification_one_set()
        {
            LocalStack.RequireAvailable();
            _people.Groups["everyone"] = [.. Enumerable.Range(0, 50).Select(i => Person($"p{i:00}"))];
            var ids = await PlanningAsync(40, """[{"kind":"group","id":"everyone","resolver":"staff"},{"kind":"user","id":"p07","resolver":"staff"}]""");

            await DrainAsync(workers: 6);

            await using var db = _database.Context();
            var subscribers = await db.Subscribers.Where(s => s.TenantId == Tenant).ToListAsync(Ct);
            Assert.Equal(50, subscribers.Count);
            var recipients = await db.Recipients.Where(r => ids.Contains(r.NotificationId)).ToListAsync(Ct);
            Assert.All(ids, id => Assert.Equal(50, recipients.Count(r => r.NotificationId == id)));
            Assert.Equal(50 * 40, recipients.Select(r => (r.NotificationId, r.SubscriberId)).Distinct().Count());
            Assert.All(await db.Notifications.Where(n => ids.Contains(n.Id)).ToListAsync(Ct), n => Assert.Equal(NotificationState.Planned, n.State));
        }

        [Fact]
        public async Task An_identity_linked_as_an_alias_reaches_its_subscriber_and_unlinking_gives_it_back()
        {
            LocalStack.RequireAvailable();
            // adam is known from another source (local); his fake identity is linked to him, and a separate fake subscriber already existed.
            var local = new Subscriber { TenantId = Tenant, Source = "local", ExternalId = "adam", Name = "Adam", CreatedAt = Seed.Now };
            var own = new Subscriber { TenantId = Tenant, Source = FakePeopleResolver.Source, ExternalId = "adam", CreatedAt = Seed.Now };
            await using (var setup = _database.Context())
            {
                setup.Subscribers.AddRange(local, own);
                await setup.SaveChangesAsync(Ct);
            }
            await LinkAsync(local.Id, FakePeopleResolver.Source, "adam");
            _people.Groups["both"] = [Person("adam"), Person("adam", "local")];
            var ids = await PlanningAsync(1, """[{"kind":"group","id":"both","resolver":"staff"},{"kind":"user","id":"adam","resolver":"staff"}]""");

            await DrainAsync();

            await using (var db = _database.Context())
            {
                var recipient = Assert.Single(await db.Recipients.Where(r => r.NotificationId == ids[0]).ToListAsync(Ct));
                Assert.Equal(local.Id, recipient.SubscriberId);
                Assert.Equal(["group:both", "user:adam"], recipient.Via);
                Assert.Equal(local.Id, (await db.Subscribers.SingleAsync(s => s.Id == own.Id, Ct)).MergedInto);
            }

            await UnlinkAsync(local.Id, FakePeopleResolver.Source, "adam");
            var after = await PlanningAsync(1, """[{"kind":"user","id":"adam","resolver":"staff"}]""");
            await DrainAsync();
            await using (var db = _database.Context())
            {
                Assert.Equal(own.Id, Assert.Single(await db.Recipients.Where(r => r.NotificationId == after[0]).ToListAsync(Ct)).SubscriberId);
                Assert.Null((await db.Subscribers.SingleAsync(s => s.Id == own.Id, Ct)).MergedInto);
            }
        }

        [Fact]
        public async Task A_resolver_disabled_after_intake_skips_its_recipients_and_plans_the_rest()
        {
            LocalStack.RequireAvailable();
            await using (var db = _database.Context())
            {
                var staff = await db.Resolvers.SingleAsync(c => c.Key == "staff", Ct);
                staff.Enabled = false;
                await db.SaveChangesAsync(Ct);
            }
            var ids = await PlanningAsync(1, """[{"kind":"user","id":"adam","resolver":"staff"},{"kind":"address","channel":"email","address":"x@example.com"}]""");

            await DrainAsync();

            await using var check = _database.Context();
            Assert.Equal(["address:email:x@example.com"], await check.Recipients.Where(r => r.NotificationId == ids[0]).Select(r => r.NamedAs).ToListAsync(Ct));
            Assert.False(await check.Subscribers.AnyAsync(Ct));
        }

        /// <summary>Through the handlers, as the API's commands run them (each a transaction of its own).</summary>
        private async Task LinkAsync(Guid subscriber, string source, string externalId)
        {
            await using var db = _database.Context();
            var handlers = new People.SubscriberHandlers(db, _clock);
            var outcome = await handlers.Handle(new People.LinkAlias(Tenant, subscriber, source, externalId, "test"), Ct);
            Assert.Equal(OutcomeKind.Ok, outcome.Kind);
            await db.SaveChangesAsync(Ct);
        }

        private async Task UnlinkAsync(Guid subscriber, string source, string externalId)
        {
            await using var db = _database.Context();
            var handlers = new People.SubscriberHandlers(db, _clock);
            Assert.True((await handlers.Handle(new People.UnlinkAlias(Tenant, subscriber, source, externalId), Ct)).Value);
            await db.SaveChangesAsync(Ct);
        }
    }
}
