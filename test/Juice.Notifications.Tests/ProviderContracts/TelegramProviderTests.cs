using System.Net;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Providers.Telegram;

namespace Juice.Notifications.Tests.ProviderContracts
{
    /// <summary>Telegram's Bot API (phase-4-plan M28) through the contract kit: errors as <c>{"ok":false,"error_code","description"}</c>.</summary>
    public sealed class TelegramProviderTests : ProviderContract
    {
        internal const string Token = "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw";
        private readonly TelegramProvider _provider;

        public TelegramProviderTests() => _provider = new TelegramProvider(new TelegramHttp(Api), EmailTestSetup.Options);

        protected override INotificationProvider Provider => _provider;

        protected override IntegrationContext Integration => new(Guid.CreateVersion7(), "tg", "telegram", "{}", Token);

        protected override RenderedDelivery Delivery(Guid id, int attempt) => ChatTestSetup.Delivery(id, attempt, TelegramChannel.Key, "-1001234567890");

        private void Error(HttpStatusCode status, string description)
            => Api.Then(status, JsonSerializer.Serialize(new { ok = false, error_code = (int)status, description }));

        protected override bool Arrange(ContractCase @case)
        {
            switch (@case)
            {
                case ContractCase.Accepted: Api.Then(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":42,"chat":{"id":-1001234567890}}}"""); break;
                case ContractCase.ServerError: Error(HttpStatusCode.BadGateway, "Bad Gateway"); break;
                case ContractCase.RateLimited: Error(HttpStatusCode.TooManyRequests, "Too Many Requests: retry after 5"); break;
                case ContractCase.CredentialsRefused: Error(HttpStatusCode.Unauthorized, "Unauthorized"); break;
                case ContractCase.AddressRefused: Error(HttpStatusCode.BadRequest, "Bad Request: chat not found"); break;
                case ContractCase.ContentRefused: Error(HttpStatusCode.BadRequest, "Bad Request: message is too long"); break;
                case ContractCase.ConnectionRefused: Api.ThenRefused(); break;
                case ContractCase.NoAnswer: Api.ThenSilent(); break;
            }
            return true;
        }

        /// <summary>HTML with the title bold and the data escaped, a button per action (its data the delivery and the action), and the link.</summary>
        [Fact]
        public async Task It_sends_escaped_HTML_with_inline_buttons()
        {
            var id = Guid.CreateVersion7();
            Arrange(ContractCase.Accepted);
            var result = await Provider.SendAsync(Delivery(id, 1), Integration, Ct);

            Assert.Equal((AttemptOutcome.Delivered, "-1001234567890:42"), (result.Outcome, result.ProviderMessageId));
            var sent = Assert.Single(Api.Requests);
            Assert.Equal($"https://api.telegram.org/bot{Token}/sendMessage", sent.Uri.ToString());
            using var json = JsonDocument.Parse(sent.Body);
            var root = json.RootElement;
            Assert.Equal("HTML", root.GetProperty("parse_mode").GetString());
            Assert.Equal("<b>Build failed</b>\n\nmain is red &lt;!channel&gt; &amp; &lt;https://evil.example|click&gt;", root.GetProperty("text").GetString());
            var rows = root.GetProperty("reply_markup").GetProperty("inline_keyboard");
            Assert.Equal([$"a:{id:N}:approve", $"a:{id:N}:reject"], rows[0].EnumerateArray().Select(b => b.GetProperty("callback_data").GetString()));
            Assert.Equal("https://ci.example.com/b/1", rows[1][0].GetProperty("url").GetString());
        }

        /// <summary>A person who blocked the bot is gone; a group that removed it is a destination failure, not gone; no error keeps the token.</summary>
        [Fact]
        public async Task A_blocked_bot_is_gone_and_errors_never_carry_the_token()
        {
            Error(HttpStatusCode.Forbidden, "Forbidden: bot was blocked by the user");
            var blocked = await Provider.SendAsync(ChatTestSetup.Delivery(Guid.CreateVersion7(), 1, TelegramChannel.Key, "123456789"), Integration, Ct);
            Error(HttpStatusCode.Forbidden, "Forbidden: bot was kicked from the supergroup chat");
            var kicked = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);
            Api.ThenRefused();
            var refused = await Provider.SendAsync(Delivery(Guid.CreateVersion7(), 1), Integration, Ct);

            Assert.True(blocked.AddressGone);
            Assert.Equal((FailureCategory.Destination, false), (kicked.Category, kicked.AddressGone));
            Assert.DoesNotContain(Token, refused.Error);
        }

        [Theory]
        [InlineData("-1001234567890", "-1001234567890")]
        [InlineData(" @juice_alerts ", "@juice_alerts")]
        [InlineData("@ab", null)]
        [InlineData("https://t.me/juice", null)]
        public void A_target_is_a_chat_id_or_a_public_channel(string address, string? stored) => Assert.Equal(stored, _provider.NormalizeTarget(address));

        /// <summary>setWebhook with the callback URL, the derived secret token, and only the updates the bot reads.</summary>
        [Fact]
        public async Task Registering_callbacks_sets_the_webhook_with_its_secret_token()
        {
            Api.Then(HttpStatusCode.OK, """{"ok":true,"result":true,"description":"Webhook was set"}""");
            var integration = Integration;
            var url = new Uri($"https://n.example/api/v1/callbacks/telegram/{integration.IntegrationId}");

            Assert.Null(await _provider.RegisterCallbacksAsync(integration, url, Ct));
            var sent = Assert.Single(Api.Requests);
            Assert.EndsWith("/setWebhook", sent.Uri.AbsolutePath);
            using var json = JsonDocument.Parse(sent.Body);
            Assert.Equal(url.AbsoluteUri, json.RootElement.GetProperty("url").GetString());
            Assert.Equal(TelegramProvider.WebhookSecret(Token, integration.IntegrationId), json.RootElement.GetProperty("secret_token").GetString());
            Assert.Equal(["message", "callback_query"], json.RootElement.GetProperty("allowed_updates").EnumerateArray().Select(u => u.GetString()));

            Api.Then(HttpStatusCode.BadRequest, """{"ok":false,"error_code":400,"description":"Bad Request: bad webhook: HTTPS url must be provided for webhook"}""");
            Assert.Contains("HTTPS url must be provided", await _provider.RegisterCallbacksAsync(integration, url, Ct));
        }
    }

    /// <summary>The bot's webhook (phase-4-plan M28): only with the integration's secret token.</summary>
    public sealed class TelegramCallbackTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly TelegramCallbackHandler _handler = new();
        private static readonly IntegrationContext Integration = new(Guid.CreateVersion7(), "tg", "telegram", "{}", TelegramProviderTests.Token);

        private Task<CallbackResult> PostAsync(object update, string? secret = null)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TelegramCallbackHandler.SecretHeader] = secret ?? TelegramProvider.WebhookSecret(TelegramProviderTests.Token, Integration.IntegrationId),
            };
            return _handler.ReadAsync(new CallbackRequest(headers, JsonSerializer.SerializeToUtf8Bytes(update), new Uri("https://n.example/")), Integration, Ct);
        }

        private static object Start(long user, string text, string chatType = "private", bool bot = false) => new
        {
            update_id = 1001, message = new { message_id = 7, date = 1763458200, text, chat = new { id = user, type = chatType }, from = new { id = user, is_bot = bot } },
        };

        [Fact]
        public async Task Start_with_a_code_in_a_private_chat_is_a_link_request_answered_in_the_chat()
        {
            var result = await PostAsync(Start(123456789, "/start ABCDEFGH"));

            Assert.True(result.Verified, result.Refusal);
            var e = Assert.Single(result.Events);
            Assert.Equal(($"u:{Integration.IntegrationId:N}:1001", CallbackKind.Link, "123456789", "ABCDEFGH"), (e.EventId, e.Kind, e.Account, e.Detail));
            using var reply = JsonDocument.Parse(result.Reply!.Body);
            Assert.Equal(("sendMessage", "123456789"), (reply.RootElement.GetProperty("method").GetString(), reply.RootElement.GetProperty("chat_id").GetString()));
        }

        [Fact]
        public async Task Start_in_a_group_or_from_a_bot_links_nothing()
        {
            Assert.Empty((await PostAsync(Start(-100123, "/start ABCDEFGH", chatType: "group"))).Events);
            Assert.Empty((await PostAsync(Start(5, "/start ABCDEFGH", bot: true))).Events);
        }

        [Fact]
        public async Task A_button_press_is_an_interaction_and_its_spinner_is_answered()
        {
            var id = Guid.CreateVersion7();
            var result = await PostAsync(new { update_id = 1002, callback_query = new { id = "cq1", from = new { id = 123456789, is_bot = false }, data = $"a:{id:N}:approve" } });

            var e = Assert.Single(result.Events);
            Assert.Equal((CallbackKind.Interaction, (Guid?)id, "approve", "123456789"), (e.Kind, e.DeliveryId, e.ActionId, e.Account));
            Assert.Contains("answerCallbackQuery", result.Reply!.Body);
        }

        [Fact]
        public async Task Without_the_secret_token_it_is_refused()
        {
            Assert.False((await PostAsync(Start(1, "/start ABCDEFGH"), secret: "guess")).Verified);
            var other = TelegramProvider.WebhookSecret(TelegramProviderTests.Token, Guid.CreateVersion7());
            Assert.False((await PostAsync(Start(1, "/start ABCDEFGH"), secret: other)).Verified);
            var noHeader = await _handler.ReadAsync(new CallbackRequest(new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}"), new Uri("https://n.example/")),
                Integration, Ct);
            Assert.False(noHeader.Verified);
        }
    }
}
