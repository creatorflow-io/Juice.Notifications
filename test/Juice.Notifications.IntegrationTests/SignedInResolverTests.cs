using System.Net;
using Juice.MediatR;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Intake;
using Juice.Notifications.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Juice.Notifications.IntegrationTests.InboxTestHost;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// M54: the <c>signed-in</c> resolver (people recorded from their own sign-in, by role) and how an embedded host provisions and plans with
    /// it: only this tenant's people seen within the TTL, in byte order and bounded; the service's own resolver row; one item per person.
    /// </summary>
    public sealed class SignedInResolverTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly TimeSpan Ttl = TimeSpan.FromDays(30);
        private readonly ScratchDatabase _database = new();

        public ValueTask InitializeAsync() => _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        private static ResolveContext Context(NotificationsDbContext db, Guid tenant, int maxPeople = 1000)
            => new(db, Seed.Request(tenant, Guid.NewGuid(), Guid.NewGuid()), DateTimeOffset.UtcNow, null, maxPeople);

        private static async Task<Guid> PersonAsync(NotificationsDbContext db, Guid tenant, string externalId, string role, DateTimeOffset seen, string source = "oidc")
        {
            var subscriber = new Subscriber { TenantId = tenant, Source = source, ExternalId = externalId, CreatedAt = Seed.Now };
            db.Subscribers.Add(subscriber);
            db.SubscriberRoles.Add(new SubscriberRole { TenantId = tenant, SubscriberId = subscriber.Id, Role = role, SeenAt = seen });
            await db.SaveChangesAsync(Ct);
            return subscriber.Id;
        }

        private static SignedInRecipientResolver Resolver(TimeSpan? ttl = null) => new(new SignedInOptions(["viewer", "operator", "admin"], ttl ?? Ttl));

        [Fact]
        public async Task SignedIn_StaleRole_PastTtl_NotResolved()
        {
            LocalStack.RequireAvailable();
            await using var db = await _database.MigratedAsync();
            await PersonAsync(db, Tenant, "fresh", "operator", DateTimeOffset.UtcNow.AddDays(-29));
            await PersonAsync(db, Tenant, "stale", "operator", DateTimeOffset.UtcNow.AddDays(-31));

            var result = await Resolver().ResolveAsync(new RecipientSpec("role", Id: "operator"), Context(db, Tenant), Ct);

            Assert.Equal(["fresh"], result.People.Select(p => p.ExternalId));
            Assert.All(result.People, p => Assert.Equal("oidc", p.Source));
        }

        [Fact]
        public async Task SignedIn_RoleResolves_OnlyThisTenantsSubscribers()
        {
            LocalStack.RequireAvailable();
            await using var db = await _database.MigratedAsync();
            await PersonAsync(db, Tenant, "ours", "operator", DateTimeOffset.UtcNow);
            await PersonAsync(db, OtherTenant, "theirs", "operator", DateTimeOffset.UtcNow);

            var ours = await Resolver().ResolveAsync(new RecipientSpec("role", Id: "operator"), Context(db, Tenant), Ct);
            var theirs = await Resolver().ResolveAsync(new RecipientSpec("role", Id: "operator"), Context(db, OtherTenant), Ct);

            Assert.Equal(["ours"], ours.People.Select(p => p.ExternalId));
            Assert.Equal(["theirs"], theirs.People.Select(p => p.ExternalId));
        }

        [Fact]
        public async Task SignedIn_UnknownRole_ReachesNoOne()
        {
            LocalStack.RequireAvailable();
            await using var db = await _database.MigratedAsync();
            await PersonAsync(db, Tenant, "ann", "operator", DateTimeOffset.UtcNow);

            var unknown = await Resolver().ResolveAsync(new RecipientSpec("role", Id: "nobody"), Context(db, Tenant), Ct);
            var none = await Resolver().ResolveAsync(new RecipientSpec("role"), Context(db, Tenant), Ct);
            // Not by another source's person: a role is the inbox's own sign-in.
            await PersonAsync(db, Tenant, "local-user", "admin", DateTimeOffset.UtcNow, source: "local");
            var other = await Resolver().ResolveAsync(new RecipientSpec("role", Id: "admin"), Context(db, Tenant), Ct);

            Assert.Empty(unknown.People);
            Assert.Empty(none.People);
            Assert.Empty(other.People);
        }

        [Fact]
        public async Task SignedIn_OverMaxPeople_Truncated_ByteOrder()
        {
            LocalStack.RequireAvailable();
            await using var db = await _database.MigratedAsync();
            // Byte order: '-' < digits < 'B' < '_' < 'a'; the database's own locale would put 'a' before 'B'.
            foreach (var id in new[] { "a-1", "B-2", "_3", "4", "-5", "zz" })
            {
                await PersonAsync(db, Tenant, id, "operator", DateTimeOffset.UtcNow);
            }

            var result = await Resolver().ResolveAsync(new RecipientSpec("role", Id: "operator"), Context(db, Tenant, maxPeople: 3), Ct);

            // One past the bound, which is all the planner needs to see to keep the first three and record the name as truncated.
            Assert.Equal(["-5", "4", "B-2", "_3"], result.People.Select(p => p.ExternalId));
        }

        [Fact]
        public async Task SignedIn_RoleRecipients_PlannedInByteOrder_BoundedAndRecordedAsTruncated()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString, new Options { RunStages = true, Settings = new() { ["Resolvers:MaxRecipients"] = "2" } });
            foreach (var sub in new[] { "carol", "Bob", "alice" })
            {
                (await host.SignInAsync(sub, "operator")).EnsureSuccessStatusCode();
            }

            var accepted = await host.SendAsync("bounded-1", [Role("operator")]);

            await using var db = _database.Context();
            var recipients = await WaitForPlannedAsync(db, accepted.Id);
            var people = recipients.Where(r => r.Kind == RecipientKind.Subscriber).ToList();
            Assert.Equal(2, people.Count);
            var named = Assert.Single(recipients, r => r.Kind == RecipientKind.Name);
            Assert.Equal(("role:operator", RecipientOutcome.Truncated, "signed-in"), (named.NamedAs, named.Outcome, named.Resolver));
            // 'Bob' < 'alice' < 'carol' in bytes: the first two are kept.
            var ids = people.Select(p => p.SubscriberId).ToList();
            var kept = await db.Subscribers.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => s.ExternalId).ToListAsync(Ct);
            Assert.Equal(["Bob", "alice"], kept.Order(StringComparer.Ordinal));
        }

        private static async Task<List<NotificationRecipient>> WaitForPlannedAsync(NotificationsDbContext db, Guid requestId)
        {
            for (var i = 0; i < 150; i++)
            {
                var notification = await db.Notifications.AsNoTracking().SingleOrDefaultAsync(n => n.RequestId == requestId, Ct);
                if (notification is { State: NotificationState.Planned or NotificationState.Running })
                {
                    return await db.Recipients.AsNoTracking().Where(r => r.NotificationId == notification.Id).ToListAsync(Ct);
                }
                await Task.Delay(100, Ct);
            }
            Assert.Fail("The notification was not planned in time.");
            return [];
        }

        [Fact]
        public async Task Embedded_InboxOff_RoleRecipient_RefusedAtIntake()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString, new Options { Inbox = null });

            // As alpha.60: no resolver handles a role, so the request is refused, whoever has signed in.
            var refused = await Assert.ThrowsAsync<NotificationRefusedException>(() => host.SendAsync("off-1", [Role("operator")]));
            // And signing in records nothing.
            (await host.SignInAsync("ann", "operator", "Ann")).EnsureSuccessStatusCode();

            Assert.Contains(refused.Errors, e => e.Pointer == "/recipients/0/kind");
            await using var db = _database.Context();
            Assert.Empty(await db.SubscriberRoles.ToListAsync(Ct));
            Assert.Equal(1, await db.Subscribers.CountAsync(Ct));
            Assert.Empty(await db.Requests.ToListAsync(Ct));
            Assert.False((await db.Resolvers.AsNoTracking().SingleAsync(c => c.Key == "signed-in", Ct)).Enabled);
        }

        [Fact]
        public async Task Embedded_InboxOn_RoleRecipient_PlannedToInbox_OneItemPerPerson()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString, new Options { RunStages = true });
            (await host.SignInAsync("ann", "operator,admin", "Ann")).EnsureSuccessStatusCode();
            (await host.SignInAsync("ben", "operator", "Ben")).EnsureSuccessStatusCode();
            (await host.SignInAsync("cat", "viewer", "Cat")).EnsureSuccessStatusCode();

            var accepted = await host.SendAsync("on-1", [Role("operator"), Role("admin")]);

            await using var db = _database.Context();
            var recipients = await WaitForPlannedAsync(db, accepted.Id);
            var people = recipients.Where(r => r.Kind == RecipientKind.Subscriber).ToList();
            Assert.Equal(2, people.Count);
            Assert.All(people, p => Assert.Equal("signed-in", p.Resolver));
            // Ann holds both roles: one recipient, reached by both names.
            var ann = await db.Subscribers.AsNoTracking().SingleAsync(s => s.ExternalId == "ann", Ct);
            Assert.Equal(["role:admin", "role:operator"], people.Single(p => p.SubscriberId == ann.Id).Via);
            for (var i = 0; i < 150 && await db.InboxItems.CountAsync(Ct) < 2; i++)
            {
                await Task.Delay(100, Ct);
            }
            var items = await db.InboxItems.AsNoTracking().ToListAsync(Ct);
            Assert.Equal(2, items.Count);
            Assert.Equal(2, items.Select(i => i.SubscriberId).Distinct().Count());
            Assert.Equal(people.Select(p => p.SubscriberId).Order(), items.Select(i => (Guid?)i.SubscriberId).Order());
        }

        [Fact]
        public async Task Embedded_InboxTurnedOff_ResolverRowDisabled_PendingRoleRecipient_SkippedWithWarning()
        {
            LocalStack.RequireAvailable();
            Guid request;
            await using (var on = await StartAsync(_database.ConnectionString))
            {
                (await on.SignInAsync("ann", "operator", "Ann")).EnsureSuccessStatusCode();
                // Accepted while the inbox is on; its stages are off, so it waits to be planned.
                request = (await on.SendAsync("pending-1", [Role("operator")])).Id;
            }

            await using var off = await StartAsync(_database.ConnectionString, new Options { Inbox = null, RunStages = true });

            await using var db = _database.Context();
            Assert.False((await db.Resolvers.AsNoTracking().SingleAsync(c => c.Key == "signed-in", Ct)).Enabled);
            // Planned without anyone (and no refusal now: it was accepted), the skip said in the log.
            var recipients = await WaitForPlannedAsync(db, request);
            Assert.DoesNotContain(recipients, r => r.Kind == RecipientKind.Subscriber);
            Assert.Contains(off.Probe.Logs, l => l.Level == LogLevel.Warning && l.Message.Contains("No resolver handles recipients of kind role"));
        }

        [Fact]
        public async Task Embedded_TwoReplicasStart_OneResolverRow()
        {
            LocalStack.RequireAvailable();
            // The first replica migrates; the others start together on the migrated database.
            await using var first = await StartAsync(_database.ConnectionString);
            var started = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => StartAsync(_database.ConnectionString)));
            try
            {
                // And the statement itself, raced.
                await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
                {
                    await using var db = _database.Context();
                    await SignedInRecipientResolver.EnsureRowAsync(db, Tenant, true, DateTimeOffset.UtcNow, Ct);
                }));
            }
            finally
            {
                foreach (var host in started)
                {
                    await host.DisposeAsync();
                }
            }

            await using var check = _database.Context();
            var row = await check.Resolvers.AsNoTracking().SingleAsync(c => c.TenantId == Tenant && c.Key == "signed-in", Ct);
            Assert.Equal(("signed-in", ManagedBy.Service, true), (row.Kind, row.ManagedBy, row.Enabled));
            Assert.Equal(["role"], row.Handles);
            Assert.Equal(ConfigurationRules.ResolverHash(row.Kind, row.Handles, row.Settings, row.SecretRef, row.Enabled), row.DefinitionHash);
        }

        [Fact]
        public async Task Embedded_ProvisioningFile_DoesNotDisableServiceRow()
        {
            LocalStack.RequireAvailable();
            // A file that lists the key itself, once: the host's start makes the row the service's own.
            var listing = $$"""
                { "tenants": { "{{Tenant}}": { "resolvers": { "signed-in": { "kind": "signed-in", "handles": ["role"], "enabled": false } } } } }
                """;
            await using (var first = await StartAsync(_database.ConnectionString, new Options { Provisioning = listing }))
            {
            }
            await using (var db = _database.Context())
            {
                var taken = await db.Resolvers.AsNoTracking().SingleAsync(c => c.Key == "signed-in", Ct);
                Assert.Equal((ManagedBy.Service, true), (taken.ManagedBy, taken.Enabled));
            }

            // A later file that no longer lists it: what the file stops naming is disabled, but only what it owns.
            var unrelated = $$"""
                { "tenants": { "{{Tenant}}": { "integrations": { "hooks": { "channel": "webhook", "kind": "webhook", "secretRef": "{{TestHooks.SecretRef}}",
                    "targets": { "t": { "address": "https://hooks.example.com/in" } } } } } } }
                """;
            await using (var second = await StartAsync(_database.ConnectionString, new Options { Provisioning = unrelated }))
            {
            }

            await using var check = _database.Context();
            var row = await check.Resolvers.AsNoTracking().SingleAsync(c => c.Key == "signed-in", Ct);
            Assert.Equal((ManagedBy.Service, true), (row.ManagedBy, row.Enabled));
        }

        [Fact]
        public async Task AdminApi_PutSignedInResolver_RefusedServiceManaged()
        {
            LocalStack.RequireAvailable();
            await using var host = await StartAsync(_database.ConnectionString);
            await using var scope = host.Services.CreateAsyncScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            using var context = NotificationsMessageContext.Begin("test");

            var put = await mediator.Send(new PutResolver(Tenant, "signed-in", "signed-in", ["role"], null, null, false), Ct);
            var disable = await mediator.Send(new PutResolver(Tenant, "signed-in", null, null, null, null, false), Ct);
            var delete = await mediator.Send(new DeleteResolver(Tenant, "signed-in"), Ct);

            Assert.All(new[] { put.Code, disable.Code, delete.Code }, code => Assert.Equal(ErrorCodes.ManagedByService, code));
            await using var db = _database.Context();
            var row = await db.Resolvers.AsNoTracking().SingleAsync(c => c.Key == "signed-in", Ct);
            Assert.Equal((ManagedBy.Service, true), (row.ManagedBy, row.Enabled));
            Assert.Equal(["role"], row.Handles);
        }

        /// <summary>The migration over an alpha.60 database that already has people: it adds the table and touches nothing else.</summary>
        [Fact]
        public async Task Migration_AddSubscriberRoles_OnAlpha60Schema_WithExistingSubscribers_Passes()
        {
            LocalStack.RequireAvailable();
            const string Previous = "20261128030103_RetireAliasOwners";
            Guid ann;
            await using (var db = await _database.MigratedAsync())
            {
                ann = await PersonAsync(db, Tenant, "ann", "operator", Seed.Now);
                await db.GetService<IMigrator>().MigrateAsync(Previous, cancellationToken: Ct);
                Assert.Null(await db.Database.SqlQuery<string?>($"SELECT to_regclass('notifications.subscriber_roles')::text AS \"Value\"").SingleAsync(Ct));
            }

            await using (var db = _database.Context())
            {
                await db.Database.MigrateAsync(Ct);
                Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
                Assert.False(db.Database.HasPendingModelChanges());
                var subscriber = await db.Subscribers.AsNoTracking().SingleAsync(s => s.Id == ann, Ct);
                Assert.Equal("ann", subscriber.ExternalId);
                Assert.Empty(await db.SubscriberRoles.ToListAsync(Ct));
                // The new table works against the existing people, and goes with them.
                db.SubscriberRoles.Add(new SubscriberRole { TenantId = Tenant, SubscriberId = ann, Role = "operator", SeenAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync(Ct);
                await db.Subscribers.Where(s => s.Id == ann).ExecuteDeleteAsync(Ct);
                Assert.Empty(await db.SubscriberRoles.ToListAsync(Ct));
            }
        }

        [Fact]
        public async Task Embedded_SchemaNotMigrated_InboxEndpoints_AnswerAsAdminDoes()
        {
            LocalStack.RequireAvailable();
            await using (var db = await _database.MigratedAsync())
            {
                await db.GetService<IMigrator>().MigrateAsync("20261128030103_RetireAliasOwners", cancellationToken: Ct);
            }
            // The code of this release over the schema of the last, with no migration at start: the host doesn't start, as the admin's own pages
            // would not (the pipeline checks the schema first), and says so in words, so no inbox call ever meets the missing table.
            var failed = await Assert.ThrowsAsync<InvalidOperationException>(
                () => StartAsync(_database.ConnectionString, new Options { Migrate = false }));

            Assert.Contains("isn't migrated", failed.Message);
            Assert.Contains("Database:Migrate", failed.Message);
            Assert.Contains("AddSubscriberRoles", failed.Message);
            await using var check = _database.Context();
            Assert.Null(await check.Database.SqlQuery<string?>($"SELECT to_regclass('notifications.subscriber_roles')::text AS \"Value\"").SingleAsync(Ct));
        }
    }
}
