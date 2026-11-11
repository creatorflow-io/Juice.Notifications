using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class DeliveryWorker(NotificationStore store, NotificationSettings settings, IHttpClientFactory clients,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var next = await store.NextDelivery();
                if (next is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                var (notification, delivery) = next.Value;
                string? error = null;
                try
                {
                    if (delivery.Channel == "email")
                        await SendEmail(notification, delivery, stoppingToken);
                    else
                        await SendWebhook(notification, delivery, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    error = ex is HttpRequestException { StatusCode: not null } http
                        ? $"Webhook returned HTTP {(int)http.StatusCode.Value}." :
                        ex is HttpRequestException ? "Webhook request failed." :
                        ex is SmtpException ? "SMTP delivery failed." : "Delivery failed.";
                    logger.LogWarning("Delivery {DeliveryId} failed: {Error}", delivery.Id, error);
                }

                await store.RecordAttempt(notification.Id, delivery.Id, error, settings);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to process notification state");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task SendEmail(NotificationRecord notification, DeliveryRecord delivery, CancellationToken token)
    {
        using var message = new MailMessage(settings.Smtp.From, delivery.Destination, notification.Subject, notification.Body);
        using var smtp = new SmtpClient(settings.Smtp.Host, settings.Smtp.Port)
        {
            EnableSsl = settings.Smtp.EnableSsl,
            Timeout = 15000
        };
        if (!string.IsNullOrEmpty(settings.Smtp.Username))
            smtp.Credentials = new NetworkCredential(settings.Smtp.Username, settings.Smtp.Password);
        await smtp.SendMailAsync(message, token);
    }

    private async Task SendWebhook(NotificationRecord notification, DeliveryRecord delivery, CancellationToken token)
    {
        if (!settings.Tenants.TryGetValue(notification.TenantId, out var tenant) ||
            !tenant.Webhooks.TryGetValue(delivery.Destination, out var webhook) ||
            string.IsNullOrWhiteSpace(webhook.Secret) ||
            !Uri.TryCreate(webhook.Url, UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("Webhook configuration is invalid.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = notification.Id, tenantId = notification.TenantId, type = notification.Type,
            data = notification.Data, subject = notification.Subject, body = notification.Body, createdAt = notification.CreatedAt
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var signed = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(signed, 0);
        payload.CopyTo(signed, prefix.Length);
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(webhook.Secret), signed)).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-Notification-Timestamp", timestamp);
        request.Headers.Add("X-Notification-Signature", "sha256=" + signature);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new("application/json");
        using var response = await clients.CreateClient("webhooks").SendAsync(request, token);
        response.EnsureSuccessStatusCode();
    }
}
