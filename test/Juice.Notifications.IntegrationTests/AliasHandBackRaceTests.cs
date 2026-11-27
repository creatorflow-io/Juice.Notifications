using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Shared setup for the hand-back tests: a host, and an alias endpoint declared in the alias's configuration.</summary>
    public abstract class AliasHandBackBase : IAsyncLifetime
    {
        protected static CancellationToken Ct => TestContext.Current.CancellationToken;
        protected const string AC = ManagedBy.AliasConfig;
        protected const string Alias = ManagedBy.WebhooksAlias;
        protected readonly Guid Tenant = Guid.NewGuid();
        protected readonly NotificationsApp App = new();
        protected static readonly JsonElement Settings = JsonElement.Parse("""{ "server": "local", "from": "ac@juice.local" }""");

        public ValueTask InitializeAsync() => App.InitializeAsync();

        public ValueTask DisposeAsync() => App.DisposeAsync();

        protected async Task<Outcome<T>> SendAsync<T>(Juice.MediatR.IRequest<Outcome<T>> command)
        {
            await using var scope = App.Services.CreateAsyncScope();
            using var context = NotificationsMessageContext.Begin("qa");
            return (await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>().Send(command, Ct))!;
        }

        protected async Task SeedEndpointAsync(string key, string owner = AC, Guid? tenant = null)
        {
            var t = tenant ?? Tenant;
            Assert.Contains((await SendAsync(new PutIntegration(t, key, "email", "smtp", Settings, null, 0, true) { ManagedBy = owner })).Kind, Made);
            Assert.Contains((await SendAsync(new PutTarget(t, key, key + "-t1", "a@example.com", null, true) { ManagedBy = owner })).Kind, Made);
            Assert.Contains((await SendAsync(new PutTarget(t, key, key + "-t2", "b@example.com", null, true) { ManagedBy = owner })).Kind, Made);
            Assert.Contains((await SendAsync(new PutRoute(t, key, JsonElement.Parse("""{"type":"x.*"}"""), [key + "-t1", key + "-t2"], true) { ManagedBy = owner })).Kind, Made);
        }

        private static readonly OutcomeKind[] Made = [OutcomeKind.Ok, OutcomeKind.Created];

        protected Task<Outcome<HandBackView>> HandBackAsync(string key, Guid? tenant = null, string? caller = AC)
            => SendAsync(new HandBackAliasConfig(tenant ?? Tenant, key) { ManagedBy = caller });

        /// <summary>The owner and enabled flag of an endpoint's integration, targets and route.</summary>
        protected async Task<(string Owner, bool Enabled)[]> StateAsync(string key, Guid? tenant = null)
        {
            var t = tenant ?? Tenant;
            await using var db = App.Database.Context();
            var integration = await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == t && i.Key == key, Ct);
            var targets = await db.IntegrationTargets.AsNoTracking().Where(x => x.TenantId == t && x.IntegrationId == integration.Id).OrderBy(x => x.Key).ToListAsync(Ct);
            var route = await db.Routes.AsNoTracking().SingleAsync(r => r.TenantId == t && r.Key == key, Ct);
            return [(integration.ManagedBy, integration.Enabled), .. targets.Select(x => (x.ManagedBy, x.Enabled)), (route.ManagedBy, route.Enabled)];
        }
    }

    /// <summary>The hand-back of an alias-config endpoint to the alias's API (phase-5 M46c): which rows move, and who may change them after.</summary>
    public sealed class AliasHandBackTests : AliasHandBackBase
    {
        [Fact]
        public async Task HandBack_MovesIntegrationTargetsAndRoute_ToWebhooksAlias_Disabled()
        {
            LocalStack.RequireAvailable();
            await SeedEndpointAsync("hook");
            var outcome = await HandBackAsync("hook");
            Assert.Equal(OutcomeKind.Ok, outcome.Kind);
            Assert.Equal(new HandBackView("hook", 1, 2, 1), outcome.Value);
            Assert.All(await StateAsync("hook"), s => Assert.Equal((Alias, false), s));

            // The hashes are those of the rows as they now are, so the next definition compare sees no drift.
            await using var db = App.Database.Context();
            var integration = await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == Tenant && i.Key == "hook", Ct);
            Assert.Equal(ConfigurationRules.IntegrationHash(integration), integration.DefinitionHash);
            foreach (var target in await db.IntegrationTargets.AsNoTracking().Where(t => t.TenantId == Tenant).ToListAsync(Ct))
            {
                Assert.Equal(ConfigurationRules.TargetHash(target), target.DefinitionHash);
            }
            var route = await db.Routes.AsNoTracking().SingleAsync(r => r.TenantId == Tenant && r.Key == "hook", Ct);
            Assert.Equal(ConfigurationRules.RouteHash(route), route.DefinitionHash);
        }

        [Fact]
        public async Task HandBack_Twice_IsNoOp()
        {
            LocalStack.RequireAvailable();
            await SeedEndpointAsync("hook");
            await HandBackAsync("hook");
            await using var before = App.Database.Context();
            var stamp = (await before.Routes.AsNoTracking().SingleAsync(r => r.TenantId == Tenant && r.Key == "hook", Ct)).UpdatedAt;
            var again = await HandBackAsync("hook");
            Assert.Equal(OutcomeKind.Ok, again.Kind);
            Assert.Equal(new HandBackView("hook", 0, 0, 0), again.Value);
            await using var after = App.Database.Context();
            Assert.Equal(stamp, (await after.Routes.AsNoTracking().SingleAsync(r => r.TenantId == Tenant && r.Key == "hook", Ct)).UpdatedAt);
        }

        [Fact]
        public async Task HandBack_LeavesApiConfigAndOtherAliasRows()
        {
            LocalStack.RequireAvailable();
            await SeedEndpointAsync("hook");
            await SeedEndpointAsync("other"); // alias-config, another key
            await SeedEndpointAsync("api-made", owner: Alias);
            // The same key made by the API: an integration the alias's file doesn't own is left whatever its key.
            Assert.Contains((await SendAsync(new PutIntegration(Tenant, "by-api", "email", "smtp", Settings, null, 0, true))).Kind, new[] { OutcomeKind.Ok, OutcomeKind.Created });
            Assert.Contains((await SendAsync(new PutIntegration(Tenant, "by-config", "email", "smtp", Settings, null, 0, true) { ManagedBy = ManagedBy.Configuration })).Kind,
                new[] { OutcomeKind.Ok, OutcomeKind.Created });

            Assert.Equal(new HandBackView("hook", 1, 2, 1), (await HandBackAsync("hook")).Value);
            Assert.Equal(new HandBackView("by-api", 0, 0, 0), (await HandBackAsync("by-api")).Value);
            Assert.Equal(new HandBackView("by-config", 0, 0, 0), (await HandBackAsync("by-config")).Value);
            Assert.Equal(new HandBackView("api-made", 0, 0, 0), (await HandBackAsync("api-made")).Value);

            Assert.All(await StateAsync("other"), s => Assert.Equal((AC, true), s));
            Assert.All(await StateAsync("api-made"), s => Assert.Equal((Alias, true), s));
            await using var db = App.Database.Context();
            var byApi = await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == Tenant && i.Key == "by-api", Ct);
            var byConfig = await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == Tenant && i.Key == "by-config", Ct);
            Assert.Equal((ManagedBy.Api, true), (byApi.ManagedBy, byApi.Enabled));
            Assert.Equal((ManagedBy.Configuration, true), (byConfig.ManagedBy, byConfig.Enabled));
        }

        [Fact]
        public async Task HandBack_UnknownKey_OkZero()
        {
            LocalStack.RequireAvailable();
            var outcome = await HandBackAsync("no-such-endpoint");
            Assert.Equal(OutcomeKind.Ok, outcome.Kind);
            Assert.Equal(new HandBackView("no-such-endpoint", 0, 0, 0), outcome.Value);
        }

        [Fact]
        public async Task HandBack_ByApiOrWebhooksAlias_RefusedManagedByConfiguration()
        {
            LocalStack.RequireAvailable();
            await SeedEndpointAsync("hook");
            foreach (var caller in new string?[] { null, ManagedBy.Api, Alias, ManagedBy.Configuration })
            {
                var outcome = await HandBackAsync("hook", caller: caller);
                Assert.Equal((OutcomeKind.Conflict, ErrorCodes.ManagedByConfiguration), (outcome.Kind, outcome.Code));
            }
            Assert.All(await StateAsync("hook"), s => Assert.Equal((AC, true), s));
        }

        [Fact]
        public async Task HandBack_OtherTenantId_TouchesNothingOfThisTenant()
        {
            LocalStack.RequireAvailable();
            var other = Guid.NewGuid();
            await SeedEndpointAsync("hook");
            await SeedEndpointAsync("hook", tenant: other);
            Assert.Equal(new HandBackView("hook", 1, 2, 1), (await HandBackAsync("hook", tenant: other)).Value);
            Assert.All(await StateAsync("hook"), s => Assert.Equal((AC, true), s));
            Assert.All(await StateAsync("hook", tenant: other), s => Assert.Equal((Alias, false), s));
        }

        [Fact]
        public async Task AfterHandBack_AliasPut_Succeeds_AdminPut_ManagedByAlias()
        {
            LocalStack.RequireAvailable();
            await SeedEndpointAsync("hook");
            await HandBackAsync("hook");

            // The alias's API (webhooks-alias) now edits and re-enables it.
            var enabled = await SendAsync(new PutIntegration(Tenant, "hook", null, null, null, null, null, true) { ManagedBy = Alias });
            Assert.Equal(OutcomeKind.Ok, enabled.Kind);
            Assert.Equal((Alias, true), (await StateAsync("hook"))[0]);

            // The admin API doesn't: it belongs to the alias until cutover.
            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            using var response = await admin.PutAsJsonAsync($"/api/v1/tenants/{Tenant}/integrations/hook", new PutIntegrationBody(null, null, null, null, null, false), Ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(ErrorCodes.ManagedByAlias, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
        }

        [Fact]
        public async Task BeforeHandBack_AliasPut_ManagedByConfiguration()
        {
            LocalStack.RequireAvailable();
            await SeedEndpointAsync("hook");
            Assert.Equal(ErrorCodes.ManagedByConfiguration,
                (await SendAsync(new PutIntegration(Tenant, "hook", null, null, null, null, null, false) { ManagedBy = Alias })).Code);
            Assert.Equal(ErrorCodes.ManagedByConfiguration, (await SendAsync(new DeleteRoute(Tenant, "hook") { ManagedBy = Alias })).Code);
            Assert.All(await StateAsync("hook"), s => Assert.Equal((AC, true), s));
        }

        /// <summary>
        /// The provisioning file hands back its own rows only: it neither hands back nor disables nor prunes what the alias's configuration
        /// owns, and leaves what was handed back (and then enabled by the alias's API) as the alias's API has it, with a file that no longer
        /// lists anything.
        /// </summary>
        [Fact]
        public async Task NotificationsProvisioning_NeverHandsBackOrPrunes_AliasConfigOrHandedBackRows()
        {
            LocalStack.RequireAvailable();
            await SeedEndpointAsync("kept");
            await SeedEndpointAsync("handed");
            await HandBackAsync("handed");
            Assert.Equal(OutcomeKind.Ok, (await SendAsync(new PutIntegration(Tenant, "handed", null, null, null, null, null, true) { ManagedBy = Alias })).Kind);

            var directory = Directory.CreateTempSubdirectory("notifications-handback-").FullName;
            try
            {
                foreach (var json in new[] { """{ "tenants": {} }""", $$"""{ "tenants": { "{{Tenant}}": {} } }""" })
                {
                    var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
                    await File.WriteAllTextAsync(path, json, Ct);
                    await ActivatorUtilities.CreateInstance<ConfigurationProvisioner>(App.Services, Options.Create(new ProvisioningOptions { Path = path })).StartAsync(Ct);
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
            Assert.All(await StateAsync("kept"), s => Assert.Equal((AC, true), s));
            var handed = await StateAsync("handed");
            Assert.Equal((Alias, true), handed[0]);
            Assert.All(handed[1..], s => Assert.Equal((Alias, false), s));
        }
    }

    /// <summary>The hand-back against concurrent writers: ownership is never half moved, and a later admin edit stands.</summary>
    public sealed class AliasHandBackRaceTests : AliasHandBackBase
    {
        private static ConfigurationHandlers HandlersOn(NotificationsDbContext db)
            => new(db, null!, [], [], Options.Create(new Juice.Notifications.Providers.DeliveryOptions()), TimeProvider.System);

        [Fact]
        public async Task Race_AliasPutLoadedBeforeHandBack_FailsConcurrentChange_NoMixedOwnership()
        {
            LocalStack.RequireAvailable();
            await SeedEndpointAsync("hook");

            // The alias's configuration reads its integration (owner alias-config) and is about to write it...
            await using var stale = App.Database.Context();
            var loaded = await stale.Integrations.SingleAsync(i => i.TenantId == Tenant && i.Key == "hook", Ct);
            Assert.Equal(AC, loaded.ManagedBy);

            // ...a hand-back commits first...
            Assert.Equal(new HandBackView("hook", 1, 2, 1), (await HandBackAsync("hook")).Value);

            // ...so that write fails as a concurrent change, and nothing of it lands.
            loaded.Priority = 9;
            var conflict = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(Ct));
            Assert.NotNull(DbConflicts.Classify(conflict));
            Assert.All(await StateAsync("hook"), s => Assert.Equal((Alias, false), s));
            await using var check = App.Database.Context();
            Assert.Equal(0, (await check.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == Tenant && i.Key == "hook", Ct)).Priority);
        }

        [Fact]
        public async Task Race_TargetPutLoadedBeforeHandBack_FailsConcurrentChange_NoAliasConfigTargetUnderAliasIntegration()
        {
            LocalStack.RequireAvailable();
            await SeedEndpointAsync("hook");

            // The alias's configuration (an old-file replica) runs its target put up to the save...
            await using var stale = App.Database.Context();
            var put = await HandlersOn(stale).Handle(new PutTarget(Tenant, "hook", "hook-t3", "c@example.com", null, true) { ManagedBy = AC }, Ct);
            Assert.Contains(put.Kind, new[] { OutcomeKind.Ok, OutcomeKind.Created });

            // ...a hand-back commits first...
            Assert.Equal(new HandBackView("hook", 1, 2, 1), (await HandBackAsync("hook")).Value);

            // ...so the save fails as a concurrent change and the new target never lands.
            var conflict = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(Ct));
            Assert.NotNull(DbConflicts.Classify(conflict));
            await using var check = App.Database.Context();
            var integration = await check.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == Tenant && i.Key == "hook", Ct);
            var targets = await check.IntegrationTargets.AsNoTracking().Where(t => t.TenantId == Tenant && t.IntegrationId == integration.Id).ToListAsync(Ct);
            Assert.Equal(Alias, integration.ManagedBy);
            Assert.Equal(2, targets.Count);
            Assert.DoesNotContain(targets, t => t.ManagedBy == AC);
        }

        [Fact]
        public async Task TargetPut_AfterHandBack_ByAliasConfig_RefusedManagedByAlias_NothingWritten()
        {
            LocalStack.RequireAvailable();
            await SeedEndpointAsync("hook");
            await HandBackAsync("hook");
            var put = await SendAsync(new PutTarget(Tenant, "hook", "hook-t3", "c@example.com", null, true) { ManagedBy = AC });
            Assert.Equal((OutcomeKind.Conflict, ErrorCodes.ManagedByAlias), (put.Kind, put.Code));
            await using var check = App.Database.Context();
            Assert.Equal(2, await check.IntegrationTargets.CountAsync(t => t.TenantId == Tenant, Ct));
            // The alias's API may still add targets to what it now owns.
            Assert.Contains((await SendAsync(new PutTarget(Tenant, "hook", "hook-t3", "c@example.com", null, true) { ManagedBy = Alias })).Kind, new[] { OutcomeKind.Ok, OutcomeKind.Created });
        }

        [Fact]
        public async Task Race_OwnerChangedUnderHandBack_FailsWholeHandBack_NothingPartlyMoved()
        {
            LocalStack.RequireAvailable();
            await SeedEndpointAsync("hook");

            // A hand-back decides on what it read (all three kinds of row alias-config)...
            await using var db = App.Database.Context();
            Assert.Equal(new HandBackView("hook", 1, 2, 1), (await HandlersOn(db).Handle(new HandBackAliasConfig(Tenant, "hook") { ManagedBy = AC }, Ct)).Value);

            // ...and the route changes owner before it saves.
            await using (var other = App.Database.Context())
            {
                (await other.Routes.SingleAsync(r => r.TenantId == Tenant && r.Key == "hook", Ct)).ManagedBy = ManagedBy.Configuration;
                await other.SaveChangesAsync(Ct);
            }
            var conflict = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal(ConflictKind.RowGone, DbConflicts.Classify(conflict)!.Kind);

            var state = await StateAsync("hook");
            Assert.Equal((AC, true), state[0]);
            Assert.Equal((AC, true), state[1]);
            Assert.Equal((AC, true), state[2]);
            Assert.Equal((ManagedBy.Configuration, true), state[3]);
        }

        [Fact]
        public async Task Race_HandBackVsAdminEditAfter_AdminEditStands()
        {
            LocalStack.RequireAvailable();
            await SeedEndpointAsync("hook");
            await HandBackAsync("hook");

            // The alias's API enables what was handed back; a later hand-back run (the next start) doesn't re-disable it.
            Assert.Equal(OutcomeKind.Ok, (await SendAsync(new PutIntegration(Tenant, "hook", null, null, null, null, null, true) { ManagedBy = Alias })).Kind);
            Assert.Equal(OutcomeKind.Ok, (await SendAsync(new PutRoute(Tenant, "hook", null, null, true) { ManagedBy = Alias })).Kind);
            var again = await HandBackAsync("hook");
            Assert.Equal(new HandBackView("hook", 0, 0, 0), again.Value);
            var state = await StateAsync("hook");
            Assert.Equal((Alias, true), state[0]);
            Assert.Equal((Alias, true), state[^1]);
        }

        [Fact]
        public async Task Race_TwoReplicasHandBack_OneMoves_OtherOkZeroOrConcurrentChange()
        {
            LocalStack.RequireAvailable();
            for (var round = 0; round < 10; round++)
            {
                var key = $"hook-{round}";
                await SeedEndpointAsync(key);
                Task<(bool Failed, HandBackView? View)> Replica() => Task.Run(async () =>
                {
                    try
                    {
                        return (false, (await HandBackAsync(key)).Value);
                    }
                    catch (Exception ex) when (DbConflicts.Classify(ex) is { })
                    {
                        return (true, null);
                    }
                });
                var results = await Task.WhenAll(Replica(), Replica());
                var moved = results.Where(r => r.View is { Integrations: 1, Targets: 2, Routes: 1 }).ToList();
                Assert.Single(moved); // exactly one replica moves the rows
                foreach (var other in results.Except(moved))
                {
                    Assert.True(other.Failed || other.View == new HandBackView(key, 0, 0, 0), $"round {round}: {other}");
                }
                Assert.All(await StateAsync(key), s => Assert.Equal((Alias, false), s));
            }
        }

        [Fact]
        public async Task HandBack_SchemaNotMigrated_FailsCleanly()
        {
            LocalStack.RequireAvailable();
            await using var scratch = new ScratchDatabase();
            await scratch.InitializeAsync();
            await using var db = scratch.Context(); // created, never migrated
            var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await HandlersOn(db).Handle(new HandBackAliasConfig(Tenant, "hook") { ManagedBy = AC }, Ct));
            // An error, not an Ok with zero counts and not a concurrent change a caller would retry.
            Assert.Null(DbConflicts.Classify(failure));
            await scratch.DisposeAsync();
        }
    }
}
