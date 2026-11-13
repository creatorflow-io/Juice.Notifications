using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using Juice.Notifications.Api.Contracts;
using static Juice.Notifications.IntegrationTests.DeliveryTests;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Traces (design §14, phase-1-plan M8): one trace from the producer's request through the stages to the provider call.</summary>
    public sealed class TelemetryTests(ProducerApp producer) : IClassFixture<ProducerApp>
    {
        [Fact]
        public async Task A_trace_links_the_request_to_the_provider_call()
        {
            LocalStack.RequireAvailable();
            var trace = ActivityTraceId.CreateRandom();
            var stopped = new ConcurrentQueue<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => { if (a.TraceId == trace) stopped.Enqueue(a); },
            };
            ActivitySource.AddActivityListener(listener);

            await using var receiver = await Receiver.StartAsync();
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            using var client = await producer.ProducerAsync();
            var target = "trace-" + Short();
            await Ok(admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port]), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/trace-hooks", new PutIntegrationBody("webhook", "webhook", null, TestHooks.SecretRef, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/trace-hooks/targets/{target}", new PutTargetBody(receiver.Url, null, null), Ct));
            var type = $"{ProducerApp.Key}.t{Short()}";
            await Ok(client.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(type, "Traced", "alerts", null, null,
                null, null, DeliveryTests.Json("""{ "type": "object" }"""), null, null, null, DeliveryTests.Json("""{ "webhook": "passthrough" }""")), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/routes/r-{Short()}", new PutRouteBody(DeliveryTests.Json($$"""{ "type": "{{type}}" }"""), [target], null), Ct));

            using var send = new HttpRequestMessage(HttpMethod.Post, $"{Tenant}/notifications")
            {
                Content = JsonContent.Create(new SendNotificationBody(type, null, Short(), null, null, null, null, null, DeliveryTests.Json("{}"), null, null)),
            };
            send.Headers.Add("traceparent", $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
            var response = await client.SendAsync(send, Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            var accepted = await response.Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            Assert.Equal(DeliveryStatus.Delivered, Assert.Single((await DeliveredAsync(client, accepted!.Id)).Deliveries).Status);

            // The dispatch step is in the producer's trace, and the webhook's HTTP request carried it to the receiver.
            for (var i = 0; i < 50 && !stopped.Any(a => a.OperationName == "notifications.dispatch"); i++)
            {
                await Task.Delay(100, Ct);
            }
            var dispatch = Assert.Single(stopped, a => a.OperationName == "notifications.dispatch");
            // Every stage on the way is in the same trace.
            Assert.Contains(stopped, a => a.OperationName == "notifications.plan");
            Assert.Contains(stopped, a => a.OperationName == "notifications.deliveries");
            Assert.Equal("trace-hooks", dispatch.GetTagItem("notifications.integration"));
            var carried = Assert.Single(receiver.Requests).TraceParent;
            Assert.NotNull(carried);
            Assert.Equal(trace.ToString(), carried.Split('-')[1]);
        }

        /// <summary>The due backlog is counted per stage from the database, for the gauge to report without waiting on it.</summary>
        [Fact]
        public async Task The_due_backlog_is_counted_for_each_stage()
        {
            LocalStack.RequireAvailable();
            var monitor = (Stages.BacklogMonitor)producer.App.Services.GetService(typeof(Stages.BacklogMonitor))!;
            await monitor.CountAsync(Ct);
            Assert.Equal(["planner", "delivery-planner", "dispatcher"], monitor.Latest.Select(m => (string)m.Tags[0].Value!));
            Assert.All(monitor.Latest, m => Assert.True(m.Value >= 0));
        }
    }
}
