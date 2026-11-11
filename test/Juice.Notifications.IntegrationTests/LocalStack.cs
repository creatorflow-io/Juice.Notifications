using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The running <c>deploy/local</c> stack: its generated secrets, scratch databases on its PostgreSQL, and Keycloak tokens. Tests call
    /// <see cref="RequireAvailable"/> first and are skipped when the stack isn't up.
    /// </summary>
    public static class LocalStack
    {
        public const string KeycloakRealm = "http://localhost:8081/realms/juice"; // the issuer tokens carry (KC_HOSTNAME);

        private static readonly Lazy<Dictionary<string, string>> Env = new(() =>
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var file = Path.Combine(dir.FullName, "deploy", "local", ".env");
                if (File.Exists(file))
                {
                    return File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
                        .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
                }
            }
            return [];
        });

        private static readonly Lazy<bool> Up = new(() =>
        {
            if (!Env.Value.ContainsKey("POSTGRES_PASSWORD"))
            {
                return false;
            }
            try
            {
                using var connection = new NpgsqlConnection(ServerConnectionString("notifications"));
                connection.Open();
                return true;
            }
            catch (NpgsqlException)
            {
                return false;
            }
        });

        /// <summary>Whether the stack is up. Fixtures check this and do nothing when it isn't: xunit v3 reports a skip thrown from a fixture as a failure.</summary>
        public static bool Available => Up.Value;

        /// <summary>Skips the calling test when the stack isn't up. Call it first in every test, never from a fixture.</summary>
        public static void RequireAvailable()
        {
            if (!Up.Value)
            {
                Assert.Skip("The local stack isn't up: deploy/local/scripts/init.sh, then docker compose up -d --wait.");
            }
        }

        public static string Secret(string name) => Env.Value[name];

        public static string ServerConnectionString(string database)
            => $"Host=127.0.0.1;Port=5434;Database={database};Username=notifications;Password={Env.Value["POSTGRES_PASSWORD"]}";

        /// <summary>A client-credentials token from the local Keycloak.</summary>
        public static async Task<string> TokenAsync(string clientId, string secretName)
        {
            using var http = new HttpClient();
            using var response = await http.PostAsync("http://127.0.0.1:8081/realms/juice/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = Secret(secretName),
            }));
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("access_token").GetString()!;
        }

        public static Task<string> AdminTokenAsync() => TokenAsync("notifications-admin", "NOTIFICATIONS_ADMIN_CLIENT_SECRET");

        public static Task<string> ProducerTokenAsync() => TokenAsync("sample-producer", "SAMPLE_PRODUCER_CLIENT_SECRET");

        /// <summary>An admin of <see cref="ProducerApp.Tenant"/> only (the realm's tenant-admin client).</summary>
        public static Task<string> TenantAdminTokenAsync() => TokenAsync("tenant-admin", "TENANT_ADMIN_CLIENT_SECRET");
    }

    /// <summary>An empty database on the local PostgreSQL, created for one test class and dropped after it.</summary>
    public sealed class ScratchDatabase : IAsyncLifetime
    {
        public string Name { get; } = "notifications_test_" + Guid.NewGuid().ToString("N")[..12];
        public string ConnectionString => LocalStack.ServerConnectionString(Name);
        private bool _created;

        public async ValueTask InitializeAsync()
        {
            if (!LocalStack.Available)
            {
                return;
            }
            await using var connection = new NpgsqlConnection(LocalStack.ServerConnectionString("notifications"));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{Name}\"", connection);
            await command.ExecuteNonQueryAsync();
            _created = true;
        }

        /// <summary>The scratch database, migrated.</summary>
        public async Task<NotificationsDbContext> MigratedAsync()
        {
            var db = Context();
            await db.Database.MigrateAsync();
            return db;
        }

        public NotificationsDbContext Context()
        {
            var options = new DbContextOptionsBuilder<NotificationsDbContext>();
            NotificationsDbContext.Configure(options, ConnectionString);
            return new NotificationsDbContext(options.Options);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_created)
            {
                return;
            }
            // Pooled connections to the scratch database would hold it open; FORCE ends them, clearing the pools stops them coming back.
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(LocalStack.ServerConnectionString("notifications"));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
