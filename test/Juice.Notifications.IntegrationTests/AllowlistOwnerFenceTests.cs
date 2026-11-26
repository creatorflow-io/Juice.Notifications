using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The allowlist entry's owner is its write fence (phase-5 decision G1, review of #3): a change or delete decided on the owner read fails as a
    /// concurrent change when another owner took the entry between that read and the write, so the refusal rule can't be raced.
    /// </summary>
    public sealed class AllowlistOwnerFenceTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = TwoTenantDatabase.TenantA;
        private readonly ScratchDatabase _database = new();

        public ValueTask InitializeAsync() => _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        private async Task SeedAsync()
        {
            await using var db = await _database.MigratedAsync();
            db.Allowlist.Add(new AllowlistEntry { TenantId = Tenant, Destination = "10.20.0.0/16", Ports = [443], ManagedBy = ManagedBy.Api, UpdatedAt = Seed.Now });
            await db.SaveChangesAsync(Ct);
        }

        private async Task TakeOverAsync()
        {
            await using var other = _database.Context();
            var entry = await other.Allowlist.SingleAsync(a => a.TenantId == Tenant && a.Destination == "10.20.0.0/16", Ct);
            entry.ManagedBy = ManagedBy.WebhooksAlias;
            await other.SaveChangesAsync(Ct);
        }

        [Fact]
        public async Task A_change_decided_on_a_stale_owner_is_a_concurrent_change()
        {
            LocalStack.RequireAvailable();
            await SeedAsync();
            await using var db = _database.Context();
            var read = await db.Allowlist.SingleAsync(a => a.TenantId == Tenant && a.Destination == "10.20.0.0/16", Ct);
            Assert.Equal(ManagedBy.Api, read.ManagedBy); // the API may change it, as read

            await TakeOverAsync();
            read.Ports = [8443];
            var conflict = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal(ConflictKind.RowGone, DbConflicts.Classify(conflict)!.Kind);

            await using var check = _database.Context();
            var stored = await check.Allowlist.AsNoTracking().SingleAsync(a => a.TenantId == Tenant, Ct);
            Assert.Equal((ManagedBy.WebhooksAlias, "443"), (stored.ManagedBy, string.Join(",", stored.Ports)));
        }

        [Fact]
        public async Task A_delete_decided_on_a_stale_owner_is_a_concurrent_change()
        {
            LocalStack.RequireAvailable();
            await SeedAsync();
            await using var db = _database.Context();
            var read = await db.Allowlist.SingleAsync(a => a.TenantId == Tenant && a.Destination == "10.20.0.0/16", Ct);

            await TakeOverAsync();
            db.Allowlist.Remove(read);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync(Ct));

            await using var check = _database.Context();
            Assert.True(await check.Allowlist.AnyAsync(a => a.TenantId == Tenant, Ct));
        }

        /// <summary>
        /// The same fence on a route (review of #3, round 2): a takeover that keeps the definition moves no hash, so the owner itself is the token
        /// (integrations, targets, routes and policies alike).
        /// </summary>
        [Fact]
        public async Task A_route_change_decided_on_a_stale_owner_is_a_concurrent_change_even_with_the_same_definition()
        {
            LocalStack.RequireAvailable();
            await using (var seed = await _database.MigratedAsync())
            {
                seed.Routes.Add(new Route { TenantId = Tenant, Key = "ops", Match = "{}", Targets = [], DefinitionHash = "h", ManagedBy = ManagedBy.Api, UpdatedAt = Seed.Now });
                await seed.SaveChangesAsync(Ct);
            }
            await using var db = _database.Context();
            var read = await db.Routes.SingleAsync(r => r.TenantId == Tenant && r.Key == "ops", Ct);

            await using (var provisioning = _database.Context())
            {
                // The file takes it over with the same definition: the hash stays as it was.
                (await provisioning.Routes.SingleAsync(r => r.TenantId == Tenant && r.Key == "ops", Ct)).ManagedBy = ManagedBy.Configuration;
                await provisioning.SaveChangesAsync(Ct);
            }
            read.Enabled = false;
            var conflict = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal(ConflictKind.RowGone, DbConflicts.Classify(conflict)!.Kind);
            await using var check = _database.Context();
            Assert.True((await check.Routes.AsNoTracking().SingleAsync(r => r.Key == "ops", Ct)).Enabled);
        }
    }
}
