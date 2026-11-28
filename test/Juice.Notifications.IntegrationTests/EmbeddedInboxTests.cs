using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Juice.Notifications.Api;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Juice.Notifications.IntegrationTests.EmbeddedHostTests;
using static Juice.Notifications.IntegrationTests.InboxTestHost;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// M54: an embedded host's inbox (<c>Juice.Notifications.Inbox</c>) behind the host's own sign-in. The tenant is the host's before the host's
    /// access is asked; a client's token or one with no subject never becomes a subscriber; a person reads only their own items; the
    /// allowlisted roles of their token are recorded as they sign in.
    /// </summary>
    public sealed class EmbeddedInboxTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly ScratchDatabase _database = new();

        public ValueTask InitializeAsync() => _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        private static async Task<(HttpStatusCode Status, string Code, string Body)> AnswerAsync(Task<HttpResponseMessage> call)
        {
            using var response = await call;
            var body = await response.Content.ReadAsStringAsync(Ct);
            var code = "";
            if (body.Length > 0 && response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
            {
                using var json = JsonDocument.Parse(body);
                code = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            }
            return (response.StatusCode, code, body);
        }

        private async Task<int> SubscribersAsync()
        {
            await using var db = _database.Context();
            return await db.Subscribers.CountAsync(Ct);
        }

        private async Task<List<string>> RolesOfAsync(string sub)
        {
            await using var db = _database.Context();
            var subscriber = await db.Subscribers.AsNoTracking().SingleAsync(s => s.TenantId == Tenant && s.Source == "oidc" && s.ExternalId == sub, Ct);
            return await db.SubscriberRoles.AsNoTracking().Where(r => r.TenantId == Tenant && r.SubscriberId == subscriber.Id).Select(r => r.Role)
                .OrderBy(r => r).ToListAsync(Ct);
        }

        private async Task<List<InboxItem>> ItemsAsync(string sub)
        {
            await using var db = _database.Context();
            return await db.InboxItems.AsNoTracking().Where(i => i.TenantId == Tenant
                && db.Subscribers.Any(s => s.TenantId == Tenant && s.Id == i.SubscriberId && s.ExternalId == sub)).ToListAsync(Ct);
        }

        private async Task<List<InboxItem>> WaitForItemsAsync(string sub, int count)
        {
            for (var i = 0; i < 150; i++)
            {
                var items = await ItemsAsync(sub);
                if (items.Count >= count)
                {
                    return items;
                }
                await Task.Delay(100, Ct);
            }
            return await ItemsAsync(sub);
        }

        // ---- the tenant, the access, the person ----

        [Fact]
        public async Task Inbox_Embedded_OtherTenantInPath_403TenantForbidden_AccessNotAsked()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString);
            using var ann = host.Client("ann", "operator", "Ann");

            var (status, code, _) = await AnswerAsync(ann.GetAsync($"{Me(OtherTenant)}/inbox/counts", Ct));
            var (writeStatus, writeCode, _) = await AnswerAsync(ann.PostAsync($"{Me(OtherTenant)}/inbox/read-all", null, Ct));

            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.TenantForbidden), (status, code));
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.TenantForbidden), (writeStatus, writeCode));
            Assert.Equal(0, host.Probe.Asked);
            Assert.Equal(0, await SubscribersAsync());
        }

        [Fact]
        public async Task InboxHub_Embedded_OtherTenantQuery_Aborted()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString);

            await using var connection = Hub(host, "ann", "operator", OtherTenant);
            var closed = new TaskCompletionSource();
            connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
            try
            {
                await connection.StartAsync(Ct);
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            }
            catch (Exception ex) when (ex is not TimeoutException)
            {
                // Turned away while connecting: just as good.
            }

            Assert.Equal(HubConnectionState.Disconnected, connection.State);
            Assert.Equal(0, host.Probe.Asked);
            Assert.Equal(0, await SubscribersAsync());
        }

        [Fact]
        public async Task InboxHub_NoToken_Refused_NothingWritten()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString);
            await using var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(host.Server.BaseAddress, $"{Prefix}/hubs/inbox?tenant={Tenant}"), options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                })
                .Build();

            await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(Ct));

            Assert.Equal(0, host.Probe.Asked);
            Assert.Equal(0, await SubscribersAsync());
        }

        [Fact]
        public async Task Inbox_ClientToken_403NotAPerson_NoSubscriberWritten()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString);
            // A service account that was given a subject and a role: still no one.
            using var client = host.Client("svc-account", "operator", client: "reporting-job");

            var (status, code, _) = await AnswerAsync(client.GetAsync($"{Me()}/inbox/counts", Ct));

            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotAPerson), (status, code));
            Assert.Equal(0, await SubscribersAsync());
            await using var db = _database.Context();
            Assert.Empty(await db.SubscriberRoles.ToListAsync(Ct));
        }

        [Fact]
        public async Task Inbox_NoSub_403()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString);
            using var noSubject = host.Client(null, "operator");

            var (status, code, _) = await AnswerAsync(noSubject.GetAsync($"{Me()}/inbox/counts", Ct));
            // And a request nobody signed.
            using var anonymous = host.Server.CreateClient();
            var (anonymousStatus, _, _) = await AnswerAsync(anonymous.GetAsync($"{Me()}/inbox/counts", Ct));

            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotTenantMember), (status, code));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousStatus);
            Assert.Equal(0, await SubscribersAsync());
        }

        [Fact]
        public async Task Inbox_AnswersAreCamelCase_WhateverTheHostsJsonIs()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString);
            using var ann = host.Client("ann", "operator", "Ann");

            var (status, _, body) = await AnswerAsync(ann.GetAsync($"{Me()}/inbox/counts", Ct));
            var (refused, _, problem) = await AnswerAsync(ann.GetAsync($"{Me()}/inbox/{Guid.NewGuid()}", Ct));

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("\"unread\"", body);
            Assert.DoesNotContain("\"Unread\"", body);
            Assert.Equal(HttpStatusCode.NotFound, refused);
            Assert.Contains("\"code\":\"not_found\"", problem);
        }

        // ---- a person's own items ----

        private async Task<(Guid Ann, Guid Ben)> TwoOperatorsWithOneItemEachAsync(InboxTestHost host)
        {
            (await host.SignInAsync("ann", "operator", "Ann")).EnsureSuccessStatusCode();
            (await host.SignInAsync("ben", "operator", "Ben")).EnsureSuccessStatusCode();
            await host.SendAsync("two-1", [Role("operator")]);
            var ann = Assert.Single(await WaitForItemsAsync("ann", 1));
            var ben = Assert.Single(await WaitForItemsAsync("ben", 1));
            return (ann.Id, ben.Id);
        }

        private static readonly Options Running = new() { RunStages = true };

        [Fact]
        public async Task Inbox_PersonReadsOnlyOwnItems_OtherPersonsItemId_404()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString, Running);
            var (annsItem, bensItem) = await TwoOperatorsWithOneItemEachAsync(host);
            using var ann = host.Client("ann", "operator", "Ann");

            var list = (await host.GetAsync<List<InboxItemResponse>>(ann, $"{Me()}/inbox"))!;
            var own = await AnswerAsync(ann.GetAsync($"{Me()}/inbox/{annsItem}", Ct));
            var theirs = await AnswerAsync(ann.GetAsync($"{Me()}/inbox/{bensItem}", Ct));

            Assert.Equal([annsItem], list.Select(i => i.Id));
            Assert.Equal(HttpStatusCode.OK, own.Status);
            Assert.Equal((HttpStatusCode.NotFound, ErrorCodes.NotFound), (theirs.Status, theirs.Code));
        }

        [Fact]
        public async Task MarkRead_OtherPersonsItem_404_NothingChanged()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString, Running);
            var (annsItem, bensItem) = await TwoOperatorsWithOneItemEachAsync(host);
            using var ann = host.Client("ann", "operator", "Ann");

            var read = await AnswerAsync(ann.PostAsync($"{Me()}/inbox/{bensItem}/read", null, Ct));
            var archive = await AnswerAsync(ann.PostAsync($"{Me()}/inbox/{bensItem}/archive", null, Ct));

            Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (read.Status, archive.Status));
            await using var db = _database.Context();
            var ben = await db.InboxItems.AsNoTracking().SingleAsync(i => i.Id == bensItem, Ct);
            Assert.Equal((null, null), (ben.ReadAt, ben.ArchivedAt));
            // Their own is theirs to change.
            Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(ann.PostAsync($"{Me()}/inbox/{annsItem}/read", null, Ct))).Status);
        }

        [Fact]
        public async Task Inbox_InteractionsFalse_ActionsAndAcknowledge_404()
        {
            LocalStack.RequireAvailable();
            var item = Guid.NewGuid();
            await using (var without = await StartAsync(_database.ConnectionString, new Options { Interactions = false }))
            {
                using var ann = without.Client("ann", "operator", "Ann");
                var acknowledge = await AnswerAsync(ann.PostAsync($"{Me()}/inbox/{item}/acknowledge", null, Ct));
                var action = await AnswerAsync(ann.PostAsync($"{Me()}/inbox/{item}/actions/anything", null, Ct));
                // Not mapped at all: no handler answered (no problem body), and they read and archive as before.
                Assert.Equal((HttpStatusCode.NotFound, ""), (acknowledge.Status, acknowledge.Code));
                Assert.Equal((HttpStatusCode.NotFound, ""), (action.Status, action.Code));
                Assert.Equal(ErrorCodes.NotFound, (await AnswerAsync(ann.PostAsync($"{Me()}/inbox/{item}/read", null, Ct))).Code);
            }
            await using var with = await StartAsync(_database.ConnectionString, new Options { Interactions = true });
            using var person = with.Client("ann", "operator", "Ann");
            // Mapped: the handler answers that there is no such item.
            Assert.Equal(ErrorCodes.NotFound, (await AnswerAsync(person.PostAsync($"{Me()}/inbox/{item}/acknowledge", null, Ct))).Code);
        }

        // ---- roles recorded from the token ----

        [Fact]
        public async Task SignedIn_FirstMeCall_CreatesSubscriber_RecordsAllowlistedRolesOnly()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString, new Options { Inbox = o => o.Roles.AddRange(["viewer", "operator"]) });

            // offline_access and admin aren't on the allowlist; Operator isn't operator (compared as written).
            (await host.SignInAsync("ann", "offline_access,operator,Operator,admin", "Ann")).EnsureSuccessStatusCode();

            await using var db = _database.Context();
            var subscriber = await db.Subscribers.AsNoTracking().SingleAsync(s => s.TenantId == Tenant, Ct);
            Assert.Equal(("oidc", "ann", "Ann"), (subscriber.Source, subscriber.ExternalId, subscriber.Name));
            Assert.Equal(["operator"], await RolesOfAsync("ann"));
        }

        [Fact]
        public async Task SignedIn_RoleRemovedFromToken_RowDeletedOnNextCall()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString);

            (await host.SignInAsync("ann", "operator,viewer", "Ann")).EnsureSuccessStatusCode();
            Assert.Equal(["operator", "viewer"], await RolesOfAsync("ann"));
            (await host.SignInAsync("ann", "viewer", "Ann")).EnsureSuccessStatusCode();
            Assert.Equal(["viewer"], await RolesOfAsync("ann"));
            // An empty set deletes every row.
            (await host.SignInAsync("ann", "offline_access", "Ann")).EnsureSuccessStatusCode();
            Assert.Empty(await RolesOfAsync("ann"));
        }

        [Fact]
        public async Task SignedIn_SameRolesWithinHour_NoWrite()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString);
            (await host.SignInAsync("ann", "operator", "Ann")).EnsureSuccessStatusCode();
            var tenMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-10);
            var twoHoursAgo = DateTimeOffset.UtcNow.AddHours(-2);
            async Task<DateTimeOffset> SeenAsync(DateTimeOffset? set = null)
            {
                await using var db = _database.Context();
                if (set is { } at)
                {
                    await db.SubscriberRoles.ExecuteUpdateAsync(s => s.SetProperty(r => r.SeenAt, at), Ct);
                }
                return await db.SubscriberRoles.AsNoTracking().Select(r => r.SeenAt).SingleAsync(Ct);
            }

            await SeenAsync(tenMinutesAgo);
            (await host.SignInAsync("ann", "operator", "Ann")).EnsureSuccessStatusCode();
            var untouched = await SeenAsync();
            // Past the hour the same set is written again, so a busy person's role never nears the TTL.
            await SeenAsync(twoHoursAgo);
            (await host.SignInAsync("ann", "operator", "Ann")).EnsureSuccessStatusCode();
            var refreshed = await SeenAsync();

            Assert.Equal(tenMinutesAgo.ToUnixTimeMilliseconds(), untouched.ToUnixTimeMilliseconds());
            Assert.True(refreshed > DateTimeOffset.UtcNow.AddMinutes(-5), $"seen_at is now: {refreshed:o}");
        }

        [Fact]
        public async Task SignedIn_TwoConcurrentFirstCalls_SameSubject_OneSubscriber_NoError()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString);

            var answers = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => host.SignInAsync("ann", "operator,viewer", "Ann")));

            Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
            Assert.Equal(1, await SubscribersAsync());
            Assert.Equal(["operator", "viewer"], await RolesOfAsync("ann"));
        }

        [Fact]
        public async Task SignedIn_ASubscriberDeleted_TakesItsRolesWithIt()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString);
            (await host.SignInAsync("ann", "operator", "Ann")).EnsureSuccessStatusCode();

            await using var db = _database.Context();
            await db.Subscribers.Where(s => s.TenantId == Tenant).ExecuteDeleteAsync(Ct);

            Assert.Empty(await db.SubscriberRoles.ToListAsync(Ct));
        }

        // ---- the hub, and a correlation's lifecycle ----

        private static HubConnection Hub(InboxTestHost host, string sub, string roles, Guid tenant)
            => new HubConnectionBuilder()
                .WithUrl(new Uri(host.Server.BaseAddress, $"{Prefix}/hubs/inbox?tenant={tenant}"), options =>
                {
                    // The in-memory server answers HTTP only: long polling, with the host's sign-in as headers.
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                    options.Headers["X-Auth"] = "1";
                    options.Headers["X-Sub"] = sub;
                    options.Headers["X-Roles"] = roles;
                })
                .Build();

        private static async Task<InboxChangedMessage> ToldAsync(ChannelReader<InboxChangedMessage> told, string kind, Guid item)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await foreach (var message in told.ReadAllAsync(timeout.Token))
            {
                if (message.Kind == kind && message.Items.Contains(item))
                {
                    return message;
                }
            }
            throw new TimeoutException();
        }

        [Fact]
        public async Task Correlation_Acknowledge_Resolve_UpdatesItem_HubToldAfterCommit()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString, Running);
            (await host.SignInAsync("ann", "operator", "Ann")).EnsureSuccessStatusCode();
            var told = Channel.CreateUnbounded<InboxChangedMessage>();
            await using var session = Hub(host, "ann", "operator", Tenant);
            session.On<InboxChangedMessage>("inboxChanged", message => told.Writer.TryWrite(message));
            await session.StartAsync(Ct);
            using var ann = host.Client("ann", "operator", "Ann");

            await host.SendAsync("alert-1", [Role("operator")], correlationKey: "alert:1");
            var item = Assert.Single(await WaitForItemsAsync("ann", 1)).Id;
            await ToldAsync(told.Reader, "added", item);

            // The producer acknowledges: the item says so, and the session is told once it is committed (it reads the new state at once).
            await host.InTransactionAsync(client => client.AcknowledgeAsync("alert:1", "oncall", Ct));
            await ToldAsync(told.Reader, "changed", item);
            var acknowledged = (await host.GetAsync<InboxItemResponse>(ann, $"{Me()}/inbox/{item}"))!;
            Assert.Equal((CorrelationState.Acknowledged, "oncall"), (acknowledged.Correlation!.State, acknowledged.Correlation.AcknowledgedBy));

            await host.InTransactionAsync(client => client.ResolveAsync("alert:1", "fixed", "oncall", Ct));
            await ToldAsync(told.Reader, "changed", item);
            var resolved = (await host.GetAsync<InboxItemResponse>(ann, $"{Me()}/inbox/{item}"))!;
            Assert.Equal(CorrelationState.Resolved, resolved.Correlation!.State);
        }
    }
}
