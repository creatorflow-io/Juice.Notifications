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
    /// <item>connecting, authenticating, any 4xx ("not now", such as greylisting), or a connection lost before the body: retryable <c>Provider</c>;</item>
    /// <item>a 5xx for the recipient: <c>Destination</c>; for the sender: <c>Provider</c> (the integration's); for the message: <c>Content</c>;</item>
    /// <item><c>250</c> after <c>DATA</c>: <c>Accepted</c> (the server took it; delivery to the mailbox is its business), whatever QUIT does;</item>
    /// <item>the connection lost or timed out once the body started: <c>Unknown</c>, since the server may have queued it.</item>
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
            SmtpSettings? s;
            try
            {
                s = JsonSerializer.Deserialize<SmtpSettings>(settings, Json);
            }
            catch (JsonException ex)
            {
                return "settings has a field of the wrong type: " + ex.Message;
            }
            if (s?.Server is null || !options.Value.SmtpServers.ContainsKey(s.Server))
            {
                var known = string.Join(", ", options.Value.SmtpServers.Keys.Order());
                return $"settings.server names one of the deployment's SMTP servers: {(known.Length > 0 ? known : "none are configured")}.";
            }
            // The bare address, checked by building it exactly as the send does (new MailboxAddress(name, from)): TryParse accepts forms the
            // constructor refuses ("(c) a@example.com", "a@example.com>"), which would fail every send. No surrounding spaces or angle brackets,
            // and a domain ("a@b" quoted has none). A punycode domain builds fine (MimeKit decodes it).
            if (s.From is null || s.From != s.From.Trim() || s.From.Contains('<') || s.From.Contains('>') || !Builds(s.From))
            {
                return "settings.from is the sender's bare email address.";
            }
            return s.FromName is { Length: > 200 } ? "settings.fromName is up to 200 characters." : null;
        }

        private static bool Builds(string from)
        {
            try
            {
                return !string.IsNullOrEmpty(new MailboxAddress("", from).Domain);
            }
            catch (Exception ex) when (ex is ParseException or ArgumentException)
            {
                return false;
            }
        }

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            SmtpServer? server;
            MimeMessage message;
            try
            {
                var settings = JsonSerializer.Deserialize<SmtpSettings>(integration.Settings, Json)!;
                if (settings.Server is null || !options.Value.SmtpServers.TryGetValue(settings.Server, out server))
                {
                    return SendResult.Permanent(FailureCategory.Provider, $"The SMTP server '{settings.Server}' isn't configured in this deployment.");
                }
                if (settings.From is null)
                {
                    return SendResult.Permanent(FailureCategory.Provider, "The integration has no sender (settings.from).");
                }
                if (!MailboxAddress.TryParse(delivery.Destination, out var to))
                {
                    return SendResult.Permanent(FailureCategory.Destination, "The destination isn't an email address.");
                }
                var content = JsonSerializer.Deserialize<EmailContent>(delivery.Content)!;
                message = new MimeMessage();
                message.From.Add(new MailboxAddress(settings.FromName ?? "", settings.From));
                message.To.Add(to);
                message.Subject = content.Subject;
                // The delivery id, the same on every attempt: a receiving system can tell a resend from a new message.
                message.MessageId = $"{delivery.DeliveryId}@juice-notifications";
                message.Body = new BodyBuilder { TextBody = content.Text, HtmlBody = content.Html }.ToMessageBody();
            }
            catch (Exception ex) when (ex is JsonException or ParseException or ArgumentException or FormatException)
            {
                // Nothing was sent: the integration's settings (a sender saved before it was checked as now) or the stored content are wrong.
                return SendResult.Permanent(FailureCategory.Provider, "The email couldn't be built from the integration's settings: " + ex.Message);
            }

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
            string reply;
            try
            {
                reply = await client.SendAsync(message, timeout.Token, progress);
            }
            catch (SmtpCommandException ex) when ((int)ex.StatusCode is >= 400 and < 500)
            {
                // Any 4xx (greylisting, a busy mailbox, a full queue) is the server saying "not now": it took nothing.
                return SendResult.Retry(FailureCategory.Provider, $"The server deferred the send: {ex.Message}", (int)ex.StatusCode);
            }
            catch (SmtpCommandException ex)
            {
                return ex.ErrorCode switch
                {
                    SmtpErrorCode.RecipientNotAccepted
                        => SendResult.Permanent(FailureCategory.Destination, $"The server refused the recipient: {ex.Message}", (int)ex.StatusCode),
                    // The sender is the integration's: another integration might be accepted.
                    SmtpErrorCode.SenderNotAccepted
                        => SendResult.Permanent(FailureCategory.Provider, $"The server refused the sender: {ex.Message}", (int)ex.StatusCode),
                    // MessageNotAccepted after the body, or UnexpectedStatusCode for a refused DATA: never this message.
                    _ => SendResult.Permanent(FailureCategory.Content, $"The server refused the message: {ex.Message}", (int)ex.StatusCode),
                };
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Lost, timed out or broken (IOException, SocketException, TimeoutException, a protocol error…): unknown once any of the body went
                // out, since the server may have queued it; before that, nothing was sent.
                return progress.Started
                    ? SendResult.Unknown("The connection was lost while sending the message: " + ex.Message)
                    : SendResult.Retry(FailureCategory.Provider, "The connection was lost before the message was sent: " + ex.Message);
            }
            // The server took it. Saying goodbye is a courtesy: a server that never answers QUIT doesn't make the send unknown.
            try
            {
                using var quit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.DisconnectAsync(true, quit.Token);
            }
            catch (Exception)
            {
                // Ignored: the 250 is the outcome.
            }
            return SendResult.Accepted(reply);
        }

        /// <summary>
        /// Whether the whole message body went out: only then can the server have queued it (it needs the final <c>.</c>), so a lost connection
        /// after that leaves the outcome unknown, and before it a retry is safe. MailKit reports the total; without one, any byte counts (unknown,
        /// the safe side).
        /// </summary>
        private sealed class BodyProgress : ITransferProgress
        {
            public bool Started { get; private set; }

            public void Report(long bytesTransferred, long totalSize) => Started |= totalSize > 0 ? bytesTransferred >= totalSize : bytesTransferred > 0;

            public void Report(long bytesTransferred) => Started |= bytesTransferred > 0;
        }
    }
}
