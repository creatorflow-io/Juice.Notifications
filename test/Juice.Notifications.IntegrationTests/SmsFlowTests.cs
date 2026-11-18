using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Providers.Sms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Twilio's Messages API as <see cref="SmsHttp"/> reaches it: every message is queued with a new sid, and kept.</summary>
    public sealed class FakeTwilio : HttpMessageHandler
    {
        public ConcurrentQueue<(string Sid, string Body)> Messages { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var sid = "SM" + Guid.NewGuid().ToString("N");
            Messages.Enqueue((sid, body));
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new { sid, status = "queued" })),
            };
        }
    }

    public sealed class SmsApp : IAsyncLifetime
    {
        // A made-up account sid, built here: a literal one looks like a real credential to secret scanners.
        public static readonly string Account = "AC" + string.Concat(Enumerable.Repeat("0a", 16));
        public const string Token = "twilio-auth-token";
        public const string SecretVariable = "NOTIFICATIONS_SECRET_TEST_TWILIO";
        public const string PublicUrl = "https://notifications.example.com/";

        public FakeTwilio Twilio { get; } = new();

        public NotificationsApp App { get; }

        public SmsApp()
        {
            Environment.SetEnvironmentVariable(SecretVariable, $"{Account}:{Token}");
            App = new(host =>
            {
                host.UseSetting("Callbacks:PublicUrl", PublicUrl);
                host.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(new SmsHttp(Twilio))));
            });
        }

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
            (await admin.PutAsJsonAsync($"/api/v1/tenants/{ProducerApp.Tenant}/integrations/sms", new PutIntegrationBody("sms", "twilio",
                JsonElement.Parse("""{"from":"+15005550006"}"""), "env:" + SecretVariable, null, true))).EnsureSuccessStatusCode();
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>
    /// SMS (phase-4-plan M29) in the real host, Twilio faked: a person adds a phone number, the verification code reaches it by SMS through the
    /// pipeline (the service's type has an SMS template once the channel is registered), and Twilio's signed status callback marks it delivered.
    /// </summary>
    public sealed class SmsFlowTests(SmsApp fixture) : IClassFixture<SmsApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly string Api = $"/api/v1/tenants/{ProducerApp.Tenant}";
        private NotificationsApp App => fixture.App;

        [Fact]
        public async Task A_phone_number_is_verified_with_a_code_sent_by_SMS_and_its_receipt_marks_it_delivered()
        {
            LocalStack.RequireAvailable();
            using var ann = App.Client(await LocalStack.PersonTokenAsync());
            var number = "+8490" + Random.Shared.Next(1_000_000, 9_999_999);
            var typed = number[..3] + " " + number[3..6] + "-" + number[6..];

            var added = await ann.PostAsJsonAsync($"{Api}/me/contact-points", new AddContactPointBody("sms", typed), Ct);
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            var point = await added.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal(number, point.GetProperty("address").GetString());
            var id = point.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Accepted, (await ann.PostAsync($"{Api}/me/contact-points/{id}/verification", null, Ct)).StatusCode);

            (string Sid, string Body)? sms = null;
            for (var i = 0; i < 150 && sms is null; i++)
            {
                sms = fixture.Twilio.Messages.Cast<(string, string)?>().FirstOrDefault(m => HttpUtility.ParseQueryString(m!.Value.Item2)["To"] == number);
                if (sms is null)
                {
                    await Task.Delay(100, Ct);
                }
            }
            Assert.NotNull(sms);
            var form = HttpUtility.ParseQueryString(sms.Value.Body);
            var code = System.Text.RegularExpressions.Regex.Match(form["Body"]!, @"\b(\d{6})\b").Groups[1].Value;
            Assert.StartsWith("Your verification code is", form["Body"]);
            var verified = await ann.PostAsJsonAsync($"{Api}/me/contact-points/{id}/verify", new VerifyContactPointBody(code), Ct);
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

            // Twilio reports delivery to the StatusCallback it was given, signed over that URL.
            var statusUrl = form["StatusCallback"]!;
            var status = new Dictionary<string, string> { ["MessageSid"] = sms.Value.Sid, ["MessageStatus"] = "delivered", ["AccountSid"] = SmsApp.Account };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(statusUrl).PathAndQuery) { Content = new FormUrlEncodedContent(status) };
            request.Headers.Add(TwilioCallbackHandler.SignatureHeader, TwilioCallbackHandler.Sign(SmsApp.Token, statusUrl, status));
            using var anonymous = App.Client();
            Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(request, Ct)).StatusCode);

            await using var db = App.Database.Context();
            Assert.Equal(DeliveryStatus.Delivered, (await db.Deliveries.AsNoTracking().SingleAsync(d => d.ProviderMessageId == sms.Value.Sid, Ct)).Status);
        }

        /// <summary>A request may name a phone number on the sms channel; a malformed one is refused with its field.</summary>
        [Fact]
        public async Task A_contact_point_number_that_isnt_E164_is_refused()
        {
            LocalStack.RequireAvailable();
            using var ann = App.Client(await LocalStack.PersonTokenAsync());
            var added = await ann.PostAsJsonAsync($"{Api}/me/contact-points", new AddContactPointBody("sms", "0901234567"), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, added.StatusCode);
            Assert.Contains("phone", await added.Content.ReadAsStringAsync(Ct));
        }
    }
}
