using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Data.PostgreSQL;
using Juice.Notifications.Delivering;
using Juice.Notifications.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// The delivery planner's rules, one recipient at a time through the stage runner on a fixed clock (phase-1-plan M5): which destinations a
    /// recipient reaches, through which integration, with which template, and what is recorded when it can't. Added after mutation testing
    /// showed these branches driven by no test.
    /// </summary>
    public sealed class DeliveryPlannerTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly StageOptions Options = new() { Batch = 10, Lease = TimeSpan.FromSeconds(30) };
        private readonly ScratchDatabase _database = new();
        private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
        private ServiceProvider? _services;
        private readonly RenderCounter _renders = new();

        public async ValueTask InitializeAsync()
        {
            await _database.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            await using (var db = await _database.MigratedAsync())
            {
            }
            var services = new ServiceCollection().AddLogging(l => l.AddProvider(new ErrorLogProvider()));
            services.AddDbContext<NotificationsDbContext>(options => NotificationsDbContext.Configure(options, _database.ConnectionString));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddPostgresClaimStore();
            services.AddSingleton(_renders);
            services.AddDeliveryPlanning(Options, runWorkers: false);
            _services = services.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync()
        {
            if (_services is not null)
            {
                await _services.DisposeAsync();
            }
            await _database.DisposeAsync();
        }

        /// <summary>A tenant of its own for each test, with one type and its request and notification.</summary>
        private sealed class Scene(Guid tenant, NotificationType type, NotificationRequest request, Notification notification)
        {
            public Guid Tenant { get; } = tenant;
            public NotificationType Type { get; } = type;
            public NotificationRequest Request { get; } = request;
            public Notification Notification { get; } = notification;
        }

        private async Task<Scene> SceneAsync(DateTimeOffset? expiresAt = null)
        {
            await using var db = _database.Context();
            var tenant = Guid.CreateVersion7();
            var app = Seed.Application("a" + Guid.NewGuid().ToString("N")[..10]);
            var type = Seed.Type(app.Id, app.Key + ".thing");
            var request = Seed.Request(tenant, app.Id, type.Id, Guid.NewGuid().ToString("N"));
            db.Entry(request).Property(r => r.TypeKey).CurrentValue = type.Key;
            db.Entry(request).Property(r => r.Data).CurrentValue = """{ "host": "db-1" }""";
            db.Entry(request).Property(r => r.ExpiresAt).CurrentValue = expiresAt;
            var notification = Seed.Notification(tenant, request.Id);
            notification.State = NotificationState.Planned;
            notification.NextAt = null;
            db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = tenant, LinkedAt = Seed.Now }, request, notification);
            await db.SaveChangesAsync(Ct);
            return new Scene(tenant, type, request, notification);
        }

        private async Task<Integration> IntegrationAsync(Scene scene, string key, string channel = Channels.Email, bool enabled = true, int priority = 0)
        {
            await using var db = _database.Context();
            var integration = new Integration
            {
                TenantId = scene.Tenant, Key = key, Channel = channel, Kind = channel == Channels.Email ? "smtp" : "webhook",
                Settings = """{ "server": "local", "from": "n@juice.local" }""", Enabled = enabled, Priority = priority, DefinitionHash = "h", UpdatedAt = Seed.Now,
            };
            db.Integrations.Add(integration);
            await db.SaveChangesAsync(Ct);
            return integration;
        }

        private async Task<IntegrationTarget> TargetAsync(Integration integration, string key, string address, bool enabled = true)
        {
            await using var db = _database.Context();
            var target = new IntegrationTarget
            {
                TenantId = integration.TenantId, IntegrationId = integration.Id, Key = key, Address = address, Enabled = enabled, DefinitionHash = "h", UpdatedAt = Seed.Now,
            };
            db.IntegrationTargets.Add(target);
            await db.SaveChangesAsync(Ct);
            return target;
        }

        private async Task<Route> RouteAsync(Scene scene, params string[] targets)
        {
            await using var db = _database.Context();
            var route = new Route { TenantId = scene.Tenant, Key = "r" + Guid.NewGuid().ToString("N")[..8], Match = "{}", Targets = targets, DefinitionHash = "h", UpdatedAt = Seed.Now };
            db.Routes.Add(route);
            await db.SaveChangesAsync(Ct);
            return route;
        }

        private async Task TemplateAsync(Scene scene, string channel, string body, int version = 1, Guid? tenant = null, string engine = "scriban")
        {
            await using var db = _database.Context();
            db.Templates.Add(new Template
            {
                TenantId = tenant, TypeId = scene.Type.Id, Channel = channel, Locale = "en", Version = version, Engine = engine, Body = body, CreatedAt = Seed.Now,
            });
            await db.SaveChangesAsync(Ct);
        }

        /// <summary>A recipient due now, as the planner leaves it.</summary>
        private async Task<NotificationRecipient> RecipientAsync(Scene scene, Route? route = null, string? address = null, string channel = Channels.Email,
            RecipientOutcome outcome = RecipientOutcome.Planned, Guid? routeId = null)
        {
            await using var db = _database.Context();
            var recipient = new NotificationRecipient
            {
                TenantId = scene.Tenant, NotificationId = scene.Notification.Id, Kind = address is null ? RecipientKind.Route : RecipientKind.External,
                RouteId = routeId ?? route?.Id, RouteKey = route?.Key, Channel = address is null ? null : channel, Address = address,
                Resolver = address is null ? "route" : "address", NamedAs = address is null ? "route:" + route?.Key : $"address:{channel}:{address}",
                Outcome = outcome, NextAt = _clock.GetUtcNow(), CreatedAt = Seed.Now,
            };
            db.Recipients.Add(recipient);
            await db.SaveChangesAsync(Ct);
            return recipient;
        }

        private Task<PassResult> RunAsync()
            => _services!.GetRequiredService<StageRunner<NotificationRecipient, DeliveryPlannerStage>>().RunOnceAsync("delivery-planner", Options, Ct);

        private async Task<(NotificationRecipient Recipient, List<Delivery> Deliveries)> AfterAsync(NotificationRecipient recipient)
        {
            await using var db = _database.Context();
            var after = await db.Recipients.SingleAsync(r => r.TenantId == recipient.TenantId && r.Id == recipient.Id, Ct);
            var ids = await db.DeliveryItems.Where(i => i.TenantId == recipient.TenantId && i.RecipientId == recipient.Id).Select(i => i.DeliveryId).ToListAsync(Ct);
            var deliveries = await db.Deliveries.Where(d => d.TenantId == recipient.TenantId && ids.Contains(d.Id)).OrderBy(d => d.DestinationKey).ToListAsync(Ct);
            return (after, deliveries);
        }

        [Fact]
        public async Task An_address_goes_through_the_first_enabled_integration_of_its_channel()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            await IntegrationAsync(scene, "off", enabled: false, priority: 0);
            await IntegrationAsync(scene, "second", priority: 5);
            var first = await IntegrationAsync(scene, "zz-first", priority: 1);
            await IntegrationAsync(scene, "aa-tie", priority: 1, channel: Channels.Webhook); // another channel: not a candidate
            await TemplateAsync(scene, Channels.Email, "Subject: Disk\n\nHost {{ data.host }}");
            var recipient = await RecipientAsync(scene, address: "ops@example.com");

            Assert.Equal(1, (await RunAsync()).Completed);

            var (after, deliveries) = await AfterAsync(recipient);
            var delivery = Assert.Single(deliveries);
            Assert.Equal((first.Id, "ext:email:ops@example.com", DeliveryStatus.Pending, (DateTimeOffset?)_clock.GetUtcNow(), (FailureCategory?)null),
                (delivery.IntegrationId, delivery.DestinationKey, delivery.Status, delivery.NextAt, delivery.FailureCategory));
            Assert.Contains("db-1", delivery.Content);
            Assert.Equal((1, "en"), (delivery.TemplateVersion, delivery.Locale));
            Assert.Equal((RecipientOutcome.Planned, (DateTimeOffset?)null), (after.Outcome, after.NextAt));
        }

        [Fact]
        public async Task An_integration_of_equal_priority_is_chosen_by_key()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            await IntegrationAsync(scene, "b-mail", priority: 1);
            var a = await IntegrationAsync(scene, "a-mail", priority: 1);
            await TemplateAsync(scene, Channels.Email, "Hello");
            var recipient = await RecipientAsync(scene, address: "ops@example.com");
            await RunAsync();
            Assert.Equal(a.Id, Assert.Single((await AfterAsync(recipient)).Deliveries).IntegrationId);
        }

        [Fact]
        public async Task An_address_with_no_enabled_integration_reaches_nothing_and_says_so()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            await IntegrationAsync(scene, "off", enabled: false);
            await IntegrationAsync(scene, "other-tenant-like", channel: Channels.Webhook);
            var recipient = await RecipientAsync(scene, address: "ops@example.com");

            Assert.Equal(1, (await RunAsync()).Completed);

            var (after, deliveries) = await AfterAsync(recipient);
            Assert.Empty(deliveries);
            Assert.Equal((RecipientOutcome.Failed, (DateTimeOffset?)null), (after.Outcome, after.NextAt));
        }

        [Fact]
        public async Task A_route_reaches_its_enabled_targets_and_records_a_disabled_integration_as_failed()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            var on = await IntegrationAsync(scene, "on");
            var off = await IntegrationAsync(scene, "off", enabled: false);
            var a = await TargetAsync(on, "a", "a@example.com");
            var b = await TargetAsync(off, "b", "b@example.com");
            await TargetAsync(on, "c", "c@example.com", enabled: false);
            var route = await RouteAsync(scene, "c", "b", "a", "missing");
            await TemplateAsync(scene, Channels.Email, "Hello {{ data.host }}");
            var recipient = await RecipientAsync(scene, route);

            Assert.Equal(1, (await RunAsync()).Completed);

            var (after, deliveries) = await AfterAsync(recipient);
            Assert.Equal(new[] { $"it:{a.Id}", $"it:{b.Id}" }.Order(), deliveries.Select(d => d.DestinationKey));
            var sent = deliveries.Single(d => d.DestinationKey == $"it:{a.Id}");
            Assert.Equal((DeliveryStatus.Pending, (DateTimeOffset?)_clock.GetUtcNow(), (string?)null, (DateTimeOffset?)null),
                (sent.Status, sent.NextAt, sent.LastError, sent.CompletedAt));
            // Its content is rendered all the same, so a dead-letter retry once the integration is back has something to send.
            var failed = deliveries.Single(d => d.DestinationKey == $"it:{b.Id}");
            Assert.Equal((DeliveryStatus.Failed, (FailureCategory?)FailureCategory.Provider, "The integration off is disabled.", (DateTimeOffset?)null,
                (DateTimeOffset?)_clock.GetUtcNow()), (failed.Status, failed.FailureCategory, failed.LastError, failed.NextAt, failed.CompletedAt));
            Assert.Contains("db-1", failed.Content);
            Assert.Contains("\"target\":\"a\"", sent.DestinationSnapshot);
            Assert.Equal(RecipientOutcome.Planned, after.Outcome);
        }

        [Fact]
        public async Task A_route_with_nothing_to_reach_fails_its_recipient()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            var on = await IntegrationAsync(scene, "on");
            await TargetAsync(on, "c", "c@example.com", enabled: false);
            var nothing = await RecipientAsync(scene, await RouteAsync(scene, "c", "missing"));
            var deleted = await RecipientAsync(scene, routeId: Guid.CreateVersion7());

            Assert.Equal(2, (await RunAsync()).Completed);

            foreach (var recipient in new[] { nothing, deleted })
            {
                var (after, deliveries) = await AfterAsync(recipient);
                Assert.Empty(deliveries);
                Assert.Equal((RecipientOutcome.Failed, (DateTimeOffset?)null), (after.Outcome, after.NextAt));
            }
        }

        [Fact]
        public async Task An_expired_request_is_cancelled_and_a_recipient_not_planned_is_left_as_it_is()
        {
            LocalStack.RequireAvailable();
            var expired = await SceneAsync(expiresAt: _clock.GetUtcNow());
            var stillDue = await SceneAsync(expiresAt: _clock.GetUtcNow().AddSeconds(1));
            foreach (var scene in new[] { expired, stillDue })
            {
                await IntegrationAsync(scene, "mail");
                await TemplateAsync(scene, Channels.Email, "Hello");
            }
            var cancelled = await RecipientAsync(expired, address: "ops@example.com");
            var sent = await RecipientAsync(stillDue, address: "ops@example.com");
            var muted = await RecipientAsync(stillDue, address: "other@example.com", outcome: RecipientOutcome.Muted);

            Assert.Equal(3, (await RunAsync()).Completed);

            var (afterCancelled, none) = await AfterAsync(cancelled);
            Assert.Empty(none);
            Assert.Equal((RecipientOutcome.Cancelled, (DateTimeOffset?)null), (afterCancelled.Outcome, afterCancelled.NextAt));
            Assert.Single((await AfterAsync(sent)).Deliveries);
            var (afterMuted, mutedDeliveries) = await AfterAsync(muted);
            Assert.Empty(mutedDeliveries);
            Assert.Equal((RecipientOutcome.Muted, (DateTimeOffset?)null), (afterMuted.Outcome, afterMuted.NextAt));
        }

        /// <summary>
        /// Content is rendered once for every destination of a channel (phase-5 M46-0), unless its template reads <c>delivery</c>: then once per
        /// delivery, each with its own id.
        /// </summary>
        [Theory]
        [InlineData("Subject: hi\nHello {{ data.host }} [{{ escalation.step }}]", 1)]
        [InlineData("Subject: hi\nid={{ delivery.id }}", 3)]
        [InlineData("Subject: hi\nid={{ this.delivery.id }}", 3)] // QA (M46-0): through this
        [InlineData("Subject: hi\n{{ with delivery }}id={{ id }}{{ end }}", 3)] // QA (M46-0): through with
        public async Task A_channel_is_rendered_once_unless_its_template_reads_the_delivery(string body, int renders)
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            var mail = await IntegrationAsync(scene, "mail");
            foreach (var key in new[] { "a", "b", "c" })
            {
                await TargetAsync(mail, key, $"{key}@example.com");
            }
            var route = await RouteAsync(scene, "a", "b", "c");
            await TemplateAsync(scene, Channels.Email, body);
            var recipient = await RecipientAsync(scene, route);

            await RunAsync();

            var (_, deliveries) = await AfterAsync(recipient);
            Assert.Equal(3, deliveries.Count);
            Assert.Equal(renders, _renders.Count);
            if (renders > 1)
            {
                Assert.All(deliveries, d => Assert.Contains($"id={d.Id}", d.Content));
            }
            else
            {
                Assert.All(deliveries, d => Assert.Contains("Hello db-1 []", d.Content));
            }
        }

        [Fact]
        public async Task The_tenants_template_wins_then_the_latest_version()
        {
            LocalStack.RequireAvailable();
            var mine = await SceneAsync();
            await IntegrationAsync(mine, "mail");
            await TemplateAsync(mine, Channels.Email, "default v1", version: 1);
            await TemplateAsync(mine, Channels.Email, "default v2", version: 2);
            await TemplateAsync(mine, Channels.Email, "another tenant's", version: 9, tenant: Guid.CreateVersion7());
            var defaultRecipient = await RecipientAsync(mine, address: "ops@example.com");
            await RunAsync();
            var byDefault = Assert.Single((await AfterAsync(defaultRecipient)).Deliveries);
            Assert.Equal(2, byDefault.TemplateVersion);
            Assert.Contains("default v2", byDefault.Content);

            await TemplateAsync(mine, Channels.Email, "mine v1", version: 1, tenant: mine.Tenant);
            await TemplateAsync(mine, Channels.Email, "mine v3", version: 3, tenant: mine.Tenant);
            var overridden = await RecipientAsync(mine, address: "dev@example.com");
            await RunAsync();
            var byTenant = Assert.Single((await AfterAsync(overridden)).Deliveries);
            Assert.Equal(3, byTenant.TemplateVersion);
            Assert.Contains("mine v3", byTenant.Content);
        }

        [Fact]
        public async Task Content_that_cant_be_made_is_a_failed_delivery_with_the_reason()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            await IntegrationAsync(scene, "mail");
            await IntegrationAsync(scene, "hook", channel: Channels.Webhook);
            // No email template at all.
            var untemplated = await RecipientAsync(scene, address: "ops@example.com");
            await RunAsync();
            var missing = Assert.Single((await AfterAsync(untemplated)).Deliveries);
            Assert.Equal((DeliveryStatus.Failed, (FailureCategory?)FailureCategory.Content, ""), (missing.Status, missing.FailureCategory, missing.Content));
            Assert.Contains("has no email template", missing.LastError);

            // A template that fails at render time: the application's, then a tenant's own (marked, so its admins know it's theirs).
            await TemplateAsync(scene, Channels.Email, "{{ while true }}x{{ end }}");
            var broken = await RecipientAsync(scene, address: "dev@example.com");
            await RunAsync();
            var appFailure = Assert.Single((await AfterAsync(broken)).Deliveries);
            Assert.Equal((DeliveryStatus.Failed, (FailureCategory?)FailureCategory.Content), (appFailure.Status, appFailure.FailureCategory));
            Assert.StartsWith("render_failed: ", appFailure.LastError);

            await TemplateAsync(scene, Channels.Email, "{{ while true }}y{{ end }}", tenant: scene.Tenant);
            var brokenByTenant = await RecipientAsync(scene, address: "qa@example.com");
            await RunAsync();
            Assert.StartsWith("render_failed (tenant template): ", Assert.Single((await AfterAsync(brokenByTenant)).Deliveries).LastError);

            // An integration on a channel no longer registered (its package removed): nothing says what its content is.
            await IntegrationAsync(scene, "texts", channel: "sms");
            var unregistered = await RecipientAsync(scene, address: "+15550100", channel: "sms");
            await RunAsync();
            var gone = Assert.Single((await AfterAsync(unregistered)).Deliveries);
            Assert.Equal((DeliveryStatus.Failed, (FailureCategory?)FailureCategory.Content, ""), (gone.Status, gone.FailureCategory, gone.Content));
            Assert.Equal("The channel sms isn't registered.", gone.LastError);
        }

        [Fact]
        public async Task A_webhook_without_a_template_sends_the_request_as_json()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            var hook = await IntegrationAsync(scene, "hook", channel: Channels.Webhook);
            var site = await TargetAsync(hook, "site", "https://hooks.example.com/in");
            var recipient = await RecipientAsync(scene, await RouteAsync(scene, "site"));

            await RunAsync();

            var delivery = Assert.Single((await AfterAsync(recipient)).Deliveries);
            Assert.Equal((DeliveryStatus.Pending, $"it:{site.Id}", 0), (delivery.Status, delivery.DestinationKey, delivery.TemplateVersion));
            Assert.Contains(scene.Request.Id.ToString(), delivery.Content);
            Assert.Contains("db-1", delivery.Content);
            Assert.Equal("application/json", JsonDocument.Parse(delivery.Content).RootElement.GetProperty("ContentType").GetString());

            // With a template of its own, its text as the body, in the webhook's shape (not an email's).
            await TemplateAsync(scene, Channels.Webhook, "plain {{ data.host }}");
            var templated = await RecipientAsync(scene, await RouteAsync(scene, "site"));
            await RunAsync();
            var content = JsonDocument.Parse(Assert.Single((await AfterAsync(templated)).Deliveries).Content).RootElement;
            Assert.Equal(("text/plain; charset=utf-8", "plain db-1"), (content.GetProperty("ContentType").GetString(), content.GetProperty("Body").GetString()));
            Assert.False(content.TryGetProperty("Subject", out _));
        }

        [Fact]
        public async Task A_step_run_again_adds_no_second_delivery()
        {
            LocalStack.RequireAvailable();
            var scene = await SceneAsync();
            await IntegrationAsync(scene, "mail");
            await TemplateAsync(scene, Channels.Email, "Hello");
            var recipient = await RecipientAsync(scene, address: "ops@example.com");
            await RunAsync();

            // As after a step whose result was saved but whose row came due again (a lost lease).
            await using (var db = _database.Context())
            {
                await db.Recipients.Where(r => r.TenantId == recipient.TenantId && r.Id == recipient.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.NextAt, _clock.GetUtcNow()), Ct);
            }
            Assert.Equal(1, (await RunAsync()).Completed);

            var (after, deliveries) = await AfterAsync(recipient);
            Assert.Single(deliveries);
            // It still has its delivery from before: planned, not failed for reaching nothing new.
            Assert.Equal(RecipientOutcome.Planned, after.Outcome);
        }
    }
}
