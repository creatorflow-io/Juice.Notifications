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
            Guid? tenant = null, string data = """{"secret":"tok-123456","note":"hello"}""", DeliveryStatus status = DeliveryStatus.Delivered)
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
            // Unfinished: its workflow waiting (a delay), due in an hour, so no stage takes it during the test.
            notification.State = finished ? NotificationState.Planned : NotificationState.Running;
            notification.NextAt = finished ? null : DateTimeOffset.UtcNow.AddHours(1);
            var recipient = Seed.Recipient(tenantId, notification.Id);
            recipient.Outcome = RecipientOutcome.Planned;
            recipient.NextAt = null;
            var subscriber = new Subscriber { TenantId = tenantId, Source = "local", ExternalId = Guid.NewGuid().ToString("N"), CreatedAt = at };
            var integration = Seed.Integration(tenantId, "rt-" + Guid.NewGuid().ToString("N")[..6]);
            var delivery = new Delivery
            {
                TenantId = tenantId, Channel = Channels.Inbox, IntegrationId = integration.Id, DestinationKey = $"inbox:{subscriber.Id}", DestinationSnapshot = "{}",
                Locale = "en", RendererVersion = "test", Content = """{"Title":"Your token tok-123456"}""", CreatedAt = at, Status = status,
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

        /// <summary>
        /// A failed inbox delivery counts as settled, so its request is redacted; after that neither a retry nor a bulk replay puts it back
        /// (the item it would write would show the masked values), and the delivery stays failed (phases 3–4 review, round 3).
        /// </summary>
        [Fact]
        public async Task A_redacted_requests_failed_inbox_delivery_is_not_retried_or_replayed()
        {
            LocalStack.RequireAvailable();
            var failed = await SeedAsync(TimeSpan.FromMinutes(5), status: DeliveryStatus.Failed);
            await Worker.RedactAsync(Ct);

            await using (var scope = App.Services.CreateAsyncScope())
            {
                using var context = NotificationsMessageContext.Begin("test");
                var mediator = scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>();
                var retried = await mediator.Send(new History.RetryDelivery(failed.Tenant, failed.Delivery.Id), Ct);
                Assert.Equal((OutcomeKind.Conflict, ErrorCodes.NotRetryable), (retried.Kind, retried.Code));
                var replayed = await mediator.Send(new History.ReplayDeliveries(failed.Tenant, new History.ReplayFilter(null, null, null, null, null, null, null)), Ct);
                Assert.Equal((0, 1), (replayed.Value!.Replayed, replayed.Value.Skipped));
            }
            await using var db = App.Database.Context();
            Assert.NotNull((await db.Requests.AsNoTracking().SingleAsync(r => r.Id == failed.Request.Id, Ct)).RedactedAt);
            Assert.Equal(DeliveryStatus.Failed, (await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == failed.Delivery.Id, Ct)).Status);
        }

        /// <summary>
        /// A retry that puts a delivery back while redaction is deciding: the retry holds the request (shared) until it commits, and redaction,
        /// locking the request, looks at the deliveries again and finds it pending, so it leaves the request for when it settles.
        /// </summary>
        [Fact]
        public async Task Redaction_waits_for_a_delivery_put_back_while_it_was_deciding()
        {
            LocalStack.RequireAvailable();
            var failed = await SeedAsync(TimeSpan.FromMinutes(5), status: DeliveryStatus.Failed);
            await using (var retry = App.Database.Context())
            {
                await using var transaction = await retry.Database.BeginTransactionAsync(Ct);
                // As a retry: the delivery, then the request (shared), then the delivery back to pending, not yet committed.
                await History.DeliveryRules.LockAsync(retry, failed.Tenant, [failed.Delivery.Id], Ct);
                await retry.Deliveries.Where(d => d.TenantId == failed.Tenant && d.Id == failed.Delivery.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatus.Pending).SetProperty(d => d.NextAt, DateTimeOffset.UtcNow.AddHours(1)), Ct);
                var redacting = Worker.RedactAsync(Ct);
                await Task.Delay(500, Ct); // redaction reads the delivery still failed, then waits for the request
                Assert.False(redacting.IsCompleted);
                await transaction.CommitAsync(Ct);
                await redacting;
            }
            await using var db = App.Database.Context();
            Assert.Null((await db.Requests.AsNoTracking().SingleAsync(r => r.Id == failed.Request.Id, Ct)).RedactedAt);
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

        /// <summary>A would-send delivery (dry run, M44-2) is finished: it goes at the request retention (90 days by default), as a delivered one does.</summary>
        [Fact]
        public async Task An_old_would_send_delivery_is_removed_and_a_recent_one_kept()
        {
            LocalStack.RequireAvailable();
            var old = await SeedAsync(TimeSpan.FromDays(100), status: DeliveryStatus.WouldSend);
            var recent = await SeedAsync(TimeSpan.FromHours(1), status: DeliveryStatus.WouldSend);

            await Worker.PurgeAsync(Ct);

            await using var check = App.Database.Context();
            Assert.False(await check.Deliveries.AnyAsync(d => d.Id == old.Delivery.Id, Ct));
            Assert.False(await check.Requests.AnyAsync(r => r.Id == old.Request.Id, Ct));
            Assert.True(await check.Deliveries.AnyAsync(d => d.Id == recent.Delivery.Id, Ct));
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
