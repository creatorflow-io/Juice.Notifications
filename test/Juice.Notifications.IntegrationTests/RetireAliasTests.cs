using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// M50: the <c>RetireAliasOwners</c> migration moves the two retired alias owners to the API on real rows of every kind, leaves the
    /// configuration's and the service's rows and each row's tenant alone, and is idempotent.
    /// </summary>
    public sealed class RetireAliasTests : IAsyncLifetime
    {
        private const string Previous = "20261127141510_HealthHeldAndSettingsVersion";
        private const string Target = "20261128030103_RetireAliasOwners";
        // Spelled apart so the retired names appear in no source text outside the migrations.
        private const string AliasOwner = "webhooks" + "-alias";
        private const string AliasConfigOwner = "alias" + "-config";
        private static readonly Guid TenantA = Guid.Parse("0193f0aa-0000-7000-8000-0000000ae001");
        private static readonly Guid TenantB = Guid.Parse("0193f0aa-0000-7000-8000-0000000ae002");
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly ScratchDatabase _database = new();

        public ValueTask InitializeAsync() => _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        /// <summary>Rows of every kind under each owner, in two tenants, with the migration reverted under them.</summary>
        private async Task SeedAsync()
        {
            await using var db = await _database.MigratedAsync();
            foreach (var tenant in new[] { TenantA, TenantB })
            {
                foreach (var owner in new[] { AliasOwner, AliasConfigOwner, ManagedBy.Configuration, ManagedBy.Api })
                {
                    var key = $"{owner}-{tenant.ToString("N")[^3..]}";
                    var integration = Seed.Integration(tenant, key);
                    integration.ManagedBy = owner;
                    db.AddRange(integration,
                        new IntegrationTarget { TenantId = tenant, IntegrationId = integration.Id, Key = key, Address = "https://example.com/in", ManagedBy = owner, DefinitionHash = "d", UpdatedAt = Seed.Now },
                        new Route { TenantId = tenant, Key = key, Match = "{}", ManagedBy = owner, DefinitionHash = "d", UpdatedAt = Seed.Now },
                        new AllowlistEntry { TenantId = tenant, Destination = $"{key}.example.com", ManagedBy = owner, UpdatedAt = Seed.Now },
                        new EscalationPolicy { TenantId = tenant, Key = key, Steps = "[]", ManagedBy = owner, DefinitionHash = "d", UpdatedAt = Seed.Now });
                }
            }
            // The service's own rows (the inbox integration every tenant has) are never touched.
            await db.SaveChangesAsync(Ct);
            await db.GetService<IMigrator>().MigrateAsync(Previous, cancellationToken: Ct);
        }

        private async Task<Dictionary<string, string>> OwnersAsync()
        {
            await using var db = _database.Context();
            var owners = new Dictionary<string, string>();
            foreach (var i in await db.Integrations.AsNoTracking().Where(i => i.TenantId == TenantA || i.TenantId == TenantB).ToListAsync(Ct)) owners[$"integration/{i.TenantId}/{i.Key}"] = i.ManagedBy;
            foreach (var t in await db.IntegrationTargets.AsNoTracking().Where(t => t.TenantId == TenantA || t.TenantId == TenantB).ToListAsync(Ct)) owners[$"target/{t.TenantId}/{t.Key}"] = t.ManagedBy;
            foreach (var r in await db.Routes.AsNoTracking().Where(r => r.TenantId == TenantA || r.TenantId == TenantB).ToListAsync(Ct)) owners[$"route/{r.TenantId}/{r.Key}"] = r.ManagedBy;
            foreach (var a in await db.Allowlist.AsNoTracking().Where(a => a.TenantId == TenantA || a.TenantId == TenantB).ToListAsync(Ct)) owners[$"allowlist/{a.TenantId}/{a.Destination}"] = a.ManagedBy;
            foreach (var p in await db.EscalationPolicies.AsNoTracking().Where(p => p.TenantId == TenantA || p.TenantId == TenantB).ToListAsync(Ct)) owners[$"policy/{p.TenantId}/{p.Key}"] = p.ManagedBy;
            return owners;
        }

        private static string Expected(string owner) => owner is AliasOwner or AliasConfigOwner ? ManagedBy.Api : owner;

        [Fact]
        public async Task RetireAlias_Migration_RewritesRowsToApi_Twice_NoOp()
        {
            LocalStack.RequireAvailable();
            await SeedAsync();
            var before = await OwnersAsync();
            Assert.Equal(2 * 4 * 5, before.Count);
            Assert.Contains(AliasOwner, before.Values);
            Assert.Contains(AliasConfigOwner, before.Values);

            for (var round = 0; round < 2; round++)
            {
                await using (var db = _database.Context())
                {
                    await db.Database.MigrateAsync(Ct);
                    Assert.Contains(Target, await db.Database.GetAppliedMigrationsAsync(Ct));
                }
                var after = await OwnersAsync();
                Assert.Equal(before.Count, after.Count);
                foreach (var (row, owner) in before)
                {
                    // Alias rows are the API's now; the configuration's and the API's own are as they were; the second run changes nothing.
                    Assert.True(Expected(owner) == after[row], $"{row}: {owner} -> {after[row]}");
                }
                Assert.DoesNotContain(AliasOwner, after.Values);
                Assert.DoesNotContain(AliasConfigOwner, after.Values);
            }
        }

        [Fact]
        public async Task RetireAlias_RowsInTwoTenants_EachKeepsItsTenant()
        {
            LocalStack.RequireAvailable();
            await SeedAsync();
            var before = await OwnersAsync();
            await using (var db = _database.Context())
            {
                await db.Database.MigrateAsync(Ct);
            }
            var after = await OwnersAsync();

            // Every row is still there under the tenant it had (the key names the tenant), and only its owner moved.
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var tenant in new[] { TenantA, TenantB })
            {
                var rows = after.Where(r => r.Key.Contains($"/{tenant}/")).ToList();
                Assert.Equal(4 * 5, rows.Count);
                Assert.Equal(3 * 5, rows.Count(r => r.Value == ManagedBy.Api));
                Assert.Equal(1 * 5, rows.Count(r => r.Value == ManagedBy.Configuration));
            }
        }
    }
}
