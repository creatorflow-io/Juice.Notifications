using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Providers.Chat;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Slack's Web API as the chat providers reach it through <see cref="ChatHttp"/>: every <c>chat.postMessage</c> succeeds and is kept.</summary>
    public sealed class FakeSlack : HttpMessageHandler
    {
        public ConcurrentQueue<(Uri Uri, string Authorization, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Enqueue((request.RequestUri!, request.Headers.Authorization?.ToString() ?? "", body));
            var channel = JsonDocument.Parse(body).RootElement.TryGetProperty("channel", out var c) ? c.GetString() : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(JsonSerializer.Serialize(new { ok = true, channel, ts = "1763458200.000100" })),
            };
        }
    }

    public sealed class ChatApp : IAsyncLifetime
    {
        public const string SecretVariable = "NOTIFICATIONS_SECRET_TEST_SLACK";
        public const string SigningSecret = "slack-signing-secret";

        public FakeSlack Slack { get; } = new();

        public NotificationsApp App { get; }

        public ChatApp()
        {
            Environment.SetEnvironmentVariable(SecretVariable, "xoxb-test-token:" + SigningSecret);
            App = new(host => host.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(new ChatHttp(Slack)))));
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
            (await admin.PutAsJsonAsync($"/api/v1/tenants/{ProducerApp.Tenant}/integrations/slack", new PutIntegrationBody("slack", "slack",
                JsonElement.Parse("{}"), "env:" + SecretVariable, null, true))).EnsureSuccessStatusCode();
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>
    /// Slack (phase-4-plan M27) in the real host, Slack's API faked: a person links their Slack account with a code sent to the app, the app posts
    /// to a channel, and a button pressed in Slack acts on the person's inbox item.
    /// </summary>
    public sealed class ChatFlowTests(ChatApp fixture) : IClassFixture<ChatApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = ProducerApp.Tenant;
        private static readonly string Api = $"/api/v1/tenants/{Tenant}";
        private NotificationsApp App => fixture.App;

        private async Task<Integration> IntegrationAsync()
        {
            await using var db = App.Database.Context();
            return await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == Tenant && i.Key == "slack", Ct);
        }

        private async Task<HttpStatusCode> PostSlackAsync(Guid integrationId, string body, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/callbacks/slack/{integrationId}") { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new(contentType);
            request.Headers.Add(SlackCallbackHandler.TimestampHeader, timestamp);
            request.Headers.Add(SlackCallbackHandler.SignatureHeader, SlackCallbackHandler.Sign(ChatApp.SigningSecret, timestamp, bytes));
            using var client = App.Client();
            return (await client.SendAsync(request, Ct)).StatusCode;
        }

        private static string DirectMessage(string account, string text) => JsonSerializer.Serialize(new
        {
            type = "event_callback", event_id = "Ev" + Guid.NewGuid().ToString("N"), event_time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            @event = new { type = "message", channel_type = "im", user = account, text },
        });

        /// <summary>Ann asks for a code and sends it to the app from <paramref name="account"/>: her Slack contact point.</summary>
        private async Task<(Guid Subscriber, JsonElement Point)> LinkAsync(string account)
        {
            using var ann = App.Client(await LocalStack.PersonTokenAsync());
            var link = await (await ann.PostAsJsonAsync($"{Api}/me/links", new LinkCodeBody("slack"), Ct)).Content.ReadFromJsonAsync<LinkCodeResponse>(Ct);
            Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", link!.Code);
            Assert.Equal(HttpStatusCode.OK, await PostSlackAsync((await IntegrationAsync()).Id, DirectMessage(account, link.Code.ToLowerInvariant()), "application/json"));
            var me = await ann.GetFromJsonAsync<JsonElement>($"{Api}/me", Ct);
            var points = await ann.GetFromJsonAsync<JsonElement>($"{Api}/me/contact-points", Ct);
            var point = points.EnumerateArray().Single(p => p.GetProperty("channel").GetString() == "slack" && p.GetProperty("address").GetString() == account);
            return (me.GetProperty("id").GetGuid(), point);
        }

        /// <summary>
        /// Linking (phase-4-plan I41): the code sent from the account makes it Ann's verified Slack contact point; the code works once; another
        /// person's message with a wrong code links nothing; and a Slack account can't be added by hand.
        /// </summary>
        [Fact]
        public async Task A_person_links_their_Slack_account_with_a_code_sent_to_the_app()
        {
            LocalStack.RequireAvailable();
            var account = "U" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            var (_, point) = await LinkAsync(account);

            Assert.Equal("link", point.GetProperty("verifiedBy").GetString());
            using var ann = App.Client(await LocalStack.PersonTokenAsync());
            var added = await ann.PostAsJsonAsync($"{Api}/me/contact-points", new AddContactPointBody("slack", "U0SOMEONE1"), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, added.StatusCode);
            Assert.Contains("linked through the bot", await added.Content.ReadAsStringAsync(Ct));

            var integration = await IntegrationAsync();
            Assert.Equal(HttpStatusCode.OK, await PostSlackAsync(integration.Id, DirectMessage("U0STRANGER1", "ABCD-EFGH"), "application/json"));
            await using var db = App.Database.Context();
            Assert.Equal(0, await db.ContactPoints.CountAsync(c => c.TenantId == Tenant && c.Channel == "slack" && c.Masked.EndsWith("GER1"), Ct));
        }

        /// <summary>
        /// A button pressed in Slack by Ann's linked account is her action on her inbox item for the same notification: recorded once, as if in the
        /// inbox; the same press from an account no one linked does nothing.
        /// </summary>
        [Fact]
        public async Task A_button_pressed_in_Slack_acts_on_the_persons_inbox_item()
        {
            LocalStack.RequireAvailable();
            var account = "U" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            var (subscriber, _) = await LinkAsync(account);
            var integration = await IntegrationAsync();
            var (slackDelivery, item) = await NotificationAsync(subscriber, integration);
            string Press(string who) => "payload=" + Uri.EscapeDataString(JsonSerializer.Serialize(new
            {
                type = "block_actions", user = new { id = who },
                actions = new[] { new { action_id = "act:approve", value = slackDelivery.ToString(), action_ts = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.{Random.Shared.Next(1000, 9999)}" } },
            }));

            Assert.Equal(HttpStatusCode.OK, await PostSlackAsync(integration.Id, Press("U0NOBODY01"), "application/x-www-form-urlencoded"));
            await using (var db = App.Database.Context())
            {
                Assert.False(await db.Interactions.AnyAsync(i => i.TenantId == Tenant && i.InboxItemId == item, Ct));
            }
            Assert.Equal(HttpStatusCode.OK, await PostSlackAsync(integration.Id, Press(account), "application/x-www-form-urlencoded"));
            Assert.Equal(HttpStatusCode.OK, await PostSlackAsync(integration.Id, Press(account), "application/x-www-form-urlencoded"));

            await using var check = App.Database.Context();
            var interactions = await check.Interactions.AsNoTracking().Where(i => i.TenantId == Tenant && i.InboxItemId == item).ToListAsync(Ct);
            Assert.Single(interactions, i => i.Kind == "action" && i.ActionId == "approve" && i.SubscriberId == subscriber);
            Assert.Single(interactions, i => i.Kind == "read");
        }

        /// <summary>A notification for Ann with a Slack delivery and an inbox item (actions approve and reject) for the same request.</summary>
        private async Task<(Guid SlackDelivery, Guid Item)> NotificationAsync(Guid subscriber, Integration slack)
        {
            await using var db = App.Database.Context();
            var app = await db.Applications.AsNoTracking().SingleAsync(a => a.Key == ProducerApp.Key, Ct);
            var type = Seed.Type(app.Id, $"{ProducerApp.Key}.chat{Guid.NewGuid():N}"[..40]);
            var request = Seed.Request(Tenant, app.Id, type.Id, Guid.NewGuid().ToString("N"));
            var notification = Seed.Notification(Tenant, request.Id);
            notification.State = NotificationState.Planned;
            notification.NextAt = null;
            var recipient = Seed.Recipient(Tenant, notification.Id);
            var inboxIntegration = await db.Integrations.AsNoTracking().FirstOrDefaultAsync(i => i.TenantId == Tenant && i.Channel == Channels.Inbox, Ct);
            Delivery Make(string channel, Guid integrationId, string key) => new()
            {
                TenantId = Tenant, Channel = channel, IntegrationId = integrationId, DestinationKey = key, DestinationSnapshot = "{}", Locale = "en",
                RendererVersion = "test", Content = "{}", CreatedAt = DateTimeOffset.UtcNow, Status = DeliveryStatus.Delivered, Attempts = 1,
                CompletedAt = DateTimeOffset.UtcNow,
            };
            var toSlack = Make("slack", slack.Id, "it:" + Guid.NewGuid());
            var toInbox = Make(Channels.Inbox, inboxIntegration?.Id ?? slack.Id, $"inbox:{subscriber}");
            var item = new InboxItem
            {
                TenantId = Tenant, SubscriberId = subscriber, DeliveryId = toInbox.Id, TypeKey = type.Key, GroupKey = "general", Title = "Approve the deploy",
                Body = "", Actions = """[{"id":"approve","label":"Approve"},{"id":"reject","label":"Reject"}]""", Actionable = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.AddRange(type, request, notification, recipient, toSlack, toInbox, item,
                new DeliveryItem { TenantId = Tenant, DeliveryId = toSlack.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = toSlack.DestinationKey, Channel = "slack" },
                new DeliveryItem { TenantId = Tenant, DeliveryId = toInbox.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = toInbox.DestinationKey, Channel = Channels.Inbox });
            await db.SaveChangesAsync(Ct);
            return (toSlack.Id, item.Id);
        }

        /// <summary>
        /// A route's Slack target: a channel id as its address (a URL is refused for the app), and the dispatcher posts to that channel with the
        /// bot token: <c>Delivered</c>, with Slack's message id.
        /// </summary>
        [Fact]
        public async Task The_app_posts_to_a_channel_target()
        {
            LocalStack.RequireAvailable();
            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            var refused = await admin.PutAsJsonAsync($"{Api}/integrations/slack/targets/bad", new PutTargetBody("https://hooks.slack.com/services/x", null, true), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("Slack channel's id", await refused.Content.ReadAsStringAsync(Ct));
            (await admin.PutAsJsonAsync($"{Api}/integrations/slack/targets/ops", new PutTargetBody("C0OPS12345", null, true), Ct)).EnsureSuccessStatusCode();

            var integration = await IntegrationAsync();
            Guid id;
            await using (var db = App.Database.Context())
            {
                var app = await db.Applications.AsNoTracking().SingleAsync(a => a.Key == ProducerApp.Key, Ct);
                var type = Seed.Type(app.Id, $"{ProducerApp.Key}.post{Guid.NewGuid():N}"[..40]);
                var request = Seed.Request(Tenant, app.Id, type.Id, Guid.NewGuid().ToString("N"));
                var notification = Seed.Notification(Tenant, request.Id);
                notification.State = NotificationState.Planned;
                notification.NextAt = null;
                var recipient = Seed.Recipient(Tenant, notification.Id);
                var target = await db.IntegrationTargets.AsNoTracking().SingleAsync(t => t.TenantId == Tenant && t.Key == "ops", Ct);
                var delivery = new Delivery
                {
                    TenantId = Tenant, Channel = "slack", IntegrationId = integration.Id, DestinationKey = $"it:{target.Id}",
                    DestinationSnapshot = """{"address":"C0OPS12345"}""", Locale = "en", RendererVersion = "test", CreatedAt = DateTimeOffset.UtcNow,
                    Content = Rendering.ChannelContent.Chat("Title: Deploy finished\nAll green.", "x", null, null), Status = DeliveryStatus.Pending,
                    NextAt = DateTimeOffset.UtcNow,
                };
                db.AddRange(type, request, notification, recipient, delivery,
                    new DeliveryItem { TenantId = Tenant, DeliveryId = delivery.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = delivery.DestinationKey, Channel = "slack" });
                await db.SaveChangesAsync(Ct);
                id = delivery.Id;
            }

            Delivery? sent = null;
            for (var i = 0; i < 150 && sent?.Status is null or DeliveryStatus.Pending or DeliveryStatus.Sending; i++)
            {
                await Task.Delay(100, Ct);
                await using var db = App.Database.Context();
                sent = await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
            }
            Assert.Equal((DeliveryStatus.Delivered, "C0OPS12345:1763458200.000100"), (sent!.Status, sent.ProviderMessageId));
            var post = Assert.Single(fixture.Slack.Requests, r => r.Body.Contains("Deploy finished"));
            Assert.Equal("Bearer xoxb-test-token", post.Authorization);
            Assert.Contains("\"channel\":\"C0OPS12345\"", post.Body);
        }
    }
}
