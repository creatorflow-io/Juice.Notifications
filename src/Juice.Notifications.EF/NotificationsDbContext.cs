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
    /// <para>A Juice <see cref="DbContextBase"/>, so changes to the rows marked <see cref="Juice.Domain.IAuditable"/> (configuration: applications,
    /// integrations, targets, routes, the allowlist) are reported as audit events, and held until their transaction commits (p1-09). Nothing else
    /// is: the stages' rows are never audited.</para>
    /// </remarks>
    public sealed class NotificationsDbContext : DbContextBase, IOutboxContext
    {
        public new const string Schema = "notifications";

        /// <summary>Without services: no audit events and no commit announcements (tools, tests that only read and write rows).</summary>
        public NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : base(options)
        {
        }

        /// <summary>From the container: the mediator (audit events, the commit announcement) and the request's user.</summary>
        public NotificationsDbContext(DbContextOptions<NotificationsDbContext> options, IServiceProvider services) : base(options)
            => ConfigureServices(services);

        public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();
        public DbSet<OutboxDelivery> OutboxDeliveries => Set<OutboxDelivery>();
        public DbSet<Application> Applications => Set<Application>();
        public DbSet<ApplicationTenant> ApplicationTenants => Set<ApplicationTenant>();
        public DbSet<NotificationType> Types => Set<NotificationType>();
        public DbSet<Template> Templates => Set<Template>();
        public DbSet<Integration> Integrations => Set<Integration>();
        public DbSet<IntegrationTarget> IntegrationTargets => Set<IntegrationTarget>();
        public DbSet<Route> Routes => Set<Route>();
        public DbSet<RouteTarget> RouteTargets => Set<RouteTarget>();
        public DbSet<AllowlistEntry> Allowlist => Set<AllowlistEntry>();
        public DbSet<RecipientResolverConfig> Resolvers => Set<RecipientResolverConfig>();
        public DbSet<Subscriber> Subscribers => Set<Subscriber>();
        public DbSet<SubscriberAlias> SubscriberAliases => Set<SubscriberAlias>();
        public DbSet<ContactPoint> ContactPoints => Set<ContactPoint>();
        public DbSet<ContactVerification> ContactVerifications => Set<ContactVerification>();
        public DbSet<DeviceRegistration> Devices => Set<DeviceRegistration>();
        public DbSet<LocalGroup> LocalGroups => Set<LocalGroup>();
        public DbSet<LocalGroupMember> LocalGroupMembers => Set<LocalGroupMember>();
        public DbSet<Topic> Topics => Set<Topic>();
        public DbSet<TopicSubscription> TopicSubscriptions => Set<TopicSubscription>();
        public DbSet<Preference> Preferences => Set<Preference>();
        public DbSet<Mute> Mutes => Set<Mute>();
        public DbSet<DisplayGroup> DisplayGroups => Set<DisplayGroup>();
        public DbSet<TypeDisplayGroup> TypeDisplayGroups => Set<TypeDisplayGroup>();
        public DbSet<ThrottleEntry> ThrottleEntries => Set<ThrottleEntry>();
        public DbSet<Correlation> Correlations => Set<Correlation>();
        public DbSet<Digest> Digests => Set<Digest>();
        public DbSet<EscalationPolicy> EscalationPolicies => Set<EscalationPolicy>();
        public DbSet<ProducerEvent> ProducerEvents => Set<ProducerEvent>();
        public DbSet<Interaction> Interactions => Set<Interaction>();
        public DbSet<ProviderEvent> ProviderEvents => Set<ProviderEvent>();
        public DbSet<LinkCode> LinkCodes => Set<LinkCode>();
        public DbSet<Storm> Storms => Set<Storm>();
        public DbSet<RouteMinute> RouteMinutes => Set<RouteMinute>();
        public DbSet<TenantSettings> TenantSettings => Set<TenantSettings>();
        public DbSet<Escalation> Escalations => Set<Escalation>();
        public DbSet<InboxItem> InboxItems => Set<InboxItem>();
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

        protected override void ConfigureModel(ModelBuilder model)
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
                // A type's declared display group, by key (the inbox catalog: a move, a group's delete).
                e.HasIndex(x => new { x.Key, x.Version });
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

            // The targets a route names, as rows the database checks (deferred #8): a target can't be deleted while a route names it, and a route
            // can't commit naming a target deleted at the same moment. routes.targets keeps the order and is what the planner reads.
            model.Entity<RouteTarget>(e =>
            {
                e.ToTable("route_targets");
                e.HasKey(x => new { x.TenantId, x.RouteId, x.TargetId });
                e.HasIndex(x => new { x.TenantId, x.TargetId });
                e.HasOne<Route>().WithMany().HasForeignKey(x => new { x.TenantId, x.RouteId }).OnDelete(DeleteBehavior.Cascade)
                    .HasConstraintName(Constraints.RouteTargetRoute);
                e.HasOne<IntegrationTarget>().WithMany().HasForeignKey(x => new { x.TenantId, x.TargetId }).OnDelete(DeleteBehavior.Restrict)
                    .HasConstraintName(Constraints.RouteTargetTarget);
            });

            model.Entity<AllowlistEntry>(e =>
            {
                e.ToTable("destination_allowlist");
                e.HasKey(x => new { x.TenantId, x.Destination });
                e.Property(x => x.Destination).HasMaxLength(253);
                e.Property(x => x.ManagedBy).HasMaxLength(10);
            });

            model.Entity<RecipientResolverConfig>(e =>
            {
                e.ToTable("recipient_resolvers");
                TenantKey(e);
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.Kind).HasMaxLength(40);
                e.Property(x => x.Settings).HasColumnType("jsonb");
                e.Property(x => x.SecretRef).HasMaxLength(500);
                e.Property(x => x.ManagedBy).HasMaxLength(10);
                e.Property(x => x.DefinitionHash).HasMaxLength(64).IsConcurrencyToken();
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            });

            model.Entity<Subscriber>(e =>
            {
                e.ToTable("subscribers");
                TenantKey(e);
                e.Property(x => x.Source).HasMaxLength(63);
                e.Property(x => x.ExternalId).HasMaxLength(200);
                e.Property(x => x.Name).HasMaxLength(200);
                e.Property(x => x.Locale).HasMaxLength(35);
                e.Property(x => x.TimeZone).HasMaxLength(64);
                e.Property(x => x.Version).IsConcurrencyToken();
                e.Property(x => x.QuietHours).HasColumnType("jsonb");
                // One subscriber per source and id: two planners resolving the same new person at once make one (phase-2-plan I15).
                e.HasIndex(x => new { x.TenantId, x.Source, x.ExternalId }).IsUnique().HasDatabaseName(Constraints.SubscriberIdentity);
                e.HasOne<Subscriber>().WithMany().HasForeignKey(x => new { x.TenantId, x.MergedInto }).OnDelete(DeleteBehavior.Restrict);
            });

            model.Entity<SubscriberAlias>(e =>
            {
                e.ToTable("subscriber_aliases");
                TenantKey(e);
                e.Property(x => x.Source).HasMaxLength(63);
                e.Property(x => x.ExternalId).HasMaxLength(200);
                e.Property(x => x.LinkedBy).HasMaxLength(200);
                // An identity is an alias of at most one subscriber.
                e.HasIndex(x => new { x.TenantId, x.Source, x.ExternalId }).IsUnique().HasDatabaseName(Constraints.AliasIdentity);
                e.HasIndex(x => new { x.TenantId, x.SubscriberId });
                e.HasOne<Subscriber>().WithMany().HasForeignKey(x => new { x.TenantId, x.SubscriberId }).OnDelete(DeleteBehavior.Cascade)
                    .HasConstraintName(Constraints.AliasSubscriber);
            });

            model.Entity<ContactPoint>(e =>
            {
                e.ToTable("contact_points");
                TenantKey(e);
                e.Property(x => x.Channel).HasMaxLength(20);
                // An address of up to 2,000 characters, encrypted and base64: about 2,700.
                e.Property(x => x.Address).HasMaxLength(4000);
                e.Property(x => x.AddressHash).HasMaxLength(64);
                e.Property(x => x.KeyId).HasMaxLength(20);
                e.Property(x => x.Masked).HasMaxLength(400);
                e.Property(x => x.VerifiedBy).HasMaxLength(63);
                e.Property(x => x.Health).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.HealthReason).HasMaxLength(500);
                // One per person, channel and address, however it was added (by them or by a source): the upserts' conflict target.
                e.HasIndex(x => new { x.TenantId, x.SubscriberId, x.Channel, x.AddressHash }).IsUnique().HasDatabaseName(Constraints.ContactPointAddress);
                // An address across the tenant's people, for bounces and complaints (phase 4).
                e.HasIndex(x => new { x.TenantId, x.AddressHash });
                e.HasOne<Subscriber>().WithMany().HasForeignKey(x => new { x.TenantId, x.SubscriberId }).OnDelete(DeleteBehavior.Cascade)
                    .HasConstraintName(Constraints.ContactPointSubscriber);
            });

            model.Entity<Storm>(e =>
            {
                e.ToTable("storms");
                TenantKey(e);
                e.Property(x => x.RouteKey).HasMaxLength(63);
                // One open storm per route: the guard's insert is ON CONFLICT DO NOTHING against it.
                e.HasIndex(x => new { x.TenantId, x.RouteKey }).IsUnique().HasFilter("ended_at IS NULL").HasDatabaseName(Constraints.StormOpen);
                e.HasIndex(x => new { x.TenantId, x.StartedAt });
            });

            model.Entity<RouteMinute>(e =>
            {
                e.ToTable("route_minutes");
                e.HasKey(x => new { x.TenantId, x.RouteKey, x.Minute });
                e.Property(x => x.RouteKey).HasMaxLength(63);
                e.HasIndex(x => x.Minute);
            });

            model.Entity<TenantSettings>(e =>
            {
                e.ToTable("tenant_settings");
                e.HasKey(x => x.TenantId);
            });

            model.Entity<LinkCode>(e =>
            {
                e.ToTable("link_codes");
                TenantKey(e);
                e.Property(x => x.Channel).HasMaxLength(20);
                e.Property(x => x.CodeHash).HasMaxLength(64);
                e.HasIndex(x => new { x.TenantId, x.CodeHash }).IsUnique().HasDatabaseName(Constraints.LinkCodeOnce);
                e.HasIndex(x => new { x.TenantId, x.SubscriberId, x.CreatedAt });
                e.HasIndex(x => x.ExpiresAt);
                e.HasOne<Subscriber>().WithMany().HasForeignKey(x => new { x.TenantId, x.SubscriberId }).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<ContactVerification>(e =>
            {
                e.ToTable("contact_verifications");
                e.HasKey(x => new { x.TenantId, x.ContactPointId });
                e.Property(x => x.CodeHash).HasMaxLength(64);
                e.HasIndex(x => x.ExpiresAt);
                e.HasOne<ContactPoint>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContactPointId }).OnDelete(DeleteBehavior.Cascade)
                    .HasConstraintName(Constraints.VerificationContactPoint);
            });

            model.Entity<DeviceRegistration>(e =>
            {
                e.ToTable("device_registrations");
                TenantKey(e);
                e.Property(x => x.Platform).HasMaxLength(10);
                e.Property(x => x.AppId).HasMaxLength(200);
                // A token or Web Push subscription of up to 4,096 characters, encrypted and base64.
                e.Property(x => x.Token).HasMaxLength(6000);
                e.Property(x => x.TokenHash).HasMaxLength(64);
                e.Property(x => x.KeyId).HasMaxLength(20);
                e.Property(x => x.Locale).HasMaxLength(35);
                // One registration per token: registering it again moves it (design §9.2).
                e.HasIndex(x => new { x.TenantId, x.Platform, x.AppId, x.TokenHash }).IsUnique().HasDatabaseName(Constraints.DeviceToken);
                e.HasIndex(x => new { x.TenantId, x.SubscriberId });
                e.HasIndex(x => x.LastSeenAt);
                e.HasOne<Subscriber>().WithMany().HasForeignKey(x => new { x.TenantId, x.SubscriberId }).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<LocalGroup>(e =>
            {
                e.ToTable("local_groups");
                TenantKey(e);
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.Name).HasMaxLength(200);
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique().HasDatabaseName(Constraints.LocalGroupKey);
            });

            model.Entity<LocalGroupMember>(e =>
            {
                e.ToTable("local_group_members");
                e.HasKey(x => new { x.TenantId, x.GroupId, x.SubscriberId }).HasName(Constraints.LocalGroupMember);
                e.HasIndex(x => new { x.TenantId, x.SubscriberId });
                e.HasOne<LocalGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId }).OnDelete(DeleteBehavior.Cascade)
                    .HasConstraintName(Constraints.LocalGroupMemberGroup);
                e.HasOne<Subscriber>().WithMany().HasForeignKey(x => new { x.TenantId, x.SubscriberId }).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<Topic>(e =>
            {
                e.ToTable("topics");
                TenantKey(e);
                e.Property(x => x.Key).HasMaxLength(200);
                e.Property(x => x.Name).HasMaxLength(200);
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique().HasDatabaseName(Constraints.TopicKey);
            });

            model.Entity<TopicSubscription>(e =>
            {
                e.ToTable("topic_subscriptions");
                e.HasKey(x => new { x.TenantId, x.TopicId, x.SubscriberId }).HasName(Constraints.TopicSubscription);
                e.HasIndex(x => new { x.TenantId, x.SubscriberId });
                e.HasOne<Topic>().WithMany().HasForeignKey(x => new { x.TenantId, x.TopicId }).OnDelete(DeleteBehavior.Cascade)
                    .HasConstraintName(Constraints.TopicSubscriptionTopic);
                e.HasOne<Subscriber>().WithMany().HasForeignKey(x => new { x.TenantId, x.SubscriberId }).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<Preference>(e =>
            {
                e.ToTable("preferences");
                TenantKey(e);
                e.Property(x => x.Category).HasMaxLength(63);
                e.Property(x => x.Channel).HasMaxLength(20);
                e.Property(x => x.Mode).HasMaxLength(10);
                // One mode per person, category and channel.
                e.HasIndex(x => new { x.TenantId, x.SubscriberId, x.Category, x.Channel }).IsUnique().HasDatabaseName(Constraints.PreferenceKey);
                e.HasOne<Subscriber>().WithMany().HasForeignKey(x => new { x.TenantId, x.SubscriberId }).OnDelete(DeleteBehavior.Cascade)
                    .HasConstraintName(Constraints.PreferenceSubscriber);
            });

            model.Entity<Mute>(e =>
            {
                e.ToTable("mutes");
                TenantKey(e);
                e.Property(x => x.Match).HasColumnType("jsonb");
                e.Property(x => x.Reason).HasMaxLength(500);
                e.Property(x => x.CreatedBy).HasMaxLength(200);
                // The planner reads the tenant's and its people's mutes that haven't been cancelled.
                e.HasIndex(x => new { x.TenantId, x.SubscriberId }).HasFilter("cancelled_at IS NULL");
                e.HasOne<Subscriber>().WithMany().HasForeignKey(x => new { x.TenantId, x.SubscriberId }).OnDelete(DeleteBehavior.Cascade)
                    .HasConstraintName(Constraints.MuteSubscriber);
            });

            model.Entity<DisplayGroup>(e =>
            {
                e.ToTable("display_groups");
                TenantKey(e);
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.Name).HasMaxLength(100);
                // The key the catalog's references name (a type's chosen group), so one unique constraint, not an index beside it.
                e.HasAlternateKey(x => new { x.TenantId, x.Key }).HasName(Constraints.DisplayGroupKey);
            });

            model.Entity<TypeDisplayGroup>(e =>
            {
                e.ToTable("type_display_groups");
                TenantKey(e);
                e.Property(x => x.TypeKey).HasMaxLength(200);
                e.Property(x => x.GroupKey).HasMaxLength(63);
                e.HasIndex(x => new { x.TenantId, x.TypeKey }).IsUnique().HasDatabaseName(Constraints.TypeDisplayGroupKey);
                // The group must exist: a move racing the group's delete fails rather than pointing at nothing.
                e.HasOne<DisplayGroup>().WithMany().HasPrincipalKey(g => new { g.TenantId, g.Key }).HasForeignKey(x => new { x.TenantId, x.GroupKey })
                    .OnDelete(DeleteBehavior.Restrict).HasConstraintName(Constraints.TypeDisplayGroupGroup);
            });

            model.Entity<InboxItem>(e =>
            {
                e.ToTable("inbox_items");
                TenantKey(e);
                e.Property(x => x.TypeKey).HasMaxLength(200);
                e.Property(x => x.GroupKey).HasMaxLength(63);
                e.Property(x => x.Title).HasMaxLength(250);
                e.Property(x => x.Body).HasMaxLength(4000);
                e.Property(x => x.Url).HasMaxLength(2000);
                e.Property(x => x.Actions).HasColumnType("jsonb");
                e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
                // One item per delivery: a repeated send (a retry after the item was written) writes nothing.
                e.HasIndex(x => new { x.TenantId, x.DeliveryId }).IsUnique().HasDatabaseName(Constraints.InboxItemDelivery);
                // A person's open items, newest first, by group; and their unread counts.
                e.HasIndex(x => new { x.TenantId, x.SubscriberId, x.GroupKey, x.Id }).HasFilter("archived_at IS NULL").HasDatabaseName("ix_inbox_items_open");
                e.HasIndex(x => new { x.TenantId, x.SubscriberId, x.Id }).HasDatabaseName("ix_inbox_items_person");
                // A type moved, or a group deleted: its open items.
                e.HasIndex(x => new { x.TenantId, x.TypeKey }).HasFilter("archived_at IS NULL").HasDatabaseName("ix_inbox_items_type");
                e.HasIndex(x => new { x.TenantId, x.GroupKey }).HasFilter("archived_at IS NULL").HasDatabaseName("ix_inbox_items_group");
                e.HasOne<Subscriber>().WithMany().HasForeignKey(x => new { x.TenantId, x.SubscriberId }).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Delivery>().WithMany().HasForeignKey(x => new { x.TenantId, x.DeliveryId }).OnDelete(DeleteBehavior.Cascade);
                // A correlation's items, to tell their people when it changes.
                e.HasIndex(x => new { x.TenantId, x.CorrelationId }).HasFilter("correlation_id IS NOT NULL").HasDatabaseName("ix_inbox_items_correlation");
                e.HasOne<Correlation>().WithMany().HasForeignKey(x => new { x.TenantId, x.CorrelationId }).OnDelete(DeleteBehavior.Restrict);
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
                e.HasIndex(x => new { x.TenantId, x.CorrelationId }).HasFilter("correlation_id IS NOT NULL");
                e.HasOne<Correlation>().WithMany().HasForeignKey(x => new { x.TenantId, x.CorrelationId }).OnDelete(DeleteBehavior.Restrict);
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
                e.Property(x => x.EscalationSpecs).HasColumnType("jsonb");
                e.HasIndex(x => new { x.TenantId, x.RequestId }).IsUnique();
                e.HasOne<NotificationRequest>().WithMany().HasForeignKey(x => new { x.TenantId, x.RequestId }).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<Correlation>(e =>
            {
                e.ToTable("correlations");
                TenantKey(e);
                e.Property(x => x.Key).HasMaxLength(200);
                e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.AcknowledgedBy).HasMaxLength(200);
                e.Property(x => x.ResolvedBy).HasMaxLength(200);
                e.Property(x => x.Outcome).HasMaxLength(500);
                e.Property(x => x.Version).IsRowVersion();
                e.HasIndex(x => new { x.TenantId, x.ApplicationId, x.Key }).IsUnique().HasDatabaseName(Constraints.CorrelationKey);
                e.HasOne<ApplicationTenant>().WithMany().HasForeignKey(x => new { x.ApplicationId, x.TenantId }).OnDelete(DeleteBehavior.Restrict);
            });

            model.Entity<ProducerEvent>(e =>
            {
                e.ToTable("producer_events");
                TenantKey(e);
                e.Property(x => x.Event).HasMaxLength(40);
                e.Property(x => x.Payload).HasColumnType("jsonb");
                e.HasIndex(x => new { x.CreatedAt, x.Id }).HasDatabaseName("ix_producer_events_order");
            });

            model.Entity<ProviderEvent>(e =>
            {
                e.ToTable("provider_events");
                TenantKey(e);
                e.Property(x => x.Provider).HasMaxLength(40);
                e.Property(x => x.ProviderEventId).HasMaxLength(200);
                e.Property(x => x.Kind).HasMaxLength(20);
                e.HasIndex(x => new { x.TenantId, x.Provider, x.ProviderEventId }).IsUnique().HasDatabaseName(Constraints.ProviderEventOnce);
                e.HasIndex(x => new { x.TenantId, x.DeliveryId }).HasFilter("delivery_id IS NOT NULL").HasDatabaseName("ix_provider_events_delivery");
            });

            model.Entity<Interaction>(e =>
            {
                e.ToTable("interactions");
                TenantKey(e);
                e.Property(x => x.Kind).HasMaxLength(20);
                e.Property(x => x.ActionId).HasMaxLength(50);
                e.HasIndex(x => new { x.TenantId, x.InboxItemId, x.Kind, x.ActionId }).IsUnique().HasDatabaseName(Constraints.InteractionOnce);
                e.HasIndex(x => new { x.TenantId, x.CorrelationId }).HasFilter("correlation_id IS NOT NULL").HasDatabaseName("ix_interactions_correlation");
                e.HasOne<InboxItem>().WithMany().HasForeignKey(x => new { x.TenantId, x.InboxItemId }).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<EscalationPolicy>(e =>
            {
                e.ToTable("escalation_policies");
                TenantKey(e);
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.Steps).HasColumnType("jsonb");
                e.Property(x => x.ManagedBy).HasMaxLength(20);
                e.Property(x => x.DefinitionHash).HasMaxLength(64).IsConcurrencyToken();
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique().HasDatabaseName(Constraints.EscalationPolicyKey);
            });

            model.Entity<Escalation>(e =>
            {
                e.ToTable("escalations");
                TenantKey(e);
                Claim(e);
                e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.StopReason).HasMaxLength(40);
                e.HasIndex(x => new { x.TenantId, x.NotificationId }).IsUnique().HasDatabaseName("ix_escalations_notification");
                e.HasIndex(x => new { x.TenantId, x.CorrelationId }).HasDatabaseName("ix_escalations_correlation");
                e.HasOne<Notification>().WithMany().HasForeignKey(x => new { x.TenantId, x.NotificationId }).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Correlation>().WithMany().HasForeignKey(x => new { x.TenantId, x.CorrelationId }).OnDelete(DeleteBehavior.Restrict);
                // No key to the policy: one deleted while an escalation runs stops it (its step finds no policy) rather than being refused.
            });

            model.Entity<Digest>(e =>
            {
                e.ToTable("digests");
                TenantKey(e);
                Claim(e);
                e.Property(x => x.TypeKey).HasMaxLength(200);
                e.Property(x => x.By).HasMaxLength(20);
                e.Property(x => x.Key).HasMaxLength(500);
                e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
                e.HasIndex(x => new { x.TenantId, x.TypeKey, x.By, x.Key }).IsUnique().HasFilter("state = 'Open'").HasDatabaseName("ix_digests_open");
            });

            model.Entity<ThrottleEntry>(e =>
            {
                e.ToTable("throttle_entries");
                e.HasKey(x => new { x.TenantId, x.NotificationId, x.Step });
                e.Property(x => x.TypeKey).HasMaxLength(200);
                e.Property(x => x.Key).HasMaxLength(500);
                e.HasIndex(x => new { x.TenantId, x.TypeKey, x.Key, x.WindowStart }).HasDatabaseName("ix_throttle_entries_window");
                e.HasOne<Notification>().WithMany().HasForeignKey(x => new { x.TenantId, x.NotificationId }).OnDelete(DeleteBehavior.Cascade);
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
                // A subscriber's history keeps its recipients: a subscriber with recipients isn't deleted.
                e.HasOne<Subscriber>().WithMany().HasForeignKey(x => new { x.TenantId, x.SubscriberId }).OnDelete(DeleteBehavior.Restrict);
                // One recipient per thing named, per escalation step: planning twice can't add a second set (proof 3).
                e.HasIndex(x => new { x.TenantId, x.NotificationId, x.EscalationStep, x.NamedAs }).IsUnique().HasDatabaseName(Constraints.RecipientNamed);
                // A digest's members, when it closes.
                e.HasIndex(x => new { x.TenantId, x.DigestId }).HasFilter("digest_id IS NOT NULL").HasDatabaseName("ix_notification_recipients_digest");
                e.HasOne<Notification>().WithMany().HasForeignKey(x => new { x.TenantId, x.NotificationId }).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<Delivery>(e =>
            {
                e.ToTable("deliveries");
                TenantKey(e);
                Claim(e);
                e.HasIndex(x => new { x.TenantId, x.DigestId }).HasFilter("digest_id IS NOT NULL").HasDatabaseName("ix_deliveries_digest");
                // Callbacks name a send by the provider's id (phase-4-plan I40).
                e.HasIndex(x => new { x.TenantId, x.ProviderMessageId }).HasFilter("provider_message_id IS NOT NULL").HasDatabaseName("ix_deliveries_provider_message");
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
