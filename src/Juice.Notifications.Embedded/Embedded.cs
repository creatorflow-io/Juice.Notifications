using System.Data.Common;
using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Hosting;
using Juice.Notifications.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Notifications.Embedded
{
    /// <summary>
    /// What an embedded host says about itself (phase-5-plan I57, I61, I63): its database, the one tenant it is (its platform tenant id), and
    /// the one application it sends as, with the types it declares.
    /// </summary>
    public sealed class NotificationsEmbeddedOptions
    {
        /// <summary>The host's PostgreSQL: the pipeline's tables are its <c>notifications</c> schema (I60).</summary>
        public string? ConnectionString { get; set; }

        /// <summary>The host's tenant, every request's and every page's (I61): a deployment has one.</summary>
        public Guid TenantId { get; set; }

        /// <summary>The application the host sends as (its types' namespace: <c>observability</c> for <c>observability.alert.firing</c>).</summary>
        public string ApplicationKey { get; set; } = "";

        public string ApplicationName { get; set; } = "";

        /// <summary>The types, declared at start (each a new version when it changes, as a producer's declaration is).</summary>
        public List<TypeDeclaration> Types { get; } = [];
    }

    public static class NotificationsEmbeddedRegistration
    {
        /// <summary>
        /// The notification pipeline in this host (design §4.2, phase-5-plan I57): intake, the planner with the route and address resolvers,
        /// workflow, correlations and escalation, the delivery planner, the dispatcher with webhook and email, storms and retention, on the
        /// host's PostgreSQL. <see cref="INotificationClient"/> writes in the host's transaction (<see cref="INotificationTransaction"/>, which
        /// the host registers, scoped), and an <see cref="INotificationsAuditSink"/> the host registers hears each change to the setup.
        /// <paramref name="configuration"/> is the pipeline's settings, a section of the host's (<c>Notifications</c>: <c>Stages</c>,
        /// <c>Retention</c>, <c>Delivery</c>, <c>Database:Migrate</c>, …). Nothing of the service is registered: no authentication handler,
        /// tenancy middleware, people, inbox hub, RabbitMQ connection or audit client. Chat providers are added by the host
        /// (<c>AddChatProviders</c>).
        /// </summary>
        public static IServiceCollection AddJuiceNotificationsEmbedded(this IServiceCollection services, IConfiguration configuration,
            Action<NotificationsEmbeddedOptions> embedded)
        {
            var options = new NotificationsEmbeddedOptions();
            embedded(options);
            if (Problem(options) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }
            services.AddSingleton(options);
            // Set by the client for its own scope only: a stage's scope connects by the connection string.
            services.AddScoped<HostConnection>();
            services.AddNotificationsPipeline(configuration, (sp, db) =>
            {
                if (sp.GetService<HostConnection>()?.Connection is { } connection)
                {
                    NotificationsDbContext.Configure(db, connection);
                }
                else
                {
                    NotificationsDbContext.Configure(db, options.ConnectionString!);
                }
            }, new NotificationsPipelineOptions
            {
                People = false,
                // After the migrator and the provisioning file, before the stages: a request can't be planned before its type exists.
                BeforeStages = s => s.AddHostedService<EmbeddedApplicationProvisioner>(),
            });
            services.AddPostgresClaimStore();
            services.Replace(ServiceDescriptor.Scoped<Configuration.IProvisioningScope, EmbeddedProvisioningScope>());
            services.TryAddScoped<HostScope>();
            // The pipeline's commands in the host's transaction, for a host that changes its rows and the setup together (phase-5 M46-2).
            services.TryAddScoped<INotificationsHostScope>(sp => sp.GetRequiredService<HostScope>());
            services.TryAddScoped<INotificationClient, EmbeddedNotificationClient>();
            // Changes to the setup, to the host's sink when it has one (I62).
            services.AddScoped(typeof(Juice.MediatR.INotificationHandler<>), typeof(AuditForwarder<>));
            return services;
        }

        /// <summary>What is wrong with the options, or null.</summary>
        internal static string? Problem(NotificationsEmbeddedOptions o)
        {
            if (string.IsNullOrWhiteSpace(o.ConnectionString))
            {
                return "Embedded notifications: ConnectionString is required (the host's PostgreSQL).";
            }
            if (o.TenantId == Guid.Empty)
            {
                return "Embedded notifications: TenantId is required (the host's tenant).";
            }
            if (!Applications.ApplicationRules.Key().IsMatch(o.ApplicationKey))
            {
                return $"Embedded notifications: ApplicationKey '{o.ApplicationKey}' is 2–63 lowercase letters, digits or hyphens, starting with a letter.";
            }
            if (o.ApplicationKey == People.ServiceTypes.ApplicationKey)
            {
                return $"Embedded notifications: the application key '{o.ApplicationKey}' is the service's own.";
            }
            if (string.IsNullOrWhiteSpace(o.ApplicationName) || o.ApplicationName.Trim().Length > 200)
            {
                return "Embedded notifications: ApplicationName is 1–200 characters.";
            }
            return o.Types.FirstOrDefault(t => t.Key?.StartsWith(o.ApplicationKey + ".", StringComparison.Ordinal) != true) is { } outside
                ? $"Embedded notifications: the type {outside.Key} isn't under the application's key ({o.ApplicationKey}.…)."
                : null;
        }
    }

    /// <summary>The host's connection, for the one scope a client call runs in.</summary>
    internal sealed class HostConnection
    {
        public DbConnection? Connection { get; set; }
    }
}
