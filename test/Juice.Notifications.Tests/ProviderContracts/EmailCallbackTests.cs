using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Providers.Email;
using Microsoft.Extensions.Logging.Abstractions;

namespace Juice.Notifications.Tests.ProviderContracts
{
    /// <summary>
    /// SNS as SES's events arrive (phase-4-plan p4-d6, M26), signed with a test certificate the fake serves at an SNS URL. <see cref="Message"/>
    /// builds a signed message; tests change fields after signing to forge one.
    /// </summary>
    public sealed class SnsSigner : IDisposable
    {
        public const string Topic = "arn:aws:sns:eu-west-1:123456789012:ses-events";
        public const string CertificateUrl = "https://sns.eu-west-1.amazonaws.com/SimpleNotificationService-test.pem";

        private readonly RSA _key = RSA.Create(2048);

        public string CertificatePem { get; }

        public SnsSigner()
        {
            var request = new CertificateRequest("CN=sns.amazonaws.com", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            CertificatePem = certificate.ExportCertificatePem();
        }

        public Dictionary<string, string?> Message(string type, string message, string version = "2", string topic = Topic, string? subscribeUrl = null)
        {
            var m = new Dictionary<string, string?>
            {
                ["Type"] = type, ["MessageId"] = Guid.NewGuid().ToString(), ["TopicArn"] = topic, ["Message"] = message,
                ["Timestamp"] = "2026-11-18T09:30:00.000Z", ["SignatureVersion"] = version, ["SigningCertURL"] = CertificateUrl,
            };
            if (type != "Notification")
            {
                m["Token"] = "token-1";
                m["SubscribeURL"] = subscribeUrl ?? "https://sns.eu-west-1.amazonaws.com/?Action=ConfirmSubscription&TopicArn=" + topic + "&Token=token-1";
            }
            Sign(m);
            return m;
        }

        public void Sign(Dictionary<string, string?> m)
        {
            var toSign = SesCallbackHandler.StringToSign(m["Type"]!, new SnsMessageFields(m["Type"], m["MessageId"], m.GetValueOrDefault("Token"), m["TopicArn"],
                m.GetValueOrDefault("Subject"), m["Message"], m.GetValueOrDefault("SubscribeURL"), m["Timestamp"]))!;
            var hash = m["SignatureVersion"] == "1" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256;
            m["Signature"] = Convert.ToBase64String(_key.SignData(Encoding.UTF8.GetBytes(toSign), hash, RSASignaturePadding.Pkcs1));
        }

        public void Dispose() => _key.Dispose();
    }

    public sealed class SesCallbackTests : IDisposable
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly SnsSigner _sns = new();
        private readonly FakeHttpApi _api = new();
        private readonly SesCallbackHandler _handler;

        public SesCallbackTests()
        {
            var http = new EmailHttp(_api);
            _api.Default = (request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri == SnsSigner.CertificateUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_sns.CertificatePem) }
                : new HttpResponseMessage(HttpStatusCode.OK));
            _handler = new SesCallbackHandler(http, new SnsCertificates(http, EmailTestSetup.Options), EmailTestSetup.Options, NullLogger<SesCallbackHandler>.Instance);
        }

        private static IntegrationContext Integration(string? topic = SnsSigner.Topic)
            => new(Guid.CreateVersion7(), "ses", "ses", JsonSerializer.Serialize(new { region = "eu-west-1", from = "a@example.com", topicArn = topic }), "AK:SK");

        private Task<CallbackResult> PostAsync(Dictionary<string, string?> message, IntegrationContext? integration = null)
            => _handler.ReadAsync(new CallbackRequest(new Dictionary<string, string>(), JsonSerializer.SerializeToUtf8Bytes(message),
                new Uri("https://notifications.example.com/api/v1/callbacks/ses/x")), integration ?? Integration(), Ct);

        private static string SesEvent(string type, Guid? deliveryId, object detail, string detailName)
            => JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["eventType"] = type,
                ["mail"] = new Dictionary<string, object>
                {
                    ["timestamp"] = "2026-11-18T09:29:00.000Z", ["messageId"] = "ses-message-1",
                    ["tags"] = deliveryId is null ? new Dictionary<string, string[]>() : new Dictionary<string, string[]> { ["delivery_id"] = [deliveryId.ToString()!] },
                },
                [detailName] = detail,
            });

        [Fact]
        public async Task A_permanent_bounce_is_a_hard_bounce_for_the_tagged_delivery_with_no_address_kept()
        {
            var id = Guid.CreateVersion7();
            var message = _sns.Message("Notification", SesEvent("Bounce", id, new
            {
                bounceType = "Permanent", bounceSubType = "General", timestamp = "2026-11-18T09:29:30.000Z",
                bouncedRecipients = new[] { new { emailAddress = "pat@example.com", diagnosticCode = "smtp; 550 5.1.1 <pat@example.com>: user unknown" } },
            }, "bounce"));

            var result = await PostAsync(message);

            Assert.True(result.Verified, result.Refusal);
            var e = Assert.Single(result.Events);
            Assert.Equal((message["MessageId"], CallbackKind.Bounced, (Guid?)id, "ses-message-1"), (e.EventId, e.Kind, e.DeliveryId, e.ProviderMessageId));
            Assert.Equal(new DateTimeOffset(2026, 11, 18, 9, 29, 30, TimeSpan.Zero), e.At);
            Assert.Contains("5.1.1", e.Detail);
            Assert.DoesNotContain("pat@example.com", e.Detail);
        }

        [Fact]
        public async Task Transient_bounces_complaints_and_receipts_read_as_their_kinds()
        {
            var soft = await PostAsync(_sns.Message("Notification", SesEvent("Bounce", null, new { bounceType = "Transient", bounceSubType = "MailboxFull" }, "bounce")));
            var complaint = await PostAsync(_sns.Message("Notification", SesEvent("Complaint", null, new { complaintFeedbackType = "abuse" }, "complaint"), version: "1"));
            var receipt = await PostAsync(_sns.Message("Notification", SesEvent("Delivery", null, new { timestamp = "2026-11-18T09:29:10.000Z" }, "delivery")));
            var open = await PostAsync(_sns.Message("Notification", SesEvent("Open", null, new { }, "open")));

            Assert.Equal(CallbackKind.SoftBounced, Assert.Single(soft.Events).Kind);
            Assert.Equal((CallbackKind.Complained, "abuse"), (Assert.Single(complaint.Events).Kind, complaint.Events[0].Detail));
            Assert.Equal((CallbackKind.Delivered, (Guid?)null, "ses-message-1"), (Assert.Single(receipt.Events).Kind, receipt.Events[0].DeliveryId, receipt.Events[0].ProviderMessageId));
            Assert.True(open.Verified);
            Assert.Empty(open.Events);
        }

        /// <summary>
        /// SNS signs whatever anyone's topic publishes, so a message from another topic is refused even when its signature holds; so is a forged
        /// field, a certificate off SNS's host, and an integration that names no topic.
        /// </summary>
        [Fact]
        public async Task Another_topic_a_forged_message_a_foreign_certificate_or_no_topic_is_refused()
        {
            var bounce = SesEvent("Bounce", Guid.CreateVersion7(), new { bounceType = "Permanent" }, "bounce");
            var otherTopic = _sns.Message("Notification", bounce, topic: "arn:aws:sns:eu-west-1:999999999999:someone-elses");
            var forged = _sns.Message("Notification", bounce);
            forged["Message"] = bounce.Replace("Permanent", "Transient");
            var foreign = _sns.Message("Notification", bounce);
            foreign["SigningCertURL"] = "https://sns.eu-west-1.amazonaws.com.evil.example/cert.pem";
            var http = _sns.Message("Notification", bounce);
            http["SigningCertURL"] = "http://sns.eu-west-1.amazonaws.com/SimpleNotificationService-test.pem";

            Assert.False((await PostAsync(otherTopic)).Verified);
            Assert.False((await PostAsync(forged)).Verified);
            Assert.False((await PostAsync(foreign)).Verified);
            Assert.False((await PostAsync(http)).Verified);
            Assert.False((await PostAsync(_sns.Message("Notification", bounce), Integration(topic: null))).Verified);
            Assert.True((await PostAsync(_sns.Message("Notification", bounce))).Verified);
            Assert.DoesNotContain(_api.Requests, r => r.Uri.Host.Contains("evil", StringComparison.Ordinal) || r.Uri.Scheme == "http");
        }

        [Fact]
        public async Task A_subscription_confirmation_is_confirmed_on_SNS_and_only_there()
        {
            var confirmation = _sns.Message("SubscriptionConfirmation", "You have chosen to subscribe to the topic.");
            var elsewhere = _sns.Message("SubscriptionConfirmation", "You have chosen to subscribe to the topic.", subscribeUrl: "https://evil.example/?Token=1");

            var confirmed = await PostAsync(confirmation);
            var refused = await PostAsync(elsewhere);

            Assert.True(confirmed.Verified, confirmed.Refusal);
            Assert.Empty(confirmed.Events);
            Assert.Contains(_api.Requests, r => r.Uri.AbsoluteUri == confirmation["SubscribeURL"]);
            Assert.False(refused.Verified);
            Assert.DoesNotContain(_api.Requests, r => r.Uri.Host == "evil.example");
        }

        [Fact]
        public async Task The_certificate_is_fetched_once()
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.True((await PostAsync(_sns.Message("Notification", "{}"))).Verified);
            }
            Assert.Single(_api.Requests, r => r.Uri.AbsoluteUri == SnsSigner.CertificateUrl);
        }

        public void Dispose() => _sns.Dispose();
    }

    public sealed class SendGridCallbackTests : IDisposable
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly SendGridCallbackHandler _handler = new();

        private IntegrationContext Integration(bool withKey = true)
            => new(Guid.CreateVersion7(), "sg", "sendgrid", JsonSerializer.Serialize(new
            {
                from = "a@example.com", eventWebhookKey = withKey ? Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()) : null,
            }), "SG.key");

        private Task<CallbackResult> PostAsync(object events, Func<byte[], byte[]>? tamper = null, IntegrationContext? integration = null, bool sign = true)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(events);
            const string timestamp = "1763458200";
            var signature = Convert.ToBase64String(_key.SignData(Encoding.UTF8.GetBytes(timestamp).Concat(body).ToArray(), HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence));
            var headers = sign
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [SendGridCallbackHandler.SignatureHeader] = signature, [SendGridCallbackHandler.TimestampHeader] = timestamp,
                }
                : new Dictionary<string, string>();
            return _handler.ReadAsync(new CallbackRequest(headers, tamper?.Invoke(body) ?? body, new Uri("https://n.example/api/v1/callbacks/sendgrid/x")),
                integration ?? Integration(), Ct);
        }

        [Fact]
        public async Task Each_event_of_a_batch_reads_as_its_kind_for_its_delivery()
        {
            var id = Guid.CreateVersion7();
            object Event(string name, string eventId, object? extra = null)
            {
                var e = new Dictionary<string, object?>
                {
                    ["email"] = "pat@example.com", ["timestamp"] = 1763458100, ["event"] = name, ["sg_event_id"] = eventId,
                    ["sg_message_id"] = "sg-message-1.filterdrecv-1-2", ["delivery_id"] = id.ToString(),
                };
                foreach (var p in extra?.GetType().GetProperties() ?? [])
                {
                    e[p.Name] = p.GetValue(extra);
                }
                return e;
            }
            var result = await PostAsync(new[]
            {
                Event("processed", "e0"),
                Event("delivered", "e1"),
                Event("bounce", "e2", new { type = "bounce", reason = "550 5.1.1 The email account pat@example.com does not exist" }),
                Event("bounce", "e3", new { type = "blocked", reason = "421 try later" }),
                Event("dropped", "e4", new { reason = "Bounced Address" }),
                Event("spamreport", "e5"),
                Event("group_unsubscribe", "e6"),
                Event("open", "e7"),
            });

            Assert.True(result.Verified, result.Refusal);
            Assert.Equal([("e1", CallbackKind.Delivered), ("e2", CallbackKind.Bounced), ("e3", CallbackKind.SoftBounced), ("e4", CallbackKind.Bounced),
                ("e5", CallbackKind.Complained), ("e6", CallbackKind.Complained)], result.Events.Select(e => (e.EventId, e.Kind)));
            Assert.All(result.Events, e => Assert.Equal((id, "sg-message-1"), (e.DeliveryId!.Value, e.ProviderMessageId)));
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1763458100), result.Events[0].At);
            Assert.Contains("5.1.1", result.Events[1].Detail);
            Assert.DoesNotContain("pat@example.com", result.Events[1].Detail);
        }

        [Fact]
        public async Task A_tampered_or_unsigned_batch_or_one_without_a_key_is_refused()
        {
            var events = new[] { new { @event = "bounce", sg_event_id = "e1", timestamp = 1 } };
            Assert.True((await PostAsync(events)).Verified);
            Assert.False((await PostAsync(events, body => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("e1", "e2")))).Verified);
            Assert.False((await PostAsync(events, sign: false)).Verified);
            Assert.False((await PostAsync(events, integration: Integration(withKey: false))).Verified);
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var stranger = Integration() with
            {
                Settings = JsonSerializer.Serialize(new { from = "a@example.com", eventWebhookKey = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()) }),
            };
            Assert.False((await PostAsync(events, integration: stranger)).Verified);
        }

        public void Dispose() => _key.Dispose();
    }
}
