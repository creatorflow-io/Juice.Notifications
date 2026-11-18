using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.Notifications.Rendering;
using Microsoft.Extensions.Options;

namespace Juice.Notifications.Providers.Chat
{
    /// <summary>A chat message as Slack's Block Kit shows it: escaped text, a header, the body, and buttons.</summary>
    internal static class SlackBlocks
    {
        /// <summary>The prefix of our buttons' <c>action_id</c>: <c>act:{actionId}</c>, its value the delivery id.</summary>
        public const string ActionPrefix = "act:";
        public const string OpenAction = "open";

        /// <summary>
        /// Slack's mrkdwn escaping: <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, so no data value can mention <c>@channel</c>, a person, or forge a
        /// link (<c>&lt;!channel&gt;</c>, <c>&lt;@U1&gt;</c>, <c>&lt;https://…|text&gt;</c>). Emphasis characters stay: harmless.
        /// </summary>
        public static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static string Cut(string text, int max) => ChannelContent.Cut(text, max)!;

        /// <summary>The message: <c>text</c> (what notifications show) and the blocks; buttons only when the integration takes their answers.</summary>
        public static Dictionary<string, object> Message(ChatContent content, Guid deliveryId, bool buttons)
        {
            var blocks = new List<object>
            {
                new { type = "header", text = new { type = "plain_text", text = Cut(content.Title.Length > 0 ? content.Title : "Notification", 150), emoji = false } },
            };
            if (content.Text.Length > 0)
            {
                blocks.Add(new { type = "section", text = new { type = "mrkdwn", text = Cut(Escape(content.Text), 3000), verbatim = true } });
            }
            var elements = new List<object>();
            if (buttons)
            {
                elements.AddRange(ChatMessages.Actions(content).Select(a => new
                {
                    type = "button", text = new { type = "plain_text", text = Cut(a.Label, 75) }, action_id = Cut(ActionPrefix + a.Id, 255), value = deliveryId.ToString(),
                }));
            }
            if (content.Url is { } url)
            {
                elements.Add(new { type = "button", text = new { type = "plain_text", text = "Open" }, action_id = OpenAction, url });
            }
            if (elements.Count > 0)
            {
                blocks.Add(new { type = "actions", elements });
            }
            return new Dictionary<string, object> { ["text"] = Cut(Escape(content.Title), 3000), ["blocks"] = blocks, ["unfurl_links"] = false, ["unfurl_media"] = false };
        }
    }

    /// <summary>
    /// The Slack app (<c>slack</c>; phase-4-plan M27): <c>chat.postMessage</c> with the app's bot token, to a channel (a target's address, the
    /// channel id; the bot must be in it) or to a person's linked account (a direct message). The type's actions are buttons, answered through
    /// <see cref="SlackCallbackHandler"/>. The secret is <c>{botToken}:{signingSecret}</c>: the token sends, the signing secret checks
    /// callbacks. Slack takes no idempotency key, so the provider isn't idempotent.
    /// <list type="bullet">
    /// <item><c>ok</c>: <c>Delivered</c> (it is in the channel), the id <c>{channel}:{ts}</c>;</item>
    /// <item><c>ratelimited</c> or 429: <c>RateLimited</c>; Slack's own failures and 5xx: retryable <c>Provider</c>;</item>
    /// <item>the token or app refused: permanent <c>Provider</c>; an unknown or deactivated person or channel: <c>Destination</c>, the address
    /// gone; a channel the bot isn't in or an archived one: <c>Destination</c>; the message refused: <c>Content</c>.</item>
    /// </list>
    /// </summary>
    public sealed partial class SlackProvider(ChatHttp http, IOptions<DeliveryOptions> options) : INotificationProvider, ITargetAddresses
    {
        public const string KindName = "slack";

        public string Channel => ChatChannels.Slack;
        public string Kind => KindName;
        public bool Idempotent => false;
        public bool RequiresSecret => true;

        /// <summary>A channel (<c>C…</c>, <c>G…</c>), a direct message (<c>D…</c>) or a person (<c>U…</c>, <c>W…</c>).</summary>
        [GeneratedRegex(@"^[CGDUW][A-Z0-9]{6,20}$")]
        internal static partial Regex Conversation();

        public string? CheckSettings(string settings) => null;

        public string? NormalizeTarget(string address)
            => Conversation().IsMatch(address.Trim()) && address.Trim()[0] is 'C' or 'G' ? address.Trim() : null;

        public string TargetRule => "The address is a Slack channel's id (C… or G…, from the channel's details); invite the app to the channel.";

        /// <summary>The bot token and the signing secret, from the integration's secret <c>{botToken}:{signingSecret}</c>.</summary>
        internal static (string Token, string SigningSecret)? Secrets(string? secret)
            => secret?.Split(':', 2) is [{ Length: > 0 } token, { Length: > 0 } signing] ? (token, signing) : null;

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            if (Secrets(integration.Secret) is not { } secrets)
            {
                return SendResult.Permanent(FailureCategory.Provider, "The Slack integration's secret isn't {botToken}:{signingSecret}.");
            }
            if (!Conversation().IsMatch(delivery.Destination))
            {
                return SendResult.Permanent(FailureCategory.Destination, "The destination isn't a Slack channel or person.");
            }
            if (ChatMessages.Read(delivery.Content) is not { } content)
            {
                return SendResult.Permanent(FailureCategory.Content, "The stored content isn't a chat message.");
            }
            var message = SlackBlocks.Message(content, delivery.DeliveryId, buttons: true);
            message["channel"] = delivery.Destination;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/chat.postMessage")
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(message)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Token);
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            return failure ?? Classify(answer!);
        }

        internal static SendResult Classify(ApiAnswer answer)
        {
            var status = answer.Status;
            if (status == 429)
            {
                return SendResult.Retry(FailureCategory.RateLimited, "429: rate limited", status);
            }
            if (status >= 500)
            {
                return SendResult.Retry(FailureCategory.Provider, $"{status}: Slack is unavailable", status);
            }
            bool ok;
            try
            {
                using var json = JsonDocument.Parse(answer.Body);
                ok = json.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
                return SendResult.Permanent(FailureCategory.Provider, $"{status}: Slack's answer isn't JSON", status);
            }
            if (ok)
            {
                return SendResult.Delivered(status, answer.Field("channel") is { } channel && answer.Field("ts") is { } ts ? $"{channel}:{ts}" : null);
            }
            var error = answer.Field("error") ?? "unknown";
            var text = $"{status}: {error}";
            return error switch
            {
                "ratelimited" => SendResult.Retry(FailureCategory.RateLimited, text, status),
                "internal_error" or "fatal_error" or "service_unavailable" or "request_timeout" => SendResult.Retry(FailureCategory.Provider, text, status),
                "channel_not_found" or "user_not_found" or "user_disabled" => SendResult.Gone(text, status),
                "is_archived" or "not_in_channel" or "cannot_dm_bot" or "messages_tab_disabled" or "restricted_action"
                    => SendResult.Permanent(FailureCategory.Destination, text, status),
                "msg_too_long" or "no_text" or "invalid_blocks" or "invalid_blocks_format" or "too_many_attachments" or "invalid_arguments"
                    => SendResult.Permanent(FailureCategory.Content, text, status),
                _ => SendResult.Permanent(FailureCategory.Provider, text, status), // invalid_auth, token_revoked, missing_scope, account_inactive, …
            };
        }
    }

    /// <summary>
    /// A Slack incoming webhook (<c>slack-webhook</c>): posts to the one channel it was made for, so it serves targets only, whose
    /// <c>addressRef</c> names the webhook's URL (a secret: anyone with it can post). Only <c>https://hooks.slack.com/</c>. No buttons that
    /// answer (a webhook has no callbacks); a link button. Answers: <c>200 ok</c> delivered; <c>400</c> the message refused; <c>403</c>, <c>404
    /// no_service</c> the webhook revoked (<c>Provider</c>); <c>404 channel_not_found</c>, <c>410</c> the channel gone (<c>Destination</c>);
    /// 429; 5xx.
    /// </summary>
    public sealed class SlackWebhookProvider(ChatHttp http, IOptions<DeliveryOptions> options) : INotificationProvider, ITargetAddresses
    {
        public const string KindName = "slack-webhook";

        public string Channel => ChatChannels.Slack;
        public string Kind => KindName;
        public bool Idempotent => false;
        public bool RequiresSecret => false;

        public string? CheckSettings(string settings) => null;

        public string? NormalizeTarget(string address) => null;

        public string TargetRule => "A Slack webhook's URL is a secret: name where it is with addressRef, not address.";

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            if (!ChatMessages.IsHttpsOn(delivery.Destination, out var url, "hooks.slack.com"))
            {
                // A person's account, or anything else: a webhook reaches only its channel. Another integration (the app) may reach it.
                return SendResult.Permanent(FailureCategory.Provider, "A Slack webhook sends only to its URL on hooks.slack.com.");
            }
            if (ChatMessages.Read(delivery.Content) is not { } content)
            {
                return SendResult.Permanent(FailureCategory.Content, "The stored content isn't a chat message.");
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(SlackBlocks.Message(content, delivery.DeliveryId, buttons: false))),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            return failure ?? Classify(answer!);
        }

        internal static SendResult Classify(ApiAnswer answer)
        {
            var status = answer.Status;
            var body = answer.Body.Length > 200 ? answer.Body[..200] : answer.Body;
            var text = $"{status}: {body}".TrimEnd(' ', ':');
            return status switch
            {
                >= 200 and < 300 => SendResult.Delivered(status),
                429 => SendResult.Retry(FailureCategory.RateLimited, text, status),
                >= 500 => SendResult.Retry(FailureCategory.Provider, text, status),
                410 => SendResult.Permanent(FailureCategory.Destination, text, status),
                404 when body.Contains("channel", StringComparison.Ordinal) => SendResult.Permanent(FailureCategory.Destination, text, status),
                400 or 413 => SendResult.Permanent(FailureCategory.Content, text, status),
                _ => SendResult.Permanent(FailureCategory.Provider, text, status), // 403 invalid_token, action_prohibited; 404 no_service
            };
        }
    }

    /// <summary>
    /// A Teams workflow webhook (<c>teams-workflow</c>; phase-4-plan p4-d2): a Power Automate "post to a channel when a webhook request is
    /// received" flow, whose URL a target's <c>addressRef</c> names (a secret: its signature is in it). The message is an Adaptive Card (1.4):
    /// the title, the text, and a link; no buttons that answer (the Teams bot comes later). Only Logic Apps and Power Platform hosts.
    /// </summary>
    public sealed class TeamsWorkflowProvider(ChatHttp http, IOptions<DeliveryOptions> options) : INotificationProvider, ITargetAddresses
    {
        public const string KindName = "teams-workflow";

        public string Channel => ChatChannels.Teams;
        public string Kind => KindName;
        public bool Idempotent => false;
        public bool RequiresSecret => false;

        public string? CheckSettings(string settings) => null;

        public string? NormalizeTarget(string address) => null;

        public string TargetRule => "A Teams workflow's URL is a secret: name where it is with addressRef, not address.";

        /// <summary>
        /// Text for a TextBlock, which renders a little Markdown: a data value mustn't forge a link, so <c>](</c> is broken with a zero-width
        /// space (it reads the same). Bare URLs still show as links, as they would anywhere.
        /// </summary>
        internal static string Escape(string text) => text.Replace("](", "]​(");

        internal static object Card(ChatContent content)
        {
            var body = new List<object>
            {
                new { type = "TextBlock", text = Escape(content.Title), weight = "Bolder", size = "Medium", wrap = true },
            };
            if (content.Text.Length > 0)
            {
                body.Add(new { type = "TextBlock", text = Escape(content.Text), wrap = true });
            }
            var card = new Dictionary<string, object>
            {
                ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json", ["type"] = "AdaptiveCard", ["version"] = "1.4", ["body"] = body,
            };
            if (content.Url is { } url)
            {
                card["actions"] = new[] { new { type = "Action.OpenUrl", title = "Open", url } };
            }
            return new { type = "message", attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive", contentUrl = (string?)null, content = card } } };
        }

        public async Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct)
        {
            if (!ChatMessages.IsHttpsOn(delivery.Destination, out var url, ".logic.azure.com", ".api.powerplatform.com"))
            {
                return SendResult.Permanent(FailureCategory.Destination, "A Teams workflow's URL is https on logic.azure.com or api.powerplatform.com.");
            }
            if (ChatMessages.Read(delivery.Content) is not { } content)
            {
                return SendResult.Permanent(FailureCategory.Content, "The stored content isn't a chat message.");
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(Card(content))),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            var (answer, failure) = await ProviderHttp.SendAsync(http.Client, request, options.Value.RequestTimeout, ct);
            return failure ?? Classify(answer!);
        }

        internal static SendResult Classify(ApiAnswer answer)
        {
            var status = answer.Status;
            var detail = answer.Field("error", "message") ?? (answer.Body.Length > 200 ? answer.Body[..200] : answer.Body);
            var text = $"{status}: {Reasons.Clean(detail)}".TrimEnd(' ', ':');
            return status switch
            {
                >= 200 and < 300 => SendResult.Accepted(), // the flow runs after answering: it took the message
                429 => SendResult.Retry(FailureCategory.RateLimited, text, status),
                >= 500 => SendResult.Retry(FailureCategory.Provider, text, status),
                404 => SendResult.Permanent(FailureCategory.Destination, text, status), // the flow is gone
                400 or 413 => SendResult.Permanent(FailureCategory.Content, text, status),
                _ => SendResult.Permanent(FailureCategory.Provider, text, status), // 401, 403: the URL's signature refused, the flow off
            };
        }
    }
}
