using System.Net.Http.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Delivering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
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

        /// <summary>M52 (#55): what the gate holds on purpose isn't undelivered; the flag is cleared by the step that sends it.</summary>
        [Fact]
        public async Task A_delivery_the_gate_holds_isnt_undelivered_and_the_next_step_clears_its_flag()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync(200);
            using var admin = _app.Client(await LocalStack.AdminTokenAsync());
            using var client = _app.Client(await LocalStack.ProducerTokenAsync());
            var target = "hook-" + Short();
            await Ok(admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port]), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/hooks", new PutIntegrationBody("webhook", "webhook", null, TestHooks.SecretRef, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/hooks/targets/{target}", new PutTargetBody(receiver.Url, null, null), Ct));

            _gate.Active = false;
            var id = await SendThroughRouteAsync(admin, client, "hooks", target, """{ "webhook": "passthrough" }""");
            for (var i = 0; i < 100 && !await HeldAsync(id); i++)
            {
                await Task.Delay(100, Ct);
            }
            Assert.True(await HeldAsync(id), "the dispatcher never held the delivery");
            await using (var db = _app.Database.Context())
            {
                // The default TenantHold of 5 minutes, not this host's 0.3 seconds: the hold hasn't lapsed.
                var health = await new Juice.Notifications.Stages.NotificationsPipelineHealth(db, new(), new(true), TimeProvider.System,
                    Microsoft.Extensions.Options.Options.Create(new Juice.Notifications.Providers.DeliveryOptions())).ReadAsync(Ct);
                Assert.Null(health.Sends.OldestUndelivered);
            }

            _gate.Active = true;
            Assert.Equal(DeliveryStatus.Delivered, Assert.Single((await DeliveredAsync(client, id)).Deliveries).Status);
            Assert.False(await HeldAsync(id));
        }

        /// <summary>M52 (#55): a held delivery whose request expires while the tenant is still inactive is expired by the next step, flag cleared.</summary>
        [Fact]
        public async Task A_held_delivery_that_expires_while_the_tenant_is_inactive_has_its_flag_cleared()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await Receiver.StartAsync(200);
            using var admin = _app.Client(await LocalStack.AdminTokenAsync());
            using var client = _app.Client(await LocalStack.ProducerTokenAsync());
            var target = "hook-" + Short();
            await Ok(admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port]), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/hooks", new PutIntegrationBody("webhook", "webhook", null, TestHooks.SecretRef, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Tenant}/integrations/hooks/targets/{target}", new PutTargetBody(receiver.Url, null, null), Ct));

            _gate.Active = false;
            var id = await SendThroughRouteAsync(admin, client, "hooks", target, """{ "webhook": "passthrough" }""");
            for (var i = 0; i < 100 && !await HeldAsync(id); i++)
            {
                await Task.Delay(100, Ct);
            }
            Assert.True(await HeldAsync(id), "the dispatcher never held the delivery");

            // The request's expiry passes while the gate still says inactive.
            await using (var db = _app.Database.Context())
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE notifications.requests SET expires_at = now() - interval '1 minute'", Ct);
            }
            var expired = Assert.Single((await DeliveredAsync(client, id)).Deliveries);
            Assert.Equal(DeliveryStatus.Expired, expired.Status);
            Assert.False(await HeldAsync(id));
            Assert.Empty(receiver.Requests);
        }

        /// <summary>Whether a delivery in this test's own database is held (the other test's delivery is final).</summary>
        private async Task<bool> HeldAsync(Guid notificationId)
        {
            await using var db = _app.Database.Context();
            return await db.Deliveries.AsNoTracking().AnyAsync(d => d.TenantHeld, Ct);
        }
    }
}
