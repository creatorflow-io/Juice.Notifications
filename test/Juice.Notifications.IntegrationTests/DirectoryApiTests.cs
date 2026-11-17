using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Local users, local groups and topics through the API, reaching people through the built-in <c>local</c> and <c>topic</c> resolvers
    /// (phase-2-plan M11), and a person following topics themselves (<c>me/topics</c>).
    /// </summary>
    public sealed class DirectoryApiTests(PeopleApp people) : IClassFixture<PeopleApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string Tenant = $"/api/v1/tenants/{ProducerApp.Tenant}";

        private static JsonElement Json(string json) => JsonElement.Parse(json);

        private static async Task<T> OkAsync<T>(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
        }

        private static async Task NoContentAsync(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        }

        /// <summary>The status, problem code and pointers (comma-separated) of a refusal.</summary>
        private static async Task<(HttpStatusCode Status, string Code, string Pointers)> ProblemAsync(Task<HttpResponseMessage> call)
        {
            var response = await call;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var pointers = json.RootElement.TryGetProperty("errors", out var errors)
                ? string.Join(",", errors.EnumerateArray().Select(e => e.GetProperty("pointer").GetString()!))
                : "";
            return (response.StatusCode, json.RootElement.GetProperty("code").GetString()!, pointers);
        }

        /// <summary>
        /// An admin client, with the tenant's local and topic resolvers in place. xUnit makes a new instance per test, so each test puts them;
        /// the put is idempotent.
        /// </summary>
        private async Task<HttpClient> AdminAsync()
        {
            var admin = people.App.Client(await LocalStack.AdminTokenAsync());
            await OkAsync<ResolverResponse>(admin.PutAsJsonAsync($"{Tenant}/resolvers/people", new PutResolverBody("local", null, null, null, null), Ct));
            await OkAsync<ResolverResponse>(admin.PutAsJsonAsync($"{Tenant}/resolvers/topics", new PutResolverBody("topic", null, null, null, null), Ct));
            return admin;
        }

        /// <summary>Declares a type, sends a request naming <paramref name="recipients"/>, waits for the planner, and returns its recipients.</summary>
        private async Task<List<NotificationRecipient>> SendAndPlanAsync(params RecipientBody[] recipients)
        {
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            var type = $"{ProducerApp.Key}.d{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            await OkAsync<JsonElement>(producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types",
                new DeclareTypeBody(type, "Directory", "things", null, null, null, null, Json("""{ "type": "object" }"""), null, null, null,
                    Json("""{ "email": "Hello" }""")), Ct));
            var accepted = await OkAsync<AcceptedResponse>(producer.PostAsJsonAsync($"{Tenant}/notifications",
                new SendNotificationBody(type, null, Guid.NewGuid().ToString("N"), null, [.. recipients], null, null, null, Json("{}"), null, null), Ct));
            await using var db = people.App.Database.Context();
            for (var waited = 0; waited < 100; waited++)
            {
                var notification = await db.Notifications.AsNoTracking().SingleAsync(n => n.RequestId == accepted.Id, Ct);
                if (notification.State != NotificationState.Planning)
                {
                    Assert.Equal(NotificationState.Planned, notification.State);
                    return await db.Recipients.AsNoTracking().Where(r => r.NotificationId == notification.Id).ToListAsync(Ct);
                }
                await Task.Delay(100, Ct);
            }
            throw new TimeoutException("The planner never planned the notification.");
        }

        [Fact]
        public async Task Local_users_and_groups_reach_their_people_and_a_change_applies_from_the_next_request()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();

            var ann = await OkAsync<SubscriberResponse>(admin.PutAsJsonAsync($"{Tenant}/local-users/ann", new PutLocalUserBody("Ann", "vi-VN", "Asia/Ho_Chi_Minh"), Ct));
            Assert.Equal(("local", "ann", "Ann", "vi-VN", "Asia/Ho_Chi_Minh"), (ann.Source, ann.ExternalId, ann.Name, ann.Locale, ann.TimeZone));
            var ben = await OkAsync<SubscriberResponse>(admin.PutAsJsonAsync($"{Tenant}/local-users/ben", new PutLocalUserBody(null, null, null), Ct));
            // A field left out keeps its value; a second Put is the same user.
            var again = await OkAsync<SubscriberResponse>(admin.PutAsJsonAsync($"{Tenant}/local-users/ann", new PutLocalUserBody(null, "en", null), Ct));
            Assert.Equal((ann.Id, "Ann", "en"), (again.Id, again.Name, again.Locale));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest, "/timeZone"),
                await ProblemAsync(admin.PutAsJsonAsync($"{Tenant}/local-users/cai", new PutLocalUserBody(null, null, "Mars/Olympus"), Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest, "/id,/locale"),
                await ProblemAsync(admin.PutAsJsonAsync($"{Tenant}/local-users/-cai", new PutLocalUserBody(null, "not a locale", null), Ct)));

            await OkAsync<GroupResponse>(admin.PutAsJsonAsync($"{Tenant}/groups/sre", new PutGroupBody("Site reliability"), Ct));
            await NoContentAsync(admin.PutAsync($"{Tenant}/groups/sre/members/{ann.Id}", null, Ct));
            await NoContentAsync(admin.PutAsync($"{Tenant}/groups/sre/members/{ben.Id}", null, Ct));
            await NoContentAsync(admin.PutAsync($"{Tenant}/groups/sre/members/{ben.Id}", null, Ct));
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsync($"{Tenant}/groups/sre/members/{Guid.NewGuid()}", null, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsync($"{Tenant}/groups/nobody/members/{ann.Id}", null, Ct)).StatusCode);
            Assert.Equal([("sre", "Site reliability", 2)], (await OkAsync<List<GroupResponse>>(admin.GetAsync($"{Tenant}/groups", Ct)))
                .Select(g => (g.Key, g.Name, g.Members)));
            Assert.Equal(new[] { ann.Id, ben.Id }.Order(), (await OkAsync<List<SubscriberResponse>>(admin.GetAsync($"{Tenant}/groups/sre/members", Ct)))
                .Select(s => s.Id).Order());

            var first = await SendAndPlanAsync(new RecipientBody("group", Id: "sre"), new RecipientBody("user", Id: "ann"), new RecipientBody("user", Id: "nobody"));
            Assert.Equal(new[] { ann.Id, ben.Id }.Order(), first.Select(r => r.SubscriberId!.Value).Order());
            Assert.Equal(["group:sre", "user:ann"], first.Single(r => r.SubscriberId == ann.Id).Via);
            Assert.All(first, r => Assert.Equal("people", r.Resolver));

            await NoContentAsync(admin.DeleteAsync($"{Tenant}/groups/sre/members/{ben.Id}", Ct));
            var second = await SendAndPlanAsync(new RecipientBody("group", Id: "sre"));
            Assert.Equal([ann.Id], second.Select(r => r.SubscriberId!.Value));

            // A deleted group, like one that never was, reaches no one; the request is still planned.
            await NoContentAsync(admin.DeleteAsync($"{Tenant}/groups/sre", Ct));
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Tenant}/groups/sre/members", Ct)).StatusCode);
            Assert.Empty(await SendAndPlanAsync(new RecipientBody("group", Id: "sre")));
        }

        [Fact]
        public async Task A_person_follows_self_service_topics_and_a_request_naming_one_reaches_its_followers()
        {
            LocalStack.RequireAvailable();
            using var admin = await AdminAsync();
            await OkAsync<TopicResponse>(admin.PutAsJsonAsync($"{Tenant}/topics/billing.service:alerts", new PutTopicBody("Billing alerts", null), Ct));
            await OkAsync<TopicResponse>(admin.PutAsJsonAsync($"{Tenant}/topics/ops.private", new PutTopicBody(null, false), Ct));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest, "/key"),
                await ProblemAsync(admin.PutAsJsonAsync($"{Tenant}/topics/.hidden", new PutTopicBody(null, null), Ct)));

            // A person: the tenant admin client's own token (its subject, and the tenant its claim names).
            using var me = people.App.Client(await LocalStack.PersonTokenAsync());
            var mine = await OkAsync<List<MyTopicResponse>>(me.GetAsync($"{Tenant}/me/topics", Ct));
            Assert.Equal([("billing.service:alerts", false)], mine.Select(t => (t.Key, t.Subscribed)));
            await NoContentAsync(me.PutAsync($"{Tenant}/me/topics/billing.service:alerts", null, Ct));
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.TopicNotSelfService, ""), await ProblemAsync(me.PutAsync($"{Tenant}/me/topics/ops.private", null, Ct)));
            Assert.Equal(HttpStatusCode.NotFound, (await me.PutAsync($"{Tenant}/me/topics/no.such", null, Ct)).StatusCode);
            var self = Assert.Single(await OkAsync<List<SubscriberResponse>>(admin.GetAsync($"{Tenant}/subscribers?source=oidc", Ct)));

            // An admin adds someone to a topic people can't join themselves.
            var dee = await OkAsync<SubscriberResponse>(admin.PutAsJsonAsync($"{Tenant}/local-users/dee", new PutLocalUserBody("Dee", null, null), Ct));
            await NoContentAsync(admin.PutAsync($"{Tenant}/topics/ops.private/subscribers/{dee.Id}", null, Ct));
            Assert.Equal([("billing.service:alerts", 1), ("ops.private", 1)], (await OkAsync<List<TopicResponse>>(admin.GetAsync($"{Tenant}/topics", Ct)))
                .Select(t => (t.Key, t.Subscribers)));

            var planned = await SendAndPlanAsync(new RecipientBody("topic", Key: "billing.service:alerts"), new RecipientBody("topic", Key: "ops.private"));
            Assert.Equal(new[] { self.Id, dee.Id }.Order(), planned.Select(r => r.SubscriberId!.Value).Order());
            Assert.Equal(["topic:billing.service:alerts"], planned.Single(r => r.SubscriberId == self.Id).Via);
            Assert.All(planned, r => Assert.Equal("topics", r.Resolver));

            await NoContentAsync(me.DeleteAsync($"{Tenant}/me/topics/billing.service:alerts", Ct));
            Assert.Equal([("billing.service:alerts", false)], (await OkAsync<List<MyTopicResponse>>(me.GetAsync($"{Tenant}/me/topics", Ct)))
                .Select(t => (t.Key, t.Subscribed)));
            Assert.Equal([dee.Id], (await SendAndPlanAsync(new RecipientBody("topic", Key: "billing.service:alerts"), new RecipientBody("topic", Key: "ops.private")))
                .Select(r => r.SubscriberId!.Value));
        }

        [Fact]
        public async Task Only_a_member_of_the_tenant_has_a_me()
        {
            LocalStack.RequireAvailable();
            // A client's own token is no person's (deferred #27), even one with the tenant claim; a person's token names another tenant than this.
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotAPerson, ""), await ProblemAsync(producer.GetAsync($"{Tenant}/me/topics", Ct)));
            using var serviceAccount = people.App.Client(await LocalStack.TenantAdminTokenAsync());
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotAPerson, ""), await ProblemAsync(serviceAccount.GetAsync($"{Tenant}/me/topics", Ct)));
            using var member = people.App.Client(await LocalStack.PersonTokenAsync());
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotTenantMember, ""),
                await ProblemAsync(member.GetAsync($"/api/v1/tenants/{ProducerApp.OtherTenant}/me/topics", Ct)));
            Assert.Equal(HttpStatusCode.Unauthorized, (await people.App.Client().GetAsync($"{Tenant}/me/topics", Ct)).StatusCode);
        }
    }
}
