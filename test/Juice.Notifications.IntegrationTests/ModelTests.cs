using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Checks on the EF model alone. They never connect, so they run in CI without the local stack.</summary>
    public sealed class ModelTests
    {
        private static NotificationsDbContext Model()
        {
            var options = new DbContextOptionsBuilder<NotificationsDbContext>();
            NotificationsDbContext.Configure(options, "Host=model-only;Database=none");
            return new NotificationsDbContext(options.Options);
        }

        /// <summary>Labels and links compare as sets of pairs, so equal values must hash alike whatever their order.</summary>
        [Fact]
        public void Equal_label_dictionaries_hash_alike_in_any_order()
        {
            using var db = Model();
            var comparer = db.Model.FindEntityType(typeof(NotificationRequest))!.FindProperty(nameof(NotificationRequest.Labels))!.GetValueComparer();
            var ab = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };
            var ba = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" };
            Assert.True(comparer.Equals(ab, ba));
            Assert.Equal(comparer.GetHashCode(ab), comparer.GetHashCode(ba));
            Assert.False(comparer.Equals(ab, new Dictionary<string, string> { ["a"] = "1", ["b"] = "3" }));
        }

        /// <summary>Guards tables added later: every reference between two tenant-owned tables must include the tenant.</summary>
        [Fact]
        public void Every_reference_between_tenant_owned_tables_includes_the_tenant()
        {
            using var db = Model();
            var references = db.Model.GetEntityTypes()
                .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType))
                .SelectMany(e => e.GetForeignKeys())
                .Where(fk => typeof(ITenantOwned).IsAssignableFrom(fk.PrincipalEntityType.ClrType))
                .ToList();
            Assert.NotEmpty(references);
            foreach (var fk in references)
            {
                var tenantAt = fk.Properties.Select(p => p.Name).ToList().IndexOf(nameof(ITenantOwned.TenantId));
                Assert.True(tenantAt >= 0, $"{fk.DeclaringEntityType.ClrType.Name} → {fk.PrincipalEntityType.ClrType.Name} doesn't include TenantId");
                Assert.Equal(nameof(ITenantOwned.TenantId), fk.PrincipalKey.Properties[tenantAt].Name);
            }
        }

        /// <summary>The API answers races by these names (ApplicationEndpoints), so the model must create exactly them.</summary>
        [Fact]
        public void The_constraints_the_api_translates_have_their_pinned_names()
        {
            using var db = Model();
            var app = db.Model.FindEntityType(typeof(Application))!;
            var link = db.Model.FindEntityType(typeof(ApplicationTenant))!;
            var request = db.Model.FindEntityType(typeof(NotificationRequest))!;
            // Each name on exactly the constraint it stands for: a swap would answer a key race as a client-id race.
            string? UniqueOn(string property) => app.GetIndexes()
                .Single(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual([property])).GetDatabaseName();
            Assert.Equal(Constraints.ApplicationKey, UniqueOn(nameof(Application.Key)));
            Assert.Equal(Constraints.ApplicationClientId, UniqueOn(nameof(Application.ClientId)));
            Assert.Equal(Constraints.ApplicationTenantLink, link.FindPrimaryKey()!.GetName());
            Assert.Equal(Constraints.RequestApplicationTenant,
                request.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(ApplicationTenant)).GetConstraintName());
        }
    }
}
