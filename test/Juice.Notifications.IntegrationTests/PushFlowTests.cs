using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.People;
using Juice.Notifications.Planning;
using Juice.Notifications.Providers.Push;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>
    /// Google's token endpoint, FCM and a Web Push service as <see cref="PushHttp"/> reaches them. FCM answers <c>UNREGISTERED</c> for the token
    /// <see cref="GoneToken"/> and takes the rest; every send is kept with its URL.
    /// </summary>
    public sealed class FakePushServices : HttpMessageHandler
    {
        public const string GoneToken = "gone-token-0001";

        public ConcurrentQueue<(Uri Uri, string Body)> Sends { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.AbsoluteUri == "https://oauth2.googleapis.com/token")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"ya29.test","expires_in":3599}""") };
            }
            var body = request.Content is null ? "" : uri.Host == "fcm.googleapis.com" && uri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal)
                ? await request.Content.ReadAsStringAsync(ct) : $"{(await request.Content.ReadAsByteArrayAsync(ct)).Length} encrypted bytes";
            Sends.Enqueue((uri, body));
            if (body.Contains(GoneToken, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}"""),
                };
            }
            return uri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"name":"projects/juice-test/messages/1"}""") }
                : new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    public sealed class PushApp : IAsyncLifetime
    {
        public const string FcmVariable = "NOTIFICATIONS_SECRET_TEST_FCM";
        public const string VapidVariable = "NOTIFICATIONS_SECRET_TEST_VAPID";

        public FakePeopleResolver People { get; } = new();

        public FakePushServices Push { get; } = new();

        public NotificationsApp App { get; }

        public string VapidPublicKey { get; }

        public PushApp()
        {
            using var rsa = RSA.Create(2048);
            Environment.SetEnvironmentVariable(FcmVariable, JsonSerializer.Serialize(new
            {
                type = "service_account", project_id = "juice-test", client_email = "push@juice-test.iam.gserviceaccount.com", private_key = rsa.ExportPkcs8PrivateKeyPem(),
            }));
            using var vapid = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var p = vapid.ExportParameters(true);
            Environment.SetEnvironmentVariable(VapidVariable, B64(p.D!));
            VapidPublicKey = B64([4, .. p.Q.X!, .. p.Q.Y!]);
            App = new NotificationsApp(host => host.ConfigureTestServices(services => services.AddSingleton<IRecipientResolver>(People)
                .Replace(ServiceDescriptor.Singleton(new PushHttp(Push)))));
        }

        internal static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public async ValueTask InitializeAsync()
        {
            await App.InitializeAsync();
            if (!LocalStack.Available)
            {
                return;
            }
            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            (await admin.PostAsJsonAsync("/api/v1/applications", new CreateApplicationBody(ProducerApp.Key, "Sample", "sample-producer"))).EnsureSuccessStatusCode();
            (await admin.PutAsync($"/api/v1/applications/{ProducerApp.Key}/tenants/{ProducerApp.Tenant}", null)).EnsureSuccessStatusCode();
            var tenant = $"/api/v1/tenants/{ProducerApp.Tenant}";
            (await admin.PutAsJsonAsync($"{tenant}/resolvers/crews", new PutResolverBody("fake", ["user"], null, null, null))).EnsureSuccessStatusCode();
            (await admin.PutAsJsonAsync($"{tenant}/integrations/fcm-a", new PutIntegrationBody("push", "fcm", JsonElement.Parse("{}"), "env:" + FcmVariable, 0, true)))
                .EnsureSuccessStatusCode();
            // A second FCM integration: a gone token must never fail over to it.
            (await admin.PutAsJsonAsync($"{tenant}/integrations/fcm-b", new PutIntegrationBody("push", "fcm",
                JsonElement.Parse("""{"projectId":"juice-other"}"""), "env:" + FcmVariable, 1, true))).EnsureSuccessStatusCode();
            (await admin.PutAsJsonAsync($"{tenant}/integrations/web", new PutIntegrationBody("push", "webpush",
                JsonSerializer.SerializeToElement(new { subject = "mailto:ops@juice.example", publicKey = VapidPublicKey }), "env:" + VapidVariable, 0, true)))
                .EnsureSuccessStatusCode();
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>
    /// Push (design §9.2, phase-4-plan I42, M30) through the real host, the push services faked: a person's devices each get a delivery through the
    /// integration of their platform, and a gone token removes its registration (phase 4 proof point 3).
    /// </summary>
    public sealed class PushFlowTests(PushApp fixture) : IClassFixture<PushApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = ProducerApp.Tenant;
        private NotificationsApp App => fixture.App;

        /// <summary>
        /// Phase 4 proof point 3. A person with two Android devices and a browser: one push delivery per device, each through its platform's
        /// integration. FCM says one token is unregistered: that delivery fails (<c>Destination</c>) after one attempt, never retried and never
        /// through the second FCM integration, and the registration is deleted; the other devices are delivered and stay registered.
        /// </summary>
        [Fact]
        public async Task A_gone_push_token_deletes_its_registration_without_retry_or_failover()
        {
            LocalStack.RequireAvailable();
            var person = "pat-" + Guid.NewGuid().ToString("N")[..8];
            Guid subscriber;
            await using (var db = App.Database.Context())
            {
                var crypto = App.Services.GetRequiredService<ContactCrypto>();
                var map = await SubscriberDirectory.MapAsync(db, Tenant, [new ResolvedPerson(FakePeopleResolver.Source, person, "Pat")], DateTimeOffset.UtcNow,
                    new SourceContacts(crypto, App.Services.GetRequiredService<IChannelRegistry>(), NullLogger.Instance), Ct);
                subscriber = map[(FakePeopleResolver.Source, person)];
            }
            Guid good, gone, browser;
            await using (var scope = App.Services.CreateAsyncScope())
            {
                var devices = scope.ServiceProvider.GetRequiredService<DeviceRegistry>();
                using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                var p = key.ExportParameters(false);
                good = (await devices.RegisterAsync(Tenant, subscriber, "fcm", "io.juice.app", "good-token-0001", null, null, Ct)).Value!.Id;
                gone = (await devices.RegisterAsync(Tenant, subscriber, "fcm", "io.juice.app", FakePushServices.GoneToken, null, null, Ct)).Value!.Id;
                browser = (await devices.RegisterAsync(Tenant, subscriber, "webpush", "web", null, new WebPushSubscription("https://fcm.googleapis.com/fcm/send/b1",
                    PushApp.B64([4, .. p.Q.X!, .. p.Q.Y!]), PushApp.B64(RandomNumberGenerator.GetBytes(16))), null, Ct)).Value!.Id;
            }
            using var producer = App.Client(await LocalStack.ProducerTokenAsync());
            var type = $"{ProducerApp.Key}.p{Guid.NewGuid():N}"[..(ProducerApp.Key.Length + 14)];
            (await producer.PutAsJsonAsync($"/api/v1/applications/{ProducerApp.Key}/types", new DeclareTypeBody(type, "Build failed", "ci", null, "high", false, null,
                JsonElement.Parse("""{ "type": "object" }"""), null, null, null, JsonElement.Parse("""{ "push": "Title: Build failed\nmain is red" }""")), Ct)).EnsureSuccessStatusCode();
            var accepted = await (await producer.PostAsJsonAsync($"/api/v1/tenants/{Tenant}/notifications", new SendNotificationBody(type, null,
                Guid.NewGuid().ToString("N"), null, [new RecipientBody("user", Id: person, Resolver: "crews")], null, null, null, JsonElement.Parse("{}"), null, null), Ct))
                .Content.ReadFromJsonAsync<AcceptedResponse>(Ct);

            List<Delivery> deliveries = [];
            for (var i = 0; i < 200; i++)
            {
                await using var db = App.Database.Context();
                deliveries = await db.Deliveries.AsNoTracking().Where(d => d.Channel == PushChannel.Key
                    && db.DeliveryItems.Any(it => it.DeliveryId == d.Id && it.RequestId == accepted!.Id)).ToListAsync(Ct);
                if (deliveries.Count == 3 && deliveries.All(d => d.Status is not (DeliveryStatus.Pending or DeliveryStatus.Sending or DeliveryStatus.Retrying)))
                {
                    break;
                }
                await Task.Delay(100, Ct);
            }

            Assert.Equal(new[] { $"dev:{browser}", $"dev:{gone}", $"dev:{good}" }.Order(StringComparer.Ordinal), deliveries.Select(d => d.DestinationKey).Order(StringComparer.Ordinal));
            var failed = Assert.Single(deliveries, d => d.DestinationKey == $"dev:{gone}");
            Assert.Equal((DeliveryStatus.Failed, FailureCategory.Destination, 1, 0), (failed.Status, failed.FailureCategory, failed.Attempts, failed.Failovers));
            Assert.All(deliveries.Where(d => d != failed), d => Assert.Equal(DeliveryStatus.Accepted, d.Status));
            await using var check = App.Database.Context();
            var integrations = await check.Integrations.AsNoTracking().Where(i => i.TenantId == Tenant).ToDictionaryAsync(i => i.Id, i => i.Key, Ct);
            Assert.Equal("fcm-a", integrations[failed.IntegrationId]);
            Assert.Equal("web", integrations[deliveries.Single(d => d.DestinationKey == $"dev:{browser}").IntegrationId]);
            Assert.Single(fixture.Push.Sends, s => s.Body.Contains(FakePushServices.GoneToken, StringComparison.Ordinal));
            Assert.DoesNotContain(fixture.Push.Sends, s => s.Uri.AbsolutePath.Contains("juice-other", StringComparison.Ordinal));
            var registered = await check.Devices.AsNoTracking().Where(d => d.TenantId == Tenant && d.SubscriberId == subscriber).Select(d => d.Id).ToListAsync(Ct);
            Assert.Equal(new[] { browser, good }.Order(), registered.Order());
        }
    }
}
