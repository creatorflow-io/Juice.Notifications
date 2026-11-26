using System.Text.Json;
using Juice.Notifications.Api.Contracts;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The admin pages per mode (m44-architecture §3.4, §7): the API's lists equal the fixture the Console library's NOTIFICATIONS_ADMIN_PAGES is
    /// reviewed against, and in the service every mapped admin route belongs to a page and needs its right.
    /// </summary>
    public sealed class AdminPagesTests(ProducerApp fixture) : IClassFixture<ProducerApp>
    {
        private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "admin-pages.json");

        [Fact]
        public void The_pages_per_mode_are_the_shared_fixture()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Fixture));
            Assert.Equal(json.RootElement.GetProperty("service").EnumerateArray().Select(p => p.GetString()!), AdminPages.Service);
            Assert.Equal(json.RootElement.GetProperty("embedded").EnumerateArray().Select(p => p.GetString()!), AdminPages.Embedded);
            Assert.All(AdminPages.Embedded, p => Assert.Contains(p, AdminPages.Service));
        }

        /// <summary>The Console library's NOTIFICATIONS_ADMIN_PAGES (console/…/core/pages.ts) lists the same pages, in the same order.</summary>
        [Fact]
        public void The_console_librarys_page_lists_are_the_apis()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Juice.Notifications.slnx")))
            {
                root = root.Parent;
            }
            Assert.NotNull(root);
            var source = File.ReadAllText(Path.Combine(root.FullName, "console", "projects", "juice-js", "notifications-admin", "src", "lib", "core", "pages.ts"));
            IReadOnlyList<string> List(string mode)
            {
                var block = System.Text.RegularExpressions.Regex.Match(source, mode + @":\s*\[(?<ids>[^\]]*)\]").Groups["ids"].Value;
                return System.Text.RegularExpressions.Regex.Matches(block, "'(?<id>[a-z-]+)'").Select(m => m.Groups["id"].Value).ToList();
            }
            Assert.Equal(AdminPages.Service, List("service"));
            Assert.Equal(AdminPages.Embedded, List("embedded"));
        }

        [Fact]
        public void Every_admin_route_of_the_service_belongs_to_a_page_and_needs_its_right()
        {
            LocalStack.RequireAvailable();
            AdminRoutes.AssertPagesAndRights(fixture.App.Services, AdminPages.Service, "/api/v1/tenants/");
        }
    }
}
