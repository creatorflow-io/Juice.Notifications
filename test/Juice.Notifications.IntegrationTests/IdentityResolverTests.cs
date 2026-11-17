using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Planning;
using Juice.Notifications.Stages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>The real host reading the local Keycloak as its identity directory <c>keycloak</c>, and the tenant's resolver <c>people</c> over it.</summary>
    public sealed class IdentityApp : IAsyncLifetime
    {
        public NotificationsApp App { get; } = new(host =>
        {
            foreach (var (key, value) in LocalStack.DirectorySettings("keycloak"))
            {
                host.UseSetting(key, value);
            }
        });

        public async ValueTask InitializeAsync()
        {
            await App.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            (await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(ProducerApp.Key, "Sample", "sample-producer"))).EnsureSuccessStatusCode();
            (await admin.PutAsync($"/api/v1/applications/{ProducerApp.Key}/tenants/{ProducerApp.Tenant}", null)).EnsureSuccessStatusCode();
            var tenant = $"/api/v1/tenants/{ProducerApp.Tenant}";
            (await admin.PutAsJsonAsync($"{tenant}/integrations/mail", new PutIntegrationBody("email", "smtp",
                JsonElement.Parse("""{ "server": "local", "from": "notifications@juice.local" }"""), null, null, true))).EnsureSuccessStatusCode();
            (await admin.PutAsJsonAsync($"{tenant}/resolvers/people", new PutResolverBody(IdentityRecipientResolver.Kind, null,
                JsonElement.Parse("""{ "directory": "keycloak" }"""), null, null))).EnsureSuccessStatusCode();
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>
    /// The identity resolver against the local Keycloak (phase-2-plan M16): its people in two tenants (deploy/local/scripts/keycloak-update.sh):
    /// ann, ben, dora (email not verified) and eli (disabled) in the tests' tenant, cara in the other; the group <c>ops</c> holds ann, dora, eli
    /// and cara, and the realm role <c>on-call</c> ben, eli and cara.
    /// </summary>
    public sealed class IdentityResolverTests(IdentityApp identity) : IClassFixture<IdentityApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid TenantId = ProducerApp.Tenant;
        private static readonly string Tenant = $"/api/v1/tenants/{TenantId}";

        private static JsonElement Json(string json) => JsonElement.Parse(json);

        private static async Task<T> OkAsync<T>(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
        }

        private async Task<string> TypeAsync()
        {
            using var producer = identity.App.Client(await LocalStack.ProducerTokenAsync());
            var type = $"{ProducerApp.Key}.i{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            await OkAsync<JsonElement>(producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types",
                new DeclareTypeBody(type, "Paged", "ops", null, null, null, null, Json("""{ "type": "object" }"""), null, null, null,
                    Json("""{ "email": "Subject: Paged\n\nYou were paged." }""")), Ct));
            return type;
        }

        private async Task<Guid> SendAsync(string type, params RecipientBody[] recipients)
        {
            using var producer = identity.App.Client(await LocalStack.ProducerTokenAsync());
            return (await OkAsync<AcceptedResponse>(producer.PostAsJsonAsync($"{Tenant}/notifications", new SendNotificationBody(type, null,
                Guid.NewGuid().ToString("N"), null, [.. recipients], null, null, null, Json("{}"), null, null), Ct))).Id;
        }

        private static RecipientBody Named(string kind, string id) => new(kind, Id: id, Resolver: "people");

        /// <summary>The people a request reached, by their directory id, with their deliveries' channels and statuses, once all are sent.</summary>
        private async Task<Dictionary<string, List<(string Channel, DeliveryStatus Status)>>> ReachedAsync(Guid requestId)
        {
            await using var db = identity.App.Database.Context();
            for (var i = 0; i < 200; i++)
            {
                var planned = await db.Notifications.AsNoTracking().AnyAsync(n => n.RequestId == requestId && n.State == NotificationState.Planned, Ct);
                var recipients = await (from r in db.Recipients
                                        join n in db.Notifications on r.NotificationId equals n.Id
                                        join s in db.Subscribers on r.SubscriberId equals s.Id
                                        where n.RequestId == requestId
                                        select new { r.Id, r.NextAt, s.Source, s.ExternalId }).AsNoTracking().ToListAsync(Ct);
                var deliveries = await (from item in db.DeliveryItems
                                        join d in db.Deliveries on new { item.TenantId, Id = item.DeliveryId } equals new { d.TenantId, d.Id }
                                        where item.RequestId == requestId
                                        select new { item.RecipientId, d.Channel, d.Status }).AsNoTracking().ToListAsync(Ct);
                if (planned && recipients.All(r => r.NextAt == null)
                    && deliveries.All(d => d.Status is not (DeliveryStatus.Pending or DeliveryStatus.Sending or DeliveryStatus.Retrying)))
                {
                    Assert.All(recipients, r => Assert.Equal("oidc", r.Source));
                    return recipients.ToDictionary(r => r.ExternalId, r => deliveries.Where(d => d.RecipientId == r.Id)
                        .Select(d => (d.Channel, d.Status)).OrderBy(d => d.Channel, StringComparer.Ordinal).ToList());
                }
                await Task.Delay(100, Ct);
            }
            throw new TimeoutException($"Request {requestId} wasn't planned and sent.");
        }

        private static async Task<string> IdAsync(string username) => await LocalStack.KeycloakUserIdAsync(username)
            ?? throw new InvalidOperationException($"The realm has no {username}: scripts/keycloak-update.sh.");

        [Fact]
        public async Task A_tenant_admin_names_a_directory_the_deployment_configures_and_nothing_more()
        {
            LocalStack.RequireDirectory();
            using var admin = identity.App.Client(await LocalStack.TenantAdminTokenAsync());
            foreach (var settings in new[] { """{ "directory": "elsewhere" }""", "{}", """{ "directory": "keycloak", "baseUrl": "http://evil.example" }""" })
            {
                var refused = await admin.PutAsJsonAsync($"{Tenant}/resolvers/staff", new PutResolverBody(IdentityRecipientResolver.Kind, null, Json(settings), null, null), Ct);
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.Contains("/settings", await refused.Content.ReadAsStringAsync(Ct));
            }
            var people = Assert.Single(await OkAsync<List<ResolverResponse>>(admin.GetAsync($"{Tenant}/resolvers", Ct)), r => r.Key == "people");
            Assert.Equal(("identity", "group,role,user"), (people.Kind, string.Join(",", people.Handles)));
        }

        /// <summary>
        /// A group reaches its enabled members of this tenant only (not cara of the other, not eli, disabled); an email the directory hasn't
        /// verified arrives as an unverified contact point, so dora's item is in her inbox and no email goes to her.
        /// </summary>
        [Fact]
        public async Task A_group_reaches_its_enabled_members_of_the_tenant_and_an_unverified_email_is_never_sent_to()
        {
            LocalStack.RequireDirectory();
            var (ann, dora) = (await IdAsync("ann"), await IdAsync("dora"));
            var reached = await ReachedAsync(await SendAsync(await TypeAsync(), Named("group", "ops")));

            Assert.Equal(new[] { ann, dora }.Order(StringComparer.Ordinal), reached.Keys.Order(StringComparer.Ordinal));
            Assert.Equal([(Channels.Email, DeliveryStatus.Accepted), (Channels.Inbox, DeliveryStatus.Delivered)], reached[ann]);
            Assert.Equal([(Channels.Inbox, DeliveryStatus.Delivered)], reached[dora]);

            await using var db = identity.App.Database.Context();
            var points = await (from p in db.ContactPoints
                                join s in db.Subscribers on p.SubscriberId equals s.Id
                                where s.TenantId == TenantId && s.Source == "oidc" && (s.ExternalId == ann || s.ExternalId == dora)
                                select new { s.ExternalId, s.Name, p.Channel, Verified = p.VerifiedAt != null }).AsNoTracking().ToListAsync(Ct);
            Assert.Equal(("Ann Local", Channels.Email, true), points.Where(p => p.ExternalId == ann).Select(p => (p.Name, p.Channel, p.Verified)).Single());
            Assert.Equal(("Dora Local", Channels.Email, false), points.Where(p => p.ExternalId == dora).Select(p => (p.Name, p.Channel, p.Verified)).Single());
        }

        [Fact]
        public async Task A_role_reaches_its_holders_in_the_tenant_and_a_user_of_another_tenant_or_none_reaches_no_one()
        {
            LocalStack.RequireDirectory();
            var type = await TypeAsync();
            var (ben, cara) = (await IdAsync("ben"), await IdAsync("cara"));

            Assert.Equal([ben], (await ReachedAsync(await SendAsync(type, Named("role", "on-call")))).Keys);
            Assert.Equal([ben], (await ReachedAsync(await SendAsync(type, Named("user", ben)))).Keys);
            Assert.Empty(await ReachedAsync(await SendAsync(type, Named("user", cara), Named("user", Guid.NewGuid().ToString()),
                Named("group", "no-such-group"), Named("role", "no-such-role"))));
        }

        /// <summary>
        /// The person the directory reached and the person signed in are one subscriber (phase-2-plan I16): the directory's id is the token's
        /// subject, under the source <c>oidc</c>. So ann sees in her inbox what was sent to her by name, and by group.
        /// </summary>
        [Fact]
        public async Task The_person_reached_through_the_directory_finds_it_in_their_own_inbox()
        {
            LocalStack.RequireDirectory();
            var type = await TypeAsync();
            var ann = await IdAsync("ann");
            await ReachedAsync(await SendAsync(type, Named("user", ann), Named("group", "ops")));

            using var me = identity.App.Client(await LocalStack.PersonTokenAsync("ann"));
            var item = Assert.Single(await OkAsync<List<InboxItemResponse>>(me.GetAsync($"{Tenant}/me/inbox", Ct)), i => i.Type == type);
            Assert.Equal("Paged", item.Title);
            await using var db = identity.App.Database.Context();
            Assert.Equal(1, await db.Subscribers.CountAsync(s => s.TenantId == TenantId && s.Source == "oidc" && s.ExternalId == ann, Ct));
        }
    }

    /// <summary>
    /// The identity resolver when Keycloak stops (phase-2-plan M16, I22): planning waits for it, storing nothing meanwhile, and plans everyone
    /// once it is back; stopped past the wait limit, the names it should have resolved are recorded as unavailable and the rest are planned.
    /// Keycloak is reached through a <see cref="TcpGate"/> the test closes, and the planner run by hand on a manual clock.
    /// </summary>
    public sealed class IdentityOutageTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 10, Lease = TimeSpan.FromSeconds(30) };
        private static readonly Guid Tenant = ProducerApp.Tenant;
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
        private readonly TcpGate _keycloak = new(new IPEndPoint(IPAddress.Loopback, 8081));
        private readonly FakePeopleResolver _staff = new();
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
                new RecipientResolverConfig { TenantId = Tenant, Key = "people", Kind = IdentityRecipientResolver.Kind, Handles = ["group", "role", "user"],
                    Settings = """{"directory":"keycloak"}""", DefinitionHash = "h", UpdatedAt = Seed.Now },
                new RecipientResolverConfig { TenantId = Tenant, Key = "staff", Kind = "fake", Handles = ["group", "user"], Settings = "{}", DefinitionHash = "h",
                    UpdatedAt = Seed.Now });
            await db.SaveChangesAsync();
            _seed = (app.Id, type.Id);

            var settings = LocalStack.DirectorySettings("keycloak", _keycloak.Url);
            var services = new ServiceCollection().AddLogging(l => l.AddProvider(new ErrorLogProvider()));
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddPostgresClaimStore();
            services.AddPlanning(Options, runWorkers: false);
            services.Configure<ResolverOptions>(o => o.CacheSeconds = 0);
            services.Configure<IdentityOptions>(o => o.Directories["keycloak"] = new KeycloakDirectoryOptions
            {
                BaseUrl = settings["Identity:Directories:keycloak:BaseUrl"], Realm = settings["Identity:Directories:keycloak:Realm"],
                ClientId = settings["Identity:Directories:keycloak:ClientId"], ClientSecretRef = settings["Identity:Directories:keycloak:ClientSecretRef"],
                Timeout = TimeSpan.FromSeconds(5),
            });
            services.AddSingleton<IRecipientResolver>(_staff);
            _services = services.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync()
        {
            if (_services is not null)
            {
                await _services.DisposeAsync();
            }
            await _keycloak.DisposeAsync();
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

        private async Task DrainAsync()
        {
            PassResult pass;
            do
            {
                pass = await Runner.RunOnceAsync("planner", Options, Ct);
                Assert.Equal(0, pass.Failed);
            }
            while (pass.Claimed > 0);
        }

        private async Task<Notification> NotificationAsync(Guid id)
        {
            await using var db = _database.Context();
            return await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == id, Ct);
        }

        private async Task<List<(RecipientOutcome Outcome, string NamedAs, string? Person)>> RecipientsAsync(Guid id)
        {
            await using var db = _database.Context();
            return (await (from r in db.Recipients
                           join s in db.Subscribers on r.SubscriberId equals s.Id into reached
                           from s in reached.DefaultIfEmpty()
                           where r.NotificationId == id
                           select new { r.Outcome, r.NamedAs, Person = s == null ? null : s.ExternalId }).AsNoTracking().ToListAsync(Ct))
                .Select(r => (r.Outcome, r.NamedAs, (string?)r.Person)).ToList();
        }

        private const string Both = """[{"kind":"group","id":"ops","resolver":"people"},{"kind":"user","id":"adam","resolver":"staff"}]""";

        [Fact]
        public async Task Keycloak_stopped_for_less_than_the_wait_limit_delays_planning_then_everyone_is_planned()
        {
            LocalStack.RequireDirectory();
            var (ann, dora) = (await LocalStack.KeycloakUserIdAsync("ann"), await LocalStack.KeycloakUserIdAsync("dora"));
            _keycloak.Open = false;
            var start = _clock.GetUtcNow();
            var id = await PlanningAsync(Both);

            await DrainAsync();
            var waiting = await NotificationAsync(id);
            Assert.Equal((NotificationState.Planning, start, start.AddSeconds(1)), (waiting.State, waiting.ResolverWaitSince, waiting.NextAt));
            Assert.Empty(await RecipientsAsync(id));

            _keycloak.Open = true;
            _clock.Advance(TimeSpan.FromSeconds(1));
            await DrainAsync();

            Assert.Equal(NotificationState.Planned, (await NotificationAsync(id)).State);
            var people = (await RecipientsAsync(id)).Select(r => (r.Outcome, r.Person)).ToList();
            Assert.Equal(3, people.Count);
            Assert.All(people, p => Assert.Equal(RecipientOutcome.Planned, p.Outcome));
            Assert.Equal(new[] { ann, dora, "adam" }.Order(StringComparer.Ordinal), people.Select(p => p.Person).Order(StringComparer.Ordinal));
        }

        [Fact]
        public async Task Keycloak_stopped_past_the_wait_limit_records_its_names_as_unavailable_and_plans_the_rest()
        {
            LocalStack.RequireDirectory();
            _keycloak.Open = false;
            var id = await PlanningAsync(Both);
            await DrainAsync();
            _clock.Advance(TimeSpan.FromMinutes(5));
            await DrainAsync();

            Assert.Equal(NotificationState.Planned, (await NotificationAsync(id)).State);
            var recipients = await RecipientsAsync(id);
            Assert.Equal(2, recipients.Count);
            Assert.Contains((RecipientOutcome.ResolverUnavailable, "group:ops", (string?)null), recipients);
            Assert.Contains(recipients, r => r is { Outcome: RecipientOutcome.Planned, Person: "adam" });
        }
    }
}
