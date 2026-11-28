using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Embedded;
using Juice.Notifications.Inbox;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Juice.Notifications.IntegrationTests.EmbeddedHostTests;
using Claim = System.Security.Claims.Claim;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// A host that embeds the pipeline with the inbox on (M54): its own header sign-in, its own <see cref="INotificationsInboxAccess"/> that counts
    /// what it is asked, the inbox mapped under <see cref="Prefix"/>, and a host transaction to send requests in.
    /// </summary>
    internal sealed class InboxTestHost : IAsyncDisposable
    {
        public static readonly Guid Tenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000b5401");
        public static readonly Guid OtherTenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000b5402");
        public const string Prefix = "/notifications";
        public const string Type = "inboxhost.alert";

        public static string Me(Guid? tenant = null) => $"{Prefix}/api/v1/tenants/{tenant ?? Tenant}/me";

        /// <summary>What the host's access was asked, and the log lines the host wrote.</summary>
        public sealed class HostProbe
        {
            private int _asked;

            public int Asked => _asked;

            public void Count() => Interlocked.Increment(ref _asked);

            public ConcurrentQueue<(LogLevel Level, string Message)> Logs { get; } = new();
        }

        /// <summary>
        /// The host's sign-in: a caller is authenticated when <c>X-Auth</c> is sent; <c>X-Sub</c>, <c>X-Name</c> and <c>X-Roles</c> (comma separated)
        /// are the token's <c>sub</c>, <c>preferred_username</c> and <c>roles</c>; <c>X-Client</c> makes it a client's own token (<c>client_id</c> =
        /// <c>azp</c>).
        /// </summary>
        private sealed class HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
        {
            public const string Name = "host";

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                if (!Request.Headers.ContainsKey("X-Auth"))
                {
                    return Task.FromResult(AuthenticateResult.NoResult());
                }
                var claims = new List<Claim>();
                void Add(string header, string claim)
                {
                    if (Request.Headers[header].ToString() is { Length: > 0 } value)
                    {
                        claims.Add(new Claim(claim, value));
                    }
                }
                Add("X-Sub", "sub");
                Add("X-Name", "preferred_username");
                Add("X-Client", "client_id");
                Add("X-Client", "azp");
                foreach (var role in Request.Headers["X-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    claims.Add(new Claim("roles", role));
                }
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Name)), Name)));
            }
        }

        /// <summary>The host's own access: its tenant only; a client's own token is no person; the person is the token's subject, name and roles.</summary>
        private sealed class HeaderInboxAccess(HostProbe probe) : SingleTenantInboxAccess(Tenant)
        {
            protected override ValueTask<InboxAccessResult> PersonAsync(ClaimsPrincipal user, CancellationToken ct)
            {
                probe.Count();
                if (user.FindFirst("client_id") is { } client && client.Value == user.FindFirst("azp")?.Value)
                {
                    return ValueTask.FromResult(InboxAccessResult.NotAPerson);
                }
                return ValueTask.FromResult(user.FindFirst("sub")?.Value is { } sub
                    ? InboxAccessResult.Of(new InboxPerson(sub, user.FindFirst("preferred_username")?.Value, [.. user.FindAll("roles").Select(c => c.Value)]))
                    : InboxAccessResult.NotAMember);
            }
        }

        private sealed class CapturingLogs(HostProbe probe) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new Capture(probe);

            public void Dispose()
            {
            }

            private sealed class Capture(HostProbe probe) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                    => probe.Logs.Enqueue((logLevel, formatter(state, exception)));
            }
        }

        private readonly WebApplication _app;

        private InboxTestHost(WebApplication app, HostProbe probe)
        {
            _app = app;
            Probe = probe;
        }

        public HostProbe Probe { get; }

        public IServiceProvider Services => _app.Services;

        public TestServer Server => _app.GetTestServer();

        public record Options
        {
            /// <summary>The inbox options; null leaves the inbox off (the host as it was).</summary>
            public Action<EmbeddedInboxOptions>? Inbox { get; init; } = o =>
            {
                o.Roles.AddRange(["viewer", "operator", "admin"]);
            };

            public bool Interactions { get; init; }

            public bool RunStages { get; init; }

            public bool Migrate { get; init; } = true;

            public Dictionary<string, string?>? Settings { get; init; }

            public string? Provisioning { get; init; }

            public bool ExceptionHandler { get; init; }
        }

        public static async Task<InboxTestHost> StartAsync(string connectionString, Options? options = null)
        {
            options ??= new Options();
            var probe = new HostProbe();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.AddProvider(new CapturingLogs(probe));
            builder.Services.AddAuthentication(HeaderAuthentication.Name).AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>(HeaderAuthentication.Name, null);
            builder.Services.AddAuthorization();
            // The host's own API writes PascalCase: the inbox's answers must not follow it.
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = null);
            builder.Services.AddProblemDetails();
            builder.Services.AddSingleton(probe);
            builder.Services.AddDbContext<HostDbContext>(o => o.UseNpgsql(connectionString));
            builder.Services.AddSingleton<EmbeddedHost.Committed>();
            builder.Services.AddScoped<HostTransaction>();
            builder.Services.AddScoped<INotificationTransaction>(sp => sp.GetRequiredService<HostTransaction>());
            var settings = new Dictionary<string, string?>
            {
                ["Database:Migrate"] = options.Migrate ? "true" : "false",
                ["Stages:Run"] = options.RunStages ? "true" : "false",
                ["Stages:Planner:Poll"] = "00:00:00.2",
                ["Stages:DeliveryPlanner:Poll"] = "00:00:00.2",
                ["Stages:Dispatcher:Poll"] = "00:00:00.2",
            };
            foreach (var (key, value) in options.Settings ?? [])
            {
                settings[key] = value;
            }
            if (options.Provisioning is { } file)
            {
                var path = Path.Combine(Path.GetTempPath(), $"inbox-provisioning-{Guid.NewGuid():N}.json");
                await File.WriteAllTextAsync(path, file);
                settings["Provisioning:Path"] = path;
            }
            builder.Services.AddJuiceNotificationsEmbedded(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), o =>
            {
                o.ConnectionString = connectionString;
                o.TenantId = Tenant;
                o.ApplicationKey = "inboxhost";
                o.ApplicationName = "Inbox host";
                o.Types.Add(new Types.TypeDeclaration(Type, "An alert", "alerts", null, null, null, null,
                    JsonSerializer.SerializeToElement(new { type = "object" }), CorrelationRules.Optional, null, null,
                    JsonSerializer.SerializeToElement(new { webhook = "passthrough" })));
                if (options.Inbox is { } inbox)
                {
                    o.Inbox = new EmbeddedInboxOptions();
                    inbox(o.Inbox);
                }
            });
            builder.Services.AddJuiceNotificationsInbox();
            builder.Services.AddScoped<INotificationsInboxAccess, HeaderInboxAccess>();
            var app = builder.Build();
            if (options.ExceptionHandler)
            {
                app.UseExceptionHandler();
            }
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapJuiceNotificationsInbox(Prefix, options.Interactions);
            await app.StartAsync(TestContext.Current.CancellationToken);
            return new InboxTestHost(app, probe);
        }

        /// <summary>A caller of the host: authenticated, as <paramref name="sub"/> (none: a token with no subject).</summary>
        public HttpClient Client(string? sub, string? roles = null, string? name = null, string? client = null)
        {
            var http = _app.GetTestClient();
            http.DefaultRequestHeaders.Add("X-Auth", "1");
            AddHeader(http, "X-Sub", sub);
            AddHeader(http, "X-Roles", roles);
            AddHeader(http, "X-Name", name);
            AddHeader(http, "X-Client", client);
            return http;
        }

        private static void AddHeader(HttpClient http, string name, string? value)
        {
            if (value is not null)
            {
                http.DefaultRequestHeaders.Add(name, value);
            }
        }

        /// <summary>The caller signs in: asks for their counts, which makes them a subscriber of the tenant.</summary>
        public async Task<HttpResponseMessage> SignInAsync(string sub, string? roles = null, string? name = null)
        {
            using var http = Client(sub, roles, name ?? sub);
            return await http.GetAsync($"{Me()}/inbox/counts", TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// <paramref name="work"/> in one host transaction, committed when it returns; the commit actions the pipeline asked for run after it commits,
        /// as the host would run them (the inbox's sessions are told then).
        /// </summary>
        public async Task<T> InTransactionAsync<T>(Func<INotificationClient, Task<T>> work)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HostDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var result = await work(scope.ServiceProvider.GetRequiredService<INotificationClient>());
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
            foreach (var action in scope.ServiceProvider.GetRequiredService<HostTransaction>().OnCommit)
            {
                action();
            }
            return result;
        }

        /// <summary>A request in a host transaction, committed: to the people named by <paramref name="recipients"/>.</summary>
        public Task<NotificationAccepted> SendAsync(string key, IReadOnlyList<NotificationRecipientSpec> recipients, string? correlationKey = null)
            => InTransactionAsync(client => client.SendAsync(
                new NotificationToSend(Type, key) { Recipients = recipients, CorrelationKey = correlationKey, Data = JsonSerializer.SerializeToElement(new { n = 1 }) },
                TestContext.Current.CancellationToken));

        public static NotificationRecipientSpec Role(string role) => new("role", Id: role);

        public async Task<T?> GetAsync<T>(HttpClient http, string path)
        {
            using var response = await http.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
            return await response.Content.ReadFromJsonAsync<T>(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }
}
