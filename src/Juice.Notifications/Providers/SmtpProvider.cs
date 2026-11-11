using System.Net.Sockets;
using System.Text.Json;
using Juice.Notifications.Rendering;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Juice.Notifications.Providers
{
    /// <summary>An email integration's settings: which deployment server (<see cref="DeliveryOptions.SmtpServers"/>), and the sender.</summary>
    public sealed record SmtpSettings(string? Server, string? From, string? FromName);

    /// <summary>
    /// Email over SMTP through MailKit (phase-1-plan I9), which tells the stages of a send apart, as the unknown-outcome rule needs (§10.3):
    /// <list type="bullet">
    /// <item>connecting, authenticating, or a refusal before the message body: retryable <c>Provider</c>;</item>
    /// <item>a refused recipient: <c>Destination</c>; a refused message: <c>Content</c>;</item>
    /// <item><c>250</c> after <c>DATA</c>: <c>Accepted</c> (the server took it; delivery to the mailbox is its business);</item>
    /// <item>the connection lost once the body started: <c>Unknown</c>, since the server may have queued it.</item>
    /// </list>
    /// </summary>
    public sealed class SmtpProvider(IOptions<DeliveryOptions> options) : INotificationProvider
    {
        public string Channel => Channels.Email;

        public string Kind => "smtp";

        public bool Idempotent => false;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public string? CheckSettings(string settings)
        {
            var s = JsonSerializer.Deserialize<SmtpSettings>(settings, Json);
            if (s?.Server is null || !options.Value.SmtpServers.ContainsKey(s.Server))
            {
                var known = string.Join(", ", options.Value.SmtpServers.Keys.Order());
                return $"settings.server names one of the deployment's SMTP servers: {(known.Length > 0 ? known : "none are configured")}.";
            }
            if (s.From is null || !MailboxAddress.TryParse(s.From, out var from) || from.Address != s.From.Trim())
            {
                return "settings.from is the sender's bare email address.";
            }
            return s.FromName is { Length: > 200 } ? "settings.fromName is up to 200 characters." : null;
        }

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            var settings = JsonSerializer.Deserialize<SmtpSettings>(integration.Settings, Json)!;
            if (settings.Server is null || !options.Value.SmtpServers.TryGetValue(settings.Server, out var server))
            {
                return SendResult.Permanent(FailureCategory.Provider, $"The SMTP server '{settings.Server}' isn't configured in this deployment.");
            }
            var content = JsonSerializer.Deserialize<EmailContent>(delivery.Content)!;
            var message = new MimeMessage();
            if (settings.From is null)
            {
                return SendResult.Permanent(FailureCategory.Provider, "The integration has no sender (settings.from).");
            }
            message.From.Add(new MailboxAddress(settings.FromName ?? "", settings.From));
            if (!MailboxAddress.TryParse(delivery.Destination, out var to))
            {
                return SendResult.Permanent(FailureCategory.Destination, "The destination isn't an email address.");
            }
            message.To.Add(to);
            message.Subject = content.Subject;
            // The delivery id, the same on every attempt: a receiving system can tell a resend from a new message.
            message.MessageId = $"{delivery.DeliveryId}@juice-notifications";
            message.Body = new BodyBuilder { TextBody = content.Text, HtmlBody = content.Html }.ToMessageBody();

            using var client = new SmtpClient { Timeout = (int)options.Value.RequestTimeout.TotalMilliseconds };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Value.ConnectTimeout + options.Value.RequestTimeout * 2);
            try
            {
                await client.ConnectAsync(server.Host, server.Port, server.Security switch
                {
                    SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                    SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
                    _ => SecureSocketOptions.None,
                }, timeout.Token);
                if (server.Username is { } user && server.PasswordRef is { } passwordRef)
                {
                    await client.AuthenticateAsync(user, SecretReferences.Resolve(passwordRef), timeout.Token);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return SendResult.Retry(FailureCategory.Provider, "Couldn't connect to the SMTP server: " + ex.Message);
            }
            var progress = new BodyProgress();
            try
            {
                var reply = await client.SendAsync(message, timeout.Token, progress);
                await client.DisconnectAsync(true, CancellationToken.None);
                return SendResult.Accepted(reply);
            }
            catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted)
            {
                return SendResult.Permanent(FailureCategory.Destination, $"The server refused the recipient: {ex.Message}", (int)ex.StatusCode);
            }
            catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.MessageNotAccepted)
            {
                // A 4xx refusal is the server saying "not now"; a 5xx, "never this message".
                return (int)ex.StatusCode is >= 400 and < 500
                    ? SendResult.Retry(FailureCategory.Provider, $"The server deferred the message: {ex.Message}", (int)ex.StatusCode)
                    : SendResult.Permanent(FailureCategory.Content, $"The server refused the message: {ex.Message}", (int)ex.StatusCode);
            }
            catch (SmtpCommandException ex)
            {
                return SendResult.Retry(FailureCategory.Provider, $"The server refused the send: {ex.Message}", (int)ex.StatusCode);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or SocketException or SmtpProtocolException or OperationCanceledException
                or ServiceNotConnectedException)
            {
                return progress.Started
                    ? SendResult.Unknown("The connection was lost while sending the message: " + ex.Message)
                    : SendResult.Retry(FailureCategory.Provider, "The connection was lost before the message was sent: " + ex.Message);
            }
        }

        /// <summary>Whether any of the message body went out: after that, a lost connection leaves the outcome unknown.</summary>
        private sealed class BodyProgress : ITransferProgress
        {
            public bool Started { get; private set; }

            public void Report(long bytesTransferred, long totalSize) => Started |= bytesTransferred > 0;

            public void Report(long bytesTransferred) => Started |= bytesTransferred > 0;
        }
    }
}
