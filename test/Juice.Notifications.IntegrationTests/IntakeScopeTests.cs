using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Delivering;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The keys that make a request or a delivery the same one (phase-1-plan M9 mutation pass): an idempotency key belongs to one application in one
    /// tenant, and a destination key names exactly one destination. Added after mutation testing found neither checked.
    /// </summary>
    public sealed class IntakeScopeTests(ProducerApp producer) : IClassFixture<ProducerApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string Url = $"/api/v1/tenants/{ProducerApp.Tenant}";

        private static async Task Ok(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        }

        private static async Task<string> DeclareAsync(HttpClient producerClient)
        {
            var key = $"{ProducerApp.Key}.s{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            await Ok(producerClient.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(key, "Scoped", "things", null, null, null,
                true, JsonElement.Parse("""{ "type": "object" }"""), null, null, null, JsonElement.Parse("""{ "email": "Hello" }""")), Ct));
            return key;
        }

        private static SendNotificationBody Send(string type, string key, List<RecipientBody>? recipients = null)
            => new(type, null, key, null, recipients, null, null, null, JsonElement.Parse("{}"), null, null);

        [Fact]
        public async Task An_idempotency_key_is_one_applications_in_one_tenant()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var type = await DeclareAsync(client);
            var key = "shared-" + Guid.NewGuid().ToString("N");
            var elsewhere = Guid.CreateVersion7();

            // The same key already used by another application in this tenant, and by this application in another tenant.
            Guid otherAppsRequest, otherTenantsRequest;
            await using (var db = producer.App.Database.Context())
            {
                var sample = await db.Applications.SingleAsync(a => a.Key == ProducerApp.Key, Ct);
                var sampleType = await db.Types.SingleAsync(t => t.ApplicationId == sample.Id && t.Key == type, Ct);
                var other = Seed.Application("o" + Guid.NewGuid().ToString("N")[..10]);
                var otherType = Seed.Type(other.Id, other.Key + ".thing");
                db.AddRange(other, otherType,
                    new ApplicationTenant { ApplicationId = other.Id, TenantId = ProducerApp.Tenant, LinkedAt = Seed.Now },
                    new ApplicationTenant { ApplicationId = sample.Id, TenantId = elsewhere, LinkedAt = Seed.Now });
                var a = Seed.Request(ProducerApp.Tenant, other.Id, otherType.Id, key);
                var b = Seed.Request(elsewhere, sample.Id, sampleType.Id, key);
                db.AddRange(a, b);
                await db.SaveChangesAsync(Ct);
                (otherAppsRequest, otherTenantsRequest) = (a.Id, b.Id);
            }

            var first = await client.PostAsJsonAsync($"{Url}/notifications", Send(type, key), Ct);
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
            var accepted = (await first.Content.ReadFromJsonAsync<AcceptedResponse>(Ct))!;
            Assert.DoesNotContain(accepted.Id, new[] { otherAppsRequest, otherTenantsRequest });

            // Within its own scope the key is the same request.
            var again = await client.PostAsJsonAsync($"{Url}/notifications", Send(type, key), Ct);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(accepted.Id, (await again.Content.ReadFromJsonAsync<AcceptedResponse>(Ct))!.Id);
        }

        [Fact]
        public async Task Each_destination_has_a_key_of_its_own()
        {
            LocalStack.RequireAvailable();
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            using var client = await producer.ProducerAsync();
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var label = "scope-" + suffix;
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/m{suffix}", new PutIntegrationBody("email", "smtp",
                JsonElement.Parse("""{ "server": "local", "from": "notifications@juice.local" }"""), null, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/m{suffix}/targets/a{suffix}", new PutTargetBody("a@example.com", null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/m{suffix}/targets/b{suffix}", new PutTargetBody("b@example.com", null, null), Ct));
            // Only this test's requests: a label no other test sends.
            await Ok(admin.PutAsJsonAsync($"{Url}/routes/r{suffix}", new PutRouteBody(JsonElement.Parse($$"""{ "labels": { "scope": "{{label}}" } }"""),
                [$"a{suffix}", $"b{suffix}"], null), Ct));
            var type = await DeclareAsync(client);

            var body = Send(type, Guid.NewGuid().ToString("N"), [new RecipientBody("route", null, null), new RecipientBody("address", "email", "x@example.com")])
                with { Labels = new Dictionary<string, string> { ["scope"] = label } };
            var sent = await client.PostAsJsonAsync($"{Url}/notifications", body, Ct);
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
            var id = (await sent.Content.ReadFromJsonAsync<AcceptedResponse>(Ct))!.Id;

            Guid targetA, targetB;
            await using (var db = producer.App.Database.Context())
            {
                targetA = (await db.IntegrationTargets.SingleAsync(t => t.TenantId == ProducerApp.Tenant && t.Key == $"a{suffix}", Ct)).Id;
                targetB = (await db.IntegrationTargets.SingleAsync(t => t.TenantId == ProducerApp.Tenant && t.Key == $"b{suffix}", Ct)).Id;
            }
            var expected = new[] { DestinationKeys.External("email", "x@example.com"), DestinationKeys.Target(targetA), DestinationKeys.Target(targetB) }.Order().ToList();
            Assert.Equal(["ext:email:x@example.com", $"it:{targetA}", $"it:{targetB}"], expected);

            for (var waited = 0; ; waited++)
            {
                await using var db = producer.App.Database.Context();
                var items = await db.DeliveryItems.Where(i => i.TenantId == ProducerApp.Tenant && i.RequestId == id).ToListAsync(Ct);
                if (items.Count >= 3 || waited >= 100)
                {
                    Assert.Equal(expected, items.Select(i => i.DestinationKey).Order());
                    var deliveries = await db.Deliveries.Where(d => d.TenantId == ProducerApp.Tenant && items.Select(i => i.DeliveryId).Contains(d.Id)).ToListAsync(Ct);
                    Assert.Equal(expected, deliveries.Select(d => d.DestinationKey).Order());
                    break;
                }
                await Task.Delay(100, Ct);
            }
        }
    }
}
