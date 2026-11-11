using System.Text.Json;

public sealed class NotificationSettings
{
    public string StateFile { get; set; } = "data/notifications.json";
    public int MaxAttempts { get; set; } = 5;
    public int RetryDelaySeconds { get; set; } = 30;
    public Dictionary<string, TenantSettings> Tenants { get; set; } = new();
    public SmtpSettings Smtp { get; set; } = new();
}

public sealed class TenantSettings
{
    public string ApiKey { get; set; } = "";
    public List<string> Emails { get; set; } = new();
    public Dictionary<string, WebhookSettings> Webhooks { get; set; } = new();
}

public sealed class WebhookSettings
{
    public string Url { get; set; } = "";
    public string Secret { get; set; } = "";
}

public sealed class SmtpSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool EnableSsl { get; set; }
    public string From { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class NotificationType
{
    public Dictionary<string, string> Fields { get; set; } = new();
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
}

public sealed class NotificationRequest
{
    public string Type { get; set; } = "";
    public Dictionary<string, JsonElement> Data { get; set; } = new();
}

public sealed class NotificationRecord
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Type { get; set; } = "";
    public Dictionary<string, JsonElement> Data { get; set; } = new();
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<DeliveryRecord> Deliveries { get; set; } = new();
}

public sealed class DeliveryRecord
{
    public Guid Id { get; set; }
    public string Channel { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTimeOffset NextAttemptAt { get; set; }
    public List<DeliveryAttempt> Attempts { get; set; } = new();
}

public sealed class DeliveryAttempt
{
    public DateTimeOffset At { get; set; }
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
}

public sealed class StoreState
{
    public Dictionary<string, Dictionary<string, NotificationType>> Types { get; set; } = new();
    public List<NotificationRecord> Notifications { get; set; } = new();
}
