using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class NotificationStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path;
    private StoreState state;

    public NotificationStore(NotificationSettings settings, IWebHostEnvironment environment)
    {
        path = Path.GetFullPath(settings.StateFile, environment.ContentRootPath);
        state = File.Exists(path)
            ? JsonSerializer.Deserialize<StoreState>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid notification state.")
            : new StoreState();
    }

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value))!;

    private async Task Save(StoreState updated)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options))
                await JsonSerializer.SerializeAsync(file, updated);
            File.Move(temporary, path, true);
            state = updated;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task PutType(string tenant, string name, NotificationType definition)
    {
        await gate.WaitAsync();
        try
        {
            var updated = Copy(state);
            if (!updated.Types.TryGetValue(tenant, out var types))
                updated.Types[tenant] = types = new();
            types[name] = definition;
            await Save(updated);
        }
        finally { gate.Release(); }
    }

    public async Task<NotificationRecord?> CreateNotification(string tenantId, NotificationRequest request, TenantSettings tenant)
    {
        await gate.WaitAsync();
        try
        {
            if (!state.Types.TryGetValue(tenantId, out var types) || !types.TryGetValue(request.Type, out var type) ||
                request.Data.Count != type.Fields.Count || type.Fields.Any(field =>
                    !request.Data.TryGetValue(field.Key, out var value) || field.Value switch
                    {
                        "string" => value.ValueKind != JsonValueKind.String,
                        "number" => value.ValueKind != JsonValueKind.Number,
                        "boolean" => value.ValueKind is not (JsonValueKind.True or JsonValueKind.False),
                        _ => true
                    }))
                return null;

            string Render(string template) => Regex.Replace(template, @"\{\{([^{}]+)\}\}", match =>
            {
                var value = request.Data[match.Groups[1].Value];
                return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            });

            var now = DateTimeOffset.UtcNow;
            var notification = new NotificationRecord
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Type = request.Type, Data = request.Data,
                Subject = Render(type.Subject), Body = Render(type.Body), CreatedAt = now,
                Deliveries = tenant.Emails.Select(email => new DeliveryRecord
                {
                    Id = Guid.NewGuid(), Channel = "email", Destination = email, NextAttemptAt = now
                }).Concat(tenant.Webhooks.Keys.Select(name => new DeliveryRecord
                {
                    Id = Guid.NewGuid(), Channel = "webhook", Destination = name, NextAttemptAt = now
                })).ToList()
            };
            var updated = Copy(state);
            updated.Notifications.Add(notification);
            await Save(updated);
            return Copy(notification);
        }
        finally { gate.Release(); }
    }

    public async Task<NotificationRecord?> GetNotification(string tenant, Guid id)
    {
        await gate.WaitAsync();
        try { return Copy(state.Notifications.FirstOrDefault(n => n.TenantId == tenant && n.Id == id)); }
        finally { gate.Release(); }
    }

    public async Task<List<NotificationRecord>> GetHistory(string tenant)
    {
        await gate.WaitAsync();
        try { return Copy(state.Notifications.Where(n => n.TenantId == tenant).ToList()); }
        finally { gate.Release(); }
    }

    public async Task<(NotificationRecord Notification, DeliveryRecord Delivery)?> NextDelivery()
    {
        await gate.WaitAsync();
        try
        {
            foreach (var notification in state.Notifications)
            {
                var delivery = notification.Deliveries.FirstOrDefault(d => d.Status == "pending" && d.NextAttemptAt <= DateTimeOffset.UtcNow);
                if (delivery is not null) return (Copy(notification), Copy(delivery));
            }
            return null;
        }
        finally { gate.Release(); }
    }

    public async Task RecordAttempt(Guid notificationId, Guid deliveryId, string? error, NotificationSettings settings)
    {
        await gate.WaitAsync();
        try
        {
            var updated = Copy(state);
            var delivery = updated.Notifications.Single(n => n.Id == notificationId).Deliveries.Single(d => d.Id == deliveryId);
            var now = DateTimeOffset.UtcNow;
            delivery.Attempts.Add(new DeliveryAttempt { At = now, Succeeded = error is null, Error = error });
            if (error is null) delivery.Status = "delivered";
            else if (delivery.Attempts.Count >= Math.Max(1, settings.MaxAttempts)) delivery.Status = "failed";
            else
            {
                var delay = Math.Min(86400, Math.Max(1, settings.RetryDelaySeconds) * Math.Pow(2, Math.Min(16, delivery.Attempts.Count - 1)));
                delivery.NextAttemptAt = now.AddSeconds(delay);
            }
            await Save(updated);
        }
        finally { gate.Release(); }
    }
}
