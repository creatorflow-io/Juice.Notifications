using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Planning;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Two replicas of the real host on one database, their inbox hubs joined by the local stack's Redis (phase-2-plan I18).</summary>
    public sealed class TwoReplicas : IAsyncLifetime
    {
        public FakePeopleResolver People { get; } = new();

        public NotificationsApp A { get; }

        public NotificationsApp B { get; }

        public TwoReplicas()
        {
            A = new NotificationsApp(Replica);
            B = new NotificationsApp(A.Database, Replica);
        }

        private void Replica(Microsoft.AspNetCore.Hosting.IWebHostBuilder host)
        {
            host.UseSetting("Inbox:SignalR:Redis", LocalStack.Redis);
            host.ConfigureTestServices(services => services.AddSingleton<IRecipientResolver>(People));
        }

        public async ValueTask InitializeAsync()
        {
            await A.InitializeAsync();
            await B.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            using var admin = A.Client(await LocalStack.AdminTokenAsync());
            (await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(ProducerApp.Key, "Sample", "sample-producer"))).EnsureSuccessStatusCode();
            (await admin.PutAsync($"/api/v1/applications/{ProducerApp.Key}/tenants/{ProducerApp.Tenant}", null)).EnsureSuccessStatusCode();
        }

        public async ValueTask DisposeAsync()
        {
            await B.DisposeAsync();
            await A.DisposeAsync();
        }
    }

    /// <summary>Phase 2 proof point 5: inbox read state syncs across two sessions, each on its own replica.</summary>
    public sealed class InboxSyncTests(TwoReplicas replicas) : IClassFixture<TwoReplicas>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string Tenant = $"/api/v1/tenants/{ProducerApp.Tenant}";

        private static async Task<T> OkAsync<T>(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
        }

        /// <summary>A session on <paramref name="replica"/>'s hub, and what it is told.</summary>
        private static async Task<(HubConnection Connection, ChannelReader<InboxChangedMessage> Told)> SessionAsync(NotificationsApp replica, string token)
        {
            var told = Channel.CreateUnbounded<InboxChangedMessage>();
            var connection = new HubConnectionBuilder()
                .WithUrl(new Uri(replica.Server.BaseAddress, $"hubs/inbox?tenant={ProducerApp.Tenant}"), options =>
                {
                    // The in-memory server answers HTTP only: long polling, with the token as a header.
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => replica.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
                .Build();
            connection.On<InboxChangedMessage>("inboxChanged", message => told.Writer.TryWrite(message));
            await connection.StartAsync(Ct);
            return (connection, told.Reader);
        }

        /// <summary>The first message told about <paramref name="item"/> with <paramref name="kind"/>, within 15 seconds.</summary>
        private static async Task<InboxChangedMessage> ToldAsync(ChannelReader<InboxChangedMessage> told, string kind, Guid? item = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await foreach (var message in told.ReadAllAsync(timeout.Token))
            {
                if (message.Kind == kind && (item is null || message.Items.Contains(item.Value)))
                {
                    return message;
                }
            }
            throw new TimeoutException();
        }

        [Fact]
        public async Task Reading_in_one_session_updates_the_other_across_two_replicas()
        {
            LocalStack.RequireRedis();
            var token = await LocalStack.PersonTokenAsync("ann");
            using var onA = replicas.A.Client(token);
            using var onB = replicas.B.Client(token);
            var ann = await OkAsync<MeResponse>(onA.GetAsync($"{Tenant}/me", Ct));
            var (sessionA, toldA) = await SessionAsync(replicas.A, token);
            var (sessionB, toldB) = await SessionAsync(replicas.B, token);
            await using var a = sessionA;
            await using var b = sessionB;

            // A notification arrives: both sessions hear of it, whichever replica's dispatcher wrote it.
            using var admin = replicas.A.Client(await LocalStack.AdminTokenAsync());
            (await admin.PutAsJsonAsync($"{Tenant}/resolvers/crews", new PutResolverBody("fake", ["group"], null, null, null), Ct)).EnsureSuccessStatusCode();
            var crew = $"crew-{Guid.NewGuid():N}";
            replicas.People.Groups[crew] = [new ResolvedPerson(ann.Source, ann.ExternalId, ann.Name)];
            using var producer = replicas.A.Client(await LocalStack.ProducerTokenAsync());
            var type = $"{ProducerApp.Key}.s{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            (await producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(type, "Synced", "ops", null, "normal", false, null,
                JsonElement.Parse("""{ "type": "object" }"""), null, null, null, null), Ct)).EnsureSuccessStatusCode();
            (await producer.PostAsJsonAsync($"{Tenant}/notifications", new SendNotificationBody(type, null, Guid.NewGuid().ToString("N"), null,
                [new RecipientBody("group", Id: crew, Resolver: "crews")], null, null, null, JsonElement.Parse("{}"), null, null), Ct)).EnsureSuccessStatusCode();
            var added = await ToldAsync(toldA, "added");
            var item = Assert.Single(added.Items);
            await ToldAsync(toldB, "added", item);

            // Read in the session on A: the session on B is told, and reads it as read from its own replica.
            await OkAsync<InboxItemResponse>(onA.PostAsync($"{Tenant}/me/inbox/{item}/read", null, Ct));
            await ToldAsync(toldB, "changed", item);
            Assert.NotNull((await OkAsync<InboxItemResponse>(onB.GetAsync($"{Tenant}/me/inbox/{item}", Ct))).ReadAt);

            // And the other way: archived on B, A is told.
            await OkAsync<InboxItemResponse>(onB.PostAsync($"{Tenant}/me/inbox/{item}/archive", null, Ct));
            await ToldAsync(toldA, "changed", item);
            Assert.DoesNotContain(await OkAsync<List<InboxItemResponse>>(onA.GetAsync($"{Tenant}/me/inbox", Ct)), i => i.Id == item);
        }

        /// <summary>The hub hears only a person of the tenant: a client's own token, or another tenant, is turned away.</summary>
        [Fact]
        public async Task The_hub_turns_away_a_client_and_another_tenant()
        {
            LocalStack.RequireAvailable();
            foreach (var (token, tenant) in new[] { (await LocalStack.TenantAdminTokenAsync(), ProducerApp.Tenant), (await LocalStack.PersonTokenAsync(), ProducerApp.OtherTenant) })
            {
                var closed = new TaskCompletionSource();
                var connection = new HubConnectionBuilder()
                    .WithUrl(new Uri(replicas.A.Server.BaseAddress, $"hubs/inbox?tenant={tenant}"), options =>
                    {
                        options.Transports = HttpTransportType.LongPolling;
                        options.HttpMessageHandlerFactory = _ => replicas.A.Server.CreateHandler();
                        options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                    })
                    .Build();
                connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
                await using var _ = connection;
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
            }
        }
    }
}
