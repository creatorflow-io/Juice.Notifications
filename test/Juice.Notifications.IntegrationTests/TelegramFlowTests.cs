using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Notifications.Api.Contracts;
using Juice.Notifications.Data;
using Juice.Notifications.Providers.Telegram;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juice.Notifications.IntegrationTests
{
    /// <summary>Telegram's Bot API as <see cref="TelegramHttp"/> reaches it: every method succeeds; <c>sendMessage</c> gives message 42.</summary>
    public sealed class FakeTelegram : HttpMessageHandler
    {
        public ConcurrentQueue<(Uri Uri, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Enqueue((request.RequestUri!, body));
            var result = request.RequestUri!.AbsolutePath.EndsWith("/sendMessage", StringComparison.Ordinal) ? (object)new { message_id = 42 } : true;
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result })) };
        }
    }

    public sealed class TelegramApp : IAsyncLifetime
    {
        public const string Token = "123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw";
        public const string SecretVariable = "NOTIFICATIONS_SECRET_TEST_TELEGRAM";

        public FakeTelegram Telegram { get; } = new();

        public NotificationsApp App { get; }

        public TelegramApp()
        {
            Environment.SetEnvironmentVariable(SecretVariable, Token);
            App = new(host =>
            {
                host.UseSetting("Callbacks:PublicUrl", "https://notifications.example.com/");
                host.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(new TelegramHttp(Telegram))));
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
            (await admin.PutAsJsonAsync($"/api/v1/tenants/{ProducerApp.Tenant}/integrations/tg", new PutIntegrationBody("telegram", "telegram",
                JsonElement.Parse("{}"), "env:" + SecretVariable, null, true))).EnsureSuccessStatusCode();
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>
    /// Telegram (phase-4-plan M28) in the real host, the Bot API faked: an admin registers the bot's webhook, a person links their chat with
    /// <c>/start CODE</c>, and the bot posts to a group target.
    /// </summary>
    public sealed class TelegramFlowTests(TelegramApp fixture) : IClassFixture<TelegramApp>
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static readonly Guid Tenant = ProducerApp.Tenant;
        private static readonly string Api = $"/api/v1/tenants/{Tenant}";
        private NotificationsApp App => fixture.App;

        private async Task<Integration> IntegrationAsync()
        {
            await using var db = App.Database.Context();
            return await db.Integrations.AsNoTracking().SingleAsync(i => i.TenantId == Tenant && i.Key == "tg", Ct);
        }

        private async Task<HttpResponseMessage> UpdateAsync(Guid integrationId, object update, string? secret = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/callbacks/telegram/{integrationId}") { Content = JsonContent.Create(update) };
            request.Headers.Add(TelegramCallbackHandler.SecretHeader, secret ?? TelegramProvider.WebhookSecret(TelegramApp.Token, integrationId));
            using var client = App.Client();
            return await client.SendAsync(request, Ct);
        }

        [Fact]
        public async Task An_admin_registers_the_bots_webhook_at_the_public_URL()
        {
            LocalStack.RequireAvailable();
            var integration = await IntegrationAsync();
            using var admin = App.Client(await LocalStack.AdminTokenAsync());

            var registered = await admin.PostAsync($"{Api}/integrations/tg/callbacks", null, Ct);
            var missing = await admin.PostAsync($"{Api}/integrations/nope/callbacks", null, Ct);

            Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var set = Assert.Single(fixture.Telegram.Requests, r => r.Uri.AbsolutePath.EndsWith("/setWebhook", StringComparison.Ordinal));
            using var body = JsonDocument.Parse(set.Body);
            Assert.Equal($"https://notifications.example.com/api/v1/callbacks/telegram/{integration.Id}", body.RootElement.GetProperty("url").GetString());
        }

        /// <summary>
        /// Linking (I41) through Telegram's deep link: <c>/start CODE</c> from Ann's private chat makes it her verified Telegram contact point, and
        /// the bot answers in the chat. A wrong secret token is refused.
        /// </summary>
        [Fact]
        public async Task A_person_links_their_Telegram_chat_with_start()
        {
            LocalStack.RequireAvailable();
            var integration = await IntegrationAsync();
            using var ann = App.Client(await LocalStack.PersonTokenAsync());
            var link = await (await ann.PostAsJsonAsync($"{Api}/me/links", new LinkCodeBody("telegram"), Ct)).Content.ReadFromJsonAsync<LinkCodeResponse>(Ct);
            var chat = Random.Shared.NextInt64(100_000_000, 999_999_999);
            object Start(string code) => new
            {
                update_id = Random.Shared.Next(), message = new { message_id = 1, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), text = "/start " + code,
                    chat = new { id = chat, type = "private" }, from = new { id = chat, is_bot = false } },
            };

            Assert.Equal(HttpStatusCode.Unauthorized, (await UpdateAsync(integration.Id, Start(link!.Code.Replace("-", "")), secret: "guess")).StatusCode);
            var answered = await UpdateAsync(integration.Id, Start(link.Code.Replace("-", "")));

            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
            Assert.Contains("sendMessage", await answered.Content.ReadAsStringAsync(Ct));
            var points = await ann.GetFromJsonAsync<JsonElement>($"{Api}/me/contact-points", Ct);
            var point = Assert.Single(points.EnumerateArray(), p => p.GetProperty("channel").GetString() == "telegram" && p.GetProperty("address").GetString() == chat.ToString());
            Assert.Equal("link", point.GetProperty("verifiedBy").GetString());
        }

        [Fact]
        public async Task The_bot_posts_to_a_group_target()
        {
            LocalStack.RequireAvailable();
            using var admin = App.Client(await LocalStack.AdminTokenAsync());
            (await admin.PutAsJsonAsync($"{Api}/integrations/tg/targets/ops", new PutTargetBody("-1001234567890", null, true), Ct)).EnsureSuccessStatusCode();
            var integration = await IntegrationAsync();
            Guid id;
            await using (var db = App.Database.Context())
            {
                var app = await db.Applications.AsNoTracking().SingleAsync(a => a.Key == ProducerApp.Key, Ct);
                var type = Seed.Type(app.Id, $"{ProducerApp.Key}.tg{Guid.NewGuid():N}"[..40]);
                var request = Seed.Request(Tenant, app.Id, type.Id, Guid.NewGuid().ToString("N"));
                var notification = Seed.Notification(Tenant, request.Id);
                notification.State = NotificationState.Planned;
                notification.NextAt = null;
                var recipient = Seed.Recipient(Tenant, notification.Id);
                var target = await db.IntegrationTargets.AsNoTracking().SingleAsync(t => t.TenantId == Tenant && t.Key == "ops", Ct);
                var delivery = new Delivery
                {
                    TenantId = Tenant, Channel = TelegramChannel.Key, IntegrationId = integration.Id, DestinationKey = $"it:{target.Id}",
                    DestinationSnapshot = """{"address":"-1001234567890"}""", Locale = "en", RendererVersion = "test", CreatedAt = DateTimeOffset.UtcNow,
                    Content = Rendering.ChannelContent.Chat("Title: Deploy <finished>\nAll green.", "x", null, null), Status = DeliveryStatus.Pending,
                    NextAt = DateTimeOffset.UtcNow,
                };
                db.AddRange(type, request, notification, recipient, delivery,
                    new DeliveryItem { TenantId = Tenant, DeliveryId = delivery.Id, RecipientId = recipient.Id, RequestId = request.Id, DestinationKey = delivery.DestinationKey, Channel = TelegramChannel.Key });
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
            Assert.Equal((DeliveryStatus.Delivered, "-1001234567890:42"), (sent!.Status, sent.ProviderMessageId));
            var post = Assert.Single(fixture.Telegram.Requests, r => r.Uri.AbsolutePath.EndsWith("/sendMessage", StringComparison.Ordinal)
                && JsonDocument.Parse(r.Body).RootElement.GetProperty("text").GetString() == "<b>Deploy &lt;finished&gt;</b>\n\nAll green.");
            Assert.EndsWith($"/bot{TelegramApp.Token}/sendMessage", post.Uri.AbsolutePath);
        }
    }
}
