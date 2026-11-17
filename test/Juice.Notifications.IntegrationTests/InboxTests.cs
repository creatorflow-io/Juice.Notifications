using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The inbox and the display group catalog (phase-2-plan M15, design §7.3, D12) in the real host, with the realm's person accounts: proof
    /// point 6, a person's own items only, reading and archiving, and the catalog's rules.
    /// </summary>
    public sealed class InboxTests(PeopleApp people) : IClassFixture<PeopleApp>
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
            var body = await response.Content.ReadAsStringAsync(Ct);
            using var json = JsonDocument.Parse(body);
            return (response.StatusCode, json.RootElement.TryGetProperty("code", out var code) ? code.GetString()! : "");
        }

        /// <summary>A person's client and their subscriber (created by their first call), and a group of the fake resolver naming them.</summary>
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

        /// <summary>Declares a type for the sample producer, with the resolver in place, and returns its key.</summary>
        private async Task<string> TypeAsync(string? displayGroup = null, string? actions = null, string templates = """{ "inbox": "Title: Disk {{ data.host }}\n\nIt is full." }""")
        {
            using var admin = people.App.Client(await LocalStack.AdminTokenAsync());
            await OkAsync<ResolverResponse>(admin.PutAsJsonAsync($"{Tenant}/resolvers/crews", new PutResolverBody("fake", ["group", "user"], null, null, null), Ct));
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            var key = $"{ProducerApp.Key}.i{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            await OkAsync<JsonElement>(producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(key, "Disk full", "ops",
                displayGroup, "normal", false, null, Json("""{ "type": "object" }"""), null, null, null, Json(templates), actions is null ? null : Json(actions)), Ct));
            return key;
        }

        private async Task SendAsync(string type, string crew, string host = "db-1")
        {
            using var producer = people.App.Client(await LocalStack.ProducerTokenAsync());
            await OkAsync<AcceptedResponse>(producer.PostAsJsonAsync($"{Tenant}/notifications", new SendNotificationBody(type, null, Guid.NewGuid().ToString("N"),
                null, [new RecipientBody("group", Id: crew, Resolver: "crews")], null, null, null, Json($$"""{ "host": "{{host}}" }"""),
                new() { ["self"] = $"https://app.example.com/disks/{host}" }, null), Ct));
        }

        /// <summary>The person's open items of a type, once <paramref name="count"/> are there.</summary>
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

        /// <summary>
        /// Phase 2 proof point 6: a type moved to a custom group shows there, its open items move with it (an archived one doesn't), its actions are
        /// still listed, and a new item goes there too. Moving it back, or deleting the group, puts its items back in the group the type declares.
        /// </summary>
        [Fact]
        public async Task A_type_moved_to_a_custom_group_shows_there_with_its_actions()
        {
            LocalStack.RequireAvailable();
            var (me, ann) = await PersonAsync("ann");
            using var _ = me;
            var type = await TypeAsync(DisplayGroups.Approval, """[ { "id": "approve", "label": "Approve" }, { "id": "reject", "label": "Reject" } ]""");
            var crew = Crew(ann);
            await SendAsync(type, crew, "db-1");
            await SendAsync(type, crew, "db-2");
            var items = await ItemsAsync(me, type, 2);
            Assert.All(items, i => Assert.Equal((DisplayGroups.Approval, true, 2), (i.Group, i.Actionable, i.Actions!.Value.GetArrayLength())));
            var first = items.Single(i => i.Title == "Disk db-1");
            Assert.Equal(("It is full.", "https://app.example.com/disks/db-1"), (first.Body, first.Url));
            var archived = items.Single(i => i.Title == "Disk db-2");
            await OkAsync<InboxItemResponse>(me.PostAsync($"{Tenant}/me/inbox/{archived.Id}/archive", null, Ct));

            using var admin = people.App.Client(await LocalStack.TenantAdminTokenAsync());
            var group = "g" + Guid.NewGuid().ToString("N")[..10];
            var added = await OkAsync<DisplayGroupResponse>(admin.PutAsJsonAsync($"{Tenant}/display-groups/{group}", new PutDisplayGroupBody("Disks", null), Ct));
            Assert.False(added.BuiltIn);
            var moved = await OkAsync<TypeDisplayGroupResponse>(admin.PutAsJsonAsync($"{Tenant}/types/{type}/display-group", new MoveTypeBody(group), Ct));
            Assert.Equal((group, true, 1), (moved.Group, moved.Chosen, moved.ItemsMoved));

            var there = await ItemsAsync(me, type, 1, $"?group={group}");
            Assert.Equal(first.Id, Assert.Single(there).Id);
            Assert.Equal(["approve", "reject"], there[0].Actions!.Value.EnumerateArray().Select(a => a.GetProperty("id").GetString()));
            // Actionable and open, wherever the type now is (design §7.3).
            Assert.Contains((await OkAsync<List<InboxItemResponse>>(me.GetAsync($"{Tenant}/me/inbox?actionable=true", Ct))), i => i.Id == first.Id);
            var counts = await OkAsync<InboxCountsResponse>(me.GetAsync($"{Tenant}/me/inbox/counts", Ct));
            Assert.Equal((1, 1), counts.Groups.Single(g => g.Key == group) is var c ? (c.Open, c.Unread) : default);
            Assert.True(counts.ActionableOpen >= 1);
            Assert.Contains((await OkAsync<List<DisplayGroupResponse>>(admin.GetAsync($"{Tenant}/display-groups", Ct))), g => g.Key == group && g.Types.Contains(type));

            // A new item goes to the group the type is in now; one archived before the move comes back to it.
            await SendAsync(type, crew, "db-3");
            Assert.Equal(2, (await ItemsAsync(me, type, 2, $"?group={group}")).Count);
            Assert.Equal(group, (await OkAsync<InboxItemResponse>(me.DeleteAsync($"{Tenant}/me/inbox/{archived.Id}/archive", Ct))).Group);

            // Deleting the group sends the type back to the group it declares, with its open items.
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Tenant}/display-groups/{group}", Ct)).StatusCode);
            Assert.All(await ItemsAsync(me, type, 3), i => Assert.Equal(DisplayGroups.Approval, i.Group));
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"{Tenant}/types/{ProducerApp.Key}.no-such/display-group", new MoveTypeBody(DisplayGroups.System), Ct)).StatusCode);
        }

        /// <summary>A person reads, changes and counts their own items only: another's is not found, whatever they try.</summary>
        [Fact]
        public async Task A_person_reads_only_their_own_items()
        {
            LocalStack.RequireAvailable();
            var (annClient, ann) = await PersonAsync("ann");
            var (benClient, ben) = await PersonAsync("ben");
            using var a = annClient;
            using var b = benClient;
            Assert.NotEqual(ann.Id, ben.Id);
            var type = await TypeAsync();
            await SendAsync(type, Crew(ann));
            var item = Assert.Single(await ItemsAsync(annClient, type, 1));

            Assert.DoesNotContain(await OkAsync<List<InboxItemResponse>>(benClient.GetAsync($"{Tenant}/me/inbox", Ct)), i => i.Id == item.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await benClient.GetAsync($"{Tenant}/me/inbox/{item.Id}", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await benClient.PostAsync($"{Tenant}/me/inbox/{item.Id}/read", null, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await benClient.PostAsync($"{Tenant}/me/inbox/{item.Id}/archive", null, Ct)).StatusCode);
            await OkAsync<ReadAllResponse>(benClient.PostAsync($"{Tenant}/me/inbox/read-all", null, Ct));
            Assert.Null((await OkAsync<InboxItemResponse>(annClient.GetAsync($"{Tenant}/me/inbox/{item.Id}", Ct))).ReadAt);
            // Another tenant's inbox isn't the person's.
            Assert.Equal(HttpStatusCode.Forbidden, (await annClient.GetAsync($"/api/v1/tenants/{ProducerApp.OtherTenant}/me/inbox", Ct)).StatusCode);
        }

        /// <summary>Read, unread, read all (in a group, or all) and archive, with the counts following.</summary>
        [Fact]
        public async Task A_person_reads_unreads_and_archives_items_and_the_counts_follow()
        {
            LocalStack.RequireAvailable();
            var (me, ann) = await PersonAsync("ann");
            using var _ = me;
            await OkAsync<ReadAllResponse>(me.PostAsync($"{Tenant}/me/inbox/read-all", null, Ct));
            var system = await TypeAsync(DisplayGroups.System);
            var general = await TypeAsync(templates: """{ "email": "Subject: x\n\ny" }"""); // no inbox template: the type's name
            var crew = Crew(ann);
            await SendAsync(system, crew);
            await SendAsync(general, crew);
            var item = Assert.Single(await ItemsAsync(me, system, 1));
            var plain = Assert.Single(await ItemsAsync(me, general, 1));
            Assert.Equal(("Disk full", "", DisplayGroups.General), (plain.Title, plain.Body, plain.Group));

            var counts = await OkAsync<InboxCountsResponse>(me.GetAsync($"{Tenant}/me/inbox/counts", Ct));
            Assert.Equal([DisplayGroups.Approval, DisplayGroups.System, DisplayGroups.General], counts.Groups.Where(g => g.Key is DisplayGroups.Approval or DisplayGroups.System or DisplayGroups.General).Select(g => g.Key));
            Assert.Equal(2, counts.Unread);

            var read = await OkAsync<InboxItemResponse>(me.PostAsync($"{Tenant}/me/inbox/{item.Id}/read", null, Ct));
            Assert.NotNull(read.ReadAt);
            Assert.Equal(read.ReadAt, (await OkAsync<InboxItemResponse>(me.PostAsync($"{Tenant}/me/inbox/{item.Id}/read", null, Ct))).ReadAt); // kept
            Assert.Equal(1, (await OkAsync<InboxCountsResponse>(me.GetAsync($"{Tenant}/me/inbox/counts", Ct))).Unread);
            Assert.DoesNotContain(await OkAsync<List<InboxItemResponse>>(me.GetAsync($"{Tenant}/me/inbox?unread=true", Ct)), i => i.Id == item.Id);
            Assert.Null((await OkAsync<InboxItemResponse>(me.DeleteAsync($"{Tenant}/me/inbox/{item.Id}/read", Ct))).ReadAt);

            Assert.Equal(1, (await OkAsync<ReadAllResponse>(me.PostAsync($"{Tenant}/me/inbox/read-all?group={DisplayGroups.System}", null, Ct))).Marked);
            Assert.Equal(1, (await OkAsync<InboxCountsResponse>(me.GetAsync($"{Tenant}/me/inbox/counts", Ct))).Unread);

            var openBefore = (await OkAsync<InboxCountsResponse>(me.GetAsync($"{Tenant}/me/inbox/counts", Ct))).Groups.Single(g => g.Key == DisplayGroups.General).Open;
            var archived = await OkAsync<InboxItemResponse>(me.PostAsync($"{Tenant}/me/inbox/{plain.Id}/archive", null, Ct));
            Assert.NotNull(archived.ArchivedAt);
            Assert.NotNull(archived.ReadAt); // archiving reads it
            Assert.DoesNotContain(await OkAsync<List<InboxItemResponse>>(me.GetAsync($"{Tenant}/me/inbox", Ct)), i => i.Id == plain.Id);
            Assert.Contains(await OkAsync<List<InboxItemResponse>>(me.GetAsync($"{Tenant}/me/inbox?archived=true", Ct)), i => i.Id == plain.Id);
            var after = await OkAsync<InboxCountsResponse>(me.GetAsync($"{Tenant}/me/inbox/counts", Ct));
            Assert.Equal((openBefore - 1, 0), after.Groups.Single(g => g.Key == DisplayGroups.General) is var g0 ? (g0.Open, g0.Unread) : default);
        }

        /// <summary>
        /// The catalog's rules (design D12): the three built-ins are there on first read, renamed and reordered but never deleted; a group's key
        /// and name are checked; and the inbox's own integration is the service's, which no one changes, deletes or gives targets.
        /// </summary>
        [Fact]
        public async Task The_catalog_keeps_its_built_ins_and_the_inbox_integration_is_the_services()
        {
            LocalStack.RequireAvailable();
            using var admin = people.App.Client(await LocalStack.TenantAdminTokenAsync());
            var groups = await OkAsync<List<DisplayGroupResponse>>(admin.GetAsync($"{Tenant}/display-groups", Ct));
            Assert.Contains(groups, g => g.Key == DisplayGroups.Approval && g.BuiltIn);
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.DisplayGroupBuiltIn), await ProblemAsync(admin.DeleteAsync($"{Tenant}/display-groups/{DisplayGroups.System}", Ct)));
            var renamed = await OkAsync<DisplayGroupResponse>(admin.PutAsJsonAsync($"{Tenant}/display-groups/{DisplayGroups.System}", new PutDisplayGroupBody("Platform", 5), Ct));
            Assert.Equal(("Platform", 5, true), (renamed.Name, renamed.Position, renamed.BuiltIn));
            await OkAsync<DisplayGroupResponse>(admin.PutAsJsonAsync($"{Tenant}/display-groups/{DisplayGroups.System}", new PutDisplayGroupBody("System", 20), Ct));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidConfiguration), await ProblemAsync(admin.PutAsJsonAsync($"{Tenant}/display-groups/Bad_Key", new PutDisplayGroupBody("x", null), Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidConfiguration), await ProblemAsync(admin.PutAsJsonAsync($"{Tenant}/display-groups/no-name", new PutDisplayGroupBody(null, null), Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidConfiguration), await ProblemAsync(admin.PutAsJsonAsync($"{Tenant}/display-groups/spaced", new PutDisplayGroupBody(" x", null), Ct)));
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Tenant}/display-groups/never-was", Ct)).StatusCode);
            // A person isn't a tenant admin.
            using var person = people.App.Client(await LocalStack.PersonTokenAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await person.GetAsync($"{Tenant}/display-groups", Ct)).StatusCode);

            // Someone has been sent something, so the tenant's inbox integration exists.
            var (me, ann) = await PersonAsync("ann");
            using var _ = me;
            var type = await TypeAsync();
            await SendAsync(type, Crew(ann));
            await ItemsAsync(me, type, 1);
            var integrations = await OkAsync<List<IntegrationResponse>>(admin.GetAsync($"{Tenant}/integrations", Ct));
            Assert.Contains(integrations, i => i.Key == Inbox.InboxIntegrations.Key && i.Channel == Channels.Inbox);
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ManagedByService), await ProblemAsync(admin.PutAsJsonAsync($"{Tenant}/integrations/{Inbox.InboxIntegrations.Key}",
                new PutIntegrationBody(null, null, null, null, null, false), Ct)));
            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ManagedByService), await ProblemAsync(admin.DeleteAsync($"{Tenant}/integrations/{Inbox.InboxIntegrations.Key}", Ct)));
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Tenant}/integrations/my-inbox",
                new PutIntegrationBody(Channels.Inbox, Inbox.InboxIntegrations.Kind, null, null, null, null), Ct)).StatusCode);
        }

        /// <summary>
        /// A catalog change runs in its own transaction (not a command's): its rows are audited when it commits, and a refused one leaves no row
        /// change in the record.
        /// </summary>
        [Fact]
        public async Task A_catalog_change_is_audited_once_committed_and_a_refused_one_has_no_changes()
        {
            LocalStack.RequireAvailable();
            using var admin = people.App.Client(await LocalStack.TenantAdminTokenAsync());
            var type = await TypeAsync();
            var group = "a" + Guid.NewGuid().ToString("N")[..10];
            await OkAsync<DisplayGroupResponse>(admin.PutAsJsonAsync($"{Tenant}/display-groups/{group}", new PutDisplayGroupBody("Audited", null), Ct));
            await OkAsync<TypeDisplayGroupResponse>(admin.PutAsJsonAsync($"{Tenant}/types/{type}/display-group", new MoveTypeBody(group), Ct));
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Tenant}/types/{type}/display-group", new MoveTypeBody("no-such-group"), Ct)).StatusCode);

            async Task<IReadOnlyList<AuditRecorder.Saved>> RecordsAsync(string suffix, int count)
            {
                for (var i = 0; i < 50 && people.App.Audit.For(ProducerApp.Tenant, suffix).Count < count; i++)
                {
                    await Task.Delay(100, Ct);
                }
                return people.App.Audit.For(ProducerApp.Tenant, suffix);
            }
            var added = Assert.Single(await RecordsAsync($"/display-groups/{group}", 1));
            Assert.Contains(added.Changes, c => c.Tbl == "display_groups" && c.Action == "Inserted");
            var moves = await RecordsAsync($"/types/{type}/display-group", 2);
            Assert.Equal(2, moves.Count);
            Assert.Contains(moves, m => m.Access.Response!.Status == 200 && m.Changes.Any(c => c.Tbl == "type_display_groups" && c.Action == "Inserted"));
            Assert.Contains(moves, m => m.Access.Response!.Status == 400 && m.Changes.Count == 0);
        }
    }
}
