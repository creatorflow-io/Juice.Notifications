using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The producer's hold and release over HTTP (phase-5-plan p5-d3; m44-architecture §3.3): <c>POST …/correlations/{key}/hold</c> and
    /// <c>…/release</c>, the producer's own application only, the hold shown on the correlation's reads and in the tenant's list.
    /// </summary>
    public sealed class CorrelationHoldApiTests(ProducerApp fixture) : IClassFixture<ProducerApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string Tenant = $"/api/v1/tenants/{ProducerApp.Tenant}";

        private static async Task<T> OkAsync<T>(Task<HttpResponseMessage> call)
        {
            var response = await call;
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
            return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
        }

        /// <summary>The status, problem code and pointers (comma-separated) of a refusal.</summary>
        private static async Task<(HttpStatusCode Status, string Code, string Pointers)> ProblemAsync(Task<HttpResponseMessage> call)
        {
            var response = await call;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var pointers = json.RootElement.TryGetProperty("errors", out var errors)
                ? string.Join(",", errors.EnumerateArray().Select(e => e.GetProperty("pointer").GetString()!))
                : "";
            return (response.StatusCode, json.RootElement.TryGetProperty("code", out var code) ? code.GetString()! : "", pointers);
        }

        /// <summary>A correlation of <paramref name="application"/> (the sample producer's by default) in the tenant; its key.</summary>
        private async Task<string> CorrelationAsync(string? key = null, string application = ProducerApp.Key)
        {
            key ??= "alert:" + Guid.NewGuid().ToString("N");
            await using var db = fixture.App.Database.Context();
            var app = await db.Applications.SingleOrDefaultAsync(a => a.Key == application, Ct);
            if (app is null)
            {
                app = Seed.Application(application);
                db.AddRange(app, new ApplicationTenant { ApplicationId = app.Id, TenantId = ProducerApp.Tenant, LinkedAt = Seed.Now });
                await db.SaveChangesAsync(Ct);
            }
            await Correlations.CorrelationBook.ForRequestAsync(db, ProducerApp.Tenant, app.Id, key, DateTimeOffset.UtcNow, Ct);
            await db.SaveChangesAsync(Ct);
            return key;
        }

        private static string Path(string key) => $"{Tenant}/correlations/{Uri.EscapeDataString(key)}";

        [Fact]
        public async Task A_hold_is_shown_on_the_correlation_and_in_the_list_and_a_release_clears_it()
        {
            LocalStack.RequireAvailable();
            var key = await CorrelationAsync();
            using var producer = await fixture.ProducerAsync();

            var held = await OkAsync<CorrelationResponse>(producer.PostAsJsonAsync($"{Path(key)}/hold", new HoldCorrelationBody("silence:7", "obs"), Ct));
            Assert.Equal((CorrelationState.Open, "silence:7", "obs"), (held.State, held.HoldReason, held.HeldBy));
            Assert.NotNull(held.HeldAt);
            var read = await OkAsync<CorrelationResponse>(producer.GetAsync(Path(key), Ct));
            Assert.Equal((held.HeldAt, "silence:7"), (read.HeldAt, read.HoldReason));
            // As the API writes it: camelCase.
            var raw = await producer.GetStringAsync(Path(key), Ct);
            Assert.Contains("\"holdReason\":\"silence:7\"", raw);

            using var admin = fixture.App.Client(await LocalStack.TenantAdminTokenAsync());
            var page = await OkAsync<CorrelationPageResponse>(admin.GetAsync($"{Tenant}/correlations?key={Uri.EscapeDataString(key)}", Ct));
            Assert.Equal(held.HeldAt, Assert.Single(page.Items).HeldAt);

            // Again: no change, the same answer.
            var again = await OkAsync<CorrelationResponse>(producer.PostAsJsonAsync($"{Path(key)}/hold", new HoldCorrelationBody("silence:7", "someone else"), Ct));
            Assert.Equal((held.HeldAt, "obs"), (again.HeldAt, again.HeldBy));

            // A release needs no body.
            var released = await OkAsync<CorrelationResponse>(producer.PostAsync($"{Path(key)}/release", null, Ct));
            Assert.Equal(((DateTimeOffset?)null, (string?)null, (string?)null), (released.HeldAt, released.HoldReason, released.HeldBy));
            Assert.Null(Assert.Single((await OkAsync<CorrelationPageResponse>(admin.GetAsync($"{Tenant}/correlations?key={Uri.EscapeDataString(key)}", Ct))).Items).HeldAt);
            Assert.Null((await OkAsync<CorrelationResponse>(producer.PostAsJsonAsync($"{Path(key)}/release", new ReleaseCorrelationBody("obs"), Ct))).HeldAt);
        }

        [Fact]
        public async Task A_bad_reason_or_by_is_refused_at_its_field()
        {
            LocalStack.RequireAvailable();
            var key = await CorrelationAsync();
            using var producer = await fixture.ProducerAsync();
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest, "/reason"),
                await ProblemAsync(producer.PostAsJsonAsync($"{Path(key)}/hold", new HoldCorrelationBody(null, "obs"), Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest, "/reason"),
                await ProblemAsync(producer.PostAsJsonAsync($"{Path(key)}/hold", new HoldCorrelationBody(new string('r', 201), null), Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest, "/reason"), await ProblemAsync(producer.PostAsync($"{Path(key)}/hold", null, Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest, "/by"),
                await ProblemAsync(producer.PostAsJsonAsync($"{Path(key)}/hold", new HoldCorrelationBody("silence:7", " obs"), Ct)));
            Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest, "/by"),
                await ProblemAsync(producer.PostAsJsonAsync($"{Path(key)}/release", new ReleaseCorrelationBody(""), Ct)));
            Assert.Null((await OkAsync<CorrelationResponse>(producer.GetAsync(Path(key), Ct))).HeldAt);
        }

        [Fact]
        public async Task Only_the_producer_holds_its_own_correlations()
        {
            LocalStack.RequireAvailable();
            var key = await CorrelationAsync();
            var others = await CorrelationAsync(application: "hold-other");
            using var producer = await fixture.ProducerAsync();

            // Another application's key, and a key no one has: not found.
            Assert.Equal((HttpStatusCode.NotFound, ErrorCodes.NotFound, ""),
                await ProblemAsync(producer.PostAsJsonAsync($"{Path(others)}/hold", new HoldCorrelationBody("silence:7", null), Ct)));
            Assert.Equal((HttpStatusCode.NotFound, ErrorCodes.NotFound, ""),
                await ProblemAsync(producer.PostAsync($"{Path("nobody:" + Guid.NewGuid().ToString("N"))}/release", null, Ct)));

            // A tenant admin isn't a producer, even naming the application; nor is a person.
            using var admin = fixture.App.Client(await LocalStack.TenantAdminTokenAsync());
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotAProducer, ""),
                await ProblemAsync(admin.PostAsJsonAsync($"{Path(key)}/hold?application={ProducerApp.Key}", new HoldCorrelationBody("silence:7", null), Ct)));
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotAProducer, ""), await ProblemAsync(admin.PostAsync($"{Path(key)}/release", null, Ct)));
            using var person = fixture.App.Client(await LocalStack.PersonTokenAsync());
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.NotAProducer, ""),
                await ProblemAsync(person.PostAsJsonAsync($"{Path(key)}/hold", new HoldCorrelationBody("silence:7", null), Ct)));
            using var anonymous = fixture.App.Client();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{Path(key)}/hold", new HoldCorrelationBody("silence:7", null), Ct)).StatusCode);
            Assert.Null((await OkAsync<CorrelationResponse>(producer.GetAsync(Path(key), Ct))).HeldAt);
        }

        /// <summary>
        /// Another tenant's path: a token naming a tenant is refused before the endpoint (<c>tenant_forbidden</c>, the tenant pattern); a producer
        /// names any tenant, and there its key is no correlation of its (not found), so the hold in its own tenant is untouched.
        /// </summary>
        [Fact]
        public async Task Another_tenants_path_is_forbidden_to_a_tenant_token_and_holds_nothing_for_a_producer()
        {
            LocalStack.RequireAvailable();
            var key = await CorrelationAsync();
            var other = $"/api/v1/tenants/{ProducerApp.OtherTenant}/correlations/{Uri.EscapeDataString(key)}";
            using var admin = fixture.App.Client(await LocalStack.TenantAdminTokenAsync());
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.TenantForbidden, ""),
                await ProblemAsync(admin.PostAsJsonAsync($"{other}/hold", new HoldCorrelationBody("silence:7", null), Ct)));
            Assert.Equal((HttpStatusCode.Forbidden, ErrorCodes.TenantForbidden, ""), await ProblemAsync(admin.PostAsync($"{other}/release", null, Ct)));

            using var producer = await fixture.ProducerAsync();
            Assert.Equal((HttpStatusCode.NotFound, ErrorCodes.NotFound, ""),
                await ProblemAsync(producer.PostAsJsonAsync($"{other}/hold", new HoldCorrelationBody("silence:7", null), Ct)));
            await OkAsync<CorrelationResponse>(producer.PostAsJsonAsync($"{Path(key)}/hold", new HoldCorrelationBody("silence:7", null), Ct));
            Assert.Equal((HttpStatusCode.NotFound, ErrorCodes.NotFound, ""), await ProblemAsync(producer.PostAsync($"{other}/release", null, Ct)));
            Assert.Equal("silence:7", (await OkAsync<CorrelationResponse>(producer.GetAsync(Path(key), Ct))).HoldReason);
        }

        [Fact]
        public async Task A_disabled_application_still_holds_and_releases_and_a_key_with_a_slash_round_trips()
        {
            LocalStack.RequireAvailable();
            var key = await CorrelationAsync("ops/cpu%41high:" + Guid.NewGuid().ToString("N"));
            using var producer = await fixture.ProducerAsync();
            await using (var db = fixture.App.Database.Context())
            {
                await db.Applications.Where(a => a.Key == ProducerApp.Key).ExecuteUpdateAsync(s => s.SetProperty(a => a.Enabled, false), Ct);
            }
            try
            {
                var held = await OkAsync<CorrelationResponse>(producer.PostAsJsonAsync($"{Path(key)}/hold", new HoldCorrelationBody("silence:7", null), Ct));
                Assert.Equal((key, "silence:7"), (held.Key, held.HoldReason));
                Assert.Null((await OkAsync<CorrelationResponse>(producer.PostAsync($"{Path(key)}/release", null, Ct))).HeldAt);
            }
            finally
            {
                await using var db = fixture.App.Database.Context();
                await db.Applications.Where(a => a.Key == ProducerApp.Key).ExecuteUpdateAsync(s => s.SetProperty(a => a.Enabled, true), CancellationToken.None);
            }
        }

        // QA (M44-1): the forced race of m44-architecture §9 over HTTP, and calls at once.

        /// <summary>
        /// A release that meets another change of the correlation (an update held by another connection, committed while the release waits on
        /// it) answers 409 <c>concurrent_change</c>, and asking again answers on the new state. The same for a hold.
        /// </summary>
        [Theory]
        [InlineData("release")]
        [InlineData("hold")]
        public async Task A_hold_or_release_racing_another_change_is_409_concurrent_change_and_asking_again_answers(string op)
        {
            LocalStack.RequireAvailable();
            var key = await CorrelationAsync();
            using var producer = await fixture.ProducerAsync();
            if (op == "release")
            {
                await OkAsync<CorrelationResponse>(producer.PostAsJsonAsync($"{Path(key)}/hold", new HoldCorrelationBody("silence:7", null), Ct));
            }
            Task<HttpResponseMessage> Call() => op == "release"
                ? producer.PostAsync($"{Path(key)}/release", null, Ct)
                : producer.PostAsJsonAsync($"{Path(key)}/hold", new HoldCorrelationBody("silence:7", null), Ct);

            await using (var other = fixture.App.Database.Context())
            {
                await using var transaction = await other.Database.BeginTransactionAsync(Ct);
                await other.Database.ExecuteSqlAsync($"UPDATE notifications.correlations SET updated_at = now() WHERE key = {key}", Ct);
                var call = Call();
                await LockWaitAsync();
                await transaction.CommitAsync(Ct);
                Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.ConcurrentChange, ""), await ProblemAsync(call));
            }
            var again = await OkAsync<CorrelationResponse>(Call());
            Assert.Equal(op == "hold", again.HeldAt is not null);
        }

        /// <summary>Holds and releases of one correlation at once: each answers 200 or 409 <c>concurrent_change</c>, never an error, and a last release clears it.</summary>
        [Fact]
        public async Task Holds_and_releases_at_once_answer_200_or_409_concurrent_change()
        {
            LocalStack.RequireAvailable();
            var key = await CorrelationAsync();
            using var producer = await fixture.ProducerAsync();
            var answers = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => i % 2 == 0
                ? ProblemOrOkAsync(producer.PostAsJsonAsync($"{Path(key)}/hold", new HoldCorrelationBody("silence:" + (i % 3), null), Ct))
                : ProblemOrOkAsync(producer.PostAsync($"{Path(key)}/release", null, Ct))));
            Assert.All(answers, a => Assert.True(a is (HttpStatusCode.OK, "") or (HttpStatusCode.Conflict, ErrorCodes.ConcurrentChange), $"{a.Status} {a.Code}"));
            Assert.Contains(answers, a => a.Status == HttpStatusCode.OK);

            Assert.Null((await OkAsync<CorrelationResponse>(producer.PostAsync($"{Path(key)}/release", null, Ct))).HeldAt);
            Assert.Null((await OkAsync<CorrelationResponse>(producer.GetAsync(Path(key), Ct))).HeldAt);

            static async Task<(HttpStatusCode Status, string Code)> ProblemOrOkAsync(Task<HttpResponseMessage> call)
            {
                var response = await call;
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return (HttpStatusCode.OK, "");
                }
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                return (response.StatusCode, json.RootElement.TryGetProperty("code", out var code) ? code.GetString()! : "");
            }
        }

        /// <summary>Waits until a statement of this database waits on a row lock: the call has read and is blocked on its update.</summary>
        private async Task LockWaitAsync()
        {
            await using var db = fixture.App.Database.Context();
            for (var i = 0; i < 100; i++)
            {
                if (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'")
                    .SingleAsync(Ct) > 0)
                {
                    return;
                }
                await Task.Delay(50, Ct);
            }
            Assert.Fail("The call never waited on the other transaction.");
        }
    }
}
