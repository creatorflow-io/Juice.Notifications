using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    public sealed class RetentionApp : IAsyncLifetime
    {
        public NotificationsApp App { get; } = new();

        public ValueTask InitializeAsync() => App.InitializeAsync();

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>
    /// Redaction and retention (design §13.2, phase-4-plan I46, M33): a finished request's redacted paths are masked in its data and its inbox
    /// items; old deliveries and requests go, and old deliveries lose their content first, each by the tenant's own retention.
    /// </summary>
    public sealed class RetentionTests(RetentionApp fixture) : IClassFixture<RetentionApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private NotificationsApp App => fixture.App;
        private RetentionWorker Worker => App.Services.GetRequiredService<RetentionWorker>();

        /// <summary>A request in its own tenant, accepted <paramref name="age"/> ago, with a delivery (and an inbox item) or still waiting to be planned.</summary>
        private async Task<(Guid Tenant, NotificationRequest Request, Delivery Delivery, InboxItem Item)> SeedAsync(TimeSpan age, bool finished = true,
            Guid? tenant = null, string data = """{"secret":"tok-123456","note":"hello"}""")
        {
            var tenantId = tenant ?? Guid.CreateVersion7();
            var at = DateTimeOffset.UtcNow - age;
            await using var db = App.Database.Context();
            var app = Seed.Application("rt" + Guid.NewGuid().ToString("N")[..8]);
            var plain = Seed.Type(app.Id, app.Key + ".thing");
            var type = new NotificationType
            {
                ApplicationId = app.Id, Key = plain.Key, Version = 1, Name = plain.Name, Category = plain.Category, DisplayGroup = plain.DisplayGroup,
                Priority = plain.Priority, Schema = plain.Schema, SchemaHash = plain.SchemaHash, Workflow = plain.Workflow, DeclarationHash = plain.DeclarationHash,
                CreatedAt = plain.CreatedAt, Redact = ["data.secret"],
            };
            var request = new NotificationRequest
            {
                TenantId = tenantId, ApplicationId = app.Id, IdempotencyKey = Guid.NewGuid().ToString("N"), TypeId = type.Id, TypeKey = type.Key, TypeVersion = 1,
                SchemaHash = "h", Data = data, Recipients = "[]", AcceptedAt = at,
            };
            var notification = Seed.Notification(tenantId, request.Id);
            notification.State = finished ? NotificationState.Planned : NotificationState.Planning;
            notification.NextAt = finished ? null : at;
            var recipient = Seed.Recipient(tenantId, notification.Id);
            recipient.Outcome = RecipientOutcome.Planned;
            recipient.NextAt = null;
            var subscriber = new Subscriber { TenantId = tenantId, Source = "local", ExternalId = Guid.NewGuid().ToString("N"), CreatedAt = at };
            var integration = Seed.Integration(tenantId, "rt-" + Guid.NewGuid().ToString("N")[..6]);
            var delivery = new Delivery
            {
                TenantId = tenantId, Channel = Channels.Inbox, IntegrationId = integration.Id, DestinationKey = $"inbox:{subscriber.Id}", DestinationSnapshot = "{}",
                Locale = "en", RendererVersion = "test", Content = """{"Title":"Your token tok-123456"}""", CreatedAt = at, Status = DeliveryStatus.Delivered,
                Attempts = 1, CompletedAt = at,
            };
            var item = new InboxItem
            {
                TenantId = tenantId, SubscriberId = subscriber.Id, DeliveryId = delivery.Id, TypeKey = type.Key, GroupKey = "general",
                Title = "Your token tok-123456", Body = "Use tok-123456 to sign in; hello.", CreatedAt = at,
            };
            db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = tenantId, LinkedAt = at });
            db.AddRange(request, notification, recipient, subscriber, integration, delivery, item,
                new DeliveryItem { TenantId = tenantId, DeliveryId = delivery.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = delivery.DestinationKey, Channel = Channels.Inbox });
            await db.SaveChangesAsync(Ct);
            return (tenantId, request, delivery, item);
        }

        [Fact]
        public async Task A_finished_requests_redacted_paths_are_masked_in_its_data_and_inbox_items()
        {
            LocalStack.RequireAvailable();
            var done = await SeedAsync(TimeSpan.FromMinutes(5));
            var waiting = await SeedAsync(TimeSpan.FromMinutes(5), finished: false);

            await Worker.RedactAsync(Ct);

            await using var db = App.Database.Context();
            var redacted = await db.Requests.AsNoTracking().SingleAsync(r => r.Id == done.Request.Id, Ct);
            Assert.NotNull(redacted.RedactedAt);
            using (var data = JsonDocument.Parse(redacted.Data))
            {
                Assert.Equal(("***", "hello"), (data.RootElement.GetProperty("secret").GetString(), data.RootElement.GetProperty("note").GetString()));
            }
            var item = await db.InboxItems.AsNoTracking().SingleAsync(i => i.Id == done.Item.Id, Ct);
            Assert.Equal(("Your token ***", "Use *** to sign in; hello."), (item.Title, item.Body));
            var untouched = await db.Requests.AsNoTracking().SingleAsync(r => r.Id == waiting.Request.Id, Ct);
            Assert.Null(untouched.RedactedAt);
            Assert.Contains("tok-123456", untouched.Data);
        }

        [Fact]
        public async Task Old_deliveries_and_requests_go_and_old_content_is_cleared_by_the_tenants_retention()
        {
            LocalStack.RequireAvailable();
            var old = await SeedAsync(TimeSpan.FromDays(10));
            var recent = await SeedAsync(TimeSpan.FromHours(1), tenant: old.Tenant);
            var middle = await SeedAsync(TimeSpan.FromDays(3));
            await using (var db = App.Database.Context())
            {
                db.TenantSettings.Add(new TenantSettings { TenantId = old.Tenant, RequestRetentionDays = 7, ContentRetentionDays = 2 });
                db.TenantSettings.Add(new TenantSettings { TenantId = middle.Tenant, RequestRetentionDays = 30, ContentRetentionDays = 2 });
                await db.SaveChangesAsync(Ct);
            }

            await Worker.PurgeAsync(Ct);

            await using var check = App.Database.Context();
            Assert.False(await check.Requests.AnyAsync(r => r.Id == old.Request.Id, Ct));
            Assert.False(await check.Deliveries.AnyAsync(d => d.Id == old.Delivery.Id, Ct));
            Assert.False(await check.InboxItems.AnyAsync(i => i.Id == old.Item.Id, Ct));
            Assert.True(await check.Requests.AnyAsync(r => r.Id == recent.Request.Id, Ct));
            Assert.NotEqual("", (await check.Deliveries.AsNoTracking().SingleAsync(d => d.Id == recent.Delivery.Id, Ct)).Content);
            // Kept 30 days, but its content only 2: the delivery stays, without what it said.
            var kept = await check.Deliveries.AsNoTracking().SingleAsync(d => d.Id == middle.Delivery.Id, Ct);
            Assert.Equal("", kept.Content);
        }

        [Fact]
        public async Task Retention_is_a_tenant_setting_within_bounds()
        {
            LocalStack.RequireAvailable();
            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            var api = $"/api/v1/tenants/{ProducerApp.Tenant}/settings";
            var refused = await admin.PutAsJsonAsync(api, new TenantSettingsBody(null, 10, 30), Ct);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
            var saved = await (await admin.PutAsJsonAsync(api, new TenantSettingsBody(null, 30, 7), Ct)).Content.ReadFromJsonAsync<TenantSettingsResponse>(Ct);
            Assert.Equal((30, 7, 90, 14), (saved!.RequestRetentionDays, saved.ContentRetentionDays, saved.DefaultRequestRetentionDays, saved.DefaultContentRetentionDays));
            await admin.PutAsJsonAsync(api, new TenantSettingsBody(null), Ct);
        }
    }
}
