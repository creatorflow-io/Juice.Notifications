using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The targets a route names are rows the database checks (deferred #8): a target can't go while a route names it, whatever the code does,
    /// so a delete racing a route that starts naming the target fails one of them instead of leaving the route naming nothing.
    /// </summary>
    public sealed class RouteTargetTests(ProducerApp producer) : IClassFixture<ProducerApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string Url = $"/api/v1/tenants/{ProducerApp.Tenant}";

        private static async Task Ok(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        }

        [Fact]
        public async Task A_route_keeps_its_targets_rows_in_step()
        {
            LocalStack.RequireAvailable();
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var (integration, a, b, route) = ($"m{suffix}", $"a{suffix}", $"b{suffix}", $"r{suffix}");
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/{integration}", new PutIntegrationBody("email", "smtp",
                JsonElement.Parse("""{ "server": "local", "from": "notifications@juice.local" }"""), null, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/{integration}/targets/{a}", new PutTargetBody("a@example.com", null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/{integration}/targets/{b}", new PutTargetBody("b@example.com", null, null), Ct));

            await Ok(admin.PutAsJsonAsync($"{Url}/routes/{route}", new PutRouteBody(JsonElement.Parse("{}"), [a, b], null), Ct));
            Assert.Equal([a, b], await NamedAsync(route));
            await Ok(admin.PutAsJsonAsync($"{Url}/routes/{route}", new PutRouteBody(null, [b], null), Ct));
            Assert.Equal([b], await NamedAsync(route));

            // Around the API's own check, the database refuses it.
            await using var connection = new NpgsqlConnection(producer.App.Database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var delete = new NpgsqlCommand("DELETE FROM notifications.integration_targets WHERE tenant_id = @t AND key = @k", connection);
            delete.Parameters.AddWithValue("t", ProducerApp.Tenant);
            delete.Parameters.AddWithValue("k", b);
            var refused = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(Ct));
            Assert.Equal((PostgresErrorCodes.ForeignKeyViolation, Constraints.RouteTargetTarget), (refused.SqlState, refused.ConstraintName));

            // Deleting the route lets the target go.
            await Ok(admin.DeleteAsync($"{Url}/routes/{route}", Ct));
            await Ok(admin.DeleteAsync($"{Url}/integrations/{integration}/targets/{b}", Ct));
        }

        [Fact]
        public async Task Missing_rows_are_repaired_and_a_late_target_is_held_like_any_other()
        {
            LocalStack.RequireAvailable();
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var (integration, a, late, route, legacy) = ($"m{suffix}", $"a{suffix}", $"l{suffix}", $"r{suffix}", $"g{suffix}");
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/{integration}", new PutIntegrationBody("email", "smtp",
                JsonElement.Parse("""{ "server": "local", "from": "notifications@juice.local" }"""), null, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/{integration}/targets/{a}", new PutTargetBody("a@example.com", null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Url}/routes/{route}", new PutRouteBody(JsonElement.Parse("{}"), [a], null), Ct));

            // A row gone missing (as the migration leaves a route naming a key with no target): putting the same route again restores it.
            await using (var db = producer.App.Database.Context())
            {
                await db.RouteTargets.Where(x => x.TenantId == ProducerApp.Tenant).Join(db.Routes.Where(r => r.Key == route),
                    x => new { x.TenantId, Id = x.RouteId }, r => new { r.TenantId, r.Id }, (x, _) => x).ExecuteDeleteAsync(Ct);
            }
            Assert.Empty(await NamedAsync(route));
            await Ok(admin.PutAsJsonAsync($"{Url}/routes/{route}", new PutRouteBody(null, [a], null), Ct));
            Assert.Equal([a], await NamedAsync(route));

            // A route stored before route_targets, naming a key that had no target then: the target, once created, is held by it.
            await using (var db = producer.App.Database.Context())
            {
                db.Routes.Add(new Route { TenantId = ProducerApp.Tenant, Key = legacy, Match = "{}", Targets = [late], DefinitionHash = "h", UpdatedAt = Seed.Now });
                await db.SaveChangesAsync(Ct);
            }
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/{integration}/targets/{late}", new PutTargetBody("late@example.com", null, null), Ct));
            Assert.Equal([late], await NamedAsync(legacy));
            var refused = await admin.DeleteAsync($"{Url}/integrations/{integration}/targets/{late}", Ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains(legacy, await refused.Content.ReadAsStringAsync(Ct));

            // A replica from before route_targets takes a target out of a route and leaves its row: the target can still go.
            await using (var connection = new NpgsqlConnection(producer.App.Database.ConnectionString))
            {
                await connection.OpenAsync(Ct);
                await using var update = new NpgsqlCommand("UPDATE notifications.routes SET targets = '{}' WHERE tenant_id = @t AND key = @k", connection);
                update.Parameters.AddWithValue("t", ProducerApp.Tenant);
                update.Parameters.AddWithValue("k", legacy);
                await update.ExecuteNonQueryAsync(Ct);
            }
            Assert.Equal([late], await NamedAsync(legacy));
            await Ok(admin.DeleteAsync($"{Url}/integrations/{integration}/targets/{late}", Ct));

            // The same for a whole integration: its targets go with it, stale rows and all.
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/{integration}/targets/{late}", new PutTargetBody("late@example.com", null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Url}/routes/{legacy}", new PutRouteBody(null, [a, late], null), Ct));
            await Ok(admin.DeleteAsync($"{Url}/routes/{route}", Ct));
            await using (var connection = new NpgsqlConnection(producer.App.Database.ConnectionString))
            {
                await connection.OpenAsync(Ct);
                await using var update = new NpgsqlCommand("UPDATE notifications.routes SET targets = '{}' WHERE tenant_id = @t AND key = @k", connection);
                update.Parameters.AddWithValue("t", ProducerApp.Tenant);
                update.Parameters.AddWithValue("k", legacy);
                await update.ExecuteNonQueryAsync(Ct);
            }
            Assert.Equal([a, late], await NamedAsync(legacy));
            await Ok(admin.DeleteAsync($"{Url}/integrations/{integration}", Ct));
            Assert.Empty(await NamedAsync(legacy));
        }

        /// <summary>
        /// Deferred #24: a route Put while a target delete releases the route's stale row (one for a target the route no longer names). The
        /// release (here by hand, as <c>ReleaseStaleRowsAsync</c> does it) holds its delete open while the Put runs, which stands for a release
        /// landing between the Put's read and its save. Before, the Put deleted the stale row too: it waited for the release, found the row gone
        /// and answered 409. Now the stale row is the release's alone.
        /// </summary>
        [Fact]
        public async Task A_route_put_leaves_a_stale_row_to_its_release_instead_of_failing()
        {
            LocalStack.RequireAvailable();
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var (integration, a, b, route) = ($"m{suffix}", $"a{suffix}", $"b{suffix}", $"r{suffix}");
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/{integration}", new PutIntegrationBody("email", "smtp",
                JsonElement.Parse("""{ "server": "local", "from": "notifications@juice.local" }"""), null, null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/{integration}/targets/{a}", new PutTargetBody("a@example.com", null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Url}/integrations/{integration}/targets/{b}", new PutTargetBody("b@example.com", null, null), Ct));
            await Ok(admin.PutAsJsonAsync($"{Url}/routes/{route}", new PutRouteBody(JsonElement.Parse("{}"), [a, b], null), Ct));

            await using var connection = new NpgsqlConnection(producer.App.Database.ConnectionString);
            await connection.OpenAsync(Ct);
            // A replica from before route_targets takes b out of the route and leaves its row.
            await using (var stale = new NpgsqlCommand("UPDATE notifications.routes SET targets = @targets WHERE tenant_id = @t AND key = @k", connection))
            {
                stale.Parameters.AddWithValue("targets", new[] { a });
                stale.Parameters.AddWithValue("t", ProducerApp.Tenant);
                stale.Parameters.AddWithValue("k", route);
                await stale.ExecuteNonQueryAsync(Ct);
            }

            await using var release = await connection.BeginTransactionAsync(Ct);
            await using (var command = new NpgsqlCommand("""
                WITH consulted AS (SELECT id FROM notifications.routes WHERE tenant_id = @t AND key = @k FOR SHARE)
                DELETE FROM notifications.route_targets rt USING consulted r, notifications.integration_targets it
                WHERE rt.tenant_id = @t AND rt.route_id = r.id AND it.tenant_id = rt.tenant_id AND it.id = rt.target_id AND it.key = @b
                """, connection, release))
            {
                command.Parameters.AddWithValue("t", ProducerApp.Tenant);
                command.Parameters.AddWithValue("k", route);
                command.Parameters.AddWithValue("b", b);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
            }

            var put = admin.PutAsJsonAsync($"{Url}/routes/{route}", new PutRouteBody(null, [a], null), Ct);
            await Task.Delay(TimeSpan.FromSeconds(1), Ct); // before: the Put read the row, then waited at its own delete of it
            await release.CommitAsync(Ct);

            await Ok(put);
            Assert.Equal([a], await NamedAsync(route));
            await Ok(admin.DeleteAsync($"{Url}/integrations/{integration}/targets/{b}", Ct));
        }

        private async Task<List<string>> NamedAsync(string route)
        {
            await using var db = producer.App.Database.Context();
            return await (from r in db.Routes
                          join rt in db.RouteTargets on new { r.TenantId, RouteId = r.Id } equals new { rt.TenantId, rt.RouteId }
                          join t in db.IntegrationTargets on new { rt.TenantId, Id = rt.TargetId } equals new { t.TenantId, t.Id }
                          where r.TenantId == ProducerApp.Tenant && r.Key == route
                          orderby t.Key
                          select t.Key).ToListAsync(Ct);
        }
    }
}
