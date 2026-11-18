using Juice.Notifications.Correlations;
using Juice.Notifications.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Closing a correlation cancels what is still due for it (phase-3-plan I32), but a digest delivery carrying other requests too (a route
    /// digest, a storm's) goes out while any of them is open or has no correlation: only what is wholly closed is cancelled (phases 3–4 review).
    /// </summary>
    public sealed class CorrelationDigestTests : IAsyncLifetime
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = Guid.CreateVersion7();
        private readonly ScratchDatabase _database = new();

        public async ValueTask InitializeAsync() => await _database.InitializeAsync();

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        [Fact]
        public async Task Resolving_one_correlation_cancels_only_deliveries_carrying_nothing_else_still_open()
        {
            LocalStack.RequireAvailable();
            await using var db = await _database.MigratedAsync();
            var app = Seed.Application("cd");
            var type = Seed.Type(app.Id, "cd.alert");
            var integration = Seed.Integration(Tenant);
            Correlation Correlation(string key) => new() { TenantId = Tenant, ApplicationId = app.Id, Key = key, State = CorrelationState.Open, CreatedAt = Seed.Now, LastRequestAt = Seed.Now };
            var (a, b) = (Correlation("a"), Correlation("b"));
            db.AddRange(app, type, integration, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now }, a, b);
            NotificationRequest Request(Correlation? c) => new()
            {
                TenantId = Tenant, ApplicationId = app.Id, IdempotencyKey = Guid.NewGuid().ToString("N"), TypeId = type.Id, TypeKey = type.Key, TypeVersion = 1,
                SchemaHash = "h", Data = "{}", Recipients = "[]", AcceptedAt = Seed.Now, CorrelationId = c?.Id, CorrelationKey = c?.Key,
            };
            Delivery Pending() => new()
            {
                TenantId = Tenant, Channel = Channels.Webhook, IntegrationId = integration.Id, DestinationKey = "it:" + Guid.NewGuid(), DestinationSnapshot = "{}",
                Locale = "en", RendererVersion = "test", Content = """{"ContentType":"text/plain","Body":"x"}""", CreatedAt = Seed.Now,
                Status = DeliveryStatus.Pending, NextAt = Seed.Now.AddYears(5),
            };
            var (onlyA, aAndB, aAndNone) = (Pending(), Pending(), Pending());
            void Carry(Delivery delivery, NotificationRequest request)
            {
                var notification = Seed.Notification(Tenant, request.Id);
                notification.State = NotificationState.Planned;
                notification.NextAt = null;
                var recipient = Seed.Recipient(Tenant, notification.Id);
                recipient.Outcome = RecipientOutcome.Planned;
                recipient.NextAt = null;
                db.AddRange(request, notification, recipient, new DeliveryItem
                {
                    TenantId = Tenant, DeliveryId = delivery.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = delivery.DestinationKey, Channel = Channels.Webhook,
                });
            }
            db.AddRange(onlyA, aAndB, aAndNone);
            Carry(onlyA, Request(a));
            Carry(aAndB, Request(a));
            Carry(aAndB, Request(b));
            Carry(aAndNone, Request(a));
            Carry(aAndNone, Request(null));
            await db.SaveChangesAsync(Ct);

            await db.Correlations.Where(c => c.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.State, CorrelationState.Resolved), Ct);
            var (_, _, cancelled) = await CorrelationLifecycle.StopAsync(db, Tenant, a.Id, DateTimeOffset.UtcNow, Ct);

            var statuses = await db.Deliveries.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Status, Ct);
            Assert.Equal(1, cancelled);
            Assert.Equal((DeliveryStatus.Cancelled, DeliveryStatus.Pending, DeliveryStatus.Pending), (statuses[onlyA.Id], statuses[aAndB.Id], statuses[aAndNone.Id]));
        }
    }
}
