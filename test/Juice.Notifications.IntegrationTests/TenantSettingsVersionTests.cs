using System.Net;
using System.Net.Http.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The tenant settings' version (M52, #48): GET shows it (body and <c>ETag</c>), a PUT may send it back (<c>If-Match</c> or body) and loses
    /// with 409 <c>concurrent_change</c> when the settings changed since; a PUT without one is written as before.
    /// </summary>
    public sealed class TenantSettingsVersionTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly NotificationsApp _app = new();
        private HttpClient _admin = null!;

        public async ValueTask InitializeAsync()
        {
            await _app.InitializeAsync();
            if (LocalStack.Available)
            {
                _admin = _app.Client(await LocalStack.AdminTokenAsync());
            }
        }

        public async ValueTask DisposeAsync()
        {
            _admin?.Dispose();
            await _app.DisposeAsync();
        }

        private static string Api(Guid tenant) => $"/api/v1/tenants/{tenant}/settings";

        private static HttpRequestMessage Put(Guid tenant, TenantSettingsBody body, string? ifMatch = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, Api(tenant)) { Content = JsonContent.Create(body) };
            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }
            return request;
        }

        private async Task<TenantSettingsResponse> GetAsync(Guid tenant)
        {
            var response = await _admin.GetAsync(Api(tenant), Ct);
            response.EnsureSuccessStatusCode();
            var body = (await response.Content.ReadFromJsonAsync<TenantSettingsResponse>(Ct))!;
            Assert.Equal($"\"{body.Version}\"", response.Headers.ETag?.Tag);
            return body;
        }

        private async Task<TenantSettingsResponse> SaveAsync(Guid tenant, TenantSettingsBody body, string? ifMatch = null)
        {
            var response = await _admin.SendAsync(Put(tenant, body, ifMatch), Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            var saved = (await response.Content.ReadFromJsonAsync<TenantSettingsResponse>(Ct))!;
            Assert.Equal($"\"{saved.Version}\"", response.Headers.ETag?.Tag);
            return saved;
        }

        private static async Task AssertConcurrentChange(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("\"concurrent_change\"", await response.Content.ReadAsStringAsync(Ct));
        }

        [Fact]
        public async Task Get_returns_version_and_etag()
        {
            LocalStack.RequireAvailable();
            var tenant = Guid.CreateVersion7();
            Assert.Equal(0, (await GetAsync(tenant)).Version);
            Assert.Equal(1, (await SaveAsync(tenant, new TenantSettingsBody(50))).Version);
            Assert.Equal(1, (await GetAsync(tenant)).Version);
        }

        [Fact]
        public async Task Put_with_the_current_version_saves_and_bumps_it_by_if_match_and_by_body()
        {
            LocalStack.RequireAvailable();
            var tenant = Guid.CreateVersion7();
            Assert.Equal(1, (await SaveAsync(tenant, new TenantSettingsBody(50), "\"0\"")).Version);
            Assert.Equal(2, (await SaveAsync(tenant, new TenantSettingsBody(60), "W/\"1\"")).Version);
            Assert.Equal(3, (await SaveAsync(tenant, new TenantSettingsBody(70, Version: 2))).Version);
            Assert.Equal(4, (await SaveAsync(tenant, new TenantSettingsBody(80, Version: 3), "\"3\"")).Version); // both, agreeing
            Assert.Equal(80, (await GetAsync(tenant)).StormThreshold);
        }

        [Fact]
        public async Task Put_with_a_stale_version_is_409_concurrent_change_and_changes_nothing()
        {
            LocalStack.RequireAvailable();
            var tenant = Guid.CreateVersion7();
            var saved = await SaveAsync(tenant, new TenantSettingsBody(50));
            await AssertConcurrentChange(await _admin.SendAsync(Put(tenant, new TenantSettingsBody(99), "\"0\""), Ct));
            await AssertConcurrentChange(await _admin.SendAsync(Put(tenant, new TenantSettingsBody(99, Version: 7)), Ct));
            var after = await GetAsync(tenant);
            Assert.Equal((50, 1, saved.UpdatedAt), (after.StormThreshold, after.Version, after.UpdatedAt));
        }

        [Fact]
        public async Task Put_without_a_version_behaves_as_before_and_still_bumps_it()
        {
            LocalStack.RequireAvailable();
            var tenant = Guid.CreateVersion7();
            Assert.Equal(1, (await SaveAsync(tenant, new TenantSettingsBody(50))).Version);
            Assert.Equal(2, (await SaveAsync(tenant, new TenantSettingsBody(null), "*")).Version); // * counts as omitted
            Assert.Null((await GetAsync(tenant)).StormThreshold);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Two_admins_saving_the_same_version_at_once_one_wins_one_409(bool rowExists)
        {
            LocalStack.RequireAvailable();
            var tenant = Guid.CreateVersion7();
            if (rowExists)
            {
                await SaveAsync(tenant, new TenantSettingsBody(10));
            }
            for (var round = 0; round < 5; round++)
            {
                var version = (await GetAsync(tenant)).Version;
                var both = await Task.WhenAll(
                    _admin.SendAsync(Put(tenant, new TenantSettingsBody(20 + round), $"\"{version}\""), Ct),
                    _admin.SendAsync(Put(tenant, new TenantSettingsBody(30 + round), $"\"{version}\""), Ct));
                Assert.Single(both, r => r.StatusCode == HttpStatusCode.OK);
                await AssertConcurrentChange(Assert.Single(both, r => r.StatusCode != HttpStatusCode.OK));
                Assert.Equal(version + 1, (await GetAsync(tenant)).Version);
            }
        }

        [Fact]
        public async Task Put_version_on_a_missing_row_other_than_0_is_409()
        {
            LocalStack.RequireAvailable();
            var tenant = Guid.CreateVersion7();
            await AssertConcurrentChange(await _admin.SendAsync(Put(tenant, new TenantSettingsBody(50), "\"3\""), Ct));
            Assert.Equal(0, (await GetAsync(tenant)).Version);
        }

        [Theory]
        [InlineData("\"1\"", 2)]
        [InlineData("abc", null)]
        [InlineData("\"1\", \"2\"", null)]
        [InlineData("W/\"-1\"", null)]
        public async Task If_match_and_body_version_disagree_or_are_unusable_is_400(string ifMatch, int? bodyVersion)
        {
            LocalStack.RequireAvailable();
            var tenant = Guid.CreateVersion7();
            var response = await _admin.SendAsync(Put(tenant, new TenantSettingsBody(50, Version: bodyVersion), ifMatch), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("/version", await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(0, (await GetAsync(tenant)).Version);
        }

        [Fact]
        public async Task Another_tenants_version_doesnt_apply_and_a_non_admin_is_still_refused()
        {
            LocalStack.RequireAvailable();
            var (a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7());
            await SaveAsync(a, new TenantSettingsBody(10));
            await SaveAsync(a, new TenantSettingsBody(11));
            Assert.Equal(2, (await GetAsync(a)).Version);
            // A's version 2 on B's PUT: B has no row, so it is judged against 0.
            await AssertConcurrentChange(await _admin.SendAsync(Put(b, new TenantSettingsBody(50), "\"2\""), Ct));
            Assert.Equal(1, (await SaveAsync(b, new TenantSettingsBody(50), "\"0\"")).Version);
            Assert.Equal(2, (await GetAsync(a)).Version);

            using var person = _app.Client(await LocalStack.PersonTokenAsync());
            var refused = await person.SendAsync(Put(a, new TenantSettingsBody(1), "\"2\""), Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        [Fact]
        public async Task Upgrade_existing_settings_rows_read_version_0_and_accept_it()
        {
            LocalStack.RequireAvailable();
            var tenant = Guid.CreateVersion7();
            await using (var db = _app.Database.Context())
            {
                // As a row from before the column: no value for it.
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO notifications.tenant_settings (tenant_id, storm_threshold, updated_at) VALUES ({tenant}, 40, now())", Ct);
            }
            var read = await GetAsync(tenant);
            Assert.Equal((40, 0), (read.StormThreshold, read.Version));
            Assert.Equal(1, (await SaveAsync(tenant, new TenantSettingsBody(41), "\"0\"")).Version);
        }

        [Fact]
        public async Task A_versioned_put_is_audited_once_and_a_409_writes_no_audit_record()
        {
            LocalStack.RequireAvailable();
            var tenant = Guid.CreateVersion7();
            await SaveAsync(tenant, new TenantSettingsBody(10), "\"0\"");
            for (var i = 0; i < 50 && _app.Audit.For(tenant, "/settings").Count < 1; i++)
            {
                await Task.Delay(100, Ct);
            }
            var record = Assert.Single(_app.Audit.For(tenant, "/settings"), r => r.Access.Request!.Method == "PUT");
            Assert.Equal("Inserted", Assert.Single(record.Changes).Action);

            await AssertConcurrentChange(await _admin.SendAsync(Put(tenant, new TenantSettingsBody(11), "\"0\""), Ct));
            await Task.Delay(1000, Ct);
            Assert.All(_app.Audit.For(tenant, "/settings").Where(r => r.Access.Request!.Method == "PUT" && r.Access.Response!.Status == 409),
                r => Assert.Empty(r.Changes));
        }
    }
}
