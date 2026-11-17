using System.Net.Http.Headers;
using Juice.Audit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>The real host (Juice.Notifications.App) on a scratch database, accepting tokens from the local Keycloak.</summary>
    public sealed class NotificationsApp : IAsyncLifetime
    {
        private readonly Action<IWebHostBuilder>? _configure;

        public NotificationsApp()
        {
        }

        /// <summary>With a test class's own settings and services, applied over the defaults.</summary>
        internal NotificationsApp(Action<IWebHostBuilder> configure) => _configure = configure;

        /// <summary>A replica: another host on <paramref name="database"/>, which its owner creates and drops.</summary>
        internal NotificationsApp(ScratchDatabase database, Action<IWebHostBuilder> configure) => (Database, _ownsDatabase, _configure) = (database, false, configure);

        private readonly bool _ownsDatabase = true;

        public ScratchDatabase Database { get; } = new();

        /// <summary>The audit records the host saved.</summary>
        public AuditRecorder Audit { get; } = new();

        /// <summary>Whether audit records go to <see cref="Audit"/>; otherwise the test's own settings say where (Audit:Endpoint).</summary>
        internal bool RecordAudit { get; init; } = true;
        private WebApplicationFactory<Program>? _factory;

        public async ValueTask InitializeAsync()
        {
            if (_ownsDatabase)
            {
                await Database.InitializeAsync();
            }
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
                // Audit records go to the recorder, not to an audit service (AuditTests reach the real one when it runs).
                if (RecordAudit)
                {
                    host.UseSetting("Audit:Endpoint", "http://audit.invalid:8080");
                    host.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<IAuditService>(_ => new AuditRecorder.Service(Audit))));
                }
                _configure?.Invoke(host);
            });
            // Starts the host, which migrates the database.
            _factory.CreateClient().Dispose();
        }

        /// <summary>The in-memory server, for clients that need its handler (SignalR).</summary>
        public TestServer Server => _factory!.Server;

        /// <summary>The host's services.</summary>
        public IServiceProvider Services => _factory!.Services;

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
            if (_ownsDatabase)
            {
                await Database.DisposeAsync();
            }
        }
    }
}
