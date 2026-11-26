using System.Security.Claims;
using Juice.Notifications.Admin;

namespace Juice.Notifications.Tests
{
    /// <summary>An embedded host's access (m44-architecture §3.4): its one tenant; any other is no right, and a caller with none has no tenants.</summary>
    public sealed class AdminAccessTests
    {
        private static readonly Guid Tenant = Guid.CreateVersion7();
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed class Roles() : SingleTenantAdminAccess(Tenant)
        {
            protected override ValueTask<AdminRights> RightsAsync(ClaimsPrincipal user, CancellationToken ct)
                => ValueTask.FromResult(user.IsInRole("Admin") ? AdminRights.All : user.IsInRole("Operator") ? AdminRights.Read | AdminRights.Operate : AdminRights.None);
        }

        private static ClaimsPrincipal As(string role) => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

        [Fact]
        public async Task The_hosts_tenant_has_the_roles_rights_and_any_other_tenant_none()
        {
            var access = new Roles();
            Assert.Equal(AdminRights.All, await access.RightsAsync(As("Admin"), Tenant, Ct));
            Assert.Equal(AdminRights.Read | AdminRights.Operate, await access.RightsAsync(As("Operator"), Tenant, Ct));
            Assert.Equal(AdminRights.None, await access.RightsAsync(As("Admin"), Guid.CreateVersion7(), Ct));
            Assert.Equal(AdminRights.None, await access.RightsAsync(As("Viewer"), Tenant, Ct));
        }

        [Fact]
        public async Task The_tenants_are_the_hosts_one_when_the_caller_has_a_right_else_none()
        {
            var access = new Roles();
            var admin = await access.TenantsAsync(As("Admin"), Ct);
            Assert.False(admin.Every);
            Assert.Equal([Tenant], admin.Ids);
            Assert.Empty((await access.TenantsAsync(As("Viewer"), Ct)).Ids);
        }

        [Fact]
        public void Rights_are_named_as_the_access_answer_names_them()
        {
            Assert.Equal(["read", "operate", "configure"], AdminRightNames.Of(AdminRights.All));
            Assert.Equal(["read", "operate"], AdminRightNames.Of(AdminRights.Read | AdminRights.Operate));
            Assert.Empty(AdminRightNames.Of(AdminRights.None));
        }
    }
}
