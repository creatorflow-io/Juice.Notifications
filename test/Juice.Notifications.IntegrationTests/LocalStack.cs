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
        /// <summary>The issuer tokens carry: the shared Keycloak's (dev-infra), as PUBLIC_HOST in deploy/local/.env names it (KC_HOSTNAME).</summary>
        public static string KeycloakRealm => $"http://{Env.Value.GetValueOrDefault("PUBLIC_HOST", "localhost")}:8080/realms/juice";

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

        private static readonly Lazy<bool> RedisUp = new(() =>
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                return tcp.ConnectAsync("127.0.0.1", RedisPort).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected;
            }
            catch (Exception)
            {
                return false;
            }
        });

        public const int RedisPort = 6380;

        /// <summary>The stack's Redis, for the inbox hub's backplane (phase-2-plan M15).</summary>
        public static string Redis => $"127.0.0.1:{RedisPort}";

        /// <summary>Skips the calling test when the stack or its Redis isn't up (a stack created before M15 has none: docker compose up -d --wait).</summary>
        public static void RequireRedis()
        {
            RequireAvailable();
            if (!RedisUp.Value)
            {
                Assert.Skip("The local stack has no Redis on " + Redis + ": docker compose up -d --wait.");
            }
        }

        public static string Secret(string name) => Env.Value[name];

        public static string ServerConnectionString(string database)
            => $"Host=127.0.0.1;Port=5434;Database={database};Username=notifications;Password={Env.Value["POSTGRES_PASSWORD"]};Maximum Pool Size={MaxPoolSize}";

        /// <summary>
        /// Every test host and scratch database has its own pool, and a full run builds hundreds of them against a server that allows 300
        /// connections (and is shared with other runs): the default of 100 each can exhaust it ("too many clients", 53300, deferred #56).
        /// </summary>
        public const int MaxPoolSize = 30;

        /// <summary>A client-credentials token from the local Keycloak.</summary>
        public static async Task<string> TokenAsync(string clientId, string secretName)
        {
            using var http = new HttpClient();
            using var response = await http.PostAsync($"{KeycloakServer}/realms/juice/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = Secret(secretName),
            }));
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("access_token").GetString()!;
        }

        public static Task<string> AdminTokenAsync() => TokenAsync("notifications-admin", "NOTIFICATIONS_ADMIN_CLIENT_SECRET");

        public static Task<string> ProducerTokenAsync() => TokenAsync("sample-producer", "SAMPLE_PRODUCER_CLIENT_SECRET");

        /// <summary>
        /// A person's token (the realm's person accounts <c>ann</c> and <c>ben</c>, signed in through <c>people-test</c>), a member of
        /// <see cref="ProducerApp.Tenant"/>: the person's API (<c>me</c>) refuses a client's own token.
        /// </summary>
        public static async Task<string> PersonTokenAsync(string username = "ann")
        {
            using var http = new HttpClient();
            using var response = await http.PostAsync($"{KeycloakServer}/realms/juice/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password", ["client_id"] = "people-test", ["username"] = username, ["password"] = Secret("PEOPLE_TEST_PASSWORD"),
            }));
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("access_token").GetString()!;
        }

        /// <summary>An admin of <see cref="ProducerApp.Tenant"/> only (the realm's tenant-admin client).</summary>
        public static Task<string> TenantAdminTokenAsync() => TokenAsync("tenant-admin", "TENANT_ADMIN_CLIENT_SECRET");

        /// <summary>The variable the identity directory's secret reference names; set in this process from .env.</summary>
        public const string DirectorySecretVariable = "NOTIFICATIONS_DIRECTORY_CLIENT_SECRET";

        public const string KeycloakServer = "http://127.0.0.1:8080";

        private static readonly Lazy<bool> DirectoryUp = new(() =>
        {
            if (!Up.Value || !Env.Value.ContainsKey(DirectorySecretVariable))
            {
                return false;
            }
            try
            {
                // The shared realm (dev-infra/keycloak/juice-realm.json, M16): the directory client, and cara in the other tenant.
                return KeycloakUserIdAsync("cara").GetAwaiter().GetResult() is not null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Not up to date or not answering: a skip, not an exception the Lazy would rethrow to every test.
                return false;
            }
        });

        /// <summary>
        /// Skips the calling test unless the realm has the identity directory's client and people (phase-2-plan M16): the shared dev
        /// infrastructure's realm has them (dev-infra: scripts/init.sh, docker compose up -d).
        /// </summary>
        public static void RequireDirectory()
        {
            RequireAvailable();
            if (!DirectoryUp.Value)
            {
                Assert.Skip("The local Keycloak has no identity directory: start the shared one (dev-infra: scripts/init.sh, docker compose up -d).");
            }
        }

        /// <summary>
        /// Settings for a directory named <paramref name="name"/> on the local realm (through <paramref name="server"/>, the local Keycloak unless a
        /// test puts something in between), its secret in this process's environment.
        /// </summary>
        public static Dictionary<string, string> DirectorySettings(string name, string server = KeycloakServer)
        {
            if (Env.Value.TryGetValue(DirectorySecretVariable, out var secret))
            {
                Environment.SetEnvironmentVariable(DirectorySecretVariable, secret);
            }
            return new()
            {
                [$"Identity:Directories:{name}:BaseUrl"] = server,
                [$"Identity:Directories:{name}:Realm"] = "juice",
                [$"Identity:Directories:{name}:ClientId"] = "notifications-directory",
                [$"Identity:Directories:{name}:ClientSecretRef"] = "env:" + DirectorySecretVariable,
            };
        }

        /// <summary>A realm user's id (the subject of their tokens), read as the directory client; null when there is none.</summary>
        public static async Task<string?> KeycloakUserIdAsync(string username)
        {
            var token = await TokenAsync("notifications-directory", DirectorySecretVariable);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var users = await http.GetFromJsonAsync<JsonElement>($"{KeycloakServer}/admin/realms/juice/users?username={Uri.EscapeDataString(username)}&exact=true");
            return users.ValueKind == JsonValueKind.Array && users.GetArrayLength() == 1 ? users[0].GetProperty("id").GetString() : null;
        }
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
            // Pooled connections to the scratch database would hold it open; FORCE ends them, clearing its pool stops them coming back.
            // Only this database's pool: ClearAllPools is process-wide and would drop the idle connections of every class running beside it.
            using (var own = new NpgsqlConnection(ConnectionString))
            {
                NpgsqlConnection.ClearPool(own);
            }
            await using var connection = new NpgsqlConnection(LocalStack.ServerConnectionString("notifications"));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
