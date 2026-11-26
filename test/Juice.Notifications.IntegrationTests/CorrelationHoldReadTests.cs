using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// A correlation's hold (phase-5-plan p5-d3) as every read shows it: the producer's <c>GET</c>, the tenant admin's <c>?application=</c>
    /// <c>GET</c> and the tenant's list, each field under its own name (they are all text or times, so a swap would compile).
    /// </summary>
    public sealed class CorrelationHoldReadTests(ProducerApp fixture) : IClassFixture<ProducerApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string Tenant = $"/api/v1/tenants/{ProducerApp.Tenant}";

        private static async Task<JsonElement> JsonAsync(HttpClient client, string path)
        {
            var response = await client.GetAsync(path, Ct);
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        }

        [Fact]
        public async Task The_hold_fields_are_read_back_under_their_own_names_by_the_producer_the_admin_and_the_list()
        {
            LocalStack.RequireAvailable();
            var key = "alert:" + Guid.NewGuid().ToString("N");
            var heldAt = new DateTimeOffset(2026, 11, 26, 8, 30, 15, TimeSpan.Zero);
            await using (var db = fixture.App.Database.Context())
            {
                var app = await db.Applications.SingleAsync(a => a.Key == ProducerApp.Key, Ct);
                await Correlations.CorrelationBook.ForRequestAsync(db, ProducerApp.Tenant, app.Id, key, DateTimeOffset.UtcNow, Ct);
                await db.SaveChangesAsync(Ct);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE notifications.correlations SET held_at = {heldAt}, hold_reason = 'silence:7', held_by = 'obs-operator'
                    WHERE tenant_id = {ProducerApp.Tenant} AND key = {key}
                    """, Ct);
            }
            var path = $"{Tenant}/correlations/{Uri.EscapeDataString(key)}";

            using var producer = await fixture.ProducerAsync();
            using var admin = fixture.App.Client(await LocalStack.TenantAdminTokenAsync());
            foreach (var read in new[] { await JsonAsync(producer, path), await JsonAsync(admin, $"{path}?application={ProducerApp.Key}") })
            {
                Assert.Equal(heldAt, read.GetProperty("heldAt").GetDateTimeOffset());
                Assert.Equal("silence:7", read.GetProperty("holdReason").GetString());
                Assert.Equal("obs-operator", read.GetProperty("heldBy").GetString());
                var typed = read.Deserialize<CorrelationResponse>(JsonSerializerOptions.Web)!;
                Assert.Equal((heldAt, "silence:7", "obs-operator"), (typed.HeldAt!.Value, typed.HoldReason, typed.HeldBy));
            }

            // The admin who leaves the application out is told it's missing (#14 review), as when embedded; a producer reads its own without.
            using (var missing = await admin.GetAsync(path, Ct))
            {
                var problem = await missing.Content.ReadFromJsonAsync<JsonElement>(Ct);
                Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest, "/application"),
                    (missing.StatusCode, problem.GetProperty("code").GetString(), problem.GetProperty("errors")[0].GetProperty("pointer").GetString()));
            }

            var page = await JsonAsync(admin, $"{Tenant}/correlations?key={Uri.EscapeDataString(key)}");
            var item = Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.Equal(heldAt, item.GetProperty("heldAt").GetDateTimeOffset());
        }
    }
}
