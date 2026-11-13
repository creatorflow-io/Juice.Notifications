using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Audit records (p1-09, design §13): changes through the API are recorded with who made them and the rows they changed; producers' requests
    /// are not. The host's records go to its recorder (<see cref="NotificationsApp.Audit"/>).
    /// </summary>
    public sealed class AuditTests(ProducerApp producer) : IClassFixture<ProducerApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string Url = $"/api/v1/tenants/{ProducerApp.Tenant}";

        private static readonly PutIntegrationBody Mail = new("email", "smtp", JsonElement.Parse("""{ "server": "local", "from": "notifications@juice.local" }"""),
            null, null, null);

        /// <summary>The records for a path, waiting briefly: a record is saved as the request ends, which may be just after the client has its answer.</summary>
        private async Task<IReadOnlyList<AuditRecorder.Saved>> RecordsAsync(Guid tenant, string pathSuffix, int atLeast = 1)
        {
            for (var waited = 0; waited < 50; waited++)
            {
                var found = producer.App.Audit.For(tenant, pathSuffix);
                if (found.Count >= atLeast)
                {
                    return found;
                }
                await Task.Delay(100, Ct);
            }
            return producer.App.Audit.For(tenant, pathSuffix);
        }

        [Fact]
        public async Task A_change_through_the_api_is_recorded_with_who_made_it_and_its_rows()
        {
            LocalStack.RequireAvailable();
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            var key = "a" + Guid.NewGuid().ToString("N")[..10];

            (await admin.PutAsJsonAsync($"{Url}/integrations/{key}", Mail, Ct)).EnsureSuccessStatusCode();

            var record = Assert.Single(await RecordsAsync(ProducerApp.Tenant, $"/integrations/{key}"));
            Assert.Equal(("PUT", 200), (record.Access.Request!.Method, record.Access.Response!.Status));
            Assert.Equal("client:notifications-admin", record.Access.User);
            Assert.Equal(32, record.Access.Request.TraceId.Length);
            var change = Assert.Single(record.Changes);
            Assert.Equal(("integrations", "Inserted", ProducerApp.Tenant.ToString()), (change.Tbl, change.Action, change.TenantId));
            Assert.Contains($"\"Key\":\"{key}\"", change.Changes);
            Assert.Equal(record.Access.Request.TraceId, change.TraceId);
            Assert.Equal("client:notifications-admin", change.User);
        }

        [Theory]
        [InlineData("preferred_username=ana;client_id=tool;sub=s1", "ana")]
        [InlineData("name=Ana B;azp=tool;sub=s1", "Ana B")]
        [InlineData("client_id=tool;azp=other;sub=s1", "client:tool")]
        [InlineData("azp=tool;sub=s1", "client:tool")]
        [InlineData("sub=s1", "s1")]
        [InlineData("preferred_username=;sub=s1", "s1")]
        public void A_record_names_the_caller_by_the_best_claim_the_token_has(string claims, string expected)
        {
            var identity = new System.Security.Claims.ClaimsIdentity(claims.Split(';').Select(c => c.Split('=', 2))
                .Select(p => new System.Security.Claims.Claim(p[0], p[1])), "Bearer");
            Assert.Equal(expected, Api.NotificationsAudit.Caller(new System.Security.Claims.ClaimsPrincipal(identity)));
            Assert.Null(Api.NotificationsAudit.Caller(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
        }

        [Fact]
        public async Task A_put_that_changes_nothing_is_recorded_without_rows()
        {
            LocalStack.RequireAvailable();
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            var key = "u" + Guid.NewGuid().ToString("N")[..10];
            var first = await (await admin.PutAsJsonAsync($"{Url}/integrations/{key}", Mail, Ct)).Content.ReadFromJsonAsync<IntegrationResponse>(Ct);
            // The same settings in another form (key order, a number written differently is covered by CanonicalJsonTests): still no change.
            var again = await admin.PutAsJsonAsync($"{Url}/integrations/{key}", Mail with
            {
                Settings = JsonElement.Parse("""{ "from": "notifications@juice.local", "server": "local" }"""),
            }, Ct);

            Assert.Equal(first!.UpdatedAt, (await again.Content.ReadFromJsonAsync<IntegrationResponse>(Ct))!.UpdatedAt);
            var records = await RecordsAsync(ProducerApp.Tenant, $"/integrations/{key}", atLeast: 2);
            Assert.Equal([1, 0], records.Select(r => r.Changes.Count));
        }

        [Fact]
        public async Task Refused_and_failed_changes_are_recorded_too()
        {
            LocalStack.RequireAvailable();
            using var tenantAdmin = producer.App.Client(await LocalStack.TenantAdminTokenAsync());
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());

            // Another tenant's setup: refused.
            var other = await tenantAdmin.PutAsJsonAsync($"/api/v1/tenants/{ProducerApp.OtherTenant}/integrations/x", Mail, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
            // An invalid one: refused by the checks.
            var invalid = await admin.PutAsJsonAsync($"{Url}/integrations/BAD", Mail, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            var refused = Assert.Single(await RecordsAsync(ProducerApp.OtherTenant, "/integrations/x"));
            Assert.Equal((403, "client:tenant-admin"), (refused.Access.Response!.Status, refused.Access.User));
            var failed = Assert.Single(await RecordsAsync(ProducerApp.Tenant, "/integrations/BAD"));
            Assert.Equal(400, failed.Access.Response!.Status);
            Assert.Empty(failed.Changes);
        }

        [Fact]
        public async Task A_dead_letter_retry_is_recorded()
        {
            LocalStack.RequireAvailable();
            using var admin = producer.App.Client(await LocalStack.AdminTokenAsync());
            var id = Guid.CreateVersion7();

            var retry = await admin.PostAsync($"{Url}/deliveries/{id}/retry", null, Ct);
            Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);

            var record = Assert.Single(await RecordsAsync(ProducerApp.Tenant, $"/deliveries/{id}/retry"));
            Assert.Equal(("POST", 404, "client:notifications-admin"), (record.Access.Request!.Method, record.Access.Response!.Status, record.Access.User));
        }

        [Fact]
        public async Task A_producers_request_is_not_recorded()
        {
            LocalStack.RequireAvailable();
            using var client = await producer.ProducerAsync();
            var body = new SendNotificationBody("sample.unknown", null, Guid.NewGuid().ToString("N"), null, null, null, null, null, JsonElement.Parse("{}"), null, null);

            var sent = await client.PostAsJsonAsync($"{Url}/notifications", body, Ct);
            Assert.False(sent.IsSuccessStatusCode);

            await Task.Delay(500, Ct);
            Assert.Empty(producer.App.Audit.For(ProducerApp.Tenant, "/notifications"));
        }
    }

    /// <summary>The same records reaching the real audit service: the local stack's audit host (deploy/local/audit, gRPC on localhost:5285).</summary>
    public sealed class AuditServiceTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private const string Endpoint = "http://127.0.0.1:5285";

        private readonly NotificationsApp _app = new(host => host.UseSetting("Audit:Endpoint", Endpoint)) { RecordAudit = false };

        public ValueTask InitializeAsync() => _app.InitializeAsync();

        public ValueTask DisposeAsync() => _app.DisposeAsync();

        private static bool AuditHostRunning()
        {
            try
            {
                using var socket = new System.Net.Sockets.TcpClient();
                socket.Connect("127.0.0.1", 5285);
                return true;
            }
            catch (System.Net.Sockets.SocketException)
            {
                return false;
            }
        }

        [Fact]
        public async Task A_change_reaches_the_audit_service()
        {
            LocalStack.RequireAvailable();
            Assert.SkipUnless(AuditHostRunning(), "The local audit host isn't running: docker compose -f deploy/local/docker-compose.yml up -d --build --wait audit");
            var tenant = Guid.CreateVersion7();
            using var admin = _app.Client(await LocalStack.AdminTokenAsync());

            var put = await admin.PutAsJsonAsync($"/api/v1/tenants/{tenant}/integrations/mail", new PutIntegrationBody("email", "smtp",
                JsonElement.Parse("""{ "server": "local", "from": "notifications@juice.local" }"""), null, null, null), Ct);
            Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync(Ct));

            await using var connection = new NpgsqlConnection(LocalStack.ServerConnectionString("notifications"));
            await connection.OpenAsync(Ct);
            for (var waited = 0; ; waited++)
            {
                await using var command = new NpgsqlCommand("""
                    SELECT (SELECT count(*) FROM audit."AccessLog" WHERE "TenantId" = @t),
                           (SELECT count(*) FROM audit."DataAudit" WHERE "TenantId" = @t AND "Tbl" = 'integrations')
                    """, connection);
                command.Parameters.AddWithValue("t", tenant.ToString());
                await using var reader = await command.ExecuteReaderAsync(Ct);
                await reader.ReadAsync(Ct);
                var (access, changes) = (reader.GetInt64(0), reader.GetInt64(1));
                if ((access, changes) == (1, 1))
                {
                    return;
                }
                Assert.True(waited < 50, $"After 5 s the audit service has {access} access records and {changes} data changes for the tenant.");
                await Task.Delay(100, Ct);
            }
        }
    }
}
