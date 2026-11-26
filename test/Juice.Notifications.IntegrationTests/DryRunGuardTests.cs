using Juice.Notifications.Embedded;
using Juice.Notifications.Hosting;
using Juice.Notifications.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// QA's M44-0 guard cases, as M44-2 answers them: dry run is built, so whichever way a host asks for it, every part that decides dry run (the
    /// planners, the dispatcher, the start notice) reads the same resolved <see cref="IOptions{DeliveryOptions}"/>, and what the host asked for
    /// and what is done can't disagree (QA D1). No database is needed, but for the service host.
    /// </summary>
    public sealed class DryRunGuardTests
    {
        // Nothing listens there: nothing here touches the database.
        private const string Unreachable = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";

        private static ServiceProvider Build(Dictionary<string, string?> settings, Action<IServiceCollection, IConfiguration> after)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection().AddLogging();
            services.AddNotificationsPipeline(configuration, (_, db) => Data.NotificationsDbContext.Configure(db, Unreachable));
            after(services, configuration);
            return services.BuildServiceProvider();
        }

        /// <summary>What the dispatcher and the planners read: the resolved options.</summary>
        private static bool DryRun(IServiceProvider services) => services.GetRequiredService<IOptions<DeliveryOptions>>().Value.DryRun;

        public static TheoryData<string> Ways =>
        [
            "settings", "options callback", "add options", "post configure", "configure options class", "another section", "direct options instance",
        ];

        [Theory]
        [MemberData(nameof(Ways))]
        public void Dry_run_asked_for_in_any_way_is_what_the_pipeline_reads(string way)
        {
            var settings = new Dictionary<string, string?>();
            Action<IServiceCollection, IConfiguration> after = (_, _) => { };
            switch (way)
            {
                case "settings": settings["Delivery:DryRun"] = "True"; break;
                case "options callback": after = (s, _) => s.Configure<DeliveryOptions>(o => o.DryRun = true); break;
                case "add options": after = (s, _) => s.AddOptions<DeliveryOptions>().Configure(o => o.DryRun = true); break;
                case "post configure": after = (s, _) => s.PostConfigure<DeliveryOptions>(o => o.DryRun = true); break;
                case "configure options class":
                    after = (s, _) => s.AddSingleton<IConfigureOptions<DeliveryOptions>>(new ConfigureOptions<DeliveryOptions>(o => o.DryRun = true));
                    break;
                case "another section":
                    settings["Shadow:DryRun"] = "true";
                    after = (s, c) => s.Configure<DeliveryOptions>(c.GetSection("Shadow"));
                    break;
                case "direct options instance":
                    after = (s, _) => s.Replace(ServiceDescriptor.Singleton<IOptions<DeliveryOptions>>(Options.Create(new DeliveryOptions { DryRun = true })));
                    break;
            }
            using var provider = Build(settings, after);
            Assert.True(DryRun(provider));
        }

        [Fact]
        public void Dry_run_false_in_the_settings_is_off()
        {
            using var provider = Build(new() { ["Delivery:DryRun"] = "false" }, (_, _) => { });
            Assert.False(DryRun(provider));
        }

        [Fact]
        public void Dry_run_in_an_embedded_hosts_section_registers_and_is_read()
        {
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:Delivery:DryRun"] = "true" }).Build();
            var services = new ServiceCollection().AddLogging().AddJuiceNotificationsEmbedded(settings.GetSection("Notifications"), o =>
            {
                o.ConnectionString = Unreachable;
                o.TenantId = Guid.CreateVersion7();
                o.ApplicationKey = "qa";
                o.ApplicationName = "QA";
            });
            using var provider = services.BuildServiceProvider();
            Assert.True(DryRun(provider));
        }

        /// <summary>The service host with a directly registered dry-run options instance starts, in dry run (D1 was the guard missing it).</summary>
        [Fact]
        public async Task The_service_starts_with_a_directly_registered_dry_run_options_instance_and_reads_it()
        {
            LocalStack.RequireAvailable();
            var app = new NotificationsApp(host => Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(host, services =>
                services.Replace(ServiceDescriptor.Singleton<IOptions<DeliveryOptions>>(Options.Create(new DeliveryOptions { DryRun = true })))));
            try
            {
                await app.InitializeAsync();
                Assert.True(DryRun(app.Services));
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
    }
}
