using System.Net;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Providers.Chat;
using Juice.Notifications.Rendering;

namespace Juice.Notifications.Tests.ProviderContracts
{
    internal static class ChatTestSetup
    {
        public static string Content(string title = "Build failed", string text = "main is red <!channel> & <https://evil.example|click>",
            string? url = "https://ci.example.com/b/1", string? actions = """[{"id":"approve","label":"Approve"},{"id":"reject","label":"Reject"}]""")
            => ChannelContent.Chat($"Title: {title}\n{text}", "fallback", url is null ? null : new Dictionary<string, string> { ["self"] = url }, actions);

        public static RenderedDelivery Delivery(Guid id, int attempt, string channel, string destination, string? content = null)
            => new(id, Guid.CreateVersion7(), channel, content ?? Content(), destination, attempt, NotificationPriority.Normal, false);
    }

    /// <summary>The Slack app (phase-4-plan M27) through the contract kit: Slack answers most failures as <c>200 {"ok":false,"error"}</c>.</summary>
    public sealed class SlackProviderTests : ProviderContract
    {
        private readonly SlackProvider _provider;

        public SlackProviderTests() => _provider = new SlackProvider(new ChatHttp(Api), EmailTestSetup.Options);

        protected override INotificationProvider Provider => _provider;

        protected override IntegrationContext Integration => new(Guid.CreateVersion7(), "slack", "slack", "{}", "xoxb-1-2-abc:signing-secret");

        protected override RenderedDelivery Delivery(Guid id, int attempt) => ChatTestSetup.Delivery(id, attempt, ChatChannels.Slack, "C0123ABCD");

        private void Error(string error) => Api.Then(HttpStatusCode.OK, JsonSerializer.Serialize(new { ok = false, error }));

        protected override bool Arrange(ContractCase @case)
        {
            switch (@case)
            {
                case ContractCase.Accepted: Api.Then(HttpStatusCode.OK, """{"ok":true,"channel":"C0123ABCD","ts":"1763458200.000100"}"""); break;
                case ContractCase.ServerError: Error("internal_error"); break;
                case ContractCase.RateLimited: Api.Then(HttpStatusCode.TooManyRequests, "", "application/json", ("Retry-After", "30")); break;
                case ContractCase.CredentialsRefused: Error("invalid_auth"); break;
                case ContractCase.AddressRefused: Error("channel_not_found"); break;
                case ContractCase.ContentRefused: Error("invalid_blocks"); break;
                case ContractCase.ConnectionRefused: Api.ThenRefused(); break;
                case ContractCase.NoAnswer: Api.ThenSilent(); break;
            }
            return true;
        }

        /// <summary>
        /// The message: the title as a header, the text escaped so <c>&lt;!channel&gt;</c> or a forged link stays text, a button per action (its
        /// value the delivery id) and an Open button; the token as bearer, and only the token.
        /// </summary>
        [Fact]
        public async Task It_posts_escaped_blocks_with_a_button_per_action()
        {
            var id = Guid.CreateVersion7();
            Arrange(ContractCase.Accepted);
            var result = await Provider.SendAsync(Delivery(id, 1), Integration, Ct);

            Assert.Equal((AttemptOutcome.Delivered, "C0123ABCD:1763458200.000100"), (result.Outcome, result.ProviderMessageId));
            var sent = Assert.Single(Api.Requests);
            Assert.Equal("https://slack.com/api/chat.postMessage", sent.Uri.ToString());
            Assert.Equal("Bearer xoxb-1-2-abc", sent.Headers["Authorization"]);
            using var json = JsonDocument.Parse(sent.Body);
            var root = json.RootElement;
            Assert.Equal("C0123ABCD", root.GetProperty("channel").GetString());
            var blocks = root.GetProperty("blocks");
            Assert.Equal("Build failed", blocks[0].GetProperty("text").GetProperty("text").GetString());
            Assert.Equal("main is red &lt;!channel&gt; &amp; &lt;https://evil.example|click&gt;", blocks[1].GetProperty("text").GetProperty("text").GetString());
            var buttons = blocks[2].GetProperty("elements").EnumerateArray().ToList();
            Assert.Equal(["act:approve", "act:reject", "open"], buttons.Select(b => b.GetProperty("action_id").GetString()));
            Assert.All(buttons.Take(2), b => Assert.Equal(id.ToString(), b.GetProperty("value").GetString()));
            Assert.Equal("https://ci.example.com/b/1", buttons[2].GetProperty("url").GetString());
        }

        [Fact]
        public async Task A_person_who_left_is_gone_and_a_channel_without_the_bot_is_not()
        {
            Error("user_disabled");
            var left = await Provider.SendAsync(ChatTestSetup.Delivery(Guid.CreateVersion7(), 1, ChatChannels.Slack, "U0123ABCD"), Integration, Ct);
            Error("not_in_channel");
            var uninvited = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);
            Error("ratelimited");
            var limited = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);

            Assert.True(left.AddressGone);
            Assert.Equal((FailureCategory.Destination, false), (uninvited.Category, uninvited.AddressGone));
            Assert.Equal(FailureCategory.RateLimited, limited.Category);
        }

        [Theory]
        [InlineData("C0123ABCD", "C0123ABCD")]
        [InlineData(" G0123ABCD ", "G0123ABCD")]
        [InlineData("U0123ABCD", null)]
        [InlineData("#general", null)]
        [InlineData("https://hooks.slack.com/services/x", null)]
        public void A_target_is_a_channel_id(string address, string? stored)
            => Assert.Equal(stored, _provider.NormalizeTarget(address));

        [Fact]
        public async Task A_secret_without_the_signing_secret_sends_nothing()
        {
            var result = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration with { Secret = "xoxb-only" }, Ct);
            Assert.Equal((AttemptOutcome.PermanentFailure, FailureCategory.Provider), (result.Outcome, result.Category));
            Assert.Empty(Api.Requests);
        }
    }

    /// <summary>A Slack incoming webhook through the contract kit: plain HTTP statuses with Slack's words in the body.</summary>
    public sealed class SlackWebhookProviderTests : ProviderContract
    {
        private const string Url = "https://hooks.slack.com/services/T000/B000/XXXX";
        private readonly SlackWebhookProvider _provider;

        public SlackWebhookProviderTests() => _provider = new SlackWebhookProvider(new ChatHttp(Api), EmailTestSetup.Options);

        protected override INotificationProvider Provider => _provider;

        protected override IntegrationContext Integration => new(Guid.CreateVersion7(), "hook", "slack-webhook", "{}", null);

        protected override RenderedDelivery Delivery(Guid id, int attempt) => ChatTestSetup.Delivery(id, attempt, ChatChannels.Slack, Url);

        protected override bool Arrange(ContractCase @case)
        {
            switch (@case)
            {
                case ContractCase.Accepted: Api.Then(HttpStatusCode.OK, "ok", "text/plain"); break;
                case ContractCase.ServerError: Api.Then(HttpStatusCode.InternalServerError, "internal_error", "text/plain"); break;
                case ContractCase.RateLimited: Api.Then(HttpStatusCode.TooManyRequests, "rate_limited", "text/plain"); break;
                case ContractCase.CredentialsRefused: Api.Then(HttpStatusCode.Forbidden, "invalid_token", "text/plain"); break;
                case ContractCase.AddressRefused: Api.Then(HttpStatusCode.Gone, "channel_is_archived", "text/plain"); break;
                case ContractCase.ContentRefused: Api.Then(HttpStatusCode.BadRequest, "invalid_payload", "text/plain"); break;
                case ContractCase.ConnectionRefused: Api.ThenRefused(); break;
                case ContractCase.NoAnswer: Api.ThenSilent(); break;
            }
            return true;
        }

        /// <summary>No answering buttons (a webhook has no callbacks): the link only. A revoked webhook is the integration's failure.</summary>
        [Fact]
        public async Task It_posts_without_action_buttons_and_a_revoked_webhook_is_a_provider_failure()
        {
            Arrange(ContractCase.Accepted);
            await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);
            Api.Then(HttpStatusCode.NotFound, "no_service", "text/plain");
            var revoked = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);

            using var json = JsonDocument.Parse(Api.Requests.First().Body);
            Assert.Equal(["open"], json.RootElement.GetProperty("blocks")[2].GetProperty("elements").EnumerateArray().Select(b => b.GetProperty("action_id").GetString()));
            Assert.Equal(FailureCategory.Provider, revoked.Category);
        }

        /// <summary>Only Slack's own webhook host: a URL elsewhere (an internal address, an http one) is never called; a person's account can't be reached.</summary>
        [Theory]
        [InlineData("https://hooks.slack.com.evil.example/services/x")]
        [InlineData("http://hooks.slack.com/services/x")]
        [InlineData("https://10.0.0.1/services/x")]
        [InlineData("U0123ABCD")]
        public async Task Only_hooks_slack_com_is_called(string destination)
        {
            var result = await Provider.SendAsync(ChatTestSetup.Delivery(Guid.CreateVersion7(), 1, ChatChannels.Slack, destination), Integration, Ct);
            Assert.Equal((AttemptOutcome.PermanentFailure, FailureCategory.Provider), (result.Outcome, result.Category));
            Assert.Empty(Api.Requests);
        }

        [Fact]
        public void A_target_names_its_URL_by_reference_only() => Assert.Null(_provider.NormalizeTarget(Url));
    }

    /// <summary>A Teams workflow webhook through the contract kit.</summary>
    public sealed class TeamsWorkflowProviderTests : ProviderContract
    {
        private const string Url = "https://prod-12.westeurope.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke?api-version=2016-06-01&sig=x";
        private readonly TeamsWorkflowProvider _provider;

        public TeamsWorkflowProviderTests() => _provider = new TeamsWorkflowProvider(new ChatHttp(Api), EmailTestSetup.Options);

        protected override INotificationProvider Provider => _provider;

        protected override IntegrationContext Integration => new(Guid.CreateVersion7(), "teams", "teams-workflow", "{}", null);

        protected override RenderedDelivery Delivery(Guid id, int attempt) => ChatTestSetup.Delivery(id, attempt, ChatChannels.Teams, Url);

        private void Error(HttpStatusCode status, string code) => Api.Then(status, JsonSerializer.Serialize(new { error = new { code, message = code } }));

        protected override bool Arrange(ContractCase @case)
        {
            switch (@case)
            {
                case ContractCase.Accepted: Api.Then(HttpStatusCode.Accepted); break;
                case ContractCase.ServerError: Error(HttpStatusCode.BadGateway, "BadGateway"); break;
                case ContractCase.RateLimited: Error(HttpStatusCode.TooManyRequests, "WorkflowTriggerIsThrottled"); break;
                case ContractCase.CredentialsRefused: Error(HttpStatusCode.Unauthorized, "DirectApiAuthorizationRequired"); break;
                case ContractCase.AddressRefused: Error(HttpStatusCode.NotFound, "WorkflowNotFound"); break;
                case ContractCase.ContentRefused: Error(HttpStatusCode.BadRequest, "InvalidRequestContent"); break;
                case ContractCase.ConnectionRefused: Api.ThenRefused(); break;
                case ContractCase.NoAnswer: Api.ThenSilent(); break;
            }
            return true;
        }

        /// <summary>An Adaptive Card with the title, the text (no forged Markdown link), and the link; no buttons that answer.</summary>
        [Fact]
        public async Task It_posts_an_adaptive_card()
        {
            Arrange(ContractCase.Accepted);
            await Provider.SendAsync(ChatTestSetup.Delivery(Guid.CreateVersion7(), 1, ChatChannels.Teams, Url,
                ChatTestSetup.Content(text: "see [docs](https://evil.example)")), Integration, Ct);

            using var json = JsonDocument.Parse(Assert.Single(Api.Requests).Body);
            var attachment = json.RootElement.GetProperty("attachments")[0];
            Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
            var card = attachment.GetProperty("content");
            Assert.Equal("Build failed", card.GetProperty("body")[0].GetProperty("text").GetString());
            Assert.DoesNotContain("](", card.GetProperty("body")[1].GetProperty("text").GetString(), StringComparison.Ordinal);
            Assert.Equal("https://ci.example.com/b/1", card.GetProperty("actions")[0].GetProperty("url").GetString());
        }

        [Theory]
        [InlineData("https://evil.example/workflows/x")]
        [InlineData("https://logic.azure.com.evil.example/x")]
        [InlineData("https://prod-1.westeurope.logic.azure.com:8443/x")]
        public async Task Only_workflow_hosts_are_called(string destination)
        {
            var result = await Provider.SendAsync(ChatTestSetup.Delivery(Guid.CreateVersion7(), 1, ChatChannels.Teams, destination), Integration, Ct);
            Assert.Equal(AttemptOutcome.PermanentFailure, result.Outcome);
            Assert.Empty(Api.Requests);
        }
    }

    /// <summary>The Slack app's callbacks (phase-4-plan M27): signed with the signing secret, within five minutes.</summary>
    public sealed class SlackCallbackTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);
        private readonly SlackCallbackHandler _handler = new(new FixedClock(Now));
        private static readonly IntegrationContext Integration = new(Guid.CreateVersion7(), "slack", "slack", "{}", "xoxb-1:signing-secret");

        private Task<CallbackResult> PostAsync(string body, string contentType, string secret = "signing-secret", DateTimeOffset? at = null, bool sign = true)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var timestamp = (at ?? Now).ToUnixTimeSeconds().ToString();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = contentType };
            if (sign)
            {
                headers[SlackCallbackHandler.TimestampHeader] = timestamp;
                headers[SlackCallbackHandler.SignatureHeader] = SlackCallbackHandler.Sign(secret, timestamp, bytes);
            }
            return _handler.ReadAsync(new CallbackRequest(headers, bytes, new Uri("https://n.example/api/v1/callbacks/slack/x")), Integration, Ct);
        }

        private static string Actions(Guid deliveryId, params string[] actionIds)
            => "payload=" + Uri.EscapeDataString(JsonSerializer.Serialize(new
            {
                type = "block_actions", user = new { id = "U0123ABCD" },
                actions = actionIds.Select((a, i) => new { action_id = a, value = deliveryId.ToString(), action_ts = $"1763458200.00{i}" }),
            }));

        [Fact]
        public async Task A_button_of_ours_is_an_interaction_by_the_account_that_pressed_it()
        {
            var id = Guid.CreateVersion7();
            var result = await PostAsync(Actions(id, "act:approve", "open"), "application/x-www-form-urlencoded");

            Assert.True(result.Verified, result.Refusal);
            var e = Assert.Single(result.Events);
            Assert.Equal((CallbackKind.Interaction, (Guid?)id, "approve", "U0123ABCD"), (e.Kind, e.DeliveryId, e.ActionId, e.Account));
        }

        [Fact]
        public async Task A_direct_message_is_a_link_request_and_other_events_are_ignored()
        {
            var dm = await PostAsync(JsonSerializer.Serialize(new
            {
                type = "event_callback", event_id = "Ev1", event_time = 1763458200,
                @event = new { type = "message", channel_type = "im", user = "U0123ABCD", text = " abcd-efgh " },
            }), "application/json");
            var fromBot = await PostAsync(JsonSerializer.Serialize(new
            {
                type = "event_callback", event_id = "Ev2", @event = new { type = "message", channel_type = "im", user = "U0B", text = "ABCD-EFGH", bot_id = "B1" },
            }), "application/json");
            var inChannel = await PostAsync(JsonSerializer.Serialize(new
            {
                type = "event_callback", event_id = "Ev3", @event = new { type = "message", channel_type = "channel", user = "U0123ABCD", text = "ABCD-EFGH" },
            }), "application/json");

            var e = Assert.Single(dm.Events);
            Assert.Equal(("Ev1", CallbackKind.Link, "U0123ABCD", "abcd-efgh"), (e.EventId, e.Kind, e.Account, e.Detail));
            Assert.Empty(fromBot.Events);
            Assert.Empty(inChannel.Events);
        }

        [Fact]
        public async Task A_url_verification_is_answered_with_its_challenge()
        {
            var result = await PostAsync("""{"type":"url_verification","challenge":"c-123"}""", "application/json");
            Assert.Equal("c-123", result.Reply?.Body);
        }

        [Fact]
        public async Task A_bad_signature_an_old_timestamp_or_no_signing_secret_is_refused()
        {
            var body = Actions(Guid.CreateVersion7(), "act:approve");
            Assert.False((await PostAsync(body, "application/x-www-form-urlencoded", secret: "other")).Verified);
            Assert.False((await PostAsync(body, "application/x-www-form-urlencoded", at: Now.AddMinutes(-6))).Verified);
            Assert.False((await PostAsync(body, "application/x-www-form-urlencoded", sign: false)).Verified);
            var noSigning = await _handler.ReadAsync(new CallbackRequest(new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body), new Uri("https://n.example/")),
                Integration with { Secret = "xoxb-only" }, Ct);
            Assert.False(noSigning.Verified);
        }
    }
}
