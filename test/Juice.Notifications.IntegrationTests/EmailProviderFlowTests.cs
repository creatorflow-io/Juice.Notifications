using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Data;
using Juice.Notifications.Providers.Email;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// SES's and SendGrid's APIs and SNS, as the email providers reach them through <see cref="EmailHttp"/>: SES's <c>SendEmail</c> answers with a
    /// message id, SendGrid's <c>mail/send</c> with <c>202</c> and <c>X-Message-Id</c>, SNS serves the signing certificate. Every request is kept.
    /// </summary>
    public sealed class FakeEmailApis : HttpMessageHandler
    {
        public const string Topic = "arn:aws:sns:eu-west-1:123456789012:ses-events";
        public const string CertificateUrl = "https://sns.eu-west-1.amazonaws.com/SimpleNotificationService-test.pem";

        private readonly RSA _sns = RSA.Create(2048);
        private readonly string _pem;

        public ConcurrentQueue<(Uri Uri, string Body)> Requests { get; } = new();

        public ECDsa SendGridKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public FakeEmailApis()
        {
            using var certificate = new CertificateRequest("CN=sns.amazonaws.com", _sns, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            _pem = certificate.ExportCertificatePem();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Enqueue((request.RequestUri!, body));
            var response = request.RequestUri!.Host switch
            {
                "email.eu-west-1.amazonaws.com" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { MessageId = "ses-" + Guid.NewGuid().ToString("N") })),
                },
                "api.sendgrid.com" => new HttpResponseMessage(HttpStatusCode.Accepted) { Headers = { { "X-Message-Id", "sg-" + Guid.NewGuid().ToString("N") } } },
                _ when request.RequestUri.AbsoluteUri == CertificateUrl => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_pem) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            response.RequestMessage = request;
            return response;
        }

        /// <summary>An SNS notification from the topic, carrying an SES event, signed as SNS signs (version 2).</summary>
        public byte[] SnsNotification(string sesEvent, string? messageId = null)
        {
            var m = new Dictionary<string, string?>
            {
                ["Type"] = "Notification", ["MessageId"] = messageId ?? Guid.NewGuid().ToString(), ["TopicArn"] = Topic, ["Message"] = sesEvent,
                ["Timestamp"] = "2026-11-18T09:30:00.000Z", ["SignatureVersion"] = "2", ["SigningCertURL"] = CertificateUrl,
            };
            var toSign = SesCallbackHandler.StringToSign("Notification", new SnsMessageFields("Notification", m["MessageId"], null, Topic, null, sesEvent, null, m["Timestamp"]))!;
            m["Signature"] = Convert.ToBase64String(_sns.SignData(Encoding.UTF8.GetBytes(toSign), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            return JsonSerializer.SerializeToUtf8Bytes(m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _sns.Dispose();
                SendGridKey.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public sealed class EmailProvidersApp : IAsyncLifetime
    {
        public FakeEmailApis Apis { get; } = new();

        public NotificationsApp App { get; }

        public EmailProvidersApp() => App = new(host => host.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton(new EmailHttp(Apis)))));

        public ValueTask InitializeAsync() => App.InitializeAsync();

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            Apis.Dispose();
        }
    }

    /// <summary>
    /// SES and SendGrid (phase-4-plan M26) in the real host, with their APIs faked: the dispatcher sends through them, and their signed callbacks
    /// (SNS for SES, the signed event webhook for SendGrid) mark deliveries and contact points.
    /// </summary>
    public sealed class EmailProviderFlowTests(EmailProvidersApp fixture) : IClassFixture<EmailProvidersApp>
    {
        private const string SesSecretVariable = "NOTIFICATIONS_SECRET_TEST_SES";
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = Guid.CreateVersion7();
        private NotificationsApp App => fixture.App;

        static EmailProviderFlowTests() => Environment.SetEnvironmentVariable(SesSecretVariable, "AKIDEXAMPLE:test-secret-key");

        private async Task<Integration> IntegrationAsync(string kind, object settings, string? secretRef)
        {
            await using var db = App.Database.Context();
            var integration = new Integration
            {
                TenantId = Tenant, Key = kind + "-" + Guid.NewGuid().ToString("N")[..8], Channel = Channels.Email, Kind = kind,
                Settings = JsonSerializer.Serialize(settings), SecretRef = secretRef, DefinitionHash = "d", UpdatedAt = Seed.Now,
            };
            db.Add(integration);
            await db.SaveChangesAsync(Ct);
            return integration;
        }

        private Task<Integration> SesAsync() => IntegrationAsync("ses", new { region = "eu-west-1", from = "alerts@example.com", topicArn = FakeEmailApis.Topic },
            "env:" + SesSecretVariable);

        private Task<Integration> SendGridAsync() => IntegrationAsync("sendgrid", new
        {
            from = "alerts@example.com", eventWebhookKey = Convert.ToBase64String(fixture.Apis.SendGridKey.ExportSubjectPublicKeyInfo()),
        }, TestHooks.SecretRef);

        private async Task<ContactPoint> ContactPointAsync()
        {
            var crypto = App.Services.GetRequiredService<People.ContactCrypto>();
            await using var db = App.Database.Context();
            var subscriber = new Subscriber { TenantId = Tenant, Source = "local", ExternalId = Guid.NewGuid().ToString("N"), CreatedAt = Seed.Now };
            const string address = "pat@example.com";
            var point = new ContactPoint
            {
                TenantId = Tenant, SubscriberId = subscriber.Id, Channel = Channels.Email, KeyId = crypto.KeyId, CreatedAt = Seed.Now,
                Address = crypto.Protect(address, People.ContactCrypto.ContactContext(Tenant, subscriber.Id, Channels.Email)),
                AddressHash = crypto.ContactHash(Tenant, Channels.Email, address), VerifiedAt = Seed.Now, VerifiedBy = "self",
                Masked = People.ContactCrypto.Mask(address),
            };
            db.AddRange(subscriber, point);
            await db.SaveChangesAsync(Ct);
            return point;
        }

        /// <summary>A delivery through <paramref name="integration"/>: pending and due now for the dispatcher, or as the dispatcher left it.</summary>
        private async Task<Delivery> DeliveryAsync(Integration integration, DeliveryStatus status, ContactPoint? point = null, string? messageId = null)
        {
            await using var db = App.Database.Context();
            var app = Seed.Application("em" + Guid.NewGuid().ToString("N")[..8]);
            var type = Seed.Type(app.Id, app.Key + ".thing");
            var request = Seed.Request(Tenant, app.Id, type.Id, Guid.NewGuid().ToString("N"));
            var notification = Seed.Notification(Tenant, request.Id);
            notification.State = NotificationState.Planned;
            notification.NextAt = null;
            var recipient = Seed.Recipient(Tenant, notification.Id);
            var key = point is null ? "ext:email:pat@example.com" : $"cp:{point.Id}";
            var delivery = new Delivery
            {
                TenantId = Tenant, Channel = Channels.Email, IntegrationId = integration.Id, DestinationKey = key,
                DestinationSnapshot = point is null ? """{"address":"pat@example.com"}""" : $$"""{"contactPoint":"{{point.Id}}"}""", Locale = "en",
                RendererVersion = "test", Content = """{"Subject":"Build failed","Text":"t","Html":"<p>h</p>"}""", CreatedAt = DateTimeOffset.UtcNow,
                Status = status, NextAt = status == DeliveryStatus.Pending ? DateTimeOffset.UtcNow : null, ProviderMessageId = messageId,
                Attempts = status == DeliveryStatus.Pending ? 0 : 1, CompletedAt = status == DeliveryStatus.Pending ? null : DateTimeOffset.UtcNow,
            };
            db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now }, request, notification, recipient, delivery,
                new DeliveryItem { TenantId = Tenant, DeliveryId = delivery.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = key, Channel = Channels.Email });
            await db.SaveChangesAsync(Ct);
            return delivery;
        }

        private async Task<Delivery> ReadAsync(Guid id)
        {
            await using var db = App.Database.Context();
            return await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        }

        private async Task<Delivery> UntilAsync(Guid id, Func<Delivery, bool> done)
        {
            for (var i = 0; i < 150; i++)
            {
                var row = await ReadAsync(id);
                if (done(row))
                {
                    return row;
                }
                await Task.Delay(100, Ct);
            }
            return await ReadAsync(id);
        }

        private async Task<HttpStatusCode> PostAsync(string provider, Guid integrationId, byte[] body, IReadOnlyDictionary<string, string>? headers = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/callbacks/{provider}/{integrationId}") { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("text/plain");
            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            {
                request.Headers.Add(name, value);
            }
            using var client = App.Client();
            return (await client.SendAsync(request, Ct)).StatusCode;
        }

        private static string SesEvent(string type, string messageId, Guid? deliveryId, string detailName, object detail)
            => JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["eventType"] = type,
                ["mail"] = new Dictionary<string, object>
                {
                    ["timestamp"] = "2026-11-18T09:29:00.000Z", ["messageId"] = messageId,
                    ["tags"] = deliveryId is null ? new Dictionary<string, string[]>() : new Dictionary<string, string[]> { ["delivery_id"] = [deliveryId.ToString()!] },
                },
                [detailName] = detail,
            });

        /// <summary>
        /// The dispatcher sends a delivery through SES (signed with the integration's key, tagged with the delivery id) and records SES's message id;
        /// SES's receipt, through SNS, then makes it <c>Delivered</c>, found by that message id.
        /// </summary>
        [Fact]
        public async Task A_delivery_goes_out_through_SES_and_its_receipt_through_SNS_marks_it_delivered()
        {
            LocalStack.RequireAvailable();
            var integration = await SesAsync();
            var delivery = await DeliveryAsync(integration, DeliveryStatus.Pending);

            var sent = await UntilAsync(delivery.Id, d => d.Status != DeliveryStatus.Pending && d.Status != DeliveryStatus.Sending);
            Assert.Equal(DeliveryStatus.Accepted, sent.Status);
            Assert.StartsWith("ses-", sent.ProviderMessageId);
            var request = Assert.Single(fixture.Apis.Requests, r => r.Uri.Host == "email.eu-west-1.amazonaws.com" && r.Body.Contains(delivery.Id.ToString()));
            Assert.Contains("\"ToAddresses\":[\"pat@example.com\"]", request.Body);

            var receipt = fixture.Apis.SnsNotification(SesEvent("Delivery", sent.ProviderMessageId!, null, "delivery", new { timestamp = "2026-11-18T09:29:05.000Z" }));
            Assert.Equal(HttpStatusCode.OK, await PostAsync("ses", integration.Id, receipt));

            Assert.Equal(DeliveryStatus.Delivered, (await ReadAsync(delivery.Id)).Status);
        }

        /// <summary>
        /// A permanent bounce through SNS bounces the delivery and fails the person's address, keeping SES's diagnostic without the address; SNS
        /// sending it again changes nothing. A message from another topic, or forged, is refused.
        /// </summary>
        [Fact]
        public async Task An_SES_bounce_fails_the_address_once_and_a_forged_one_is_refused()
        {
            LocalStack.RequireAvailable();
            var integration = await SesAsync();
            var point = await ContactPointAsync();
            var delivery = await DeliveryAsync(integration, DeliveryStatus.Accepted, point, "ses-message-" + Guid.NewGuid().ToString("N"));
            var bounce = SesEvent("Bounce", delivery.ProviderMessageId!, delivery.Id, "bounce", new
            {
                bounceType = "Permanent", bounceSubType = "General",
                bouncedRecipients = new[] { new { emailAddress = "pat@example.com", diagnosticCode = "smtp; 550 5.1.1 <pat@example.com>: user unknown" } },
            });
            var notification = fixture.Apis.SnsNotification(bounce);
            var forged = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(fixture.Apis.SnsNotification(bounce)).Replace("Permanent", "Transient"));

            Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("ses", integration.Id, forged));
            Assert.Equal(HttpStatusCode.OK, await PostAsync("ses", integration.Id, notification));
            var bounced = await ReadAsync(delivery.Id);
            Assert.Equal(HttpStatusCode.OK, await PostAsync("ses", integration.Id, notification));

            Assert.Equal((DeliveryStatus.Bounced, FailureCategory.Destination), (bounced.Status, bounced.FailureCategory));
            Assert.Equal(bounced.Version, (await ReadAsync(delivery.Id)).Version);
            await using var db = App.Database.Context();
            var marked = await db.ContactPoints.AsNoTracking().SingleAsync(c => c.Id == point.Id, Ct);
            Assert.Equal(ContactHealth.Failed, marked.Health);
            Assert.Contains("5.1.1", marked.HealthReason);
            Assert.DoesNotContain("pat@example.com", marked.HealthReason);
            Assert.Equal(1, await db.ProviderEvents.CountAsync(e => e.IntegrationId == integration.Id, Ct));
        }

        /// <summary>
        /// The dispatcher sends through SendGrid with the delivery id as a custom argument; SendGrid's signed batch (a receipt for it, a spam report for
        /// a person's earlier message) marks the delivery and the address. An unsigned batch is refused.
        /// </summary>
        [Fact]
        public async Task A_delivery_goes_out_through_SendGrid_and_its_signed_events_apply()
        {
            LocalStack.RequireAvailable();
            var integration = await SendGridAsync();
            var delivery = await DeliveryAsync(integration, DeliveryStatus.Pending);
            var point = await ContactPointAsync();
            var earlier = await DeliveryAsync(integration, DeliveryStatus.Delivered, point);

            var sent = await UntilAsync(delivery.Id, d => d.Status != DeliveryStatus.Pending && d.Status != DeliveryStatus.Sending);
            Assert.Equal(DeliveryStatus.Accepted, sent.Status);
            Assert.Contains(fixture.Apis.Requests, r => r.Uri.Host == "api.sendgrid.com" && r.Body.Contains($"\"delivery_id\":\"{delivery.Id}\""));

            var body = JsonSerializer.SerializeToUtf8Bytes(new object[]
            {
                new { email = "pat@example.com", timestamp = 1763458100, @event = "delivered", sg_event_id = "d-" + delivery.Id, sg_message_id = sent.ProviderMessageId + ".filter1", delivery_id = delivery.Id.ToString() },
                new { email = "pat@example.com", timestamp = 1763458100, @event = "spamreport", sg_event_id = "s-" + earlier.Id, sg_message_id = "x.filter1", delivery_id = earlier.Id.ToString() },
            });
            const string timestamp = "1763458200";
            var signature = Convert.ToBase64String(fixture.Apis.SendGridKey.SignData(Encoding.UTF8.GetBytes(timestamp).Concat(body).ToArray(),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

            Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("sendgrid", integration.Id, body));
            Assert.Equal(HttpStatusCode.OK, await PostAsync("sendgrid", integration.Id, body, new Dictionary<string, string>
            {
                [SendGridCallbackHandler.SignatureHeader] = signature, [SendGridCallbackHandler.TimestampHeader] = timestamp,
            }));

            Assert.Equal(DeliveryStatus.Delivered, (await ReadAsync(delivery.Id)).Status);
            await using var db = App.Database.Context();
            var marked = await db.ContactPoints.AsNoTracking().SingleAsync(c => c.Id == point.Id, Ct);
            Assert.Equal(ContactHealth.Failed, marked.Health);
            Assert.StartsWith("The person complained", marked.HealthReason);
        }
    }
}
