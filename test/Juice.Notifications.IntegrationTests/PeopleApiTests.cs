using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Planning;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>The real host with <see cref="FakePeopleResolver"/> installed, and the sample producer linked to the test tenant.</summary>
    public sealed class PeopleApp : IAsyncLifetime
    {
        public FakePeopleResolver People { get; } = new();

        public NotificationsApp App { get; }

        public PeopleApp() => App = new NotificationsApp(host => host.ConfigureTestServices(services => services.AddSingleton<IRecipientResolver>(People)));

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
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>
    /// Resolvers, person recipients and subscriber aliases through the API (phase-2-plan M10), with tokens from the local Keycloak.
    /// </summary>
    public sealed class PeopleApiTests(PeopleApp people) : IClassFixture<PeopleApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string Tenant = $"/api/v1/tenants/{ProducerApp.Tenant}";
        private static readonly string Other = $"/api/v1/tenants/{ProducerApp.OtherTenant}";

        private static JsonElement Json(string json) => JsonElement.Parse(json);

        private async Task<HttpClient> AdminAsync() => people.App.Client(await LocalStack.AdminTokenAsync());

        private static async Task<T> OkAsync<T>(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
        }

        /// <summary>The status, problem code and pointers of a refusal.</summary>
        private static async Task<(HttpStatusCode Status, string Code, string Pointers)> ProblemAsync(Task<HttpResponseMessage> call)
        {
            var response = await call;
            var body = await response.Content.ReadAsStringAsync(Ct);
            using var json = JsonDocument.Parse(body);
            var pointers = json.RootElement.TryGetProperty("errors", out var errors)
                ? string.Join(",", errors.EnumerateArray().Select(e => e.GetProperty("pointer").GetString()!))
                : "";
            return (response.StatusCode, json.RootElement.GetProperty("code").GetString()!, pointers);
        }

        [Fact]
        public async Task A_resolver_is_checked_against_its_implementation_and_kept_per_tenant()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();

            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidConfiguration, "/kind"),
                await ProblemAsync(admin.PutAsJsonAsync($"{Other}/resolvers/x", new PutResolverBody("nope", null, null, null, null), Ct)));
            // Built-in kinds aren't configured.
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidConfiguration, "/kind"),
                await ProblemAsync(admin.PutAsJsonAsync($"{Other}/resolvers/x", new PutResolverBody("route", null, null, null, null), Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidConfiguration, "/handles/1"),
                await ProblemAsync(admin.PutAsJsonAsync($"{Other}/resolvers/x", new PutResolverBody("fake", ["user", "topic"], null, null, null), Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidConfiguration, "/handles"),
                await ProblemAsync(admin.PutAsJsonAsync($"{Other}/resolvers/x", new PutResolverBody("fake", [], null, null, null), Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidConfiguration, "/settings"),
                await ProblemAsync(admin.PutAsJsonAsync($"{Other}/resolvers/x", new PutResolverBody("fake", null, Json("""{ "invalid": 1 }"""), null, null), Ct)));

            // Handles everything its implementation can when left out, in order.
            var created = await OkAsync<ResolverResponse>(admin.PutAsJsonAsync($"{Other}/resolvers/people", new PutResolverBody("fake", null, null, null, null), Ct));
            Assert.Equal(("fake", "group,role,user", true, ManagedBy.Api), (created.Kind, string.Join(",", created.Handles), created.Enabled, created.ManagedBy));
            // A Put keeps what it leaves out; the kind is fixed.
            var changed = await OkAsync<ResolverResponse>(admin.PutAsJsonAsync($"{Other}/resolvers/people", new PutResolverBody(null, ["user", "user"], null, null, false), Ct));
            Assert.Equal(("user", false), (string.Join(",", changed.Handles), changed.Enabled));

            Assert.Equal(["people"], (await OkAsync<List<ResolverResponse>>(admin.GetAsync($"{Other}/resolvers", Ct))).Select(r => r.Key));
            Assert.Empty(await OkAsync<List<ResolverResponse>>(admin.GetAsync($"{Tenant}/resolvers", Ct)));
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Other}/resolvers/people", Ct)).StatusCode);
            Assert.Empty(await OkAsync<List<ResolverResponse>>(admin.GetAsync($"{Other}/resolvers", Ct)));
        }

        [Fact]
        public async Task A_person_recipient_needs_a_resolver_the_tenant_enabled_and_becomes_subscribers()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            var type = $"{ProducerApp.Key}.p{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            await OkAsync<JsonElement>(producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types",
                new DeclareTypeBody(type, "People", "things", null, null, null, null, Json("""{ "type": "object" }"""), null, null, null,
                    Json("""{ "email": "Hello" }""")), Ct));
            SendNotificationBody Send(params RecipientBody[] recipients)
                => new(type, null, Guid.NewGuid().ToString("N"), null, [.. recipients], null, null, null, Json("{}"), null, null);
            var group = new RecipientBody("group", Id: "squad");

            Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.RecipientNotAllowed, "/recipients/0/kind"),
                await ProblemAsync(producer.PostAsJsonAsync($"{Tenant}/notifications", Send(group), Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest, "/recipients/0/id"),
                await ProblemAsync(producer.PostAsJsonAsync($"{Tenant}/notifications", Send(new RecipientBody("group")), Ct)));

            await OkAsync<ResolverResponse>(admin.PutAsJsonAsync($"{Tenant}/resolvers/squads", new PutResolverBody("fake", ["group"], null, null, null), Ct));
            people.People.Groups["squad"] = [new ResolvedPerson("fake", "ann", "Ann"), new ResolvedPerson("fake", "ben", "Ben")];
            var accepted = await OkAsync<AcceptedResponse>(producer.PostAsJsonAsync($"{Tenant}/notifications", Send(group), Ct));

            await using var db = people.App.Database.Context();
            var request = await db.Requests.SingleAsync(r => r.Id == accepted.Id, Ct);
            Assert.Contains("\"resolver\": \"squads\"", request.Recipients);
            for (var waited = 0; waited < 100 && !await db.Notifications.AnyAsync(n => n.RequestId == accepted.Id && n.State != NotificationState.Planning, Ct); waited++)
            {
                await Task.Delay(100, Ct);
            }
            var listed = await OkAsync<List<SubscriberResponse>>(admin.GetAsync($"{Tenant}/subscribers?source=fake", Ct));
            Assert.Equal([("ann", "Ann"), ("ben", "Ben")], listed.Select(s => (s.ExternalId, s.Name!)).Order());
            // Users aren't handled by that resolver.
            Assert.Equal((HttpStatusCode.UnprocessableEntity, ErrorCodes.RecipientNotAllowed, "/recipients/0/kind"),
                await ProblemAsync(producer.PostAsJsonAsync($"{Tenant}/notifications", Send(new RecipientBody("user", Id: "ann")), Ct)));
        }

        [Fact]
        public async Task An_admin_links_and_unlinks_an_identity()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();
            var tenant = ProducerApp.Tenant;
            var dana = new Subscriber { TenantId = tenant, Source = "local", ExternalId = "dana", CreatedAt = Seed.Now };
            var eve = new Subscriber { TenantId = tenant, Source = "local", ExternalId = "eve", CreatedAt = Seed.Now };
            var danaElsewhere = new Subscriber { TenantId = tenant, Source = "oidc", ExternalId = "sub-dana", CreatedAt = Seed.Now };
            await using (var setup = people.App.Database.Context())
            {
                setup.Subscribers.AddRange(dana, eve, danaElsewhere);
                await setup.SaveChangesAsync(Ct);
            }
            var aliases = $"{Tenant}/subscribers/{dana.Id}/aliases";

            var linked = await OkAsync<SubscriberResponse>(admin.PostAsJsonAsync(aliases, new LinkAliasBody("oidc", "sub-dana"), Ct));
            Assert.Equal([("oidc", "sub-dana")], linked.Aliases.Select(a => (a.Source, a.ExternalId)));
            Assert.Equal("client:notifications-admin", linked.Aliases[0].LinkedBy);
            Assert.Equal(dana.Id, (await OkAsync<SubscriberResponse>(admin.GetAsync($"{Tenant}/subscribers/{danaElsewhere.Id}", Ct))).MergedInto);
            // Found by the identity it's linked as.
            Assert.Equal(new[] { dana.Id, danaElsewhere.Id }.Order(), (await OkAsync<List<SubscriberResponse>>(admin.GetAsync($"{Tenant}/subscribers?externalId=sub-dana", Ct)))
                .Select(s => s.Id).Order());
            // Again is the same; to another subscriber, its own identity, or into a merged one is refused.
            await OkAsync<SubscriberResponse>(admin.PostAsJsonAsync(aliases, new LinkAliasBody("oidc", "sub-dana"), Ct));
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.AliasInUse, ""),
                await ProblemAsync(admin.PostAsJsonAsync($"{Tenant}/subscribers/{eve.Id}/aliases", new LinkAliasBody("oidc", "sub-dana"), Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest, "/externalId"),
                await ProblemAsync(admin.PostAsJsonAsync(aliases, new LinkAliasBody("local", "dana"), Ct)));
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.SubscriberMerged, ""),
                await ProblemAsync(admin.PostAsJsonAsync($"{Tenant}/subscribers/{danaElsewhere.Id}/aliases", new LinkAliasBody("local", "x"), Ct)));
            // Linking dana into eve would leave sub-dana pointing at one that points elsewhere.
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.SubscriberMerged, ""),
                await ProblemAsync(admin.PostAsJsonAsync($"{Tenant}/subscribers/{eve.Id}/aliases", new LinkAliasBody("local", "dana"), Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest, "/source,/externalId"),
                await ProblemAsync(admin.PostAsJsonAsync(aliases, new LinkAliasBody("Not A Source", ""), Ct)));
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"{Tenant}/subscribers/{Guid.NewGuid()}/aliases", new LinkAliasBody("oidc", "z"), Ct)).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{aliases}?source=oidc&externalId=sub-dana", Ct)).StatusCode);
            Assert.Empty((await OkAsync<SubscriberResponse>(admin.GetAsync($"{Tenant}/subscribers/{dana.Id}", Ct))).Aliases);
            Assert.Null((await OkAsync<SubscriberResponse>(admin.GetAsync($"{Tenant}/subscribers/{danaElsewhere.Id}", Ct))).MergedInto);
            // The link and the unlink are audited as data changes.
            IReadOnlyList<AuditRecorder.Saved> records = [];
            for (var waited = 0; waited < 50; waited++)
            {
                records = [.. people.App.Audit.For(tenant, "/aliases").Where(r => r.Changes.Any(c => c.Tbl == "subscriber_aliases"))];
                if (records.Count >= 2)
                {
                    break;
                }
                await Task.Delay(100, Ct);
            }
            Assert.Equal(["Inserted", "Deleted"], records.Select(r => r.Changes.Single(c => c.Tbl == "subscriber_aliases").Action));
        }

        [Fact]
        public async Task Only_the_tenants_admins_see_its_people()
        {
            LocalStack.RequireAvailable();
            using var tenantAdmin = people.App.Client(await LocalStack.TenantAdminTokenAsync());
            Assert.Equal(HttpStatusCode.OK, (await tenantAdmin.GetAsync($"{Tenant}/subscribers", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync($"{Other}/subscribers", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync($"{Other}/resolvers", Ct)).StatusCode);
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await producer.GetAsync($"{Tenant}/subscribers", Ct)).StatusCode);
        }
    }
}
