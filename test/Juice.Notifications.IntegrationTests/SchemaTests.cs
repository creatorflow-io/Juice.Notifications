using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>A migrated scratch database holding a whole chain of rows in each of two tenants.</summary>
    public sealed class TwoTenantDatabase : IAsyncLifetime
    {
        public static readonly Guid TenantA = Guid.Parse("0193f0aa-0000-7000-8000-00000000000a");
        public static readonly Guid TenantB = Guid.Parse("0193f0bb-0000-7000-8000-00000000000b");

        public sealed record Chain(Guid Application, Guid Type, Guid Integration, Guid Request, Guid Notification, Guid Recipient, Guid Delivery);

        public ScratchDatabase Database { get; } = new();
        public Chain A { get; private set; } = null!;
        public Chain B { get; private set; } = null!;
        /// <summary>An application linked to tenant A only.</summary>
        public Guid OnlyA { get; private set; }

        public async ValueTask InitializeAsync()
        {
            await Database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using var db = await Database.MigratedAsync();
            var app = Seed.Application("schema-both");
            var onlyA = Seed.Application("schema-only-a");
            var type = Seed.Type(app.Id, "schema-both.thing");
            db.AddRange(app, onlyA, type,
                new ApplicationTenant { ApplicationId = app.Id, TenantId = TenantA, LinkedAt = Seed.Now },
                new ApplicationTenant { ApplicationId = app.Id, TenantId = TenantB, LinkedAt = Seed.Now },
                new ApplicationTenant { ApplicationId = onlyA.Id, TenantId = TenantA, LinkedAt = Seed.Now });
            Chain Build(Guid tenant)
            {
                var integration = Seed.Integration(tenant);
                var request = Seed.Request(tenant, app.Id, type.Id);
                var notification = Seed.Notification(tenant, request.Id);
                var recipient = Seed.Recipient(tenant, notification.Id);
                var delivery = Seed.Delivery(tenant, integration.Id);
                db.AddRange(integration, request, notification, recipient, delivery,
                    Seed.Item(tenant, delivery.Id, recipient.Id), Seed.Attempt(tenant, delivery.Id, integration.Id));
                return new Chain(app.Id, type.Id, integration.Id, request.Id, notification.Id, recipient.Id, delivery.Id);
            }
            A = Build(TenantA);
            B = Build(TenantB);
            OnlyA = onlyA.Id;
            // The positive control: the same references within one tenant are accepted.
            await db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    /// <summary>The first migration, and tenant isolation enforced by the database itself (design §12).</summary>
    public sealed class SchemaTests(TwoTenantDatabase fixture) : IClassFixture<TwoTenantDatabase>
    {
        [Fact]
        public async Task Migrations_apply_to_an_empty_database_and_match_the_model()
        {
            LocalStack.RequireAvailable();
            await using var db = fixture.Database.Context();
            var ct = TestContext.Current.CancellationToken;
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
            Assert.False(db.Database.HasPendingModelChanges(), "The model has changes no migration captures: add a migration.");
            var tables = await db.Database.SqlQuery<string>(
                $"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'notifications'").ToListAsync(ct);
            foreach (var table in new[] { "applications", "application_tenants", "notification_types", "templates", "integrations", "integration_targets",
                "routes", "destination_allowlist", "requests", "notifications", "notification_recipients", "deliveries", "delivery_items", "delivery_attempts",
                "outbox_events", "outbox_deliveries" })
            {
                Assert.Contains(table, tables);
            }
        }

        [Fact]
        public async Task A_plain_text_actor_is_stored()
        {
            LocalStack.RequireAvailable();
            await using var db = fixture.Database.Context();
            var request = await db.Requests.AsNoTracking().FirstAsync(r => r.Id == fixture.A.Request, TestContext.Current.CancellationToken);
            Assert.Equal("alice@example.com", request.Actor);
        }

        public static TheoryData<string> CrossTenantReferences =>
        [
            "integration target → another tenant's integration",
            "request → an application linked only to another tenant",
            "notification → another tenant's request",
            "recipient → another tenant's notification",
            "delivery → another tenant's integration",
            "delivery item → another tenant's delivery",
            "delivery item → another tenant's recipient",
            "attempt → another tenant's delivery",
            "attempt → another tenant's integration",
        ];

        /// <summary>Each row is valid in tenant B except for the one reference into tenant A.</summary>
        [Theory]
        [MemberData(nameof(CrossTenantReferences))]
        public async Task The_database_refuses_a_reference_to_another_tenants_row(string reference)
        {
            LocalStack.RequireAvailable();
            var (a, b, tenantB) = (fixture.A, fixture.B, TwoTenantDatabase.TenantB);
            object row = reference switch
            {
                "integration target → another tenant's integration" => new IntegrationTarget
                {
                    TenantId = tenantB, IntegrationId = a.Integration, Key = "cross", Address = "https://example.com", DefinitionHash = "d",
                },
                "request → an application linked only to another tenant" => Seed.Request(tenantB, fixture.OnlyA, b.Type, "cross"),
                "notification → another tenant's request" => Seed.Notification(tenantB, a.Request),
                "recipient → another tenant's notification" => Seed.Recipient(tenantB, a.Notification),
                "delivery → another tenant's integration" => Seed.Delivery(tenantB, a.Integration),
                // A destination key b's recipient doesn't have yet, so only the reference can fail.
                "delivery item → another tenant's delivery" => new DeliveryItem
                {
                    TenantId = tenantB, DeliveryId = a.Delivery, RecipientId = b.Recipient, DestinationKey = "ext:webhook:https://example.com/other", Channel = Channels.Webhook,
                },
                "delivery item → another tenant's recipient" => Seed.Item(tenantB, b.Delivery, a.Recipient),
                "attempt → another tenant's delivery" => Seed.Attempt(tenantB, a.Delivery, b.Integration),
                // Number 2: the chain's delivery already has attempt 1, and attempt numbers are unique per delivery.
                "attempt → another tenant's integration" => Seed.Attempt(tenantB, b.Delivery, a.Integration, number: 2),
                _ => throw new ArgumentOutOfRangeException(nameof(reference)),
            };
            await using var db = fixture.Database.Context();
            db.Add(row);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
    }
}
