using System.Net;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Juice.Notifications.Planning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Tests
{
    /// <summary>
    /// The identity resolver and its Keycloak directory (phase-2-plan M16) against a fake admin API: the tenant filter, paging and its bounds,
    /// the token's reuse and renewal, and which failures make the directory unavailable (I22). The local stack's Keycloak is the integration test.
    /// </summary>
    public sealed class IdentityDirectoryTests
    {
        private static readonly Guid Tenant = Guid.Parse("0193f0aa-0000-7000-8000-0000000000a1");
        private static readonly Guid Other = Guid.Parse("0193f0aa-0000-7000-8000-0000000000b2");
        private const string SecretVariable = "NOTIFICATIONS_TEST_DIRECTORY_SECRET";
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly CancellationToken None = CancellationToken.None;

        static IdentityDirectoryTests() => Environment.SetEnvironmentVariable(SecretVariable, "s3cret");

        /// <summary>A Keycloak admin API: answers by path (and query), counts tokens issued, and records each request.</summary>
        private sealed class FakeKeycloak : HttpMessageHandler
        {
            public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new(StringComparer.Ordinal);
            public readonly List<string> Requests = [];
            public int Tokens;
            public Func<HttpRequestMessage, HttpResponseMessage?>? Before;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                await Task.Yield();
                var path = request.RequestUri!.PathAndQuery;
                Requests.Add(path);
                if (Before?.Invoke(request) is { } early)
                {
                    return early;
                }
                if (path == "/realms/juice/protocol/openid-connect/token")
                {
                    var form = await request.Content!.ReadAsStringAsync(ct);
                    Assert.Contains("client_secret=s3cret", form);
                    Tokens++;
                    return Json(HttpStatusCode.OK, $$"""{ "access_token": "t{{Tokens}}", "expires_in": 300 }""");
                }
                Assert.StartsWith("Bearer t", request.Headers.Authorization!.ToString());
                return Routes.TryGetValue(path, out var answer) ? answer() : Json(HttpStatusCode.NotFound, """{ "error": "not found" }""");
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static string User(string id, Guid? tenant, bool enabled = true, bool verified = true, string? email = null, string extra = "")
            => $$"""
               { "id": "{{id}}", "username": "{{id}}-name", "firstName": "{{id.ToUpperInvariant()}}", "lastName": "Local", "enabled": {{(enabled ? "true" : "false")}},
                 "email": "{{email ?? id + "@example.com"}}", "emailVerified": {{(verified ? "true" : "false")}},
                 "attributes": { {{(tenant is { } t ? $"\"platform_tenant\": [\"{t}\"]" : "")}} {{extra}} } }
               """;

        private static KeycloakDirectoryOptions Settings(int pageSize = 100, int maxScanned = 100_000) => new()
        {
            BaseUrl = "http://keycloak.test", Realm = "juice", ClientId = "notifications-directory", ClientSecretRef = "env:" + SecretVariable,
            PageSize = pageSize, MaxScanned = maxScanned, Timeout = TimeSpan.FromSeconds(5),
        };

        private static KeycloakIdentityDirectory Directory(HttpMessageHandler fake, KeycloakDirectoryOptions? settings = null, ILogger<KeycloakIdentityDirectory>? logger = null)
            => new(Options.Create(new IdentityOptions { Directories = { ["kc"] = settings ?? Settings() } }), TimeProvider.System,
                logger ?? NullLogger<KeycloakIdentityDirectory>.Instance, fake);

        /// <summary>The levels of what was logged.</summary>
        private sealed class Levels : ILogger<KeycloakIdentityDirectory>
        {
            public readonly List<(LogLevel Level, string Message)> Entries = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception)));
        }

        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.BadRequest)]
        public async Task A_page_after_the_first_that_isnt_there_makes_the_directory_unavailable_not_its_people_dropped(HttpStatusCode status)
        {
            var fake = new FakeKeycloak();
            fake.Routes["/admin/realms/juice/roles/on-call/users?first=0&max=2&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, $"[{User("a", Tenant)},{User("b", Tenant)}]");
            fake.Routes["/admin/realms/juice/roles/on-call/users?first=2&max=2&briefRepresentation=false"] = () => Json(status, "{}");
            await Assert.ThrowsAsync<ResolverUnavailableException>(() => Directory(fake, Settings(pageSize: 2)).RoleHoldersAsync("kc", Tenant, "on-call", None, Ct));
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("object")]
        public async Task A_scan_limit_check_that_isnt_a_list_makes_the_directory_unavailable(string answer)
        {
            var fake = new FakeKeycloak();
            fake.Routes["/admin/realms/juice/roles/on-call/users?first=0&max=2&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, $"[{User("a", Tenant)},{User("b", Tenant)}]");
            if (answer == "object")
            {
                fake.Routes["/admin/realms/juice/roles/on-call/users?first=2&max=1&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, """{ "error": "?" }""");
            }
            await Assert.ThrowsAsync<ResolverUnavailableException>(()
                => Directory(fake, Settings(pageSize: 2, maxScanned: 2)).RoleHoldersAsync("kc", Tenant, "on-call", None, Ct));
        }

        [Fact]
        public async Task A_listing_that_outlasts_its_budget_keeps_the_people_read_so_far_truncated()
        {
            var fake = new FakeKeycloak();
            // Each page is quick, but there are many: together they pass the budget.
            for (var first = 0; first < 1000; first += 2)
            {
                fake.Routes[$"/admin/realms/juice/roles/big/users?first={first}&max=2&briefRepresentation=false"] = () =>
                {
                    Thread.Sleep(20);
                    return Json(HttpStatusCode.OK, $"[{User("x", Other)},{User("y", Other)}]");
                };
            }
            fake.Routes["/admin/realms/juice/roles/big/users?first=0&max=2&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, $"[{User("a", Tenant)},{User("y", Other)}]");
            var settings = Settings(pageSize: 2, maxScanned: 1000);
            settings.NameBudget = TimeSpan.FromMilliseconds(300);
            var (people, truncated) = await Directory(fake, settings).RoleHoldersAsync("kc", Tenant, "big", None, Ct);
            Assert.Equal(["a"], people.Select(p => p.ExternalId));
            Assert.True(truncated);
        }

        [Fact]
        public async Task A_name_with_nothing_read_when_time_runs_out_makes_the_directory_unavailable()
        {
            // The token never comes within the budget.
            var settings = Settings();
            settings.NameBudget = TimeSpan.FromMilliseconds(200);
            var failure = await Assert.ThrowsAsync<ResolverUnavailableException>(() => Directory(new SlowKeycloak(), settings).RoleHoldersAsync("kc", Tenant, "big", None, Ct));
            Assert.Contains("role big", failure.Message);
        }

        [Fact]
        public async Task The_callers_deadline_ends_the_work_as_the_budget_does()
        {
            var fake = new FakeKeycloak();
            fake.Routes["/admin/realms/juice/roles/r/users?first=0&max=100&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, $"[{User("a", Tenant)}]");
            using var over = new CancellationTokenSource();
            over.Cancel();
            var failure = await Assert.ThrowsAsync<ResolverUnavailableException>(() => Directory(fake).RoleHoldersAsync("kc", Tenant, "r", over.Token, Ct));
            Assert.Contains("planner step", failure.Message);
            // A slow page past the deadline: what came before it is kept, truncated.
            var paged = new FakeKeycloak();
            using var deadline = new CancellationTokenSource();
            paged.Routes["/admin/realms/juice/roles/r/users?first=0&max=2&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, $"[{User("a", Tenant)},{User("b", Tenant)}]");
            paged.Routes["/admin/realms/juice/roles/r/users?first=2&max=2&briefRepresentation=false"] = () =>
            {
                deadline.Cancel();
                Thread.Sleep(50);
                return Json(HttpStatusCode.OK, $"[{User("c", Tenant)}]");
            };
            var (people, truncated) = await Directory(paged, Settings(pageSize: 2)).RoleHoldersAsync("kc", Tenant, "r", deadline.Token, Ct);
            Assert.Equal(["a", "b"], people.Select(p => p.ExternalId));
            Assert.True(truncated);
        }

        [Fact]
        public async Task A_directory_the_deployment_doesnt_configure_is_unavailable()
        {
            var directory = Directory(new FakeKeycloak());
            await Assert.ThrowsAsync<ResolverUnavailableException>(() => directory.UserAsync("gone", Tenant, "ann", None, Ct));
            await Assert.ThrowsAsync<ResolverUnavailableException>(() => directory.GroupMembersAsync("gone", Tenant, "ops", None, Ct));
            await Assert.ThrowsAsync<ResolverUnavailableException>(() => directory.RoleHoldersAsync("gone", Tenant, "r", None, Ct));
        }

        [Fact]
        public async Task A_refusal_is_logged_at_Error_once_a_minute_for_each_resource_and_status()
        {
            var fake = new FakeKeycloak();
            fake.Routes["/admin/realms/juice/roles/r/users?first=0&max=100&briefRepresentation=false"] = () => Json(HttpStatusCode.Forbidden, "{}");
            fake.Routes["/admin/realms/juice/users/u"] = () => Json(HttpStatusCode.Forbidden, "{}");
            var log = new Levels();
            var directory = Directory(fake, logger: log);
            for (var i = 0; i < 3; i++)
            {
                await Assert.ThrowsAsync<ResolverUnavailableException>(() => directory.RoleHoldersAsync("kc", Tenant, "r", None, Ct));
            }
            await Assert.ThrowsAsync<ResolverUnavailableException>(() => directory.UserAsync("kc", Tenant, "u", None, Ct));
            // Once for the roles, once for the users (another problem, not hidden by the first), the repeats at Debug.
            Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Error));
            Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Debug));
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("users/u"));
        }

        /// <summary>
        /// The realm file imports the user profile as a string (Keycloak's form), and scripts/keycloak-update.sh applies keycloak/user-profile.json
        /// to an existing realm: the two must be the same profile, or new and upgraded stacks differ.
        /// </summary>
        [Fact]
        public void The_realm_files_user_profile_is_the_one_the_update_script_applies()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "deploy", "local", "keycloak", "user-profile.json")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            var keycloak = Path.Combine(dir.FullName, "deploy", "local", "keycloak");
            using var realm = JsonDocument.Parse(File.ReadAllText(Path.Combine(keycloak, "juice-realm.json")));
            var embedded = realm.RootElement.GetProperty("components").GetProperty("org.keycloak.userprofile.UserProfileProvider")[0]
                .GetProperty("config").GetProperty("kc.user.profile.config")[0].GetString()!;
            Assert.Equal(CanonicalJson.Write(JsonElement.Parse(File.ReadAllText(Path.Combine(keycloak, "user-profile.json")))),
                CanonicalJson.Write(JsonElement.Parse(embedded)));
        }

        [Fact]
        public async Task A_group_reaches_its_enabled_members_of_the_tenant_only_with_their_email_as_verified_as_the_directory_says()
        {
            var fake = new FakeKeycloak();
            fake.Routes["/admin/realms/juice/group-by-path/ops"] = () => Json(HttpStatusCode.OK, """{ "id": "g1", "path": "/ops" }""");
            fake.Routes["/admin/realms/juice/groups/g1/members?first=0&max=100&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, $"""
                [ {User("ann", Tenant, extra: ", \"locale\": [\"vi\"]")}, {User("cara", Other)}, {User("dora", Tenant, verified: false)},
                  {User("eli", Tenant, enabled: false)}, {User("nobody", null)},
                  {"{ \"id\": \"two\", \"enabled\": true, \"attributes\": { \"platform_tenant\": [\"" + Tenant + "\", \"" + Other + "\"] } }"} ]
                """);
            var (people, truncated) = await Directory(fake).GroupMembersAsync("kc", Tenant, "ops", None, Ct);
            Assert.False(truncated);
            Assert.Equal(["ann", "dora"], people.Select(p => p.ExternalId));
            var ann = people[0];
            Assert.Equal(("oidc", "ANN Local", "vi"), (ann.Source, ann.Name, ann.Locale));
            Assert.Equal([new ResolvedContact(Channels.Email, "ann@example.com", true)], ann.Contacts!);
            Assert.Equal([new ResolvedContact(Channels.Email, "dora@example.com", false)], people[1].Contacts!);
        }

        [Fact]
        public async Task Members_are_read_page_by_page_to_the_end_whatever_the_planners_bound()
        {
            var fake = new FakeKeycloak();
            fake.Routes["/admin/realms/juice/group-by-path/eng/sre"] = () => Json(HttpStatusCode.OK, """{ "id": "g2" }""");
            string[][] pages = [["a", "b"], ["c", "d"], ["e"]];
            for (var i = 0; i < pages.Length; i++)
            {
                var page = pages[i];
                fake.Routes[$"/admin/realms/juice/groups/g2/members?first={i * 2}&max=2&briefRepresentation=false"]
                    = () => Json(HttpStatusCode.OK, "[" + string.Join(",", page.Select(id => User(id, Tenant))) + "]");
            }
            var directory = Directory(fake, Settings(pageSize: 2));
            // All of them, though the planner may keep fewer: it keeps the first by id, and Keycloak lists by username.
            var (all, truncated) = await directory.GroupMembersAsync("kc", Tenant, "eng/sre", None, Ct);
            Assert.Equal(["a", "b", "c", "d", "e"], all.Select(p => p.ExternalId));
            Assert.False(truncated);
        }

        [Fact]
        public async Task A_listing_of_other_tenants_people_stops_at_the_scan_limit_and_says_it_was_cut_short()
        {
            var fake = new FakeKeycloak();
            for (var first = 0; first < 10; first += 2)
            {
                var at = first;
                fake.Routes[$"/admin/realms/juice/roles/on-call/users?first={at}&max=2&briefRepresentation=false"]
                    = () => Json(HttpStatusCode.OK, $"[{User("x" + at, Other)},{User("y" + at, at == 2 ? Tenant : Other)}]");
            }
            fake.Routes["/admin/realms/juice/roles/on-call/users?first=4&max=1&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, $"[{User("z", Tenant)}]");
            var (people, truncated) = await Directory(fake, Settings(pageSize: 2, maxScanned: 4)).RoleHoldersAsync("kc", Tenant, "on-call", None, Ct);
            Assert.Equal(["y2"], people.Select(p => p.ExternalId));
            Assert.True(truncated);
            // Past the limit, only whether anyone is left: one person, not a page.
            Assert.Equal(["first=0&max=2", "first=2&max=2", "first=4&max=1"], fake.Requests.Where(r => r.Contains("/roles/")).Select(r => r[(r.IndexOf('?') + 1)..r.IndexOf("&brief")]));
        }

        [Fact]
        public async Task A_listing_that_ends_at_the_scan_limit_is_not_cut_short()
        {
            var fake = new FakeKeycloak();
            fake.Routes["/admin/realms/juice/roles/on-call/users?first=0&max=2&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, $"[{User("a", Tenant)},{User("b", Tenant)}]");
            fake.Routes["/admin/realms/juice/roles/on-call/users?first=2&max=1&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, "[]");
            var (people, truncated) = await Directory(fake, Settings(pageSize: 2, maxScanned: 2)).RoleHoldersAsync("kc", Tenant, "on-call", None, Ct);
            Assert.Equal(["a", "b"], people.Select(p => p.ExternalId));
            Assert.False(truncated);
        }

        [Fact]
        public async Task A_name_Keycloak_refuses_with_400_reaches_no_one()
        {
            var fake = new FakeKeycloak();
            fake.Routes["/admin/realms/juice/roles/bad/users?first=0&max=100&briefRepresentation=false"] = () => Json(HttpStatusCode.BadRequest, "{}");
            fake.Routes["/admin/realms/juice/users/bad"] = () => Json(HttpStatusCode.BadRequest, "{}");
            var directory = Directory(fake);
            Assert.Empty((await directory.RoleHoldersAsync("kc", Tenant, "bad", None, Ct)).People);
            Assert.Empty(await directory.UserAsync("kc", Tenant, "bad", None, Ct));
        }

        [Fact]
        public async Task A_missing_group_role_or_user_and_a_user_of_another_tenant_reach_no_one()
        {
            var fake = new FakeKeycloak();
            fake.Routes["/admin/realms/juice/users/cara"] = () => Json(HttpStatusCode.OK, User("cara", Other));
            fake.Routes["/admin/realms/juice/users/ann"] = () => Json(HttpStatusCode.OK, User("ann", Tenant));
            var directory = Directory(fake);
            Assert.Empty((await directory.GroupMembersAsync("kc", Tenant, "nope", None, Ct)).People);
            Assert.Empty((await directory.RoleHoldersAsync("kc", Tenant, "nope", None, Ct)).People);
            Assert.Empty(await directory.UserAsync("kc", Tenant, "nope", None, Ct));
            Assert.Empty(await directory.UserAsync("kc", Tenant, "cara", None, Ct));
            Assert.Equal("ann", Assert.Single(await directory.UserAsync("kc", Tenant, "ann", None, Ct)).ExternalId);
            // One token for all of them.
            Assert.Equal(1, fake.Tokens);
        }

        [Fact]
        public async Task Names_are_escaped_into_the_path()
        {
            var fake = new FakeKeycloak();
            var directory = Directory(fake);
            await directory.UserAsync("kc", Tenant, "a?b#c d", None, Ct);
            await directory.GroupMembersAsync("kc", Tenant, "eng team/s?re", None, Ct);
            await directory.RoleHoldersAsync("kc", Tenant, "on/call", None, Ct);
            Assert.Contains("/admin/realms/juice/users/a%3Fb%23c%20d", fake.Requests);
            Assert.Contains("/admin/realms/juice/group-by-path/eng%20team/s%3Fre", fake.Requests);
            Assert.Contains("/admin/realms/juice/roles/on%2Fcall/users?first=0&max=100&briefRepresentation=false", fake.Requests);
        }

        [Fact]
        public async Task A_rejected_token_is_renewed_once()
        {
            var fake = new FakeKeycloak();
            var refused = 0;
            fake.Routes["/admin/realms/juice/users/ann"] = () => refused++ == 0
                ? Json(HttpStatusCode.Unauthorized, "{}")
                : Json(HttpStatusCode.OK, User("ann", Tenant));
            Assert.Single(await Directory(fake).UserAsync("kc", Tenant, "ann", None, Ct));
            Assert.Equal(2, fake.Tokens);

            var always = new FakeKeycloak();
            always.Routes["/admin/realms/juice/users/ann"] = () => Json(HttpStatusCode.Unauthorized, "{}");
            await Assert.ThrowsAsync<ResolverUnavailableException>(() => Directory(always).UserAsync("kc", Tenant, "ann", None, Ct));
            Assert.Equal(2, always.Tokens);
        }

        public static TheoryData<string> Failures => ["503", "429", "403", "token-401", "connection", "timeout", "not-json", "not-a-list", "no-secret"];

        [Theory]
        [MemberData(nameof(Failures))]
        public async Task An_outage_or_a_refusal_makes_the_directory_unavailable(string failure)
        {
            var fake = new FakeKeycloak();
            var settings = Settings();
            fake.Routes["/admin/realms/juice/roles/r/users?first=0&max=100&briefRepresentation=false"] = failure switch
            {
                "503" => () => Json(HttpStatusCode.ServiceUnavailable, "{}"),
                "429" => () => Json(HttpStatusCode.TooManyRequests, "{}"),
                "403" => () => Json(HttpStatusCode.Forbidden, "{}"),
                "not-json" => () => Json(HttpStatusCode.OK, "<html>"),
                "not-a-list" => () => Json(HttpStatusCode.OK, "{}"),
                _ => () => Json(HttpStatusCode.OK, "[]"),
            };
            fake.Before = failure switch
            {
                "token-401" => r => r.RequestUri!.AbsolutePath.EndsWith("/token") ? Json(HttpStatusCode.Unauthorized, """{ "error": "invalid_client" }""") : null,
                "connection" => _ => throw new HttpRequestException("Connection refused (keycloak.test:80)"),
                _ => null,
            };
            if (failure == "timeout")
            {
                settings.Timeout = TimeSpan.FromMilliseconds(100);
            }
            if (failure == "no-secret")
            {
                settings.ClientSecretRef = "env:NOTIFICATIONS_TEST_DIRECTORY_SECRET_UNSET";
            }
            var directory = failure == "timeout" ? Directory(new SlowKeycloak(), settings) : Directory(fake, settings);
            await Assert.ThrowsAsync<ResolverUnavailableException>(() => directory.RoleHoldersAsync("kc", Tenant, "r", None, Ct));
        }

        /// <summary>Answers nothing until cancelled.</summary>
        private sealed class SlowKeycloak : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException();
            }
        }

        [Fact]
        public void A_directory_must_be_complete_and_its_source_a_key()
        {
            Assert.Null(Settings().Problem());
            Assert.NotNull(new KeycloakDirectoryOptions().Problem());
            var bad = Settings();
            bad.BaseUrl = "ftp://keycloak.test";
            Assert.NotNull(bad.Problem());
            bad = Settings();
            bad.ClientSecretRef = "s3cret";
            Assert.NotNull(bad.Problem());
            bad = Settings();
            bad.Source = "OIDC";
            Assert.NotNull(bad.Problem());
            Assert.NotNull(Settings(pageSize: 0).Problem());
            Assert.NotNull(Settings(pageSize: 100, maxScanned: 50).Problem());
            bad = Settings();
            bad.BaseUrl = "http://keycloak.test/#x";
            Assert.NotNull(bad.Problem());
            bad = Settings();
            bad.Timeout = TimeSpan.MaxValue;
            Assert.NotNull(bad.Problem());
            // Both limits within the planner's lease.
            bad = Settings();
            bad.Timeout = TimeSpan.FromMinutes(6);
            Assert.NotNull(bad.Problem());
            bad = Settings();
            bad.NameBudget = TimeSpan.FromMinutes(6);
            Assert.NotNull(bad.Problem());
            // A longer lease may want longer limits: the step's deadline, not these, keeps the step within it.
            var longer = Settings();
            (longer.Timeout, longer.NameBudget) = (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
            Assert.Null(longer.Problem());
            bad = Settings();
            bad.NameBudget = TimeSpan.Zero;
            Assert.NotNull(bad.Problem());
        }

        [Fact]
        public async Task A_group_or_person_of_another_shape_is_unavailable_or_skipped_never_a_step_failure()
        {
            var fake = new FakeKeycloak();
            fake.Routes["/admin/realms/juice/group-by-path/list"] = () => Json(HttpStatusCode.OK, "[]");
            fake.Routes["/admin/realms/juice/group-by-path/number"] = () => Json(HttpStatusCode.OK, """{ "id": 7 }""");
            fake.Routes["/admin/realms/juice/roles/r/users?first=0&max=100&briefRepresentation=false"] = () => Json(HttpStatusCode.OK,
                $"[{{ \"id\": 7, \"enabled\": true }}, {User("ann", Tenant)}]");
            var directory = Directory(fake);
            await Assert.ThrowsAsync<ResolverUnavailableException>(() => directory.GroupMembersAsync("kc", Tenant, "list", None, Ct));
            await Assert.ThrowsAsync<ResolverUnavailableException>(() => directory.GroupMembersAsync("kc", Tenant, "number", None, Ct));
            Assert.Equal(["ann"], (await directory.RoleHoldersAsync("kc", Tenant, "r", None, Ct)).People.Select(p => p.ExternalId));
        }

        // The resolver.

        private static IdentityRecipientResolver Resolver(FakeKeycloak fake, int cacheSeconds = 0)
            => new(Directory(fake), new ResolverCache(Options.Create(new ResolverOptions { CacheSeconds = cacheSeconds }), TimeProvider.System));

        private static ResolveContext Context(string settings) => new(null!,
            new NotificationRequest { TenantId = Tenant, IdempotencyKey = "k", TypeKey = "t", SchemaHash = "h", Data = "{}", Recipients = "[]" },
            DateTimeOffset.UtcNow,
            new RecipientResolverConfig { TenantId = Tenant, Key = "people", Kind = "identity", Settings = settings, DefinitionHash = "h" }, 10);

        [Fact]
        public void Its_settings_name_a_directory_the_deployment_configures_and_nothing_else()
        {
            var resolver = Resolver(new FakeKeycloak());
            Assert.Null(resolver.CheckSettings("""{ "directory": "kc" }"""));
            Assert.Contains("kc", resolver.CheckSettings("""{ "directory": "other" }"""));
            Assert.NotNull(resolver.CheckSettings("{}"));
            Assert.NotNull(resolver.CheckSettings("""{ "directory": 1 }"""));
            Assert.NotNull(resolver.CheckSettings("""{ "directory": "kc", "baseUrl": "http://evil.test" }"""));
        }

        [Fact]
        public async Task It_resolves_each_kind_through_the_directory_and_asks_nothing_for_a_name_a_path_cant_carry()
        {
            var fake = new FakeKeycloak();
            fake.Routes["/admin/realms/juice/users/ann"] = () => Json(HttpStatusCode.OK, User("ann", Tenant));
            fake.Routes["/admin/realms/juice/roles/on-call/users?first=0&max=100&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, $"[{User("ben", Tenant)}]");
            var resolver = Resolver(fake);
            var context = Context("""{ "directory": "kc" }""");
            Assert.Equal("ann", Assert.Single((await resolver.ResolveAsync(new RecipientSpec(RecipientKinds.User, Id: "ann"), context, Ct)).People).ExternalId);
            Assert.Equal("ben", Assert.Single((await resolver.ResolveAsync(new RecipientSpec(RecipientKinds.Role, Id: "on-call"), context, Ct)).People).ExternalId);
            fake.Requests.Clear();
            foreach (var spec in new[] { new RecipientSpec(RecipientKinds.User, Id: ".."), new RecipientSpec(RecipientKinds.Group, Id: "ops/../x"),
                new RecipientSpec(RecipientKinds.Group, Id: "//ops"), new RecipientSpec(RecipientKinds.Group, Id: "/"), new RecipientSpec(RecipientKinds.Role, Id: ".") })
            {
                Assert.Empty((await resolver.ResolveAsync(spec, context, Ct)).People);
            }
            Assert.Empty(fake.Requests);
            // A group's path as Keycloak writes it, with its leading slash, is the same group.
            fake.Routes["/admin/realms/juice/group-by-path/ops"] = () => Json(HttpStatusCode.OK, """{ "id": "g1" }""");
            fake.Routes["/admin/realms/juice/groups/g1/members?first=0&max=100&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, $"[{User("ann", Tenant)}]");
            Assert.Single((await resolver.ResolveAsync(new RecipientSpec(RecipientKinds.Group, Id: "/ops"), context, Ct)).People);
            // A directory the deployment no longer has: unavailable, so the name waits and is then recorded unreached.
            await Assert.ThrowsAsync<ResolverUnavailableException>(async ()
                => await resolver.ResolveAsync(new RecipientSpec(RecipientKinds.User, Id: "ann"), Context("""{ "directory": "gone" }"""), Ct));
        }

        [Fact]
        public async Task An_expansion_is_cached_and_an_outage_is_not()
        {
            var fake = new FakeKeycloak();
            var calls = 0;
            fake.Routes["/admin/realms/juice/group-by-path/ops"] = () => calls++ == 0
                ? Json(HttpStatusCode.ServiceUnavailable, "{}")
                : Json(HttpStatusCode.OK, """{ "id": "g1" }""");
            fake.Routes["/admin/realms/juice/groups/g1/members?first=0&max=100&briefRepresentation=false"] = () => Json(HttpStatusCode.OK, $"[{User("ann", Tenant)}]");
            var resolver = Resolver(fake, cacheSeconds: 300);
            var context = Context("""{ "directory": "kc" }""");
            var spec = new RecipientSpec(RecipientKinds.Group, Id: "ops");
            await Assert.ThrowsAsync<ResolverUnavailableException>(async () => await resolver.ResolveAsync(spec, context, Ct));
            Assert.Single((await resolver.ResolveAsync(spec, context, Ct)).People);
            Assert.Single((await resolver.ResolveAsync(spec, context, Ct)).People);
            Assert.Equal(2, calls);
        }
    }
}
