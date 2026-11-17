using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// A provider's callbacks as a test provider package would read them: signed with HMAC-SHA256 over the body with the integration's secret
    /// (header <c>x-test-signature</c>, hex), the events as JSON. Integrations of kind <c>cb</c> send through it.
    /// </summary>
    public sealed class TestCallbackHandler : ICallbackHandler
    {
        public string Provider => "test";

        public bool Handles(string integrationKind) => integrationKind == "cb";

        public static string Sign(string secret, byte[] body) => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

        public Task<CallbackResult> ReadAsync(CallbackRequest request, IntegrationContext integration, CancellationToken ct)
        {
            if (integration.Secret is null || request.Header("x-test-signature") is not { } signature
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(Sign(integration.Secret, request.Body))))
            {
                return Task.FromResult(CallbackResult.Refused("bad signature"));
            }
            using var json = JsonDocument.Parse(request.Body);
            var events = json.RootElement.GetProperty("events").EnumerateArray().Select(e => new CallbackEvent(e.GetProperty("id").GetString()!,
                Enum.Parse<CallbackKind>(e.GetProperty("kind").GetString()!), DateTimeOffset.UtcNow)
            {
                DeliveryId = e.TryGetProperty("delivery", out var d) ? d.GetGuid() : null,
                ProviderMessageId = e.TryGetProperty("message", out var m) ? m.GetString() : null,
                Detail = e.TryGetProperty("detail", out var x) ? x.GetString() : null,
            }).ToArray();
            return Task.FromResult(json.RootElement.TryGetProperty("challenge", out var challenge)
                ? new CallbackResult(null, events, new CallbackReply(200, "text/plain", challenge.GetString()!))
                : new CallbackResult(null, events));
        }
    }

    public sealed class CallbackApp : IAsyncLifetime
    {
        public const int PerMinute = 30;

        public NotificationsApp App { get; } = new(host =>
        {
            host.UseSetting("Callbacks:PerMinute", PerMinute.ToString());
            host.ConfigureTestServices(services => services.AddSingleton<ICallbackHandler, TestCallbackHandler>()
                .AddSingleton<INotificationProvider>(new ScriptedProvider(Channels.Email, "cb")));
        });

        public ValueTask InitializeAsync() => App.InitializeAsync();

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>
    /// Provider callbacks (phase-4-plan I40, p4-d6, M25) through the real host: <c>POST /api/v1/callbacks/{provider}/{integrationId}</c>, anonymous,
    /// verified by the integration's provider handler, each event applied once.
    /// </summary>
    public sealed class CallbackTests(CallbackApp fixture) : IClassFixture<CallbackApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = Guid.CreateVersion7();
        private NotificationsApp App => fixture.App;

        /// <summary>An integration of kind <c>cb</c> (the test handler's), its secret the tests' signing secret unless another reference is given.</summary>
        private async Task<Integration> IntegrationAsync(string? secretRef = TestHooks.SecretRef)
        {
            await using var db = App.Database.Context();
            var integration = new Integration
            {
                TenantId = Tenant, Key = "cb-" + Guid.NewGuid().ToString("N")[..8], Channel = Channels.Email, Kind = "cb", Settings = "{}", SecretRef = secretRef,
                DefinitionHash = "d", UpdatedAt = Seed.Now,
            };
            db.Add(integration);
            await db.SaveChangesAsync(Ct);
            return integration;
        }

        /// <summary>A delivery through <paramref name="integration"/>, as the dispatcher left it, to an external address or to a person's contact point.</summary>
        private async Task<Delivery> DeliveryAsync(Integration integration, DeliveryStatus status = DeliveryStatus.Accepted, string? messageId = null,
            ContactPoint? point = null, string? lastError = null)
        {
            await using var db = App.Database.Context();
            var app = Seed.Application("cb" + Guid.NewGuid().ToString("N")[..8]);
            var type = Seed.Type(app.Id, app.Key + ".thing");
            var request = Seed.Request(Tenant, app.Id, type.Id, Guid.NewGuid().ToString("N"));
            var notification = Seed.Notification(Tenant, request.Id);
            notification.State = NotificationState.Planned;
            notification.NextAt = null;
            var recipient = Seed.Recipient(Tenant, notification.Id);
            var key = point is null ? "ext:email:a@example.com" : $"cp:{point.Id}";
            var delivery = new Delivery
            {
                TenantId = Tenant, Channel = Channels.Email, IntegrationId = integration.Id, DestinationKey = key,
                DestinationSnapshot = point is null ? """{"address":"a@example.com"}""" : $$"""{"contactPoint":"{{point.Id}}"}""", Locale = "en",
                RendererVersion = "test", Content = """{"subject":"s","text":"t"}""", CreatedAt = Seed.Now, Status = status, Attempts = 1,
                ProviderMessageId = messageId, LastError = lastError, FailureCategory = lastError is null ? null : FailureCategory.Provider,
                CompletedAt = status is DeliveryStatus.Accepted or DeliveryStatus.Failed ? Seed.Now : null,
            };
            db.AddRange(app, type, new ApplicationTenant { ApplicationId = app.Id, TenantId = Tenant, LinkedAt = Seed.Now }, request, notification, recipient, delivery,
                new DeliveryItem { TenantId = Tenant, DeliveryId = delivery.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = key, Channel = Channels.Email });
            await db.SaveChangesAsync(Ct);
            return delivery;
        }

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

        private async Task<HttpResponseMessage> PostAsync(Guid integrationId, object body, string? signWith = TestHooks.Secret, string provider = "test")
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/callbacks/{provider}/{integrationId}") { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/json");
            if (signWith is not null)
            {
                request.Headers.Add("x-test-signature", TestCallbackHandler.Sign(signWith, bytes));
            }
            using var client = App.Client(); // no token: callbacks are anonymous
            return await client.SendAsync(request, Ct);
        }

        private static object Events(params object[] events) => new { events };

        private async Task<Delivery> ReadAsync(Guid id)
        {
            await using var db = App.Database.Context();
            return await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        }

        /// <summary>
        /// A delivery receipt moves an accepted delivery to <c>Delivered</c>, found by our id or by the provider's message id. A repeated event (same id) is
        /// answered <c>200</c> and changes nothing; the callback isn't an audited change.
        /// </summary>
        [Fact]
        public async Task A_receipt_marks_the_delivery_delivered_once_and_a_repeat_is_ignored()
        {
            LocalStack.RequireAvailable();
            var integration = await IntegrationAsync();
            var byId = await DeliveryAsync(integration);
            var byMessage = await DeliveryAsync(integration, messageId: "msg-" + Guid.NewGuid());
            var receipts = Events(new { id = "e1-" + byId.Id, kind = "Delivered", delivery = byId.Id },
                new { id = "e2-" + byMessage.Id, kind = "Delivered", message = byMessage.ProviderMessageId });

            Assert.Equal(HttpStatusCode.OK, (await PostAsync(integration.Id, receipts)).StatusCode);
            var first = (await ReadAsync(byId.Id), await ReadAsync(byMessage.Id));
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(integration.Id, receipts)).StatusCode);

            Assert.Equal((DeliveryStatus.Delivered, DeliveryStatus.Delivered), (first.Item1.Status, first.Item2.Status));
            Assert.Equal(first.Item1.Version, (await ReadAsync(byId.Id)).Version);
            await using var db = App.Database.Context();
            var recorded = await db.ProviderEvents.AsNoTracking().Where(e => e.IntegrationId == integration.Id).ToListAsync(Ct);
            Assert.Equal(2, recorded.Count);
            Assert.All(recorded, e => Assert.Equal(("test", "Delivered"), (e.Provider, e.Kind)));
            Assert.Equal([byId.Id, byMessage.Id], recorded.OrderBy(e => e.ProviderEventId).Select(e => e.DeliveryId!.Value));
            Assert.True(!App.Audit.All.Any(s => s.Access.Request?.Path.Contains("/callbacks/", StringComparison.Ordinal) == true), string.Join("; ", App.Audit.All.Select(s => $"{s.Access.Request?.Method} {s.Access.Request?.Path} {s.Access.Action} changes={s.Changes.Count}")));
        }

        /// <summary>
        /// A receipt for a delivery that failed with an unknown outcome, or is waiting to retry after one, shows it went out: <c>Delivered</c>, and a
        /// retrying one isn't sent again (no longer due). A receipt doesn't undo a bounce or touch a delivery that never went out.
        /// </summary>
        [Fact]
        public async Task A_receipt_settles_an_unknown_outcome_but_not_a_bounce_or_a_pending_delivery()
        {
            LocalStack.RequireAvailable();
            var integration = await IntegrationAsync();
            var unknown = await DeliveryAsync(integration, DeliveryStatus.Failed, lastError: "unknown_outcome: connection lost after DATA");
            var retrying = await DeliveryAsync(integration, DeliveryStatus.Retrying);
            var bounced = await DeliveryAsync(integration, DeliveryStatus.Bounced);
            var pending = await DeliveryAsync(integration, DeliveryStatus.Pending);

            Assert.Equal(HttpStatusCode.OK, (await PostAsync(integration.Id, Events(new[] { unknown, retrying, bounced, pending }
                .Select(d => (object)new { id = "r-" + d.Id, kind = "Delivered", delivery = d.Id }).ToArray()))).StatusCode);

            Assert.Equal((DeliveryStatus.Delivered, (string?)null), ((await ReadAsync(unknown.Id)).Status, (await ReadAsync(unknown.Id)).LastError));
            var r = await ReadAsync(retrying.Id);
            Assert.Equal((DeliveryStatus.Delivered, (DateTimeOffset?)null), (r.Status, r.NextAt));
            Assert.Equal(DeliveryStatus.Bounced, (await ReadAsync(bounced.Id)).Status);
            Assert.Equal(DeliveryStatus.Pending, (await ReadAsync(pending.Id)).Status);
        }

        /// <summary>
        /// A hard bounce bounces the delivery (<c>Destination</c>) and fails the person's contact point, so nothing more is planned to it; a soft bounce
        /// marks a working one bouncing and leaves the delivery; a complaint fails it. A failed point stays failed after a later soft bounce.
        /// </summary>
        [Fact]
        public async Task Bounces_and_complaints_mark_the_contact_point_and_a_hard_bounce_the_delivery()
        {
            LocalStack.RequireAvailable();
            var integration = await IntegrationAsync();
            var (soft, hard, complained) = (await ContactPointAsync(), await ContactPointAsync(), await ContactPointAsync());
            var toSoft = await DeliveryAsync(integration, point: soft);
            var toHard = await DeliveryAsync(integration, DeliveryStatus.Delivered, point: hard);
            var toComplained = await DeliveryAsync(integration, DeliveryStatus.Delivered, point: complained);

            Assert.Equal(HttpStatusCode.OK, (await PostAsync(integration.Id, Events(
                new { id = "s-" + toSoft.Id, kind = "SoftBounced", delivery = toSoft.Id, detail = "452 mailbox full" },
                new { id = "h-" + toHard.Id, kind = "Bounced", delivery = toHard.Id, detail = "550 5.1.1 no such user" },
                new { id = "h2-" + toHard.Id, kind = "SoftBounced", delivery = toHard.Id },
                new { id = "c-" + toComplained.Id, kind = "Complained", delivery = toComplained.Id }))).StatusCode);

            await using var db = App.Database.Context();
            var points = await db.ContactPoints.AsNoTracking().Where(c => c.TenantId == Tenant).ToDictionaryAsync(c => c.Id, Ct);
            Assert.Equal((ContactHealth.Bouncing, "452 mailbox full"), (points[soft.Id].Health, points[soft.Id].HealthReason));
            Assert.Equal((ContactHealth.Failed, "550 5.1.1 no such user"), (points[hard.Id].Health, points[hard.Id].HealthReason));
            Assert.Equal(ContactHealth.Failed, points[complained.Id].Health);
            Assert.StartsWith("The person complained", points[complained.Id].HealthReason);
            Assert.Equal(DeliveryStatus.Accepted, (await ReadAsync(toSoft.Id)).Status);
            var h = await ReadAsync(toHard.Id);
            Assert.Equal((DeliveryStatus.Bounced, FailureCategory.Destination), (h.Status, h.FailureCategory));
            Assert.Equal(DeliveryStatus.Delivered, (await ReadAsync(toComplained.Id)).Status);
        }

        /// <summary>
        /// What a caller learns without the secret: a bad or missing signature is <c>401</c> and nothing is recorded; an unknown integration, or a provider
        /// the integration doesn't use, is <c>404</c>. An integration whose secret isn't set answers <c>503</c>, so the provider tries again later.
        /// </summary>
        [Fact]
        public async Task An_unsigned_or_misdirected_callback_is_refused_and_changes_nothing()
        {
            LocalStack.RequireAvailable();
            var integration = await IntegrationAsync();
            var delivery = await DeliveryAsync(integration);
            var receipt = Events(new { id = "x-" + delivery.Id, kind = "Delivered", delivery = delivery.Id });

            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(integration.Id, receipt, signWith: "not-the-secret")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(integration.Id, receipt, signWith: null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(Guid.NewGuid(), receipt)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(integration.Id, receipt, provider: "ses")).StatusCode);
            var unset = await IntegrationAsync("env:NOTIFICATIONS_SECRET_NOT_SET_" + Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostAsync(unset.Id, receipt)).StatusCode);

            Assert.Equal(DeliveryStatus.Accepted, (await ReadAsync(delivery.Id)).Status);
            await using var db = App.Database.Context();
            Assert.False(await db.ProviderEvents.AnyAsync(e => e.IntegrationId == integration.Id, Ct));
        }

        /// <summary>A provider that wants an answer (a URL verification's challenge) gets it as the handler gave it.</summary>
        [Fact]
        public async Task A_handlers_reply_is_returned_to_the_provider()
        {
            LocalStack.RequireAvailable();
            var integration = await IntegrationAsync();

            var response = await PostAsync(integration.Id, new { events = Array.Empty<object>(), challenge = "abc123" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("abc123", await response.Content.ReadAsStringAsync(Ct));
        }

        /// <summary>More than <c>Callbacks:PerMinute</c> calls for one integration in a minute are <c>429</c>; another integration isn't affected.</summary>
        [Fact]
        public async Task Callbacks_past_the_limit_for_an_integration_are_answered_429()
        {
            LocalStack.RequireAvailable();
            var (busy, quiet) = (await IntegrationAsync(), await IntegrationAsync());
            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i <= CallbackApp.PerMinute; i++)
            {
                statuses.Add((await PostAsync(busy.Id, Events())).StatusCode);
            }

            Assert.All(statuses.Take(CallbackApp.PerMinute), s => Assert.Equal(HttpStatusCode.OK, s));
            Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(quiet.Id, Events())).StatusCode);
        }
    }
}
