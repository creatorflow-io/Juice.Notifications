using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Juice.Notifications.Callbacks;
using Juice.Notifications.Providers.Sms;
using Juice.Notifications.Rendering;

namespace Juice.Notifications.Tests.ProviderContracts
{
    /// <summary>Twilio's Messages API (phase-4-plan M29) through the contract kit: errors as <c>{"code","message","status"}</c>.</summary>
    public sealed class TwilioProviderTests : ProviderContract
    {
        // A made-up account sid, built here: a literal one looks like a real credential to secret scanners.
        internal static readonly string Account = "AC" + string.Concat(Enumerable.Repeat("0a", 16));
        internal static readonly string Token = "auth-token-0123";
        internal static readonly Microsoft.Extensions.Options.IOptions<CallbackOptions> Callbacks
            = Microsoft.Extensions.Options.Options.Create(new CallbackOptions { PublicUrl = new Uri("https://notifications.example.com/") });

        private readonly TwilioProvider _provider;

        public TwilioProviderTests() => _provider = new TwilioProvider(new SmsHttp(Api), EmailTestSetup.Options, Callbacks);

        protected override INotificationProvider Provider => _provider;

        protected override IntegrationContext Integration => new(Guid.CreateVersion7(), "sms", "twilio", """{"from":"+15005550006"}""", $"{Account}:{Token}");

        protected override RenderedDelivery Delivery(Guid id, int attempt)
            => new(id, Guid.CreateVersion7(), SmsChannel.Key, ChannelContent.Text("Build failed on main."), "+84901234567", attempt, NotificationPriority.Normal, false);

        private void Error(HttpStatusCode status, int code, string message)
            => Api.Then(status, JsonSerializer.Serialize(new { code, message, status = (int)status }));

        protected override bool Arrange(ContractCase @case)
        {
            switch (@case)
            {
                case ContractCase.Accepted: Api.Then(HttpStatusCode.Created, """{"sid":"SM0123","status":"queued"}"""); break;
                case ContractCase.ServerError: Error(HttpStatusCode.ServiceUnavailable, 20500, "Internal Server Error"); break;
                case ContractCase.RateLimited: Error(HttpStatusCode.TooManyRequests, 20429, "Too Many Requests"); break;
                case ContractCase.CredentialsRefused: Error(HttpStatusCode.Unauthorized, 20003, "Authenticate"); break;
                case ContractCase.AddressRefused: Error(HttpStatusCode.BadRequest, 21211, "The 'To' number +84901234567 is not a valid phone number."); break;
                case ContractCase.ContentRefused: Error(HttpStatusCode.BadRequest, 21617, "The concatenated message body exceeds the 1600 character limit."); break;
                case ContractCase.ConnectionRefused: Api.ThenRefused(); break;
                case ContractCase.NoAnswer: Api.ThenSilent(); break;
            }
            return true;
        }

        [Fact]
        public async Task It_posts_the_message_with_basic_auth_and_a_status_callback()
        {
            var integration = Integration;
            Arrange(ContractCase.Accepted);
            var result = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), integration, Ct);

            Assert.Equal((AttemptOutcome.Accepted, "SM0123"), (result.Outcome, result.ProviderMessageId));
            var sent = Assert.Single(Api.Requests);
            Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{Account}/Messages.json", sent.Uri.ToString());
            Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Account}:{Token}")), sent.Headers["Authorization"]);
            var form = HttpUtility.ParseQueryString(sent.Body);
            Assert.Equal(("+84901234567", "+15005550006", "Build failed on main."), (form["To"], form["From"], form["Body"]));
            Assert.Equal($"https://notifications.example.com/api/v1/callbacks/twilio/{integration.IntegrationId}", form["StatusCallback"]);
        }

        /// <summary>A number that replied STOP is gone (its contact point fails); a refused sender or region fails over; no number is kept in the error.</summary>
        [Fact]
        public async Task An_opted_out_number_is_gone_and_a_refused_sender_fails_over()
        {
            Error(HttpStatusCode.BadRequest, 21610, "Attempt to send to unsubscribed recipient +84901234567");
            var stopped = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);
            Error(HttpStatusCode.BadRequest, 21408, "Permission to send an SMS has not been enabled for the region indicated by the 'To' number");
            var region = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);

            Assert.True(stopped.AddressGone);
            Assert.Equal((AttemptOutcome.PermanentFailure, FailureCategory.Provider), (region.Outcome, region.Category));
        }

        [Theory]
        [InlineData("""{"from":"+15005550006"}""", null)]
        [InlineData("""{"messagingServiceSid":"MG0123456789abcdef0123456789abcdef"}""", null)]
        [InlineData("""{}""", "settings names the sender")]
        [InlineData("""{"from":"+15005550006","messagingServiceSid":"MG0123456789abcdef0123456789abcdef"}""", "settings names the sender")]
        [InlineData("""{"from":"5005550006"}""", "settings.from")]
        public void Settings_name_one_sender(string settings, string? error)
        {
            var refusal = Provider.CheckSettings(settings);
            if (error is null)
            {
                Assert.Null(refusal);
            }
            else
            {
                Assert.StartsWith(error, refusal);
            }
        }

        [Theory]
        [InlineData("+84 90 123-4567", "+84901234567")]
        [InlineData("(+1) 500.555.0006", "+15005550006")]
        [InlineData("0901234567", null)]
        [InlineData("+0901234567", null)]
        public void Targets_are_E164(string address, string? stored) => Assert.Equal(stored, _provider.NormalizeTarget(address));
    }

    /// <summary>Twilio's status callbacks (phase-4-plan M29): signed over the URL the provider gave and the sorted parameters.</summary>
    public sealed class TwilioCallbackTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly TwilioCallbackHandler _handler = new(TwilioProviderTests.Callbacks);
        private static readonly IntegrationContext Integration = new(Guid.CreateVersion7(), "sms", "twilio", "{}", $"{TwilioProviderTests.Account}:{TwilioProviderTests.Token}");

        private Task<CallbackResult> PostAsync(Dictionary<string, string> form, string? token = null, string? url = null)
        {
            var signedUrl = url ?? $"https://notifications.example.com/api/v1/callbacks/twilio/{Integration.IntegrationId}";
            var body = Encoding.UTF8.GetBytes(string.Join("&", form.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")));
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TwilioCallbackHandler.SignatureHeader] = TwilioCallbackHandler.Sign(token ?? TwilioProviderTests.Token, signedUrl, form),
            };
            // The host sees an internal address behind its proxy: the signature is checked against the public one.
            return _handler.ReadAsync(new CallbackRequest(headers, body, new Uri("http://app:8080/api/v1/callbacks/twilio/x")), Integration, Ct);
        }

        private static Dictionary<string, string> Status(string status, string? error = null, string? account = null)
        {
            var form = new Dictionary<string, string>
            {
                ["MessageSid"] = "SM0123", ["MessageStatus"] = status, ["AccountSid"] = account ?? TwilioProviderTests.Account, ["To"] = "+84901234567",
            };
            if (error is not null)
            {
                form["ErrorCode"] = error;
            }
            return form;
        }

        [Fact]
        public async Task Statuses_read_as_receipts_and_bounces_by_the_message_sid()
        {
            var delivered = await PostAsync(Status("delivered"));
            var unknown = await PostAsync(Status("undelivered", "30005"));
            var unreachable = await PostAsync(Status("undelivered", "30003"));
            var filtered = await PostAsync(Status("undelivered", "30007"));
            var sent = await PostAsync(Status("sent"));

            Assert.Equal(("SM0123:delivered", CallbackKind.Delivered, "SM0123"), (delivered.Events[0].EventId, delivered.Events[0].Kind, delivered.Events[0].ProviderMessageId));
            Assert.Equal(CallbackKind.Bounced, Assert.Single(unknown.Events).Kind);
            Assert.Equal(CallbackKind.SoftBounced, Assert.Single(unreachable.Events).Kind);
            Assert.Empty(filtered.Events);
            Assert.Empty(sent.Events);
        }

        [Fact]
        public async Task A_wrong_signature_another_URL_or_another_account_is_refused()
        {
            Assert.False((await PostAsync(Status("delivered"), token: "other")).Verified);
            Assert.False((await PostAsync(Status("delivered"), url: "https://evil.example/api/v1/callbacks/twilio/x")).Verified);
            Assert.False((await PostAsync(Status("delivered", account: "AC" + new string('f', 32)))).Verified);
            Assert.True((await PostAsync(Status("delivered"))).Verified);
        }
    }
}
