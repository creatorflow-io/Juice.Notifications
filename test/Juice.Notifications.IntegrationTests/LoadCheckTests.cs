using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Juice.Notifications.Api.Contracts;
using Microsoft.EntityFrameworkCore;
using static Juice.Notifications.IntegrationTests.DeliveryTests;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The M9 load check (phase-1-plan §5 M9): 1,000 requests a minute to two webhook routes on the local stack; p95 from accepted to first
    /// attempt at most 30 s; no lease storms. It takes minutes, so it runs only when <c>NOTIFICATIONS_LOAD=1</c> (and
    /// <c>NOTIFICATIONS_LOAD_MINUTES</c>, default 2). The result is written to the test output for the acceptance record.
    /// </summary>
    public sealed class LoadCheckTests(ProducerApp producer, ITestOutputHelper output) : IClassFixture<ProducerApp>
    {
        [Fact]
        public async Task A_thousand_requests_a_minute_reach_their_first_attempt_within_30_seconds_at_p95()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("NOTIFICATIONS_LOAD") == "1", "set NOTIFICATIONS_LOAD=1 to run the load check");
            LocalStack.RequireAvailable();
            var minutes = int.TryParse(Environment.GetEnvironmentVariable("NOTIFICATIONS_LOAD_MINUTES"), out var m) ? m : 2;
            await using var first = await Receiver.StartAsync();
            await using var second = await Receiver.StartAsync();
            // In process by default; or the service at NOTIFICATIONS_LOAD_API (the compose app container, whose database traffic stays inside Docker's
            // network: from the host, every round trip goes through Docker Desktop's port proxy, which on Windows caps the in-process run).
            var external = Environment.GetEnvironmentVariable("NOTIFICATIONS_LOAD_API") is { Length: > 0 } api ? new Uri(api) : null;
            HttpClient Remote(string token) => new() { BaseAddress = external, DefaultRequestHeaders = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token) } };
            using var admin = external is null ? producer.App.Client(await LocalStack.AdminTokenAsync()) : Remote(await LocalStack.AdminTokenAsync());
            using var client = external is null ? await producer.ProducerAsync() : Remote(await LocalStack.ProducerTokenAsync());
            if (external is not null)
            {
                var created = await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(ProducerApp.Key, "Sample", "sample-producer"), Ct);
                Assert.True(created.IsSuccessStatusCode || created.StatusCode == System.Net.HttpStatusCode.Conflict, created.StatusCode.ToString());
                await Ok(admin.PutAsync($"/api/v1/applications/{ProducerApp.Key}/tenants/{ProducerApp.Tenant}", null, Ct));
            }
            var type = $"{ProducerApp.Key}.load{Short()}";
            await Ok(client.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(type, "Load", "alerts", null, null,
                null, null, DeliveryTests.Json("""{ "type": "object" }"""), null, null, null, DeliveryTests.Json("""{ "webhook": "passthrough" }""")), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/load-hooks", new PutIntegrationBody("webhook", "webhook", null, null, null, null), Ct));
            foreach (var (receiver, name) in new[] { (first, "load-a"), (second, "load-b") })
            {
                await Ok(admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [first.Port, second.Port]), Ct));
                await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/load-hooks/targets/{name}", new PutTargetBody(receiver.Url, null, null), Ct));
                await Ok(admin.PutAsJsonAsync($"{Tenant}/routes/{name}", new PutRouteBody(DeliveryTests.Json($$"""{ "type": "{{type}}" }"""), [name], null), Ct));
            }

            long leaseLost = 0;
            using var meter = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == Telemetry.Name && instrument.Name == "notifications.lease_lost")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            meter.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref leaseLost, value));
            meter.Start();

            // 1,000 a minute: one every 60 ms, sent on a schedule (not one after another), so a slow answer doesn't lower the rate.
            var total = 1000 * minutes;
            var accepted = new ConcurrentBag<Guid>();
            var failures = 0;
            var started = DateTimeOffset.UtcNow;
            var sends = new List<Task>(total);
            for (var i = 0; i < total; i++)
            {
                var due = started + TimeSpan.FromMilliseconds(60 * i);
                if (due > DateTimeOffset.UtcNow)
                {
                    await Task.Delay(due - DateTimeOffset.UtcNow, Ct);
                }
                var key = $"{type}-{i}"; // unique per run: a key seen before is a duplicate, answered with the original
                sends.Add(Task.Run(async () =>
                {
                    var response = await client.PostAsJsonAsync($"{Tenant}/notifications", new SendNotificationBody(type, null, key, null, null, null, null, null,
                        DeliveryTests.Json("""{ "n": 1 }"""), null, null), Ct);
                    if (response.IsSuccessStatusCode)
                    {
                        accepted.Add((await response.Content.ReadFromJsonAsync<AcceptedResponse>(Ct))!.Id);
                    }
                    else
                    {
                        Interlocked.Increment(ref failures);
                    }
                }, Ct));
            }
            await Task.WhenAll(sends);
            var sentFor = DateTimeOffset.UtcNow - started;

            // Every delivery attempted once (2 per request), or give up waiting after 3 minutes more.
            await using var db = external is null ? producer.App.Database.Context() : RemoteDatabase();
            var ids = accepted.ToHashSet();
            List<double> delays = [];
            for (var wait = 0; wait < 1800; wait++)
            {
                delays = await (
                    from request in db.Requests.Where(r => r.TypeKey == type)
                    join notification in db.Notifications on new { request.TenantId, RequestId = request.Id } equals new { notification.TenantId, notification.RequestId }
                    join recipient in db.Recipients on new { notification.TenantId, NotificationId = notification.Id } equals new { recipient.TenantId, recipient.NotificationId }
                    join item in db.DeliveryItems on new { recipient.TenantId, RecipientId = recipient.Id } equals new { item.TenantId, item.RecipientId }
                    join delivery in db.Deliveries on new { item.TenantId, Id = item.DeliveryId } equals new { delivery.TenantId, delivery.Id }
                    where delivery.FirstAttemptAt != null
                    select new { delivery.FirstAttemptAt, request.AcceptedAt }).AsNoTracking()
                    .Select(x => (x.FirstAttemptAt!.Value - x.AcceptedAt).TotalSeconds).ToListAsync(Ct);
                if (delays.Count >= ids.Count * 2)
                {
                    break;
                }
                await Task.Delay(100, Ct);
            }
            // Where the time goes, stage by stage (p95 of each): accepted → planned (planner), → delivery created (delivery planner), → first attempt (dispatcher).
            var stages = await (
                from request in db.Requests.Where(r => r.TypeKey == type)
                join notification in db.Notifications on new { request.TenantId, RequestId = request.Id } equals new { notification.TenantId, notification.RequestId }
                join recipient in db.Recipients on new { notification.TenantId, NotificationId = notification.Id } equals new { recipient.TenantId, recipient.NotificationId }
                join item in db.DeliveryItems on new { recipient.TenantId, RecipientId = recipient.Id } equals new { item.TenantId, item.RecipientId }
                join delivery in db.Deliveries on new { item.TenantId, Id = item.DeliveryId } equals new { delivery.TenantId, delivery.Id }
                where delivery.FirstAttemptAt != null
                select new { request.AcceptedAt, Planned = recipient.CreatedAt, delivery.CreatedAt, delivery.FirstAttemptAt }).AsNoTracking().ToListAsync(Ct);
            static double P95(IEnumerable<double> values) { var v = values.Order().ToList(); return v.Count == 0 ? double.NaN : v[Math.Min(v.Count - 1, (int)Math.Ceiling(0.95 * v.Count) - 1)]; }
            output.WriteLine($"load check stages (p95): planner {P95(stages.Select(s => (s.Planned - s.AcceptedAt).TotalSeconds)):F2} s, delivery planner " +
                $"{P95(stages.Select(s => (s.CreatedAt - s.Planned).TotalSeconds)):F2} s, dispatcher {P95(stages.Select(s => (s.FirstAttemptAt!.Value - s.CreatedAt).TotalSeconds)):F2} s");
            var durations = await db.Attempts.AsNoTracking().Select(a => a.DurationMs).ToListAsync(Ct);
            var slow = stages.Where(s => (s.FirstAttemptAt!.Value - s.CreatedAt).TotalSeconds > 5).Select(s => (s.CreatedAt - started).TotalSeconds).Order().ToList();
            output.WriteLine($"load check diag: attempt duration p95 {P95(durations.Select(d => (double)d)):F0} ms, max {durations.DefaultIfEmpty().Max()} ms; " +
                $"{slow.Count} slow deliveries, created at s {string.Join(",", slow.Select(s => ((int)s).ToString()).Distinct().Take(40))}");
            delays.Sort();
            double Percentile(double p) => delays.Count == 0 ? double.NaN : delays[Math.Min(delays.Count - 1, (int)Math.Ceiling(p * delays.Count) - 1)];
            var p95 = Percentile(0.95);
            output.WriteLine($"load check: {total} requests over {sentFor.TotalSeconds:F0} s ({total / sentFor.TotalMinutes:F0}/min), {failures} refused, " +
                $"{delays.Count}/{ids.Count * 2} deliveries attempted; accepted→first attempt p50 {Percentile(0.5):F2} s, p95 {p95:F2} s, " +
                $"p99 {Percentile(0.99):F2} s, max {delays.LastOrDefault():F2} s; lease_lost {leaseLost}; receivers {first.Requests.Count} + {second.Requests.Count}");

            Assert.Equal(0, failures);
            Assert.Equal(ids.Count * 2, delays.Count);
            Assert.True(p95 <= 30, $"p95 {p95:F2} s");
            Assert.True(leaseLost <= total / 100, $"lease_lost {leaseLost}: a lease storm");
            // A lease storm (rows claimed again while being sent) shows as sends beyond one per delivery, also when the meter is out of reach.
            Assert.Equal(ids.Count * 2, first.Requests.Count + second.Requests.Count);
        }

        private static Data.NotificationsDbContext RemoteDatabase()
        {
            var options = new DbContextOptionsBuilder<Data.NotificationsDbContext>();
            Data.NotificationsDbContext.Configure(options, LocalStack.ServerConnectionString("notifications"));
            return new Data.NotificationsDbContext(options.Options);
        }
    }
}
