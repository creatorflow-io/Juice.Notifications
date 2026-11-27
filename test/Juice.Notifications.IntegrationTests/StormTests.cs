using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    public sealed class StormApp : IAsyncLifetime
    {
        /// <summary>Takes every webhook delivery of kind <c>storm-hook</c> (accepted).</summary>
        public ScriptedProvider Hooks { get; } = new(Channels.Webhook, "storm-hook");

        /// <summary>Kind <c>replay-hook</c>: fails what it is scripted to, then takes the rest.</summary>
        public ScriptedProvider Replays { get; } = new(Channels.Webhook, "replay-hook");

        public NotificationsApp App { get; }

        public StormApp() => App = new(host =>
        {
            // A short digest window, so the test doesn't wait a minute for each; the threshold is the default (100 a minute).
            host.UseSetting("Storm:Window", "00:00:05");
            host.ConfigureTestServices(services => services.AddSingleton<INotificationProvider>(Hooks).AddSingleton<INotificationProvider>(Replays));
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
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>
    /// Storm protection and bulk replay (design §14, phase-4-plan I43, I44, M31) in the real host, the webhook provider scripted.
    /// </summary>
    public sealed class StormTests(StormApp fixture) : IClassFixture<StormApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = ProducerApp.Tenant;
        private static readonly string Api = $"/api/v1/tenants/{Tenant}";
        private NotificationsApp App => fixture.App;

        private static async Task Ok(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        /// <summary>A type sent passthrough to the webhook channel, and an integration of <paramref name="kind"/> with a target per route.</summary>
        private async Task<string> SetUpAsync(string kind, params string[] routes)
        {
            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            using var producer = App.Client(await LocalStack.ProducerTokenAsync());
            var type = $"{ProducerApp.Key}.s{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            await Ok(producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(type, "Alert", "alerts", null, null, null, null,
                JsonElement.Parse("""{ "type": "object" }"""), null, null, null, JsonElement.Parse("""{ "webhook": "passthrough" }""")), Ct));
            var integration = kind + "-" + type[^6..];
            await Ok(admin.PutAsJsonAsync($"{Api}/integrations/{integration}", new PutIntegrationBody("webhook", kind, null, null, null, true), Ct));
            foreach (var route in routes)
            {
                await Ok(admin.PutAsJsonAsync($"{Api}/integrations/{integration}/targets/{route}", new PutTargetBody($"https://{route}.example.com/hook", null, true), Ct));
                await Ok(admin.PutAsJsonAsync($"{Api}/routes/{route}", new PutRouteBody(JsonSerializer.SerializeToElement(new { type }), [route], null), Ct));
            }
            return type;
        }

        private async Task<List<Guid>> SendAsync(string type, int count)
        {
            using var producer = App.Client(await LocalStack.ProducerTokenAsync());
            var ids = new System.Collections.Concurrent.ConcurrentBag<Guid>();
            await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Ct }, async (i, ct) =>
            {
                var response = await producer.PostAsJsonAsync($"{Api}/notifications", new SendNotificationBody(type, null, $"{type}-{i}", null, null, null, null, null,
                    JsonSerializer.SerializeToElement(new { n = i }), null, null), ct);
                Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
                ids.Add((await response.Content.ReadFromJsonAsync<AcceptedResponse>(ct))!.Id);
            });
            return [.. ids];
        }

        /// <summary>
        /// Phase 4 proof point 4. A thousand notifications in a burst to two routes: past 100 a minute each route storms, and its target gets the rest
        /// as digests (a delivery carrying many notifications) instead of a delivery each. Nothing is lost: each target's deliveries carry every one
        /// of the thousand requests, once. Each route has one storm, which the admin API shows.
        /// </summary>
        [Fact]
        public async Task A_storm_of_a_thousand_reaches_each_route_as_digests_and_nothing_is_lost()
        {
            LocalStack.RequireAvailable();
            var routes = new[] { "storm-a-" + Guid.NewGuid().ToString("N")[..6], "storm-b-" + Guid.NewGuid().ToString("N")[..6] };
            var type = await SetUpAsync("storm-hook", routes);

            var requests = await SendAsync(type, 1000);
            Assert.Equal(1000, requests.Distinct().Count());

            // A progress-based budget: the pipeline may be slow under a full run, but a stalled one makes no progress. Poll with two cheap
            // counts; give up after a minute without growth in either, or at the hard cap (deferred #56).
            var noProgress = TimeSpan.FromSeconds(60);
            var hardCap = TimeSpan.FromMinutes(8);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var lastProgress = clock.Elapsed;
            (int Carried, int Accepted) seen = (0, 0);
            Dictionary<Guid, (int Deliveries, int Requests, int Carried)> byTarget = [];
            string? gaveUp = null;
            while (true)
            {
                await using var db = App.Database.Context();
                var carriedNow = await (from item in db.DeliveryItems
                                        join request in db.Requests on new { item.TenantId, Id = item.RequestId } equals new { request.TenantId, request.Id }
                                        where request.TypeKey == type
                                        select item.RequestId).CountAsync(Ct);
                var acceptedNow = await (from item in db.DeliveryItems
                                         join delivery in db.Deliveries on new { item.TenantId, Id = item.DeliveryId } equals new { delivery.TenantId, delivery.Id }
                                         join request in db.Requests on new { item.TenantId, Id = item.RequestId } equals new { request.TenantId, request.Id }
                                         where request.TypeKey == type && delivery.Status == DeliveryStatus.Accepted
                                         select item.RequestId).CountAsync(Ct);
                if (carriedNow > seen.Carried || acceptedNow > seen.Accepted)
                {
                    lastProgress = clock.Elapsed;
                }
                seen = (carriedNow, acceptedNow);
                // Every item of both routes is in and accepted: only now the heavier check (nothing still pending).
                if (carriedNow == 2000 && acceptedNow == 2000)
                {
                    var pending = await db.Recipients.AsNoTracking().CountAsync(r => r.Outcome == RecipientOutcome.Pending
                        && db.Notifications.Any(n => n.Id == r.NotificationId && db.Requests.Any(q => q.Id == n.RequestId && q.TypeKey == type)), Ct);
                    if (pending == 0)
                    {
                        break;
                    }
                }
                if (clock.Elapsed - lastProgress > noProgress || clock.Elapsed > hardCap)
                {
                    gaveUp = $"The storm stalled after {clock.Elapsed.TotalSeconds:F0} s ({(clock.Elapsed - lastProgress).TotalSeconds:F0} s without progress): "
                        + $"{seen.Carried} of 2000 items carried, {seen.Accepted} accepted.";
                    break;
                }
                await Task.Delay(500, Ct);
            }
            {
                await using var db = App.Database.Context();
                var items = await (from item in db.DeliveryItems
                                   join delivery in db.Deliveries on new { item.TenantId, Id = item.DeliveryId } equals new { delivery.TenantId, delivery.Id }
                                   join request in db.Requests on new { item.TenantId, Id = item.RequestId } equals new { request.TenantId, request.Id }
                                   where request.TypeKey == type
                                   select new { delivery.Id, delivery.DestinationKey, delivery.Status, item.RequestId }).AsNoTracking().ToListAsync(Ct);
                byTarget = items.GroupBy(x => x.DestinationKey).ToDictionary(g => Guid.Parse(g.Key[3..]),
                    g => (g.Select(x => x.Id).Distinct().Count(), g.Select(x => x.RequestId).Distinct().Count(), g.Count()));
            }
            Assert.True(gaveUp is null, gaveUp);

            Assert.Equal(2, byTarget.Count);
            foreach (var (_, (deliveries, carried, items)) in byTarget)
            {
                Assert.Equal(1000, carried);        // every request reached the target
                Assert.Equal(1000, items);          // each once
                Assert.True(deliveries < 400, $"{deliveries} deliveries: the storm should have sent most as digests");
            }
            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            var storms = await admin.GetFromJsonAsync<List<StormResponse>>($"{Api}/storms?all=true", Ct);
            foreach (var route in routes)
            {
                var storm = Assert.Single(storms!, s => s.Route == route);
                Assert.True(storm.Peak > 100 && storm.Digested > 0, $"peak {storm.Peak}, digested {storm.Digested}");
            }
        }

        /// <summary>
        /// Bulk replay (I44): the failed deliveries of one integration go back under their ids and go out again; another type's failures, and a
        /// non-admin, are refused or untouched; the limit says when more matched.
        /// </summary>
        [Fact]
        public async Task Failed_deliveries_are_replayed_in_bulk_under_their_ids()
        {
            LocalStack.RequireAvailable();
            var route = "replay-" + Guid.NewGuid().ToString("N")[..6];
            var type = await SetUpAsync("replay-hook", route);
            for (var i = 0; i < 5; i++)
            {
                fixture.Replays.Then(SendResult.Permanent(FailureCategory.Provider, "down for maintenance"));
            }
            var requests = await SendAsync(type, 5);
            List<Delivery> failed = [];
            for (var i = 0; i < 300 && failed.Count < 5; i++)
            {
                await Task.Delay(100, Ct);
                await using var db = App.Database.Context();
                failed = await db.Deliveries.AsNoTracking().Where(d => d.Status == DeliveryStatus.Failed
                    && db.DeliveryItems.Any(it => it.DeliveryId == d.Id && requests.Contains(it.RequestId))).ToListAsync(Ct);
            }
            Assert.Equal(5, failed.Count);
            var integration = (await App.Database.Context().Integrations.AsNoTracking().SingleAsync(x => x.Id == failed[0].IntegrationId, Ct)).Key;

            using var producer = App.Client(await LocalStack.ProducerTokenAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await producer.PostAsJsonAsync($"{Api}/deliveries/replay", new ReplayBody(null, integration, null, null, null, null, null), Ct)).StatusCode);
            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            var other = await (await admin.PostAsJsonAsync($"{Api}/deliveries/replay", new ReplayBody(null, integration, type + "x", null, null, null, null), Ct))
                .Content.ReadFromJsonAsync<ReplayResponse>(Ct);
            Assert.Equal(new ReplayResponse(0, 0, false), other);
            var first = await (await admin.PostAsJsonAsync($"{Api}/deliveries/replay", new ReplayBody("webhook", integration, type, "provider", null, null, 3), Ct))
                .Content.ReadFromJsonAsync<ReplayResponse>(Ct);
            var rest = await (await admin.PostAsJsonAsync($"{Api}/deliveries/replay", new ReplayBody(null, integration, null, null, null, null, 3), Ct))
                .Content.ReadFromJsonAsync<ReplayResponse>(Ct);
            Assert.Equal(new ReplayResponse(3, 0, true), first);
            Assert.Equal(new ReplayResponse(2, 0, false), rest);

            List<Delivery> after = [];
            for (var i = 0; i < 300; i++)
            {
                await Task.Delay(100, Ct);
                await using var db = App.Database.Context();
                var ids = failed.Select(d => d.Id).ToList();
                after = await db.Deliveries.AsNoTracking().Where(d => ids.Contains(d.Id)).ToListAsync(Ct);
                if (after.All(d => d.Status == DeliveryStatus.Accepted))
                {
                    break;
                }
            }
            Assert.All(after, d => Assert.Equal((DeliveryStatus.Accepted, 1, 2), (d.Status, d.Replays, d.Attempts)));
        }
    }
}
