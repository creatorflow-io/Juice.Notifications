using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The service's routes (phase-5 M44-3): moving the tenant-admin endpoints into Juice.Notifications.Admin keeps every route and method the
    /// service had, byte for byte. The fixture was taken from the service before the move.
    /// </summary>
    public sealed class ServiceRoutesTests(ProducerApp fixture) : IClassFixture<ProducerApp>
    {
        internal static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "service-routes.txt");

        internal static List<string> Routes(IServiceProvider services)
            => services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                    .Select(m => $"{m} /{e.RoutePattern.RawText?.TrimStart('/')}"))
                .Distinct().Order(StringComparer.Ordinal).ToList();

        [Fact]
        public void The_service_maps_the_same_routes_as_before_the_admin_package()
        {
            LocalStack.RequireAvailable();
            var routes = Routes(fixture.App.Services);
            if (Environment.GetEnvironmentVariable("JUICE_NOTIFICATIONS_WRITE_ROUTES") is { Length: > 0 } target)
            {
                File.WriteAllLines(target, routes);
            }
            Assert.Equal(File.ReadAllLines(Path), routes);
        }
    }
}
