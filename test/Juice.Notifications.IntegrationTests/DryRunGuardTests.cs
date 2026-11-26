using Juice.Notifications.Embedded;
using Juice.Notifications.Hosting;
using Juice.Notifications.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// QA (M44-0, phase-5 waves §4): until M44-2 builds dry run, a host that asks for it must fail at start, whichever way it asks, and before
    /// any of the pipeline's hosted services (migrator, stages, dispatcher) start. No database is needed: the guard runs first.
    /// </summary>
    public sealed class DryRunGuardTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private const string D1 = "QA defect D1: an IOptions<DeliveryOptions> registered directly bypasses the start guard; the host starts and the dispatcher sees DryRun true.";

        /// <summary>Registered before the pipeline: had any hosted service started, this one would have.</summary>
        private sealed class Probe : IHostedService
        {
            public bool Started { get; private set; }
            public Task StartAsync(CancellationToken cancellationToken) { Started = true; return Task.CompletedTask; }
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        // Nothing listens there: a hosted service that got as far as the database would fail differently, and late.
        private const string Unreachable = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1";

        private static (IHost Host, Probe Probe) Build(Dictionary<string, string?> settings, Action<IServiceCollection, IConfiguration> after)
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Configuration.AddInMemoryCollection(settings);
            var probe = new Probe();
            builder.Services.AddSingleton<IHostedService>(probe);
            builder.Services.AddNotificationsPipeline(builder.Configuration, (_, db) => Data.NotificationsDbContext.Configure(db, Unreachable));
            after(builder.Services, builder.Configuration);
            return (builder.Build(), probe);
        }

        private static async Task AssertRefusedAtStartAsync(IHost host, Probe probe)
        {
            using (host)
            {
                var refused = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Ct));
                Assert.Contains("M44-2", refused.Message);
                Assert.False(probe.Started, "a hosted service started before the dry-run guard refused the host");
            }
        }

        [Theory]
        [InlineData("true")]
        [InlineData("True")]
        [InlineData("TRUE")]
        public void Dry_run_in_the_settings_is_refused_at_registration(string value)
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:DryRun"] = value });
            var refused = Assert.Throws<InvalidOperationException>(
                () => builder.Services.AddNotificationsPipeline(builder.Configuration, (_, db) => Data.NotificationsDbContext.Configure(db, Unreachable)));
            Assert.Contains("M44-2", refused.Message);
        }

        [Fact]
        public void Dry_run_false_in_the_settings_registers()
        {
            var (host, _) = Build(new() { ["Delivery:DryRun"] = "false" }, (_, _) => { });
            using (host)
            {
                Assert.False(host.Services.GetRequiredService<IOptions<DeliveryOptions>>().Value.DryRun);
            }
        }

        [Fact]
        public async Task Dry_run_added_to_the_settings_after_registration_is_refused_at_start()
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            var probe = new Probe();
            builder.Services.AddSingleton<IHostedService>(probe);
            builder.Services.AddNotificationsPipeline(builder.Configuration, (_, db) => Data.NotificationsDbContext.Configure(db, Unreachable));
            // A source the host adds later (ConfigurationManager is live): the registration-time check can't see it; the start must.
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:DryRun"] = "true" });
            await AssertRefusedAtStartAsync(builder.Build(), probe);
        }

        [Fact]
        public async Task Dry_run_set_in_an_options_callback_is_refused_at_start()
        {
            var (host, probe) = Build([], (s, _) => s.Configure<DeliveryOptions>(o => o.DryRun = true));
            await AssertRefusedAtStartAsync(host, probe);
        }

        [Fact]
        public async Task Dry_run_set_through_add_options_is_refused_at_start()
        {
            var (host, probe) = Build([], (s, _) => s.AddOptions<DeliveryOptions>().Configure(o => o.DryRun = true));
            await AssertRefusedAtStartAsync(host, probe);
        }

        [Fact]
        public async Task Dry_run_set_in_post_configure_is_refused_at_start()
        {
            var (host, probe) = Build([], (s, _) => s.PostConfigure<DeliveryOptions>(o => o.DryRun = true));
            await AssertRefusedAtStartAsync(host, probe);
        }

        [Fact]
        public async Task Dry_run_set_by_a_configure_options_class_is_refused_at_start()
        {
            var (host, probe) = Build([], (s, _) => s.AddSingleton<IConfigureOptions<DeliveryOptions>>(new ConfigureOptions<DeliveryOptions>(o => o.DryRun = true)));
            await AssertRefusedAtStartAsync(host, probe);
        }

        [Fact]
        public async Task Dry_run_bound_from_another_section_is_refused_at_start()
        {
            var (host, probe) = Build(new() { ["Shadow:DryRun"] = "true" }, (s, c) => s.Configure<DeliveryOptions>(c.GetSection("Shadow")));
            await AssertRefusedAtStartAsync(host, probe);
        }

        [Fact]
        public async Task Dry_run_set_in_code_before_the_pipeline_is_refused_at_start()
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            var probe = new Probe();
            builder.Services.AddSingleton<IHostedService>(probe);
            builder.Services.Configure<DeliveryOptions>(o => o.DryRun = true);
            builder.Services.AddNotificationsPipeline(builder.Configuration, (_, db) => Data.NotificationsDbContext.Configure(db, Unreachable));
            await AssertRefusedAtStartAsync(builder.Build(), probe);
        }

        [Fact]
        public void Dry_run_in_an_embedded_hosts_section_is_refused_at_registration()
        {
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:Delivery:DryRun"] = "true" }).Build();
            var refused = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddJuiceNotificationsEmbedded(settings.GetSection("Notifications"), o =>
            {
                o.ConnectionString = Unreachable;
                o.TenantId = Guid.CreateVersion7();
                o.ApplicationKey = "qa";
                o.ApplicationName = "QA";
            }));
            Assert.Contains("M44-2", refused.Message);
        }

        /// <summary>
        /// The validator reads the options through IOptionsMonitor; the dispatcher and the providers take IOptions&lt;DeliveryOptions&gt;.
        /// A host that registers IOptions&lt;DeliveryOptions&gt; itself (Options.Create) bypasses the factory, so validation sees DryRun false
        /// while the dispatcher sees true.
        /// </summary>
        [Fact]
        public async Task Dry_run_in_a_directly_registered_options_instance_is_refused_at_start()
        {
            var (host, probe) = Build([], (s, _) => s.Replace(ServiceDescriptor.Singleton<IOptions<DeliveryOptions>>(Options.Create(new DeliveryOptions { DryRun = true }))));
            await AssertRefusedAtStartAsync(host, probe);
        }

        /// <summary>D1 in the real service host: the App starts with DryRun true where the dispatcher reads it.</summary>
        [Fact]
        public async Task The_service_refuses_to_start_with_a_directly_registered_dry_run_options_instance()
        {
            LocalStack.RequireAvailable();
            var app = new NotificationsApp(host => Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(host, services =>
                services.Replace(ServiceDescriptor.Singleton<IOptions<DeliveryOptions>>(Options.Create(new DeliveryOptions { DryRun = true })))));
            try
            {
                var refused = await Assert.ThrowsAnyAsync<Exception>(async () => await app.InitializeAsync());
                Assert.Contains("M44-2", refused.ToString());
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
    }
}
