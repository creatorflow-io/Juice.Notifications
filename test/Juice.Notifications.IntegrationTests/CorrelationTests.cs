using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Correlations in the real host (phase-3-plan M20, design §5.3, §7.3): phase 3 proof point 1, acknowledging, reopening, and the rules of the
    /// lifecycle, with the realm's person accounts reading their items.
    /// </summary>
    public sealed class CorrelationTests(PeopleApp people) : IClassFixture<PeopleApp>
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

        private static async Task<(HttpStatusCode Status, string Code)> ProblemAsync(Task<HttpResponseMessage> call)
        {
            var response = await call;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            return (response.StatusCode, json.RootElement.TryGetProperty("code", out var code) ? code.GetString()! : "");
        }

        private async Task<(HttpClient Client, MeResponse Me)> PersonAsync(string username)
        {
            var client = people.App.Client(await LocalStack.PersonTokenAsync(username));
            return (client, await OkAsync<MeResponse>(client.GetAsync($"{Tenant}/me", Ct)));
        }

        private string Crew(params MeResponse[] members)
        {
            var crew = $"crew-{Guid.NewGuid():N}";
            people.People.Groups[crew] = members.Select(m => new ResolvedPerson(m.Source, m.ExternalId, m.Name)).ToArray();
            return crew;
        }

        private async Task<string> TypeAsync(string? workflow = null, string? actions = null, string correlation = "optional")
        {
            using var admin = people.App.Client(await LocalStack.AdminTokenAsync());
            await OkAsync<ResolverResponse>(admin.PutAsJsonAsync($"{Tenant}/resolvers/crews", new PutResolverBody("fake", ["group", "user"], null, null, null), Ct));
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            var key = $"{ProducerApp.Key}.c{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            await OkAsync<JsonElement>(producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(key, "Approve spend", "spend",
                null, "normal", false, null, Json("""{ "type": "object" }"""), correlation, null, workflow is null ? null : Json(workflow),
                Json("""{ "inbox": "Title: Approve the spend\n\nPlease decide." }"""), actions is null ? null : Json(actions)), Ct));
            return key;
        }

        private async Task<Guid> SendAsync(string type, string crew, string correlationKey)
        {
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            return (await OkAsync<AcceptedResponse>(producer.PostAsJsonAsync($"{Tenant}/notifications", new SendNotificationBody(type, null,
                Guid.NewGuid().ToString("N"), correlationKey, [new RecipientBody("group", Id: crew, Resolver: "crews")], null, null, null, Json("{}"), null, null), Ct))).Id;
        }

        private static async Task<List<InboxItemResponse>> ItemsAsync(HttpClient me, string type, int count, string? query = null)
        {
            for (var i = 0; i < 200; i++)
            {
                var items = (await OkAsync<List<InboxItemResponse>>(me.GetAsync($"{Tenant}/me/inbox{query}", Ct))).Where(x => x.Type == type).ToList();
                if (items.Count >= count)
                {
                    return items;
                }
                await Task.Delay(100, Ct);
            }
            throw new TimeoutException($"{count} items of {type} didn't arrive.");
        }

        private async Task<NotificationState> StateAsync(Guid requestId, NotificationState until)
        {
            await using var db = people.App.Database.Context();
            for (var i = 0; i < 200; i++)
            {
                var state = await db.Notifications.AsNoTracking().Where(n => n.RequestId == requestId).Select(n => n.State).SingleAsync(Ct);
                if (state == until)
                {
                    return state;
                }
                await Task.Delay(100, Ct);
            }
            throw new TimeoutException($"Request {requestId} didn't become {until}.");
        }

        private static string Key() => "spend:" + Guid.NewGuid().ToString("N");

        /// <summary>
        /// Phase 3 proof point 1: two people have the approval in their inbox, and a second notification of the correlation is still waiting in its
        /// workflow. Resolving it cancels the waiting one and its people, and every person's item shows the outcome and is no longer actionable
        /// and open, wherever it was decided.
        /// </summary>
        [Fact]
        public async Task Resolving_cancels_every_waiting_recipient_delivery_and_escalation_and_updates_each_actionable_item()
        {
            LocalStack.RequireAvailable();
            var (ann, annMe) = await PersonAsync("ann");
            var (ben, benMe) = await PersonAsync("ben");
            using var _ = ann;
            using var __ = ben;
            var crew = Crew(annMe, benMe);
            var approval = await TypeAsync(actions: """[{ "id": "approve", "label": "Approve" }, { "id": "reject", "label": "Reject" }]""");
            var reminder = await TypeAsync(workflow: """[{ "step": "delay", "seconds": 3600 }, { "step": "send" }]""");
            var key = Key();

            await SendAsync(approval, crew, key);
            var annItem = Assert.Single(await ItemsAsync(ann, approval, 1));
            Assert.Equal(CorrelationState.Open, annItem.Correlation!.State);
            Assert.Contains(await ItemsAsync(ann, approval, 1, "?actionable=true"), i => i.Id == annItem.Id);
            var waiting = await SendAsync(reminder, crew, key);
            await StateAsync(waiting, NotificationState.Running);

            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            var resolved = await OkAsync<CorrelationResponse>(producer.PostAsJsonAsync($"{Tenant}/correlations/{Uri.EscapeDataString(key)}/resolve",
                new ResolveCorrelationBody("Approved by adam", "adam"), Ct));
            Assert.Equal((CorrelationState.Resolved, "Approved by adam", "adam"), (resolved.State, resolved.Outcome, resolved.ResolvedBy));
            Assert.Equal(2, resolved.Notifications.Count);
            Assert.False(resolved.Deliveries.ContainsKey("pending") || resolved.Deliveries.ContainsKey("retrying"));

            await using (var db = people.App.Database.Context())
            {
                var notification = await db.Notifications.AsNoTracking().SingleAsync(n => n.RequestId == waiting, Ct);
                Assert.Equal((NotificationState.Cancelled, (DateTimeOffset?)null), (notification.State, notification.NextAt));
                Assert.All(await db.Recipients.AsNoTracking().Where(r => r.NotificationId == notification.Id).ToListAsync(Ct),
                    r => Assert.Equal((RecipientOutcome.Cancelled, (DateTimeOffset?)null), (r.Outcome, r.NextAt)));
            }
            foreach (var me in new[] { ann, ben })
            {
                var item = Assert.Single(await ItemsAsync(me, approval, 1));
                Assert.Equal((CorrelationState.Resolved, "Approved by adam"), (item.Correlation!.State, item.Correlation.Outcome));
                Assert.DoesNotContain((await OkAsync<List<InboxItemResponse>>(me.GetAsync($"{Tenant}/me/inbox?actionable=true", Ct))), i => i.Id == item.Id);
            }

            // Resolving again answers as it stands; acknowledging or cancelling it now is refused.
            Assert.Equal(CorrelationState.Resolved, (await OkAsync<CorrelationResponse>(producer.PostAsJsonAsync($"{Tenant}/correlations/{Uri.EscapeDataString(key)}/resolve",
                new ResolveCorrelationBody("Approved by adam", "adam"), Ct))).State);
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.CorrelationClosed),
                await ProblemAsync(producer.PostAsJsonAsync($"{Tenant}/correlations/{Uri.EscapeDataString(key)}/acknowledge", new AcknowledgeCorrelationBody("eve"), Ct)));
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.CorrelationClosed),
                await ProblemAsync(producer.PostAsJsonAsync($"{Tenant}/correlations/{Uri.EscapeDataString(key)}/cancel", new CancelCorrelationBody(null), Ct)));
        }

        [Fact]
        public async Task Acknowledging_keeps_it_going_and_a_request_on_a_resolved_correlation_reopens_it()
        {
            LocalStack.RequireAvailable();
            var (ann, annMe) = await PersonAsync("ann");
            using var _ = ann;
            var crew = Crew(annMe);
            var type = await TypeAsync(actions: """[{ "id": "approve", "label": "Approve" }]""");
            var key = Key();
            var path = $"{Tenant}/correlations/{Uri.EscapeDataString(key)}";
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            await SendAsync(type, crew, key);
            await ItemsAsync(ann, type, 1);

            var acknowledged = await OkAsync<CorrelationResponse>(producer.PostAsJsonAsync($"{path}/acknowledge", new AcknowledgeCorrelationBody("eve"), Ct));
            Assert.Equal((CorrelationState.Acknowledged, "eve"), (acknowledged.State, acknowledged.AcknowledgedBy));
            // Acknowledged is still open: the item stays actionable, and another notification of it is sent.
            var item = Assert.Single(await ItemsAsync(ann, type, 1, "?actionable=true"));
            Assert.Equal((CorrelationState.Acknowledged, "eve"), (item.Correlation!.State, item.Correlation.AcknowledgedBy));
            await SendAsync(type, crew, key);
            await ItemsAsync(ann, type, 2);

            await OkAsync<CorrelationResponse>(producer.PostAsJsonAsync($"{path}/cancel", new CancelCorrelationBody("eve"), Ct));
            Assert.Equal(CorrelationState.Cancelled, (await OkAsync<CorrelationResponse>(producer.GetAsync(path, Ct))).State);

            // The thing happened again: the same key reopens it.
            await SendAsync(type, crew, key);
            var reopened = await OkAsync<CorrelationResponse>(producer.GetAsync(path, Ct));
            Assert.Equal((CorrelationState.Open, (string?)null, (string?)null), (reopened.State, reopened.AcknowledgedBy, reopened.Outcome));
            Assert.NotNull(reopened.ReopenedAt);
            Assert.Equal(3, reopened.Notifications.Count);
            // The new item is open and actionable; the earlier ones show the correlation as it is now.
            Assert.Equal(3, (await ItemsAsync(ann, type, 3)).Count(i => i.Correlation!.State == CorrelationState.Open));
        }

        /// <summary>
        /// Inbox actions (phase-3-plan M23, design §19 decision 13): an action is one interaction however often it's chosen, an action the type
        /// doesn't declare is not found, and once the correlation is closed it's refused. The item lists the person's own interactions.
        /// </summary>
        [Fact]
        public async Task An_action_is_one_interaction_and_is_refused_once_decided()
        {
            LocalStack.RequireAvailable();
            var (ann, annMe) = await PersonAsync("ann");
            using var _ = ann;
            var type = await TypeAsync(actions: """[{ "id": "approve", "label": "Approve" }, { "id": "reject", "label": "Reject" }]""");
            var key = Key();
            await SendAsync(type, Crew(annMe), key);
            var item = Assert.Single(await ItemsAsync(ann, type, 1));
            var actions = $"{Tenant}/me/inbox/{item.Id}/actions";

            var acted = await OkAsync<InboxItemResponse>(ann.PostAsync($"{actions}/approve", null, Ct));
            await OkAsync<InboxItemResponse>(ann.PostAsync($"{actions}/approve", null, Ct));
            Assert.NotNull(acted.ReadAt);
            Assert.Equal(["action:approve", "read"], acted.Interactions!.Select(i => i.ActionId is null ? i.Kind : $"{i.Kind}:{i.ActionId}").Order());
            await using (var db = people.App.Database.Context())
            {
                Assert.Equal(1, await db.Interactions.CountAsync(i => i.InboxItemId == item.Id && i.Kind == "action", Ct));
            }
            Assert.Equal(HttpStatusCode.NotFound, (await ann.PostAsync($"{actions}/delete", null, Ct)).StatusCode);
            await OkAsync<InboxItemResponse>(ann.PostAsync($"{Tenant}/me/inbox/{item.Id}/acknowledge", null, Ct));

            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            await OkAsync<CorrelationResponse>(producer.PostAsJsonAsync($"{Tenant}/correlations/{Uri.EscapeDataString(key)}/resolve",
                new ResolveCorrelationBody("Approved", "ann"), Ct));
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.CorrelationClosed), await ProblemAsync(ann.PostAsync($"{actions}/reject", null, Ct)));
        }

        /// <summary>A key with a slash and its own percent sequence comes back as sent: the path is decoded once (phases 3–4 review).</summary>
        [Fact]
        public async Task A_key_with_a_slash_and_a_percent_sequence_is_found_as_sent()
        {
            LocalStack.RequireAvailable();
            var type = await TypeAsync();
            var key = "ops/cpu%41high:" + Guid.NewGuid().ToString("N");
            await SendAsync(type, "nobody-" + Guid.NewGuid().ToString("N")[..6], key);
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());

            var found = await OkAsync<CorrelationResponse>(producer.GetAsync($"{Tenant}/correlations/{Uri.EscapeDataString(key)}", Ct));
            Assert.Equal(key, found.Key);
            var resolved = await OkAsync<CorrelationResponse>(producer.PostAsJsonAsync($"{Tenant}/correlations/{Uri.EscapeDataString(key)}/resolve",
                new ResolveCorrelationBody("done", null), Ct));
            Assert.Equal(CorrelationState.Resolved, resolved.State);
        }

        [Fact]
        public async Task An_unknown_key_is_not_found_and_a_bad_body_is_refused_at_the_field()
        {
            LocalStack.RequireAvailable();
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            var missing = $"{Tenant}/correlations/{Uri.EscapeDataString(Key())}";
            Assert.Equal(HttpStatusCode.NotFound, (await producer.GetAsync(missing, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await producer.PostAsJsonAsync($"{missing}/resolve", new ResolveCorrelationBody(null, null), Ct)).StatusCode);

            var (ann, annMe) = await PersonAsync("ann");
            using var _ = ann;
            var key = "with/slash:" + Guid.NewGuid().ToString("N");
            await SendAsync(await TypeAsync(), Crew(annMe), key);
            var path = $"{Tenant}/correlations/{Uri.EscapeDataString(key)}";
            Assert.Equal(key, (await OkAsync<CorrelationResponse>(producer.GetAsync(path, Ct))).Key);
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest),
                await ProblemAsync(producer.PostAsJsonAsync($"{path}/resolve", new ResolveCorrelationBody(" spaced ", null), Ct)));
            // Not a producer: a person's token.
            Assert.Equal(HttpStatusCode.Forbidden, (await ann.GetAsync(path, Ct)).StatusCode);
        }
    }
}
