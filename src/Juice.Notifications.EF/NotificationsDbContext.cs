using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.EF;
using Juice.Messaging.Outbox;
using Juice.Messaging.Outbox.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juice.Notifications.Data
{
    /// <summary>
    /// The notification service's database, schema <c>notifications</c>, snake_case. Tenant-owned tables are keyed <c>(tenant_id, id)</c> and reference
    /// each other through both columns (design §12), so a row can't point at another tenant's row whatever the code does.
    /// </summary>
    /// <remarks>
    /// The mediator's unit of work (Juice <see cref="UnitOfWork"/>) and the outbox's store (<see cref="IOutboxContext"/>): a command's changes and the
    /// messages it publishes commit together, saved by the TransactionBehavior, never by the command.
    /// </remarks>
    public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : UnitOfWork(options), IOutboxContext
    {
        public const string Schema = "notifications";

        public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();
        public DbSet<OutboxDelivery> OutboxDeliveries => Set<OutboxDelivery>();
        public DbSet<Application> Applications => Set<Application>();
        public DbSet<ApplicationTenant> ApplicationTenants => Set<ApplicationTenant>();
        public DbSet<NotificationType> Types => Set<NotificationType>();
        public DbSet<Template> Templates => Set<Template>();
        public DbSet<Integration> Integrations => Set<Integration>();
        public DbSet<IntegrationTarget> IntegrationTargets => Set<IntegrationTarget>();
        public DbSet<Route> Routes => Set<Route>();
        public DbSet<AllowlistEntry> Allowlist => Set<AllowlistEntry>();
        public DbSet<NotificationRequest> Requests => Set<NotificationRequest>();
        public DbSet<Notification> Notifications => Set<Notification>();
        public DbSet<NotificationRecipient> Recipients => Set<NotificationRecipient>();
        public DbSet<Delivery> Deliveries => Set<Delivery>();
        public DbSet<DeliveryItem> DeliveryItems => Set<DeliveryItem>();
        public DbSet<DeliveryAttempt> Attempts => Set<DeliveryAttempt>();

        /// <summary>PostgreSQL, snake_case, migrations from <paramref name="migrationsAssembly"/>. Call it when configuring the context.</summary>
        public static void Configure(DbContextOptionsBuilder options, string connectionString, string? migrationsAssembly = "Juice.Notifications.EF.PostgreSQL")
            => options.UseNpgsql(connectionString, npgsql =>
                {
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", Schema);
                    if (migrationsAssembly is not null)
                    {
                        npgsql.MigrationsAssembly(migrationsAssembly);
                    }
                })
                .UseSnakeCaseNamingConvention();

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.HasDefaultSchema(Schema);

            // The framework's outbox, in this database so it commits with the change that wrote it. Its index filters are written for SQL Server
            // ("[State] = 0"); here they follow PostgreSQL and snake_case.
            this.ConfigureOutbox(model);
            model.Entity<OutboxEvent>().ToTable("outbox_events");
            model.Entity<OutboxDelivery>(e =>
            {
                e.ToTable("outbox_deliveries");
                foreach (var index in e.Metadata.GetIndexes())
                {
                    if (index.GetFilter() is { } filter)
                    {
                        index.SetFilter(Regex.Replace(filter, @"\[(\w+)\]", m => Regex.Replace(m.Groups[1].Value, "(?<!^)([A-Z])", "_$1").ToLowerInvariant()));
                    }
                }
            });

            model.Entity<Application>(e =>
            {
                e.ToTable("applications");
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.Name).HasMaxLength(200);
                e.Property(x => x.ClientId).HasMaxLength(200);
                e.HasIndex(x => x.Key).IsUnique().HasDatabaseName(Constraints.ApplicationKey);
                e.HasIndex(x => x.ClientId).IsUnique().HasDatabaseName(Constraints.ApplicationClientId);
                e.Property(x => x.BrokerUser).HasMaxLength(200);
                e.HasIndex(x => x.BrokerUser).IsUnique().HasDatabaseName(Constraints.ApplicationBrokerUser);
            });

            model.Entity<ApplicationTenant>(e =>
            {
                e.ToTable("application_tenants");
                e.HasKey(x => new { x.ApplicationId, x.TenantId }).HasName(Constraints.ApplicationTenantLink);
                e.HasIndex(x => x.TenantId);
                e.HasOne<Application>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<NotificationType>(e =>
            {
                e.ToTable("notification_types");
                e.Property(x => x.Key).HasMaxLength(200);
                e.Property(x => x.Name).HasMaxLength(200);
                e.Property(x => x.Category).HasMaxLength(63);
                e.Property(x => x.DisplayGroup).HasMaxLength(63);
                e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Schema).HasColumnType("jsonb");
                e.Property(x => x.SchemaHash).HasMaxLength(64);
                e.Property(x => x.Workflow).HasColumnType("jsonb");
                e.Property(x => x.Actions).HasColumnType("jsonb");
                e.Property(x => x.DeclarationHash).HasMaxLength(64);
                e.Property(x => x.Correlation).HasMaxLength(10);
                e.HasIndex(x => new { x.ApplicationId, x.Key, x.Version }).IsUnique().HasDatabaseName(Constraints.TypeVersion);
                e.HasOne<Application>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            });

            model.Entity<Template>(e =>
            {
                e.ToTable("templates");
                e.Property(x => x.Channel).HasMaxLength(20);
                e.Property(x => x.Locale).HasMaxLength(35);
                e.Property(x => x.Engine).HasMaxLength(20);
                // A default (no tenant) and each tenant's override are each one row per version.
                e.HasIndex(x => new { x.TypeId, x.Channel, x.Locale, x.TenantId, x.Version }).IsUnique().AreNullsDistinct(false);
                e.HasOne<NotificationType>().WithMany().HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.Restrict);
            });

            model.Entity<Integration>(e =>
            {
                e.ToTable("integrations");
                TenantKey(e);
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.Channel).HasMaxLength(20);
                e.Property(x => x.Kind).HasMaxLength(40);
                e.Property(x => x.Settings).HasColumnType("jsonb");
                e.Property(x => x.SecretRef).HasMaxLength(500);
                e.Property(x => x.ManagedBy).HasMaxLength(10);
                // The hash covers every field a PUT can change, so it fences updates: two PUTs that merged fields from the same stale read
                // can't both land, and the stored hash always describes the row.
                e.Property(x => x.DefinitionHash).HasMaxLength(64).IsConcurrencyToken();
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            });

            model.Entity<IntegrationTarget>(e =>
            {
                e.ToTable("integration_targets");
                TenantKey(e);
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.Address).HasMaxLength(2000);
                e.Property(x => x.AddressRef).HasMaxLength(500);
                e.Property(x => x.ManagedBy).HasMaxLength(10);
                e.Property(x => x.DefinitionHash).HasMaxLength(64).IsConcurrencyToken();
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
                e.HasOne<Integration>().WithMany().HasForeignKey(x => new { x.TenantId, x.IntegrationId }).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<Route>(e =>
            {
                e.ToTable("routes");
                TenantKey(e);
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.Match).HasColumnType("jsonb");
                e.Property(x => x.ManagedBy).HasMaxLength(10);
                e.Property(x => x.DefinitionHash).HasMaxLength(64).IsConcurrencyToken();
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            });

            model.Entity<AllowlistEntry>(e =>
            {
                e.ToTable("destination_allowlist");
                e.HasKey(x => new { x.TenantId, x.Destination });
                e.Property(x => x.Destination).HasMaxLength(253);
                e.Property(x => x.ManagedBy).HasMaxLength(10);
            });

            model.Entity<NotificationRequest>(e =>
            {
                e.ToTable("requests");
                TenantKey(e);
                e.Property(x => x.IdempotencyKey).HasMaxLength(200);
                e.Property(x => x.TypeKey).HasMaxLength(200);
                e.Property(x => x.SchemaHash).HasMaxLength(64);
                e.Property(x => x.CorrelationKey).HasMaxLength(200);
                e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
                Json(e.Property(x => x.Labels));
                Json(e.Property(x => x.Links));
                e.Property(x => x.Data).HasColumnType("jsonb");
                e.Property(x => x.Recipients).HasColumnType("jsonb");
                e.Property(x => x.Actor).HasMaxLength(400);
                e.Property(x => x.TraceParent).HasMaxLength(55);
                e.HasIndex(x => new { x.TenantId, x.ApplicationId, x.IdempotencyKey }).IsUnique().HasDatabaseName(Constraints.RequestIdempotencyKey);
                e.HasIndex(x => new { x.TenantId, x.ApplicationId, x.CorrelationKey });
                e.HasIndex(x => new { x.TenantId, x.AcceptedAt });
                // The application must be linked to the tenant (application_tenants), not merely exist.
                e.HasOne<ApplicationTenant>().WithMany().HasForeignKey(x => new { x.ApplicationId, x.TenantId }).OnDelete(DeleteBehavior.Restrict)
                    .HasConstraintName(Constraints.RequestApplicationTenant);
                e.HasOne<NotificationType>().WithMany().HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.Restrict);
            });

            model.Entity<Notification>(e =>
            {
                e.ToTable("notifications");
                TenantKey(e);
                Claim(e);
                e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Error).HasMaxLength(2000);
                e.HasIndex(x => new { x.TenantId, x.RequestId }).IsUnique();
                e.HasOne<NotificationRequest>().WithMany().HasForeignKey(x => new { x.TenantId, x.RequestId }).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<NotificationRecipient>(e =>
            {
                e.ToTable("notification_recipients");
                TenantKey(e);
                Claim(e);
                e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.RouteKey).HasMaxLength(63);
                e.Property(x => x.Channel).HasMaxLength(20);
                e.Property(x => x.Address).HasMaxLength(2000);
                e.Property(x => x.Resolver).HasMaxLength(63);
                e.Property(x => x.NamedAs).HasMaxLength(500);
                e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30);
                // One recipient per thing named, per escalation step: planning twice can't add a second set (proof 3).
                e.HasIndex(x => new { x.TenantId, x.NotificationId, x.EscalationStep, x.NamedAs }).IsUnique().HasDatabaseName(Constraints.RecipientNamed);
                e.HasOne<Notification>().WithMany().HasForeignKey(x => new { x.TenantId, x.NotificationId }).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<Delivery>(e =>
            {
                e.ToTable("deliveries");
                TenantKey(e);
                Claim(e);
                e.Property(x => x.Channel).HasMaxLength(20);
                e.Property(x => x.DestinationKey).HasMaxLength(2100);
                // text: an address of up to 2,000 characters plus its target and secret reference, as JSON (M5 review).
                e.Property(x => x.DestinationSnapshot);
                e.Property(x => x.Locale).HasMaxLength(35);
                e.Property(x => x.RendererVersion).HasMaxLength(40);
                // The enum's value (Low 0 … Critical 3), not its name: the dispatcher claims by it, from the index below (M7 review: a CASE over the
                // names sorted every due row on each claim).
                e.Property(x => x.Priority);
                e.HasIndex(x => new { x.Priority, x.NextAt }).IsDescending(true, false).HasFilter("next_at IS NOT NULL").HasDatabaseName("ix_deliveries_claim");
                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.FailureCategory).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.LastError).HasMaxLength(2000);
                e.Property(x => x.ProviderMessageId).HasMaxLength(500);
                e.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
                e.HasOne<Integration>().WithMany().HasForeignKey(x => new { x.TenantId, x.IntegrationId }).OnDelete(DeleteBehavior.Restrict);
            });

            model.Entity<DeliveryItem>(e =>
            {
                e.ToTable("delivery_items");
                TenantKey(e);
                e.Property(x => x.DestinationKey).HasMaxLength(2100);
                e.Property(x => x.Channel).HasMaxLength(20);
                // A recipient reaches one destination on one channel at most once, digest or not (design D6, D19).
                e.HasIndex(x => new { x.TenantId, x.RecipientId, x.DestinationKey, x.Channel }).IsUnique();
                e.HasIndex(x => new { x.TenantId, x.DeliveryId });
                e.HasOne<Delivery>().WithMany().HasForeignKey(x => new { x.TenantId, x.DeliveryId }).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<NotificationRecipient>().WithMany().HasForeignKey(x => new { x.TenantId, x.RecipientId }).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(x => new { x.TenantId, x.RequestId });
                e.HasOne<NotificationRequest>().WithMany().HasForeignKey(x => new { x.TenantId, x.RequestId }).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<DeliveryAttempt>(e =>
            {
                e.ToTable("delivery_attempts");
                TenantKey(e);
                e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Error).HasMaxLength(2000);
                e.Property(x => x.ProviderMessageId).HasMaxLength(500);
                // One attempt per number: two workers recording at once (one whose lease ended) can't both take it.
                e.HasIndex(x => new { x.TenantId, x.DeliveryId, x.Number }).IsUnique().HasDatabaseName(Constraints.AttemptNumber);
                e.HasOne<Delivery>().WithMany().HasForeignKey(x => new { x.TenantId, x.DeliveryId }).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Integration>().WithMany().HasForeignKey(x => new { x.TenantId, x.IntegrationId }).OnDelete(DeleteBehavior.Restrict);
            });
        }

        private static void TenantKey<T>(EntityTypeBuilder<T> e) where T : class, ITenantOwned
            => e.HasKey(x => new { x.TenantId, x.Id });

        /// <summary>Claim columns, with the open rows indexed by when they're due.</summary>
        private static void Claim<T>(EntityTypeBuilder<T> e) where T : Claimable
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.ClaimedBy).HasMaxLength(200);
            e.HasIndex(x => x.NextAt).HasFilter("next_at IS NOT NULL").HasDatabaseName($"ix_{e.Metadata.GetTableName()}_due");
        }

        private static void Json(PropertyBuilder<Dictionary<string, string>> property)
            => property.HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonSerializerOptions.Default) ?? new Dictionary<string, string>(),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => a != null && b != null && a.Count == b.Count && !a.Except(b).Any(),
                    // Order-independent, like the equality above: the same pairs in any order hash the same.
                    v => v.Aggregate(0, (h, kv) => h ^ HashCode.Combine(kv.Key, kv.Value)),
                    v => new Dictionary<string, string>(v)));
    }
}
