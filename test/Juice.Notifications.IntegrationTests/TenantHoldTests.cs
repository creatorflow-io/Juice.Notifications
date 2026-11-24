using System.Net.Http.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Delivering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static Juice.Notifications.IntegrationTests.DeliveryTests;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// A tenant that isn't active has its deliveries held, not dropped (the tenant pattern decision record; owner, 24 Nov): they go out once it
    /// is active again.
    /// </summary>
    public sealed class TenantHoldTests : IAsyncLifetime
    {
        private sealed class SwitchedGate : ITenantGate
        {
            public volatile bool Active = true;

            public ValueTask<bool> IsActiveAsync(Guid tenantId, CancellationToken ct) => ValueTask.FromResult(Active);
        }

        private readonly SwitchedGate _gate = new();
        private readonly NotificationsApp _app;

        public TenantHoldTests() => _app = new NotificationsApp(host =>
        {
            host.UseSetting("Delivery:TenantHold", "00:00:00.300");
            host.ConfigureTestServices(services => services.AddSingleton<ITenantGate>(_gate));
        });

        public async ValueTask InitializeAsync()
        {
            await _app.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            using var admin = _app.Client(await LocalStack.AdminTokenAsync());
            (await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(ProducerApp.Key, "Sample", "sample-producer"))).EnsureSuccessStatusCode();
            (await admin.PutAsync($"/api/v1/applications/{ProducerApp.Key}/tenants/{ProducerApp.Tenant}", null)).EnsureSuccessStatusCode();
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();

        [Fact]
        public async Task A_suspended_tenants_delivery_waits_and_goes_out_once_it_is_active()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync(200);
            using var admin = _app.Client(await LocalStack.AdminTokenAsync());
            using var client = _app.Client(await LocalStack.ProducerTokenAsync());
            var target = "hook-" + Short();
            await Ok(admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port]), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/hooks", new PutIntegrationBody("webhook", "webhook", null, TestHooks.SecretRef, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/hooks/targets/{target}", new PutTargetBody(receiver.Url, null, null), Ct));

            // The tenant is suspended (the gate says so; the API still takes the request, as RabbitMQ intake would): planned, not sent.
            _gate.Active = false;
            var id = await SendThroughRouteAsync(admin, client, "hooks", target, """{ "webhook": "passthrough" }""");
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            var held = await client.GetFromJsonAsync<NotificationResponse>($"{Tenant}/notifications/{id}", Ct);
            var waiting = Assert.Single(held!.Deliveries);
            Assert.Equal((DeliveryStatus.Pending, 0), (waiting.Status, waiting.Attempts));
            Assert.Empty(receiver.Requests);

            _gate.Active = true;
            var delivered = Assert.Single((await DeliveredAsync(client, id)).Deliveries);
            Assert.Equal((DeliveryStatus.Delivered, 1), (delivered.Status, delivered.Attempts));
            Assert.Single(receiver.Requests);
        }
    }
}
