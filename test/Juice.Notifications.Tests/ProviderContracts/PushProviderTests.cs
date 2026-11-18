using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Providers.Push;
using Juice.Notifications.Rendering;

namespace Juice.Notifications.Tests.ProviderContracts
{
    internal static class PushTestSetup
    {
        public static string Content() => ChannelContent.Chat("Title: Build failed\nmain is red", "x", new Dictionary<string, string> { ["self"] = "https://ci.example.com/b/1" }, null);

        public static RenderedDelivery Delivery(Guid id, int attempt, string destination)
            => new(id, Guid.CreateVersion7(), PushChannel.Key, Content(), destination, attempt, NotificationPriority.High, false);
    }

    /// <summary>FCM HTTP v1 (phase-4-plan M30) through the contract kit; Google's token endpoint answers apart (a route).</summary>
    public sealed class FcmProviderTests : ProviderContract
    {
        private static readonly string ServiceAccount;
        private readonly FcmProvider _provider;
        private int _tokens;

        static FcmProviderTests()
        {
            using var rsa = RSA.Create(2048);
            ServiceAccount = JsonSerializer.Serialize(new
            {
                type = "service_account", project_id = "juice-test", client_email = "push@juice-test.iam.gserviceaccount.com",
                private_key = rsa.ExportPkcs8PrivateKeyPem(), token_uri = "https://evil.example/token",
            });
        }

        public FcmProviderTests()
        {
            _provider = new FcmProvider(new PushHttp(Api), EmailTestSetup.Options, new FixedClock(DateTimeOffset.UtcNow));
            Api.Route(r => r.RequestUri!.AbsoluteUri == "https://oauth2.googleapis.com/token", (_, _) =>
            {
                Interlocked.Increment(ref _tokens);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"ya29.test","expires_in":3599,"token_type":"Bearer"}"""),
                });
            });
        }

        protected override INotificationProvider Provider => _provider;

        protected override IntegrationContext Integration => new(Guid.CreateVersion7(), "fcm", "fcm", "{}", ServiceAccount);

        protected override RenderedDelivery Delivery(Guid id, int attempt) => PushTestSetup.Delivery(id, attempt, "fcm-token-abc:APA91b");

        private void Error(HttpStatusCode status, string grpc, string? code, string message)
            => Api.Then(status, JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = (int)status, message, status = grpc,
                    details = code is null ? Array.Empty<object>() : [new Dictionary<string, string> { ["@type"] = "type.googleapis.com/google.firebase.fcm.v1.FcmError", ["errorCode"] = code }],
                },
            }));

        protected override bool Arrange(ContractCase @case)
        {
            switch (@case)
            {
                case ContractCase.Accepted: Api.Then(HttpStatusCode.OK, """{"name":"projects/juice-test/messages/0:1"}"""); break;
                case ContractCase.ServerError: Error(HttpStatusCode.ServiceUnavailable, "UNAVAILABLE", "UNAVAILABLE", "The service is unavailable."); break;
                case ContractCase.RateLimited: Error(HttpStatusCode.TooManyRequests, "RESOURCE_EXHAUSTED", "QUOTA_EXCEEDED", "Quota exceeded."); break;
                case ContractCase.CredentialsRefused: Error(HttpStatusCode.Forbidden, "PERMISSION_DENIED", "SENDER_ID_MISMATCH", "SenderId mismatch"); break;
                case ContractCase.AddressRefused: Error(HttpStatusCode.NotFound, "NOT_FOUND", "UNREGISTERED", "Requested entity was not found."); break;
                case ContractCase.ContentRefused: Error(HttpStatusCode.BadRequest, "INVALID_ARGUMENT", "INVALID_ARGUMENT", "Message is too big"); break;
                case ContractCase.ConnectionRefused: Api.ThenRefused(); break;
                case ContractCase.NoAnswer: Api.ThenSilent(); break;
            }
            return true;
        }

        /// <summary>
        /// The message to the token with the collapse key the delivery id, sent with an access token from Google's endpoint (never the key's own
        /// <c>token_uri</c>), asked for once and kept.
        /// </summary>
        [Fact]
        public async Task It_sends_with_a_cached_access_token_and_the_delivery_id_as_collapse_key()
        {
            var id = Guid.CreateVersion7();
            Arrange(ContractCase.Accepted);
            Arrange(ContractCase.Accepted);
            await Provider.SendAsync(Delivery(id, 1), Integration, Ct);
            await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);

            Assert.Equal(1, _tokens);
            var assertion = System.Web.HttpUtility.ParseQueryString(Api.Routed.Single().Body)["assertion"]!;
            using var claims = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Pad(assertion.Split('.')[1]))));
            Assert.Equal("https://oauth2.googleapis.com/token", claims.RootElement.GetProperty("aud").GetString());
            var sent = Api.Requests.First();
            Assert.Equal("https://fcm.googleapis.com/v1/projects/juice-test/messages:send", sent.Uri.ToString());
            Assert.Equal("Bearer ya29.test", sent.Headers["Authorization"]);
            using var body = JsonDocument.Parse(sent.Body);
            var message = body.RootElement.GetProperty("message");
            Assert.Equal("fcm-token-abc:APA91b", message.GetProperty("token").GetString());
            Assert.Equal(id.ToString(), message.GetProperty("android").GetProperty("collapse_key").GetString());
            Assert.Equal(("Build failed", "main is red"), (message.GetProperty("notification").GetProperty("title").GetString(),
                message.GetProperty("notification").GetProperty("body").GetString()));
        }

        private static string Pad(string s) => s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    /// <summary>APNs (phase-4-plan M30) through the contract kit: HTTP/2, a provider token, <c>{"reason"}</c> errors.</summary>
    public sealed class ApnsProviderTests : ProviderContract
    {
        private static readonly string Key;
        private const string Token = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
        private readonly ApnsProvider _provider;

        static ApnsProviderTests()
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Key = ec.ExportPkcs8PrivateKeyPem();
        }

        public ApnsProviderTests() => _provider = new ApnsProvider(new PushHttp(Api), EmailTestSetup.Options, new FixedClock(DateTimeOffset.UtcNow));

        protected override INotificationProvider Provider => _provider;

        protected override IntegrationContext Integration => new(Guid.CreateVersion7(), "apns", "apns",
            """{"teamId":"ABCDE12345","keyId":"KEY1234567","bundleId":"io.juice.app","environment":"sandbox"}""", Key);

        protected override RenderedDelivery Delivery(Guid id, int attempt) => PushTestSetup.Delivery(id, attempt, Token);

        private void Reason(HttpStatusCode status, string reason) => Api.Then(status, JsonSerializer.Serialize(new { reason }));

        protected override bool Arrange(ContractCase @case)
        {
            switch (@case)
            {
                case ContractCase.Accepted: Api.Then(HttpStatusCode.OK, "", "application/json", ("apns-id", "8B0A1C6E-0000-0000-0000-000000000000")); break;
                case ContractCase.ServerError: Reason(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable"); break;
                case ContractCase.RateLimited: Reason(HttpStatusCode.TooManyRequests, "TooManyRequests"); break;
                case ContractCase.CredentialsRefused: Reason(HttpStatusCode.Forbidden, "InvalidProviderToken"); break;
                case ContractCase.AddressRefused: Reason(HttpStatusCode.Gone, "Unregistered"); break;
                case ContractCase.ContentRefused: Reason(HttpStatusCode.RequestEntityTooLarge, "PayloadTooLarge"); break;
                case ContractCase.ConnectionRefused: Api.ThenRefused(); break;
                case ContractCase.NoAnswer: Api.ThenSilent(); break;
            }
            return true;
        }

        [Fact]
        public async Task It_posts_to_the_device_with_the_topic_a_provider_token_and_the_collapse_id()
        {
            var id = Guid.CreateVersion7();
            Arrange(ContractCase.Accepted);
            var result = await Provider.SendAsync(Delivery(id, 1), Integration, Ct);

            Assert.Equal(AttemptOutcome.Accepted, result.Outcome);
            var sent = Assert.Single(Api.Requests);
            Assert.Equal($"https://api.sandbox.push.apple.com/3/device/{Token}", sent.Uri.ToString());
            Assert.Equal(("io.juice.app", "alert", "10", id.ToString()), (sent.Headers["apns-topic"], sent.Headers["apns-push-type"], sent.Headers["apns-priority"],
                sent.Headers["apns-collapse-id"]));
            var jwt = sent.Headers["Authorization"]["bearer ".Length..];
            using var header = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(jwt.Split('.')[0].Replace('-', '+').Replace('_', '/').PadRight(((jwt.Split('.')[0].Length + 3) / 4) * 4, '='))));
            Assert.Equal(("ES256", "KEY1234567"), (header.RootElement.GetProperty("alg").GetString(), header.RootElement.GetProperty("kid").GetString()));
            using var body = JsonDocument.Parse(sent.Body);
            Assert.Equal("Build failed", body.RootElement.GetProperty("aps").GetProperty("alert").GetProperty("title").GetString());
        }

        [Fact]
        public async Task A_bad_or_foreign_token_is_gone()
        {
            Reason(HttpStatusCode.BadRequest, "DeviceTokenNotForTopic");
            Assert.True((await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct)).AddressGone);
            Assert.True((await Provider.SendAsync(PushTestSetup.Delivery(Guid.CreateVersion7(), 1, "not-hex"), Integration, Ct)).AddressGone);
        }
    }

    /// <summary>Web Push (phase-4-plan M30) through the contract kit, its encryption decrypted as a browser would, and its VAPID signature.</summary>
    public sealed class WebPushProviderTests : ProviderContract, IDisposable
    {
        private const string Endpoint = "https://fcm.googleapis.com/fcm/send/abc123";
        private readonly ECDiffieHellman _browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] _auth = RandomNumberGenerator.GetBytes(16);
        private readonly ECDsa _vapid = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly WebPushProvider _provider;

        public WebPushProviderTests() => _provider = new WebPushProvider(new PushHttp(Api), EmailTestSetup.Options, new FixedClock(DateTimeOffset.UtcNow));

        private static byte[] Point(ECParameters p) => [4, .. p.Q.X!, .. p.Q.Y!];

        private string VapidPublic => B64(Point(_vapid.ExportParameters(false)));

        private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        protected override INotificationProvider Provider => _provider;

        protected override IntegrationContext Integration => new(Guid.CreateVersion7(), "web", "webpush",
            JsonSerializer.Serialize(new { subject = "mailto:ops@juice.example", publicKey = VapidPublic }), B64(_vapid.ExportParameters(true).D!));

        private string Subscription(string endpoint = Endpoint)
            => JsonSerializer.Serialize(new { endpoint, p256dh = B64(Point(_browser.ExportParameters(false))), auth = B64(_auth) });

        protected override RenderedDelivery Delivery(Guid id, int attempt) => PushTestSetup.Delivery(id, attempt, Subscription());

        protected override bool Arrange(ContractCase @case)
        {
            switch (@case)
            {
                case ContractCase.Accepted: Api.Then(HttpStatusCode.Created, "", "text/plain", ("Location", "https://fcm.googleapis.com/fcm/send/abc123/msg1")); break;
                case ContractCase.ServerError: Api.Then(HttpStatusCode.BadGateway, "bad gateway", "text/plain"); break;
                case ContractCase.RateLimited: Api.Then(HttpStatusCode.TooManyRequests, "slow down", "text/plain"); break;
                case ContractCase.CredentialsRefused: Api.Then(HttpStatusCode.Forbidden, "the VAPID credentials don't match", "text/plain"); break;
                case ContractCase.AddressRefused: Api.Then(HttpStatusCode.Gone, "push subscription has unsubscribed or expired", "text/plain"); break;
                case ContractCase.ContentRefused: Api.Then(HttpStatusCode.RequestEntityTooLarge, "payload too large", "text/plain"); break;
                case ContractCase.ConnectionRefused: Api.ThenRefused(); break;
                case ContractCase.NoAnswer: Api.ThenSilent(); break;
            }
            return true;
        }

        /// <summary>The browser decrypts the body (RFC 8291) to the message, and the VAPID token verifies with the public key for the service's origin.</summary>
        [Fact]
        public async Task The_browser_decrypts_the_payload_and_the_VAPID_token_verifies()
        {
            var id = Guid.CreateVersion7();
            Api.Then(async (request, ct) =>
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
                using var payload = JsonDocument.Parse(Decrypt(bytes));
                Assert.Equal(("Build failed", id.ToString()), (payload.RootElement.GetProperty("title").GetString(), payload.RootElement.GetProperty("deliveryId").GetString()));
                Assert.Equal("aes128gcm", string.Join(",", request.Content.Headers.ContentEncoding));
                return new HttpResponseMessage(HttpStatusCode.Created);
            });
            var result = await Provider.SendAsync(Delivery(id, 1), Integration, Ct);

            Assert.Equal(AttemptOutcome.Accepted, result.Outcome);
            var sent = Assert.Single(Api.Requests);
            Assert.Equal((id.ToString("N"), "high"), (sent.Headers["Topic"], sent.Headers["Urgency"]));
            var vapid = sent.Headers["Authorization"];
            Assert.EndsWith($", k={VapidPublic}", vapid);
            var jwt = vapid["vapid t=".Length..vapid.IndexOf(',')];
            var parts = jwt.Split('.');
            Assert.True(_vapid.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromB64(parts[2]), HashAlgorithmName.SHA256));
            using var claims = JsonDocument.Parse(FromB64(parts[1]));
            Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        }

        /// <summary>The endpoint comes from a person's browser: an address that isn't a known push service is never called.</summary>
        [Theory]
        [InlineData("https://10.0.0.5/push")]
        [InlineData("https://fcm.googleapis.com.evil.example/x")]
        [InlineData("https://fcm.googleapis.com:8443/x")]
        public async Task Only_known_push_services_are_called(string endpoint)
        {
            var result = await Provider.SendAsync(PushTestSetup.Delivery(Guid.CreateVersion7(), 1, Subscription(endpoint)), Integration, Ct);
            Assert.Equal((AttemptOutcome.PermanentFailure, FailureCategory.Destination), (result.Outcome, result.Category));
            Assert.Empty(Api.Requests);
        }

        /// <summary>RFC 8291 decryption, as a browser does it: the receiving side of <see cref="WebPushProvider.Encrypt"/>, written from the RFC.</summary>
        private byte[] Decrypt(byte[] body)
        {
            var salt = body[..16];
            var recordSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16));
            Assert.Equal(4096u, recordSize);
            var keyLength = body[20];
            var serverPublic = body[21..(21 + keyLength)];
            var cipher = body[(21 + keyLength)..];
            using var server = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] },
            });
            var shared = _browser.DeriveRawSecretAgreement(server.PublicKey);
            var browserPublic = Point(_browser.ExportParameters(false));
            var prkKey = HMACSHA256.HashData(_auth, shared);
            var ikm = HMACSHA256.HashData(prkKey, (byte[])[.. Encoding.ASCII.GetBytes("WebPush: info\0"), .. browserPublic, .. serverPublic, 1]);
            var prk = HMACSHA256.HashData(salt, ikm);
            var cek = HMACSHA256.HashData(prk, (byte[])[.. Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"), 1])[..16];
            var nonce = HMACSHA256.HashData(prk, (byte[])[.. Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"), 1])[..12];
            var plain = new byte[cipher.Length - 16];
            using var aes = new AesGcm(cek, 16);
            aes.Decrypt(nonce, cipher[..^16], cipher[^16..], plain);
            Assert.Equal(2, plain[^1]);
            return plain[..^1];
        }

        private static byte[] FromB64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));

        public void Dispose()
        {
            _browser.Dispose();
            _vapid.Dispose();
        }
    }
}
