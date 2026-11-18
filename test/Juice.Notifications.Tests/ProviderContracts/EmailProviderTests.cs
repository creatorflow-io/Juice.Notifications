using System.Net;
using System.Text.Json;
using Juice.Notifications.Providers;
using Juice.Notifications.Providers.Email;
using Microsoft.Extensions.Options;
 

namespace Juice.Notifications.Tests.ProviderContracts
{
    /// <summary>SigV4 against AWS's own worked example (IAM <c>ListUsers</c>, "Create a signed AWS API request").</summary>
    public sealed class AwsSigV4Tests
    {
        [Fact]
        public void Signs_as_AWS_documents()
        {
            var headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/x-www-form-urlencoded; charset=utf-8",
                ["host"] = "iam.amazonaws.com",
                ["x-amz-date"] = "20150830T123600Z",
            };
            var authorization = AwsSigV4.Authorization("GET", new Uri("https://iam.amazonaws.com/?Action=ListUsers&Version=2010-05-08"), headers, [],
                "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "us-east-1", "iam", new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero));
            Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/iam/aws4_request, SignedHeaders=content-type;host;x-amz-date, "
                + "Signature=5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7", authorization);
        }
    }

    internal static class EmailTestSetup
    {
        public static IOptions<DeliveryOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(new DeliveryOptions
        {
            RequestTimeout = TimeSpan.FromMilliseconds(200),
        });

        public static RenderedDelivery Email(Guid id, int attempt, string to = "pat@example.com", string content = """{"Subject":"Build failed","Text":"t","Html":"<p>h</p>"}""")
            => new(id, Guid.CreateVersion7(), Channels.Email, content, to, attempt, NotificationPriority.Normal, false);
    }

    /// <summary>SES (phase-4-plan M26) through the contract kit, and what it sends.</summary>
    public sealed class SesProviderTests : ProviderContract
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);
        private readonly SesProvider _provider;

        public SesProviderTests() => _provider = new SesProvider(new EmailHttp(Api), EmailTestSetup.Options, new FixedClock(Now));

        protected override INotificationProvider Provider => _provider;

        private const string Settings = """{"region":"eu-west-1","from":"alerts@example.com","fromName":"Juice Alerts","configurationSet":"juice-events"}""";

        protected override IntegrationContext Integration => new(Guid.CreateVersion7(), "ses", "ses", Settings, "AKIDEXAMPLE:secret-key");

        protected override RenderedDelivery Delivery(Guid id, int attempt) => EmailTestSetup.Email(id, attempt);

        private static (HttpStatusCode, string, string, (string, string)[]) Error(HttpStatusCode status, string type, string message)
            => (status, $$"""{"message":"{{message}}"}""", "application/json", [("x-amzn-ErrorType", type + ":http://internal.amazon.com/coral/com.amazonaws.sesv2/")]);

        private void Answer((HttpStatusCode Status, string Body, string Type, (string, string)[] Headers) a) => Api.Then(a.Status, a.Body, a.Type, a.Headers);

        protected override bool Arrange(ContractCase @case)
        {
            switch (@case)
            {
                case ContractCase.Accepted: Api.Then(HttpStatusCode.OK, """{"MessageId":"0102018f-ses-id"}"""); break;
                case ContractCase.ServerError: Answer(Error(HttpStatusCode.InternalServerError, "InternalFailure", "Internal failure")); break;
                case ContractCase.RateLimited: Answer(Error(HttpStatusCode.TooManyRequests, "TooManyRequestsException", "Maximum sending rate exceeded.")); break;
                case ContractCase.CredentialsRefused: Answer(Error(HttpStatusCode.Forbidden, "UnrecognizedClientException", "The security token included in the request is invalid.")); break;
                case ContractCase.AddressRefused: Answer(Error(HttpStatusCode.BadRequest, "BadRequestException", "Illegal address")); break;
                case ContractCase.ContentRefused: Answer(Error(HttpStatusCode.BadRequest, "MessageRejected", "Message length is more than 10485760 bytes long.")); break;
                case ContractCase.ConnectionRefused: Api.ThenRefused(); break;
                case ContractCase.NoAnswer: Api.ThenSilent(); break;
            }
            return true;
        }

        [Fact]
        public async Task It_sends_a_signed_SendEmail_tagged_with_the_delivery_id()
        {
            var id = Guid.CreateVersion7();
            Arrange(ContractCase.Accepted);
            var result = await Provider.SendAsync(Delivery(id, 1), Integration, Ct);

            Assert.Equal((AttemptOutcome.Accepted, "0102018f-ses-id"), (result.Outcome, result.ProviderMessageId));
            var sent = Assert.Single(Api.Requests);
            Assert.Equal("https://email.eu-west-1.amazonaws.com/v2/email/outbound-emails", sent.Uri.ToString());
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261118/eu-west-1/ses/aws4_request, SignedHeaders=content-type;host;x-amz-date, Signature=",
                sent.Headers["Authorization"]);
            Assert.Equal("20261118T093000Z", sent.Headers["x-amz-date"]);
            using var body = JsonDocument.Parse(sent.Body);
            var root = body.RootElement;
            Assert.Equal("\"Juice Alerts\" <alerts@example.com>", root.GetProperty("FromEmailAddress").GetString());
            Assert.Equal("pat@example.com", root.GetProperty("Destination").GetProperty("ToAddresses")[0].GetString());
            Assert.Equal("Build failed", root.GetProperty("Content").GetProperty("Simple").GetProperty("Subject").GetProperty("Data").GetString());
            Assert.Equal("<p>h</p>", root.GetProperty("Content").GetProperty("Simple").GetProperty("Body").GetProperty("Html").GetProperty("Data").GetString());
            Assert.Equal("juice-events", root.GetProperty("ConfigurationSetName").GetString());
            var tag = root.GetProperty("EmailTags")[0];
            Assert.Equal(("delivery_id", id.ToString()), (tag.GetProperty("Name").GetString(), tag.GetProperty("Value").GetString()));
        }

        [Fact]
        public void A_sender_name_outside_ASCII_is_encoded()
            => Assert.Equal("=?utf-8?B?" + Convert.ToBase64String("Cảnh báo"u8.ToArray()) + "?= <a@example.com>", SesProvider.From("a@example.com", "Cảnh báo"));

        /// <summary>The sandbox's refusal is the account's limit: another integration may send, so it fails over. Throttling as 400 still waits.</summary>
        [Fact]
        public async Task Sandbox_refusals_fail_over_and_throttling_in_a_400_waits()
        {
            Answer(Error(HttpStatusCode.BadRequest, "MessageRejected", "Email address is not verified. The following identities failed the check in region EU-WEST-1: pat@example.com"));
            var sandbox = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);
            Answer(Error(HttpStatusCode.BadRequest, "ThrottlingException", "Rate exceeded"));
            var throttled = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);
            Answer(Error(HttpStatusCode.BadRequest, "LimitExceededException", "Daily message quota exceeded."));
            var quota = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);

            Assert.Equal((AttemptOutcome.PermanentFailure, FailureCategory.Provider), (sandbox.Outcome, sandbox.Category));
            Assert.DoesNotContain("pat@example.com", sandbox.Error);
            Assert.Equal((AttemptOutcome.RetryableFailure, FailureCategory.RateLimited), (throttled.Outcome, throttled.Category));
            Assert.Equal((AttemptOutcome.RetryableFailure, FailureCategory.Provider), (quota.Outcome, quota.Category));
        }

        [Fact]
        public async Task A_malformed_secret_or_address_sends_nothing()
        {
            var badSecret = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration with { Secret = "no-colon" }, Ct);
            var badAddress = await Provider.SendAsync(EmailTestSetup.Email(Guid.CreateVersion7(), 1, "not an address"), Integration, Ct);

            Assert.Equal(FailureCategory.Provider, badSecret.Category);
            Assert.Equal(FailureCategory.Destination, badAddress.Category);
            Assert.Empty(Api.Requests);
        }

        [Theory]
        [InlineData("""{"region":"eu-west-1","from":"a@example.com"}""", null)]
        [InlineData("""{"region":"eu-west-1","from":"a@example.com","topicArn":"arn:aws:sns:eu-west-1:123456789012:ses-events"}""", null)]
        [InlineData("""{"region":"evil.example.com/","from":"a@example.com"}""", "settings.region")]
        [InlineData("""{"region":"eu-west-1","from":"Alerts <a@example.com>"}""", "settings.from")]
        [InlineData("""{"region":"eu-west-1","from":"a@example.com","topicArn":"arn:aws:sqs:eu-west-1:1:x"}""", "settings.topicArn")]
        [InlineData("""{"region":"eu-west-1","from":"a@example.com","configurationSet":"a b"}""", "settings.configurationSet")]
        [InlineData("""[]""", "settings is")]
        public void Settings_are_checked_when_saved(string settings, string? error)
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
    }

    /// <summary>SendGrid (phase-4-plan M26) through the contract kit, and what it sends.</summary>
    public sealed class SendGridProviderTests : ProviderContract
    {
        private readonly SendGridProvider _provider;

        public SendGridProviderTests() => _provider = new SendGridProvider(new EmailHttp(Api), EmailTestSetup.Options);

        protected override INotificationProvider Provider => _provider;

        protected override IntegrationContext Integration => new(Guid.CreateVersion7(), "sg", "sendgrid", """{"from":"alerts@example.com","fromName":"Alerts"}""", "SG.key");

        protected override RenderedDelivery Delivery(Guid id, int attempt) => EmailTestSetup.Email(id, attempt);

        private void Errors(HttpStatusCode status, string message, string? field = null)
            => Api.Then(status, JsonSerializer.Serialize(new { errors = new[] { new { message, field, help = (string?)null } } }));

        protected override bool Arrange(ContractCase @case)
        {
            switch (@case)
            {
                case ContractCase.Accepted: Api.Then(HttpStatusCode.Accepted, "", "application/json", ("X-Message-Id", "sg-message-1")); break;
                case ContractCase.ServerError: Api.Then(HttpStatusCode.ServiceUnavailable); break;
                case ContractCase.RateLimited: Errors(HttpStatusCode.TooManyRequests, "too many requests"); break;
                case ContractCase.CredentialsRefused: Errors(HttpStatusCode.Unauthorized, "The provided authorization grant is invalid, expired, or revoked"); break;
                case ContractCase.AddressRefused: Errors(HttpStatusCode.BadRequest, "Does not contain a valid address.", "personalizations.0.to.0.email"); break;
                case ContractCase.ContentRefused: Errors(HttpStatusCode.RequestEntityTooLarge, "The JSON payload you have included in your request is too large."); break;
                case ContractCase.ConnectionRefused: Api.ThenRefused(); break;
                case ContractCase.NoAnswer: Api.ThenSilent(); break;
            }
            return true;
        }

        [Fact]
        public async Task It_sends_the_delivery_id_as_a_custom_argument_with_the_key()
        {
            var id = Guid.CreateVersion7();
            Arrange(ContractCase.Accepted);
            var result = await Provider.SendAsync(Delivery(id, 1), Integration, Ct);

            Assert.Equal((AttemptOutcome.Accepted, "sg-message-1"), (result.Outcome, result.ProviderMessageId));
            var sent = Assert.Single(Api.Requests);
            Assert.Equal("https://api.sendgrid.com/v3/mail/send", sent.Uri.ToString());
            Assert.Equal("Bearer SG.key", sent.Headers["Authorization"]);
            using var body = JsonDocument.Parse(sent.Body);
            var root = body.RootElement;
            Assert.Equal(id.ToString(), root.GetProperty("custom_args").GetProperty("delivery_id").GetString());
            Assert.Equal("pat@example.com", root.GetProperty("personalizations")[0].GetProperty("to")[0].GetProperty("email").GetString());
            Assert.Equal(("alerts@example.com", "Alerts"), (root.GetProperty("from").GetProperty("email").GetString(), root.GetProperty("from").GetProperty("name").GetString()));
            Assert.Equal(["text/plain", "text/html"], root.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("type").GetString()));
        }

        [Fact]
        public async Task The_eu_region_sends_to_the_eu_host()
        {
            Arrange(ContractCase.Accepted);
            await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration with { Settings = """{"from":"a@example.com","region":"eu"}""" }, Ct);
            Assert.Equal("api.eu.sendgrid.com", Assert.Single(Api.Requests).Uri.Host);
        }

        /// <summary>An unverified sender (403, or a 400 on <c>from</c>) is the integration's fault: it fails over, and no address is blamed.</summary>
        [Fact]
        public async Task A_refused_sender_is_a_provider_failure()
        {
            Errors(HttpStatusCode.Forbidden, "The from address does not match a verified Sender Identity.", "from");
            var forbidden = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);
            Errors(HttpStatusCode.BadRequest, "The from email does not contain a valid address.", "from.email");
            var invalid = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);

            Assert.Equal((AttemptOutcome.PermanentFailure, FailureCategory.Provider, false), (forbidden.Outcome, forbidden.Category, forbidden.AddressGone));
            Assert.Equal((AttemptOutcome.PermanentFailure, FailureCategory.Provider, false), (invalid.Outcome, invalid.Category, invalid.AddressGone));
        }

        [Theory]
        [InlineData("""{"from":"a@example.com"}""", null)]
        [InlineData("""{"from":"a@example.com","region":"eu"}""", null)]
        [InlineData("""{"from":"a@example.com","region":"us"}""", "settings.region")]
        [InlineData("""{"from":"a@example.com","eventWebhookKey":"bm90IGEga2V5"}""", "settings.eventWebhookKey")]
        [InlineData("""{"fromName":"x"}""", "settings.from")]
        public void Settings_are_checked_when_saved(string settings, string? error)
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
    }
}

namespace Juice.Notifications.Tests.ProviderContracts
{
    internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
