using Juice.Notifications.Data;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Rows for tests that need a whole chain: an application linked to a tenant, a type, an integration, and one notification's history.</summary>
    public static class Seed
    {
        public static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

        public static Application Application(string key) => new() { Key = key, Name = key, ClientId = key + "-client", CreatedAt = Now, UpdatedAt = Now };

        public static NotificationType Type(Guid applicationId, string key) => new()
        {
            ApplicationId = applicationId, Key = key, Version = 1, Name = key, Category = "test", DisplayGroup = DisplayGroups.General,
            Priority = NotificationPriority.Normal, Schema = "{}", SchemaHash = "h", Workflow = """[{"step":"send"}]""", DeclarationHash = "d", CreatedAt = Now,
        };

        public static Integration Integration(Guid tenantId, string key = "hook") => new()
        {
            TenantId = tenantId, Key = key, Channel = Channels.Webhook, Kind = "webhook", Settings = "{}", DefinitionHash = "d", UpdatedAt = Now,
        };

        public static NotificationRequest Request(Guid tenantId, Guid applicationId, Guid typeId, string idempotencyKey = "k1") => new()
        {
            TenantId = tenantId, ApplicationId = applicationId, IdempotencyKey = idempotencyKey, TypeId = typeId, TypeKey = "t", TypeVersion = 1,
            SchemaHash = "h", Data = "{}", Recipients = "[]", AcceptedAt = Now,
            Actor = "alice@example.com", // plain text, not JSON
        };

        public static Notification Notification(Guid tenantId, Guid requestId) => new()
        {
            TenantId = tenantId, RequestId = requestId, State = NotificationState.Planning, NextAt = Now, CreatedAt = Now, UpdatedAt = Now,
        };

        public static NotificationRecipient Recipient(Guid tenantId, Guid notificationId) => new()
        {
            TenantId = tenantId, NotificationId = notificationId, Kind = RecipientKind.External, Channel = Channels.Webhook,
            Address = "https://example.com/hook", Resolver = "address", NamedAs = "address:webhook:https://example.com/hook", CreatedAt = Now,
        };

        public static Delivery Delivery(Guid tenantId, Guid integrationId) => new()
        {
            TenantId = tenantId, Channel = Channels.Webhook, IntegrationId = integrationId, DestinationKey = "ext:webhook:https://example.com/hook",
            DestinationSnapshot = "https://example.com/hook", Locale = "en", RendererVersion = "test", Content = "{}", CreatedAt = Now,
        };

        public static DeliveryItem Item(Guid tenantId, Guid deliveryId, Guid recipientId) => new()
        {
            TenantId = tenantId, DeliveryId = deliveryId, RecipientId = recipientId, DestinationKey = "ext:webhook:https://example.com/hook", Channel = Channels.Webhook,
        };

        public static DeliveryAttempt Attempt(Guid tenantId, Guid deliveryId, Guid integrationId, int number = 1) => new()
        {
            TenantId = tenantId, DeliveryId = deliveryId, Number = number, IntegrationId = integrationId, At = Now, Outcome = AttemptOutcome.Delivered,
        };
    }
}
