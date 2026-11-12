using System.Net;
using System.Net.Http.Json;
using Juice.Notifications.Api.Contracts;
using static Juice.Notifications.IntegrationTests.DeliveryTests;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Delivery history and dead-letter retry (phase-1-plan M8), through the real host and pipeline.</summary>
    public sealed class HistoryTests(ProducerApp producer) : IClassFixture<ProducerApp>
    {
        private async Task<(HttpClient Admin, HttpClient Client, Receiver Receiver, Guid Request, DeliveryResponse Delivery)> FailedWebhookAsync()
        {
            var receiver = await Receiver.StartAsync(404); // gone once, then 200
            var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            var client = await producer.ProducerAsync();
            var target = "hist-" + Short();
            await Ok(admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port]), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/history-hooks", new PutIntegrationBody("webhook", "webhook", null, null, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/history-hooks/targets/{target}", new PutTargetBody(receiver.Url, null, null), Ct));
            var request = await SendThroughRouteAsync(admin, client, "history-hooks", target, """{ "webhook": "passthrough" }""");
            var delivery = Assert.Single((await DeliveredAsync(client, request)).Deliveries);
            Assert.Equal((DeliveryStatus.Failed, FailureCategory.Destination), (delivery.Status, delivery.FailureCategory));
            return (admin, client, receiver, request, delivery);
        }

        private static async Task<DeliveryDetailResponse> FinishedAsync(HttpClient client, Guid id)
        {
            for (var i = 0; i < 150; i++)
            {
                var detail = await client.GetFromJsonAsync<DeliveryDetailResponse>($"{Tenant}/deliveries/{id}", Ct);
                if (detail!.Delivery.Status is not (DeliveryStatus.Pending or DeliveryStatus.Retrying or DeliveryStatus.Sending))
                {
                    return detail;
                }
                await Task.Delay(100, Ct);
            }
            throw new TimeoutException("The delivery never finished.");
        }

        [Fact]
        public async Task Delivery_id_is_stable_across_attempts_history_and_dead_letter_retry()
        {
            LocalStack.RequireAvailable();
            var (admin, client, receiver, request, failed) = await FailedWebhookAsync();
            await using var _ = receiver;
            using var __ = admin;
            using var ___ = client;

            var retry = await admin.PostAsync($"{Tenant}/deliveries/{failed.Id}/retry", null, Ct);
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            var retried = await retry.Content.ReadFromJsonAsync<RetryResponse>(Ct);
            Assert.Equal((failed.Id, DeliveryStatus.Pending, 1, 1), (retried!.Id, retried.Status, retried.Attempts, retried.Replays));

            var detail = await FinishedAsync(client, failed.Id);
            Assert.Equal((failed.Id, DeliveryStatus.Delivered, 2, request), (detail.Delivery.Id, detail.Delivery.Status, detail.Delivery.Attempts, detail.Delivery.RequestId));
            Assert.Equal([(1, AttemptOutcome.PermanentFailure, (int?)404), (2, AttemptOutcome.Delivered, 200)],
                detail.Attempts.Select(a => (a.Number, a.Outcome, a.StatusCode)));
            Assert.All(detail.Attempts, a => Assert.Equal("history-hooks", a.Integration));
            Assert.Equal(2, receiver.Requests.Count);
            Assert.All(receiver.Requests, r => Assert.Equal(failed.Id.ToString(), r.Id));
            Assert.Single(receiver.Requests.Select(r => r.Body).Distinct()); // the same stored bytes

            // The same id in the list, for the admin and for the producer that sent it.
            foreach (var reader in new[] { admin, client })
            {
                var page = await reader.GetFromJsonAsync<DeliveryPageResponse>($"{Tenant}/deliveries?request={request}", Ct);
                Assert.Equal(failed.Id, Assert.Single(page!.Items).Id);
            }

            // Delivered now: a second retry is refused.
            var again = await admin.PostAsync($"{Tenant}/deliveries/{failed.Id}/retry", null, Ct);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains(ErrorCodes.NotRetryable, await again.Content.ReadAsStringAsync(Ct));
        }

        /// <summary>A template that fails when rendered: the delivery is Failed {Content} with nothing stored to send, so a retry is refused.</summary>
        [Fact]
        public async Task A_render_failure_is_recorded_and_cannot_be_retried()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync();
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            using var client = await producer.ProducerAsync();
            var target = "huge-" + Short();
            await Ok(admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port]), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/history-hooks", new PutIntegrationBody("webhook", "webhook", null, null, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/history-hooks/targets/{target}", new PutTargetBody(receiver.Url, null, null), Ct));
            var request = await SendThroughRouteAsync(admin, client, "history-hooks", target, """{ "webhook": "{{ data.host | string.pad_left 300000 }}" }""");

            var delivery = Assert.Single((await DeliveredAsync(client, request)).Deliveries);
            Assert.Equal((DeliveryStatus.Failed, FailureCategory.Content, 0), (delivery.Status, delivery.FailureCategory, delivery.Attempts));
            var detail = await admin.GetFromJsonAsync<DeliveryDetailResponse>($"{Tenant}/deliveries/{delivery.Id}", Ct);
            Assert.StartsWith("render_failed", detail!.Delivery.LastError);
            Assert.Empty(receiver.Requests);

            var retry = await admin.PostAsync($"{Tenant}/deliveries/{delivery.Id}/retry", null, Ct);
            Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
            Assert.Contains("never rendered", await retry.Content.ReadAsStringAsync(Ct));
        }

        [Fact]
        public async Task A_producer_reads_history_but_cannot_retry()
        {
            LocalStack.RequireAvailable();
            var (admin, client, receiver, _, failed) = await FailedWebhookAsync();
            await using var _r = receiver;
            using var _a = admin;
            using var _c = client;

            var retry = await client.PostAsync($"{Tenant}/deliveries/{failed.Id}/retry", null, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
            var detail = await client.GetFromJsonAsync<DeliveryDetailResponse>($"{Tenant}/deliveries/{failed.Id}", Ct);
            Assert.Equal(DeliveryStatus.Failed, detail!.Delivery.Status);
            Assert.Equal(1, Assert.Single(detail.Attempts).Number);
        }

        [Fact]
        public async Task History_filters_narrow_the_list_and_pages_follow_the_cursor()
        {
            LocalStack.RequireAvailable();
            var (admin, client, receiver, request, failed) = await FailedWebhookAsync();
            await using var _r = receiver;
            using var _a = admin;
            using var _c = client;

            async Task<DeliveryPageResponse> List(string query) => (await admin.GetFromJsonAsync<DeliveryPageResponse>($"{Tenant}/deliveries?{query}", Ct))!;

            Assert.Contains(failed.Id, (await List($"status=failed&request={request}")).Items.Select(i => i.Id));
            Assert.Empty((await List($"status=delivered&request={request}")).Items);
            Assert.Contains(failed.Id, (await List("integration=history-hooks&channel=webhook&limit=200")).Items.Select(i => i.Id));
            Assert.Empty((await List($"channel=email&request={request}")).Items);
            Assert.Empty((await List($"request={request}&from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"))}")).Items);

            // Two pages of one, newest first, then the end.
            var first = await List("integration=history-hooks&limit=1");
            Assert.NotNull(first.Next);
            var second = await List($"integration=history-hooks&limit=1&before={first.Next}");
            Assert.True(second.Items.Count <= 1);
            Assert.All(second.Items, i => Assert.True(i.CreatedAt <= first.Items[0].CreatedAt));
            Assert.DoesNotContain(first.Items[0].Id, second.Items.Select(i => i.Id));

            var bad = await admin.GetAsync($"{Tenant}/deliveries?status=lost", Ct);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"{Tenant}/deliveries?limit=0", Ct)).StatusCode);
        }
    }
}
