using System.Net;
using System.Net.Http.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Providers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>A host whose producer alias (Observability's webhooks) has its legacy API's secret limits, as Observability sets them.</summary>
    public sealed class AliasSecretApp : IAsyncLifetime
    {
        public const string Prefix = "JUICE_WEBHOOK_";

        /// <summary>The alias's secret folder (QA M46-3), with one secret file in it.</summary>
        public static readonly string Folder = Path.Combine(Path.GetTempPath(), "notifications-alias-secrets-" + Environment.ProcessId);
        public const string FileSecret = "legacy-file-secret-for-qa";

        public NotificationsApp App { get; } = new(host =>
        {
            host.UseSetting("Delivery:AliasSecretEnvPrefix", Prefix);
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, "hook"), FileSecret);
            host.UseSetting("Delivery:AliasSecretDirectory", Folder);
            host.ConfigureLogging(logging => logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace).AddProvider(new EverythingLogged()));
        });

        /// <summary>Every log line the host writes, at every level, for the leak check.</summary>
        public static System.Collections.Concurrent.ConcurrentQueue<string> Logged { get; } = new();

        private sealed class EverythingLogged : Microsoft.Extensions.Logging.ILoggerProvider
        {
            public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger();

            public void Dispose()
            {
            }

            private sealed class Logger : Microsoft.Extensions.Logging.ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

                public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
                    Func<TState, Exception?, string> formatter)
                    => Logged.Enqueue($"{formatter(state, exception)} {exception}");
            }
        }

        public async ValueTask InitializeAsync()
        {
            await App.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            (await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(ProducerApp.Key, "Sample", "sample-producer"))).EnsureSuccessStatusCode();
            (await admin.PutAsync($"/api/v1/applications/{ProducerApp.Key}/tenants/{ProducerApp.Tenant}", null)).EnsureSuccessStatusCode();
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>
    /// Secrets are scoped by who writes the row (phase-5 §5.3, M46-3): the API within <c>Delivery:ApiSecret*</c>, the deployment's configuration
    /// any, and a producer's compatibility alias within <c>Delivery:AliasSecret*</c>, none when those are unset. A row the alias wrote is
    /// signed at send time with the secret it names, and the admin API can neither change it nor point a target of its own at its secret.
    /// </summary>
    public sealed class AliasSecretScopeTests(AliasSecretApp alias, ProducerApp unscoped) : IClassFixture<AliasSecretApp>, IClassFixture<ProducerApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string Tenant = $"/api/v1/tenants/{ProducerApp.Tenant}";
        private const string Variable = AliasSecretApp.Prefix + "M46_3";
        private const string Secret = "legacy-signing-secret";

        static AliasSecretScopeTests() => Environment.SetEnvironmentVariable(Variable, Secret);

        private static string Short() => Guid.NewGuid().ToString("N")[..8];

        private static async Task<Outcome<T>> AsAliasAsync<T>(NotificationsApp app, Juice.MediatR.IRequest<Outcome<T>> command)
        {
            await using var scope = app.Services.CreateAsyncScope();
            using var context = NotificationsMessageContext.Begin("webhooks-alias-test");
            return (await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>().Send(command, Ct))!;
        }

        private static PutIntegration Hook(string key, string secretRef)
            => new(ProducerApp.Tenant, key, "webhook", "webhook", null, secretRef, null, null) { ManagedBy = ManagedBy.WebhooksAlias };

        [Fact]
        public async Task The_api_may_not_name_the_aliases_secrets()
        {
            LocalStack.RequireAvailable();
            using var admin = alias.App.Client(await LocalStack.AdminTokenAsync());

            using var response = await admin.PutAsJsonAsync($"{Tenant}/integrations/api-{Short()}", new PutIntegrationBody("webhook", "webhook", null, $"env:{Variable}", null, null), Ct);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("/secretRef", await response.Content.ReadAsStringAsync(Ct));
        }

        [Fact]
        public async Task The_alias_names_its_own_secrets_and_its_webhook_is_signed_with_them()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await DeliveryTests.Receiver.StartAsync(200);
            using var admin = alias.App.Client(await LocalStack.AdminTokenAsync());
            using var producer = alias.App.Client(await LocalStack.ProducerTokenAsync());
            var (integration, target, route, type) = ($"legacy-{Short()}", $"t-{Short()}", $"r-{Short()}", $"{ProducerApp.Key}.a{Short()}");
            await DeliveryTests.Ok(admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port]), Ct));

            var created = await AsAliasAsync(alias.App, Hook(integration, $"env:{Variable}"));
            Assert.True(created.Kind is OutcomeKind.Ok or OutcomeKind.Created, $"{created.Kind} {created.Code} {created.Message}");
            Assert.Contains((await AsAliasAsync(alias.App,
                new PutTarget(ProducerApp.Tenant, integration, target, receiver.Url, null, null) { ManagedBy = ManagedBy.WebhooksAlias })).Kind, new[] { OutcomeKind.Ok, OutcomeKind.Created });
            await DeliveryTests.Ok(producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(type, "Legacy", "alerts", null, "high",
                null, null, DeliveryTests.Json("""{ "type": "object" }"""), null, null, null, DeliveryTests.Json("""{ "webhook": "passthrough" }""")), Ct));
            Assert.Contains((await AsAliasAsync(alias.App,
                new PutRoute(ProducerApp.Tenant, route, DeliveryTests.Json($$"""{ "type": "{{type}}" }"""), [target], null) { ManagedBy = ManagedBy.WebhooksAlias })).Kind, new[] { OutcomeKind.Ok, OutcomeKind.Created });
            var accepted = await (await producer.PostAsJsonAsync($"{Tenant}/notifications", new SendNotificationBody(type, null, Short(), null, null, null, null,
                null, DeliveryTests.Json("""{ "host": "db-1" }"""), null, null), Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            var detail = await DeliveryTests.DeliveredAsync(producer, accepted!.Id);

            Assert.Equal(DeliveryStatus.Delivered, Assert.Single(detail.Deliveries).Status);
            var request = Assert.Single(receiver.Requests);
            // Signed at send time with the alias's secret: resolution applies no API limit.
            Assert.True(WebhookSigner.Verify(Secret, request.Id, request.Timestamp, request.Signature, request.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));

            // The admin API leaves the alias's row alone, and can't send to an address of its own with the alias's secret either.
            using (var edit = await admin.PutAsJsonAsync($"{Tenant}/integrations/{integration}", new PutIntegrationBody("webhook", "webhook", null, null, null, false), Ct))
            {
                Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
                Assert.Contains(ErrorCodes.ManagedByAlias, await edit.Content.ReadAsStringAsync(Ct));
            }
            using (var own = await admin.PutAsJsonAsync($"{Tenant}/integrations/{integration}/targets/admin-{Short()}", new PutTargetBody("https://elsewhere.example/hook", null, null), Ct))
            {
                // Refused on the integration's signing secret, with the alias's remedy, not the API's.
                Assert.Equal(HttpStatusCode.BadRequest, own.StatusCode);
                var problem = await own.Content.ReadAsStringAsync(Ct);
                Assert.Contains("\"/integration\"", problem);
                Assert.Contains("through the producer's own API", problem);
            }
        }

        [Fact]
        public async Task Without_the_alias_limits_the_alias_names_no_secret()
        {
            LocalStack.RequireAvailable();

            foreach (var reference in new[] { $"env:{Variable}", TestHooks.SecretRef, "file:/run/secrets/hook" })
            {
                var outcome = await AsAliasAsync(unscoped.App, Hook($"legacy-{Short()}", reference));
                Assert.True(outcome.Kind == OutcomeKind.Invalid, $"{reference}: {outcome.Kind} {outcome.Code}");
                Assert.Contains(outcome.Errors!, e => e.Pointer == "/secretRef");
            }
        }

        [Fact]
        public async Task The_alias_names_nothing_outside_its_limits()
        {
            LocalStack.RequireAvailable();

            // The API's own prefix is no alias secret.
            var outcome = await AsAliasAsync(alias.App, Hook($"legacy-{Short()}", TestHooks.SecretRef));

            Assert.True(outcome.Kind == OutcomeKind.Invalid, $"{outcome.Kind} {outcome.Code}");
        }

        // QA (M46-3).

        /// <summary>
        /// An alias <c>file:</c> reference is a file directly in the alias's folder, written as an absolute path: one climbing out with <c>..</c>,
        /// a relative one, one in a sub-folder, or one elsewhere is refused at <c>/secretRef</c>; the API may name none of the alias's files.
        /// </summary>
        [Fact]
        public async Task An_alias_file_reference_is_only_a_file_directly_in_its_folder()
        {
            LocalStack.RequireAvailable();
            var folder = AliasSecretApp.Folder;
            var inFolder = await AsAliasAsync(alias.App, Hook($"legacy-{Short()}", "file:" + Path.Combine(folder, "hook")));
            Assert.True(inFolder.Kind is OutcomeKind.Ok or OutcomeKind.Created, $"{inFolder.Kind} {inFolder.Code}");
            foreach (var reference in new[]
            {
                "file:" + Path.Combine(folder, "..", Path.GetFileName(folder), "hook"),
                "file:" + Path.Combine(folder, "sub", "hook"),
                "file:hook",
                "file:" + Path.Combine("..", "hook"),
                "file:" + Path.Combine(Path.GetTempPath(), "hook"),
                "file:" + folder,
            })
            {
                var outcome = await AsAliasAsync(alias.App, Hook($"legacy-{Short()}", reference));
                Assert.True(outcome.Kind == OutcomeKind.Invalid && outcome.Errors!.Any(e => e.Pointer == "/secretRef"), $"{reference}: {outcome.Kind} {outcome.Code}");
            }
            using var admin = alias.App.Client(await LocalStack.AdminTokenAsync());
            using var api = await admin.PutAsJsonAsync($"{Tenant}/integrations/api-{Short()}",
                new PutIntegrationBody("webhook", "webhook", null, "file:" + Path.Combine(folder, "hook"), null, null), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, api.StatusCode);
        }

        /// <summary>
        /// No secret value is ever shown: after an alias webhook signed with a file secret is delivered, and after the API is refused naming an
        /// alias secret, neither the env secret nor the file secret appears in any answer the admin and producer read (the integration, its
        /// target, the deliveries and their detail, the refusals), in the stored rows, or in anything the host logged at any level.
        /// </summary>
        [Fact]
        public async Task No_secret_value_appears_in_any_answer_row_or_log()
        {
            LocalStack.RequireAvailable();
            await using var receiver = await DeliveryTests.Receiver.StartAsync(200);
            using var admin = alias.App.Client(await LocalStack.AdminTokenAsync());
            using var producer = alias.App.Client(await LocalStack.ProducerTokenAsync());
            var (integration, target, route, type) = ($"legacy-{Short()}", $"t-{Short()}", $"r-{Short()}", $"{ProducerApp.Key}.l{Short()}");
            var answers = new List<string>();
            async Task<string> Read(HttpResponseMessage response)
            {
                var text = await response.Content.ReadAsStringAsync(Ct);
                answers.Add(text);
                response.Dispose();
                return text;
            }
            await Read(await admin.PutAsJsonAsync($"{Tenant}/allowlist", new PutAllowlistBody(receiver.Address.ToString(), [receiver.Port]), Ct));
            Assert.True((await AsAliasAsync(alias.App, Hook(integration, "file:" + Path.Combine(AliasSecretApp.Folder, "hook")))).Kind is OutcomeKind.Ok or OutcomeKind.Created);
            await AsAliasAsync(alias.App, new PutTarget(ProducerApp.Tenant, integration, target, receiver.Url, null, null) { ManagedBy = ManagedBy.WebhooksAlias });
            await DeliveryTests.Ok(producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(type, "Legacy", "alerts", null, "high",
                null, null, DeliveryTests.Json("""{ "type": "object" }"""), null, null, null, DeliveryTests.Json("""{ "webhook": "passthrough" }""")), Ct));
            await AsAliasAsync(alias.App, new PutRoute(ProducerApp.Tenant, route, DeliveryTests.Json($$"""{ "type": "{{type}}" }"""), [target], null) { ManagedBy = ManagedBy.WebhooksAlias });
            var accepted = await (await producer.PostAsJsonAsync($"{Tenant}/notifications", new SendNotificationBody(type, null, Short(), null, null, null, null,
                null, DeliveryTests.Json("""{ "host": "db-1" }"""), null, null), Ct)).Content.ReadFromJsonAsync<AcceptedResponse>(Ct);
            var detail = await DeliveryTests.DeliveredAsync(producer, accepted!.Id);
            var request = Assert.Single(receiver.Requests);
            Assert.True(WebhookSigner.Verify(AliasSecretApp.FileSecret, request.Id, request.Timestamp, request.Signature, request.Body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));

            await Read(await admin.GetAsync($"{Tenant}/integrations", Ct));
            await Read(await admin.GetAsync($"{Tenant}/integrations/{integration}/targets", Ct));
            await Read(await admin.GetAsync($"{Tenant}/deliveries?limit=100", Ct));
            await Read(await admin.GetAsync($"{Tenant}/deliveries/{Assert.Single(detail.Deliveries).Id}", Ct));
            await Read(await producer.GetAsync($"{Tenant}/notifications/{accepted.Id}", Ct));
            await Read(await admin.PutAsJsonAsync($"{Tenant}/integrations/api-{Short()}", new PutIntegrationBody("webhook", "webhook", null, $"env:{Variable}", null, null), Ct));
            await Read(await admin.PutAsJsonAsync($"{Tenant}/integrations/{integration}/targets/admin-{Short()}", new PutTargetBody("https://elsewhere.example/hook", null, null), Ct));

            await using var db = alias.App.Database.Context();
            var rows = await db.Database.SqlQuery<string>($"""
                SELECT coalesce(string_agg(t::text, ' '), '') AS "Value" FROM (
                    SELECT row_to_json(i)::text AS t FROM notifications.integrations i
                    UNION ALL SELECT row_to_json(x)::text FROM notifications.integration_targets x
                    UNION ALL SELECT row_to_json(d)::text FROM notifications.deliveries d
                    UNION ALL SELECT row_to_json(a)::text FROM notifications.delivery_attempts a) s
                """).SingleAsync(Ct);
            foreach (var secret in new[] { Secret, AliasSecretApp.FileSecret })
            {
                Assert.All(answers, a => Assert.DoesNotContain(secret, a, StringComparison.Ordinal));
                Assert.DoesNotContain(secret, rows, StringComparison.Ordinal);
                Assert.DoesNotContain(AliasSecretApp.Logged, line => line.Contains(secret, StringComparison.Ordinal));
            }
            Assert.NotEmpty(AliasSecretApp.Logged);
        }
    }
}
