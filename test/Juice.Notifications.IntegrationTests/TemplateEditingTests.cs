using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    public sealed class TemplateApp : IAsyncLifetime
    {
        public ScriptedProvider Hooks { get; } = new(Channels.Webhook, "tpl-hook");

        public NotificationsApp App { get; }

        public TemplateApp() => App = new(host => host.ConfigureTestServices(services => services.AddSingleton<INotificationProvider>(Hooks)));

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
    /// Tenants' templates (design D22, phase-4-plan I45, M32): a tenant admin overrides a type's template for a channel, checked as declarations
    /// are, previews it with sample or given data, and its deliveries render with it until the override is removed.
    /// </summary>
    public sealed class TemplateEditingTests(TemplateApp fixture) : IClassFixture<TemplateApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string Api = $"/api/v1/tenants/{ProducerApp.Tenant}";
        private NotificationsApp App => fixture.App;

        private async Task<string> DeliveredContentAsync(HttpClient producer, string type, int n)
        {
            var accepted = await (await producer.PostAsJsonAsync($"{Api}/notifications", new SendNotificationBody(type, null, Guid.NewGuid().ToString("N"), null, null,
                null, null, null, JsonSerializer.SerializeToElement(new { build = n, branch = "main" }), null, null), Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            for (var i = 0; i < 150; i++)
            {
                await using var db = App.Database.Context();
                var delivery = await db.Deliveries.AsNoTracking().Where(d => db.DeliveryItems.Any(it => it.DeliveryId == d.Id && it.RequestId == accepted!.Id))
                    .FirstOrDefaultAsync(Ct);
                if (delivery is { Status: DeliveryStatus.Accepted })
                {
                    return JsonDocument.Parse(delivery.Content).RootElement.GetProperty("Body").GetString()!;
                }
                await Task.Delay(100, Ct);
            }
            throw new TimeoutException("The delivery never went out.");
        }

        [Fact]
        public async Task A_tenant_overrides_previews_and_removes_a_types_template()
        {
            LocalStack.RequireAvailable();
            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            using var producer = App.Client(await LocalStack.ProducerTokenAsync());
            var type = $"{ProducerApp.Key}.t{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            (await producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(type, "Build", "ci", null, null, null, null,
                JsonElement.Parse("""{ "type": "object", "properties": { "build": { "type": "integer", "examples": [42] }, "branch": { "type": "string" } } }"""),
                null, null, null, JsonElement.Parse("""{ "webhook": "app: build {{ data.build }}" }""")), Ct)).EnsureSuccessStatusCode();
            (await admin.PutAsJsonAsync($"{Api}/integrations/tpl", new PutIntegrationBody("webhook", "tpl-hook", null, null, null, true), Ct)).EnsureSuccessStatusCode();
            (await admin.PutAsJsonAsync($"{Api}/integrations/tpl/targets/tpl-ops", new PutTargetBody("https://ops.example.com/hook", null, true), Ct)).EnsureSuccessStatusCode();
            (await admin.PutAsJsonAsync($"{Api}/routes/tpl-ops", new PutRouteBody(JsonSerializer.SerializeToElement(new { type }), ["tpl-ops"], null), Ct)).EnsureSuccessStatusCode();

            var types = await admin.GetFromJsonAsync<List<TenantTypeResponse>>($"{Api}/types", Ct);
            var listed = Assert.Single(types!, t => t.Key == type);
            Assert.Contains(listed.Channels, c => c is { Channel: "webhook", Declared: true, Overridden: false });

            // Checked as a declaration's: a template the sandbox refuses, or one that doesn't parse, is refused with the field.
            var refused = await admin.PutAsJsonAsync($"{Api}/types/{type}/templates/webhook", new PutTemplateBody("{{ include 'x' }}", null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("/body", await refused.Content.ReadAsStringAsync(Ct));
            Assert.Equal(HttpStatusCode.Forbidden, (await producer.PutAsJsonAsync($"{Api}/types/{type}/templates/webhook", new PutTemplateBody("x", null), Ct)).StatusCode);

            // A preview with the schema's sample data, and with given data; nothing is stored.
            var sample = await (await admin.PostAsJsonAsync($"{Api}/templates/preview", new PreviewBody(type, "webhook", "tenant: build {{ data.build }} on {{ data.branch }}", null), Ct))
                .Content.ReadFromJsonAsync<PreviewResponse>(Ct);
            Assert.Equal("tenant: build 42 on branch", sample!.Content!.Value.GetProperty("Body").GetString());
            var given = await (await admin.PostAsJsonAsync($"{Api}/templates/preview", new PreviewBody(type, "webhook", "{{ data.build | math.plus 1 }}",
                JsonSerializer.SerializeToElement(new { build = 7 })), Ct)).Content.ReadFromJsonAsync<PreviewResponse>(Ct);
            Assert.Equal("8", given!.Content!.Value.GetProperty("Body").GetString());

            var saved = await (await admin.PutAsJsonAsync($"{Api}/types/{type}/templates/webhook", new PutTemplateBody("tenant: build {{ data.build }}", null), Ct))
                .Content.ReadFromJsonAsync<TenantTemplateResponse>(Ct);
            Assert.Equal(("app: build {{ data.build }}", "tenant: build {{ data.build }}", 1), (saved!.Application, saved.Tenant, saved.TenantVersion));
            Assert.Equal("tenant: build 5", await DeliveredContentAsync(producer, type, 5));

            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Api}/types/{type}/templates/webhook", Ct)).StatusCode);
            Assert.Equal("app: build 6", await DeliveredContentAsync(producer, type, 6));
            var after = await admin.GetFromJsonAsync<TenantTemplateResponse>($"{Api}/types/{type}/templates/webhook", Ct);
            Assert.Null(after!.Tenant);
        }
    }
}
