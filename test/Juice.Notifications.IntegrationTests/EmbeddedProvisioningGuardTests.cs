using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Juice.Notifications.IntegrationTests.EmbeddedHostTests;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// M50: an embedded host has one tenant, and its provisioning file may name only that one; a file naming another, or a database nobody
    /// migrated, stops the start before anything is written.
    /// </summary>
    public sealed class EmbeddedProvisioningGuardTests : IAsyncLifetime
    {
        private static readonly Guid Own = Guid.Parse("0193f0aa-0000-7000-8000-0000000e0b01");
        private static readonly Guid Foreign = Guid.Parse("0193f0aa-0000-7000-8000-0000000e0b99");
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly ScratchDatabase _database = new();

        public ValueTask InitializeAsync() => _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        private static string Tenant(Guid id, string key) => $$"""
            "{{id}}": {
              "integrations": { "{{key}}": { "channel": "webhook", "kind": "webhook", "secretRef": "{{TestHooks.SecretRef}}",
                                             "targets": { "{{key}}-t": { "address": "https://hooks.example.com/{{key}}" } } } },
              "routes": { "{{key}}": { "match": { "type": "host.*" }, "targets": ["{{key}}-t"] } },
              "allowlist": [ { "destination": "{{key}}.example.com", "ports": [443] } ]
            }
            """;

        [Fact]
        public async Task Embedded_ProvisioningFileForeignTenant_RefusedAtStart_NothingWritten()
        {
            LocalStack.RequireAvailable();
            // The host's own tenant listed first, a foreign one after it: nothing of either is written.
            var file = $$"""{ "tenants": { {{Tenant(Own, "own")}}, {{Tenant(Foreign, "foreign")}} } }""";

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false, provisioning: file));

            Assert.Contains(Foreign.ToString(), refused.Message);
            Assert.Contains("Nothing was provisioned", refused.Message);
            await using var db = _database.Context();
            Assert.False(await db.Integrations.AnyAsync(Ct));
            Assert.False(await db.IntegrationTargets.AnyAsync(Ct));
            Assert.False(await db.Routes.AnyAsync(Ct));
            Assert.False(await db.Allowlist.AnyAsync(Ct));
        }

        [Fact]
        public async Task Embedded_ProvisioningFileOwnTenantOnly_Starts()
        {
            LocalStack.RequireAvailable();
            await using var host = await EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false, provisioning: $$"""{ "tenants": { {{Tenant(Own, "own")}} } }""");

            await using var db = _database.Context();
            Assert.Contains(await db.Integrations.AsNoTracking().ToListAsync(Ct), i => i.TenantId == Own && i.Key == "own");
        }

        [Fact]
        public async Task Embedded_ProvisioningSchemaNotMigrated_StartFailsCleanly()
        {
            LocalStack.RequireAvailable();
            var file = $$"""{ "tenants": { {{Tenant(Own, "own")}} } }""";

            var failed = await Assert.ThrowsAsync<InvalidOperationException>(
                () => EmbeddedHost.StartAsync(_database.ConnectionString, runStages: false, provisioning: file, migrate: false));

            Assert.Contains("isn't migrated", failed.Message);
            Assert.Contains("Database:Migrate", failed.Message);
            // Said in words, not as a missing table, and nothing was created.
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'notifications'", connection);
            Assert.Equal(0L, await command.ExecuteScalarAsync(Ct));
        }
    }
}
