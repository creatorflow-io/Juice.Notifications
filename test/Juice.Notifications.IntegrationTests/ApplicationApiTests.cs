using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Registering applications and linking tenants (phase-1-plan M1), with real Keycloak tokens.</summary>
    public sealed class ApplicationApiTests(NotificationsApp app) : IClassFixture<NotificationsApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string NewKey() => "app-" + Guid.NewGuid().ToString("N")[..10];

        private static async Task<string> CodeAsync(HttpResponseMessage response)
            => (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString()!;

        [Fact]
        public async Task Only_a_platform_admin_manages_applications()
        {
            LocalStack.RequireAvailable();
            using var anonymous = app.Client();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/applications", Ct)).StatusCode);

            using var producer = app.Client(await LocalStack.ProducerTokenAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await producer.GetAsync("/api/v1/applications", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await producer.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(NewKey(), "x", "x"), Ct)).StatusCode);
        }

        [Fact]
        public async Task An_admin_registers_an_application_and_links_and_unlinks_a_tenant()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var key = NewKey();
            var created = await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(key, " Observability ", key + "-client"), Ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal($"/api/v1/applications/{key}", created.Headers.Location?.ToString());
            var body = await created.Content.ReadFromJsonAsync<ApplicationResponse>(Ct);
            Assert.Equal("Observability", body!.Name);
            Assert.True(body.Enabled);
            Assert.Empty(body.Tenants);

            var tenant = Guid.CreateVersion7();
            var linked = await admin.PutAsync($"/api/v1/applications/{key}/tenants/{tenant}", null, Ct);
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
            Assert.Equal([tenant], (await linked.Content.ReadFromJsonAsync<ApplicationResponse>(Ct))!.Tenants);
            // Linking again changes nothing.
            Assert.Equal([tenant], (await (await admin.PutAsync($"/api/v1/applications/{key}/tenants/{tenant}", null, Ct))
                .Content.ReadFromJsonAsync<ApplicationResponse>(Ct))!.Tenants);

            var listed = await admin.GetFromJsonAsync<List<ApplicationResponse>>("/api/v1/applications", Ct);
            Assert.Contains(listed!, a => a.Key == key && a.Tenants.SequenceEqual([tenant]));

            var unlinked = await admin.DeleteAsync($"/api/v1/applications/{key}/tenants/{tenant}", Ct);
            Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
            Assert.Empty((await admin.GetFromJsonAsync<ApplicationResponse>($"/api/v1/applications/{key}", Ct))!.Tenants);
        }

        [Fact]
        public async Task Keys_and_client_ids_are_unique_and_keys_are_checked()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var key = NewKey();
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(key, "A", key + "-c"), Ct)).StatusCode);

            var sameKey = await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(key, "B", key + "-other"), Ct);
            Assert.Equal(HttpStatusCode.Conflict, sameKey.StatusCode);
            Assert.Equal(ErrorCodes.ApplicationExists, await CodeAsync(sameKey));

            var sameClient = await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(NewKey(), "C", key + "-c"), Ct);
            Assert.Equal(HttpStatusCode.Conflict, sameClient.StatusCode);
            Assert.Equal(ErrorCodes.ClientIdInUse, await CodeAsync(sameClient));

            foreach (var bad in new[] { "A", "1abc", "has space", "has.dot", "x" })
            {
                var invalid = await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(bad, "D", "d-" + Guid.NewGuid()), Ct);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Equal(ErrorCodes.InvalidApplication, await CodeAsync(invalid));
            }

            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/applications/" + NewKey(), Ct)).StatusCode);
        }

        [Fact]
        public async Task Updating_changes_only_the_fields_given()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var key = NewKey();
            await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(key, "Before", key + "-c"), Ct);

            var disabled = await admin.PutAsJsonAsync($"/api/v1/applications/{key}", new UpdateApplicationBody(null, null, false), Ct);
            var view = await disabled.Content.ReadFromJsonAsync<ApplicationResponse>(Ct);
            Assert.Equal(("Before", key + "-c", false), (view!.Name, view.ClientId, view.Enabled));

            var renamed = await admin.PutAsJsonAsync($"/api/v1/applications/{key}", new UpdateApplicationBody("After", key + "-c2", null), Ct);
            view = await renamed.Content.ReadFromJsonAsync<ApplicationResponse>(Ct);
            Assert.Equal(("After", key + "-c2", false), (view!.Name, view.ClientId, view.Enabled));
        }

        [Fact]
        public async Task A_tenant_the_application_has_sent_for_stays_linked()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var key = NewKey();
            var tenant = Guid.CreateVersion7();
            var body = await (await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(key, "A", key + "-c"), Ct))
                .Content.ReadFromJsonAsync<ApplicationResponse>(Ct);
            await admin.PutAsync($"/api/v1/applications/{key}/tenants/{tenant}", null, Ct);

            // A request as intake (M2) will write it.
            await using (var db = app.Database.Context())
            {
                var type = Seed.Type(body!.Id, key + ".thing");
                db.Types.Add(type);
                db.Requests.Add(Seed.Request(tenant, body.Id, type.Id));
                await db.SaveChangesAsync(Ct);
            }

            var refused = await admin.DeleteAsync($"/api/v1/applications/{key}/tenants/{tenant}", Ct);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal(ErrorCodes.TenantInUse, await CodeAsync(refused));
            await using var check = app.Database.Context();
            Assert.True(await check.ApplicationTenants.AnyAsync(t => t.ApplicationId == body.Id && t.TenantId == tenant, Ct));
        }

        [Fact]
        public async Task Linking_the_same_tenant_at_once_succeeds_every_time()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var key = NewKey();
            await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(key, "Racing", key + "-c"), Ct);
            var tenant = Guid.CreateVersion7();

            var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => admin.PutAsync($"/api/v1/applications/{key}/tenants/{tenant}", null, Ct)));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            await using var check = app.Database.Context();
            Assert.Equal(1, await check.ApplicationTenants.CountAsync(t => t.TenantId == tenant, Ct));
        }

        [Fact]
        public async Task An_update_that_changes_nothing_leaves_UpdatedAt_alone()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var key = NewKey();
            var created = await (await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(key, "Same", key + "-c"), Ct))
                .Content.ReadFromJsonAsync<ApplicationResponse>(Ct);

            var empty = await (await admin.PutAsJsonAsync($"/api/v1/applications/{key}", new UpdateApplicationBody(null, null, null), Ct))
                .Content.ReadFromJsonAsync<ApplicationResponse>(Ct);
            var same = await (await admin.PutAsJsonAsync($"/api/v1/applications/{key}", new UpdateApplicationBody(" Same ", key + "-c", true), Ct))
                .Content.ReadFromJsonAsync<ApplicationResponse>(Ct);
            var changed = await (await admin.PutAsJsonAsync($"/api/v1/applications/{key}", new UpdateApplicationBody(null, null, false), Ct))
                .Content.ReadFromJsonAsync<ApplicationResponse>(Ct);

            Assert.Equal(created!.UpdatedAt, empty!.UpdatedAt);
            Assert.Equal(created.UpdatedAt, same!.UpdatedAt);
            Assert.True(changed!.UpdatedAt > created.UpdatedAt);
        }

        [Fact]
        public async Task The_callers_correlation_id_is_kept_and_echoed()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/applications");
            request.Headers.Add("x-correlation-id", "trace-abc-123");
            var given = await admin.SendAsync(request, Ct);
            Assert.Equal("trace-abc-123", Assert.Single(given.Headers.GetValues("x-correlation-id")));

            var fresh = await admin.GetAsync("/api/v1/applications", Ct);
            Assert.False(string.IsNullOrWhiteSpace(Assert.Single(fresh.Headers.GetValues("x-correlation-id"))));

            // Error responses keep it too.
            using var anonymous = app.Client();
            using var refused = new HttpRequestMessage(HttpMethod.Get, "/api/v1/applications");
            refused.Headers.Add("x-correlation-id", "trace-refused");
            var unauthorized = await anonymous.SendAsync(refused, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal("trace-refused", Assert.Single(unauthorized.Headers.GetValues("x-correlation-id")));
        }

        [Fact]
        public async Task An_empty_tenant_id_is_refused_for_link_and_unlink_alike()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var key = NewKey();
            await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(key, "Empty", key + "-c"), Ct);
            var url = $"/api/v1/applications/{key}/tenants/{Guid.Empty}";

            var link = await admin.PutAsync(url, null, Ct);
            var unlink = await admin.DeleteAsync(url, Ct);

            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidTenant), (link.StatusCode, await CodeAsync(link)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidTenant), (unlink.StatusCode, await CodeAsync(unlink)));
        }

        [Fact]
        public async Task Unlinking_the_same_tenant_at_once_succeeds_every_time()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var key = NewKey();
            await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(key, "Unlinking", key + "-c"), Ct);
            for (var round = 0; round < 5; round++)
            {
                var tenant = Guid.CreateVersion7();
                await admin.PutAsync($"/api/v1/applications/{key}/tenants/{tenant}", null, Ct);

                var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => admin.DeleteAsync($"/api/v1/applications/{key}/tenants/{tenant}", Ct)));

                Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
                await using var check = app.Database.Context();
                Assert.False(await check.ApplicationTenants.AnyAsync(t => t.TenantId == tenant, Ct));
            }
        }

        /// <summary>
        /// The combination: links and unlinks of one tenant racing each other. Each race is answered from its constraint (200, the state read
        /// fresh); PostgreSQL may abort one of a deadlocked pair (409 concurrent_change). Never a server error, and the stored state is what a
        /// read shows.
        /// </summary>
        [Fact]
        public async Task Linking_and_unlinking_one_tenant_at_once_never_fails()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var key = NewKey();
            await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(key, "Mixed", key + "-c"), Ct);
            var tenant = Guid.CreateVersion7();
            var url = $"/api/v1/applications/{key}/tenants/{tenant}";
            for (var round = 0; round < 5; round++)
            {
                var responses = await Task.WhenAll(Enumerable.Range(0, 12)
                    .Select(i => i % 2 == 0 ? admin.PutAsync(url, null, Ct) : admin.DeleteAsync(url, Ct)));

                foreach (var r in responses)
                {
                    if (r.StatusCode != HttpStatusCode.OK)
                    {
                        Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ConcurrentChange), (r.StatusCode, await CodeAsync(r)));
                    }
                }
                Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
                await using var check = app.Database.Context();
                var stored = await check.ApplicationTenants.CountAsync(t => t.TenantId == tenant, Ct);
                var shown = (await admin.GetFromJsonAsync<ApplicationResponse>($"/api/v1/applications/{key}", Ct))!.Tenants.Count(t => t == tenant);
                Assert.Equal(stored, shown);
                Assert.InRange(stored, 0, 1);
            }
        }

        /// <summary>
        /// A request is accepted (not yet committed) while the unlink runs: the unlink's check sees no request, its delete waits on the request's
        /// lock, and fails on the reference once the request commits. The answer is tenant_in_use, and the link stays.
        /// </summary>
        [Fact]
        public async Task Unlinking_while_a_request_for_the_tenant_is_accepted_answers_tenant_in_use()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var key = NewKey();
            var created = await (await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(key, "Busy", key + "-c"), Ct))
                .Content.ReadFromJsonAsync<ApplicationResponse>(Ct);
            var tenant = Guid.CreateVersion7();
            await admin.PutAsync($"/api/v1/applications/{key}/tenants/{tenant}", null, Ct);
            await using (var setup = app.Database.Context())
            {
                setup.Add(Seed.Type(created!.Id, key + ".thing"));
                await setup.SaveChangesAsync(Ct);
            }

            await using var producer = app.Database.Context();
            var typeId = await producer.Types.Where(t => t.ApplicationId == created!.Id).Select(t => t.Id).SingleAsync(Ct);
            await using var accepting = await producer.Database.BeginTransactionAsync(Ct);
            producer.Add(Seed.Request(tenant, created!.Id, typeId, "racing-unlink"));
            await producer.SaveChangesAsync(Ct);

            var unlink = admin.DeleteAsync($"/api/v1/applications/{key}/tenants/{tenant}", Ct);
            // Wait until the unlink's DELETE is actually blocked on the request's lock, so the commit below drives the reference failure.
            await WaitForBlockedDeleteAsync(app.Database.ConnectionString);
            Assert.False(unlink.IsCompleted);
            await accepting.CommitAsync(Ct);
            var refused = await unlink;

            Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.TenantInUse), (refused.StatusCode, await CodeAsync(refused)));
            await using var check = app.Database.Context();
            Assert.True(await check.ApplicationTenants.AnyAsync(t => t.ApplicationId == created.Id && t.TenantId == tenant, Ct));
        }

        /// <summary>
        /// Polls PostgreSQL until a DELETE on application_tenants waits on a lock; fails after 10 seconds. It uses its own connection, outside any
        /// transaction: PostgreSQL keeps one activity snapshot for a whole transaction, so polling inside one would never see the change.
        /// </summary>
        private static async Task WaitForBlockedDeleteAsync(string connectionString)
        {
            await using var connection = new Npgsql.NpgsqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(10); waited += TimeSpan.FromMilliseconds(50))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT count(*) FROM pg_stat_activity
                    WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%DELETE FROM%application_tenants%'
                    """;
                if ((long)(await command.ExecuteScalarAsync(Ct))! > 0)
                {
                    return;
                }
                await Task.Delay(50, Ct);
            }
            Assert.Fail("The unlink never blocked on the uncommitted request.");
        }

        /// <summary>
        /// The same body sent twice at once (a double submit, a client retry): both values clash, and PostgreSQL may report either index first.
        /// The losers get application_exists, as sending it again later would.
        /// </summary>
        [Fact]
        public async Task Sending_the_same_create_at_once_answers_application_exists_to_the_losers()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            for (var round = 0; round < 5; round++)
            {
                var key = NewKey();
                var body = new CreateApplicationBody(key, "Twice", key + "-c");

                var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => admin.PostAsJsonAsync("/api/v1/applications", body, Ct)));

                Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
                foreach (var lost in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
                {
                    Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ApplicationExists), (lost.StatusCode, await CodeAsync(lost)));
                }
            }
        }

        [Fact]
        public async Task Creating_one_key_at_once_answers_application_exists_to_the_losers()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var key = NewKey();

            var responses = await Task.WhenAll(Enumerable.Range(0, 6)
                .Select(i => admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(key, "Race", $"{key}-c{i}"), Ct)));

            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            foreach (var lost in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
            {
                Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ApplicationExists), (lost.StatusCode, await CodeAsync(lost)));
            }
        }

        [Fact]
        public async Task Creating_with_one_client_id_at_once_answers_client_id_in_use_to_the_losers()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var client = NewKey() + "-shared";

            var responses = await Task.WhenAll(Enumerable.Range(0, 6)
                .Select(_ => admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(NewKey(), "Race", client), Ct)));

            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            foreach (var lost in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
            {
                Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ClientIdInUse), (lost.StatusCode, await CodeAsync(lost)));
            }
        }

        [Theory]
        [InlineData("abc\u0001")]
        [InlineData("has space")]
        [InlineData("tab\there")]
        [InlineData("café")]
        public async Task An_unusable_correlation_id_is_replaced_not_echoed(string given)
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/applications");
            Assert.True(request.Headers.TryAddWithoutValidation("x-correlation-id", given));

            var response = await admin.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var echoed = Assert.Single(response.Headers.GetValues("x-correlation-id"));
            Assert.NotEqual(given, echoed);
            Assert.True(Guid.TryParse(echoed, out _));
        }

        [Fact]
        public async Task A_correlation_id_longer_than_128_characters_is_replaced()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            using var exact = new HttpRequestMessage(HttpMethod.Get, "/api/v1/applications");
            exact.Headers.Add("x-correlation-id", new string('a', 128));
            using var tooLong = new HttpRequestMessage(HttpMethod.Get, "/api/v1/applications");
            tooLong.Headers.Add("x-correlation-id", new string('a', 129));

            Assert.Equal(new string('a', 128), Assert.Single((await admin.SendAsync(exact, Ct)).Headers.GetValues("x-correlation-id")));
            Assert.True(Guid.TryParse(Assert.Single((await admin.SendAsync(tooLong, Ct)).Headers.GetValues("x-correlation-id")), out _));
        }

        [Fact]
        public async Task An_empty_tenant_id_is_a_400_even_for_an_unknown_application()
        {
            LocalStack.RequireAvailable();
            using var admin = app.Client(await LocalStack.AdminTokenAsync());
            var url = $"/api/v1/applications/{NewKey()}/tenants/{Guid.Empty}";
            var link = await admin.PutAsync(url, null, Ct);
            var unlink = await admin.DeleteAsync(url, Ct);
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidTenant), (link.StatusCode, await CodeAsync(link)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidTenant), (unlink.StatusCode, await CodeAsync(unlink)));
            // A valid id on an unknown application is still a 404.
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsync($"/api/v1/applications/{NewKey()}/tenants/{Guid.CreateVersion7()}", null, Ct)).StatusCode);
        }

        [Fact]
        public async Task Health_endpoints_answer_without_a_token()
        {
            LocalStack.RequireAvailable();
            using var anonymous = app.Client();
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/live", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/ready", Ct)).StatusCode);
        }
    }
}
