using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>The real host (Juice.Notifications.App) on a scratch database, accepting tokens from the local Keycloak.</summary>
    public sealed class NotificationsApp : IAsyncLifetime
    {
        public ScratchDatabase Database { get; } = new();
        private WebApplicationFactory<Program>? _factory;

        public async ValueTask InitializeAsync()
        {
            await Database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            {
                host.UseSetting("ConnectionStrings:Notifications", Database.ConnectionString);
                host.UseSetting("Database:Migrate", "true");
                host.UseSetting("Auth:Authority", LocalStack.KeycloakRealm);
                host.UseSetting("Auth:RequireHttpsMetadata", "false");
                // Retries within a test's patience; the stages poll quickly.
                host.UseSetting("Delivery:RetryBase", "00:00:00.300");
                host.UseSetting("Delivery:RetryMax", "00:00:01");
                host.UseSetting("Stages:Dispatcher:Poll", "00:00:00.200");
                host.UseSetting("Stages:Dispatcher:Lease", "00:01:00");
                host.ConfigureLogging(logging => logging.AddProvider(new ErrorLogProvider()));
            });
            // Starts the host, which migrates the database.
            _factory.CreateClient().Dispose();
        }

        /// <summary>A client with no token, or with the given one.</summary>
        public HttpClient Client(string? token = null)
        {
            var client = _factory!.CreateClient();
            if (token is not null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            if (_factory is not null)
            {
                await _factory.DisposeAsync();
            }
            await Database.DisposeAsync();
        }
    }
}
