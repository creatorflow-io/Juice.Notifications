# Providers

How to set up each provider integration a tenant can use. Every integration has a `kind` (the provider), a `channel`, `settings` (checked when
saved) and usually a `secretRef` (`env:` or `file:`, never the secret itself; design D15). Providers report back to
`POST /api/v1/callbacks/{provider}/{integrationId}`; set `Callbacks:PublicUrl` so the service can tell providers its address, and make that
path reachable from the internet. Callbacks are anonymous and checked per provider.

The phase 1 providers (`smtp`, `webhook`) are described in [`design.md`](design.md) §9.

## Email: Amazon SES (`ses`)

Package `Juice.Notifications.Providers.Email` (`AddEmailProviders`). Sends with SES API v2 `SendEmail`, signed with SigV4.

| Field | |
|---|---|
| `settings.region` | the AWS region, such as `eu-west-1` |
| `settings.from` | the sender's bare address, an identity verified in SES |
| `settings.fromName` | optional display name |
| `settings.configurationSet` | optional; the configuration set whose event destination publishes to SNS |
| `settings.topicArn` | the SNS topic SES publishes events to. Without it, callbacks are refused |
| `secretRef` | names a value `{accessKeyId}:{secretAccessKey}` of an IAM user allowed `ses:SendEmail` |

Each message is tagged `delivery_id`. To receive bounces, complaints and delivery receipts:

1. Create an SNS topic and, in the configuration set, an event destination publishing *Bounce*, *Complaint* and *Delivery* to it.
2. Save the topic's ARN as `settings.topicArn`.
3. Subscribe `https://{public url}/api/v1/callbacks/ses/{integrationId}` to the topic (HTTPS). The service confirms the subscription itself.

The service checks that each message comes from that topic and verifies SNS's signature against the certificate it fetches from SNS's own
host. SES has no idempotency key: a send that gets no answer is an unknown outcome and is not repeated.

## Email: SendGrid (`sendgrid`)

| Field | |
|---|---|
| `settings.from` | the sender's bare address, a verified sender identity |
| `settings.fromName` | optional display name |
| `settings.region` | `global` (default) or `eu` (`api.eu.sendgrid.com`, for EU subusers) |
| `settings.eventWebhookKey` | the signed event webhook's verification key (base64), as SendGrid shows it. Without it, callbacks are refused |
| `secretRef` | names an API key with *Mail Send* permission |

Each message carries the custom argument `delivery_id`. To receive events, set the event webhook's URL to
`https://{public url}/api/v1/callbacks/sendgrid/{integrationId}`, select *Delivered*, *Bounced*, *Dropped*, *Spam reports* and
*Unsubscribes*, turn on the signed event webhook, and copy its verification key to `settings.eventWebhookKey`.

## Chat: Slack (`slack`, `slack-webhook`)

Package `Juice.Notifications.Providers.Chat` (`AddChatProviders`). Channel `slack` reaches channels (targets) and people (linked accounts).

**The Slack app** (`slack`) posts with `chat.postMessage`, to channels and as direct messages, with the type's actions as buttons.

| Field | |
|---|---|
| `secretRef` | names a value `{botToken}:{signingSecret}`: the bot token (`xoxb-…`, scopes `chat:write`, `im:history`) and the app's signing secret |
| target `address` | a channel's id (`C…` or `G…`); invite the app to the channel |

In the app's settings, set both *Interactivity* and *Event Subscriptions* (bot event `message.im`) to
`https://{public url}/api/v1/callbacks/slack/{integrationId}`; Slack's URL check is answered. Each call is checked with the signing secret
and refused when its timestamp is more than five minutes off.

- **Buttons**: a press by a person who linked their Slack account acts on their inbox item for the same notification, as in the inbox (the
  action must still be open). A press from an account no one linked does nothing.
- **Linking**: a person asks for a code (`POST /api/v1/tenants/{tenantId}/me/links` with `{"channel": "slack"}`; 15 minutes, used once)
  and sends it to the app in a direct message. That account becomes their verified Slack contact point. Slack accounts can't be added by hand.

**Incoming webhooks** (`slack-webhook`) post to the one channel each was made for: a target whose `addressRef` names the webhook's URL
(`https://hooks.slack.com/…`, a secret). A link button, no answering buttons.

## Chat: Microsoft Teams (`teams-workflow`)

Channel `teams` reaches channels only. Create a Teams workflow "Post to a channel when a webhook request is received" and give a target
`addressRef` naming its URL (a secret; `*.logic.azure.com` or `*.api.powerplatform.com`). Messages are Adaptive Cards with the title,
the text and a link. The Teams bot, with buttons and direct messages, comes later (phase-4-plan p4-d2).

Chat text is plain: Slack's `<!channel>`-style mentions and links, and Markdown links in Teams, are escaped. A type with no template for a
chat channel sends its name, its link and its actions.

## What callbacks change

| Event | Delivery | Person's contact point |
|---|---|---|
| receipt | `Delivered` | |
| hard bounce (SES `Permanent`; SendGrid `bounce`, or `dropped` for a bounced or invalid address) | `Bounced` | `failed` |
| soft bounce (SES `Transient`, `Undetermined`; SendGrid `bounce` of type `blocked`) | unchanged | `bouncing`, if it was working |
| complaint (SES `Complaint`; SendGrid `spamreport`, `unsubscribe`, `group_unsubscribe`, `dropped` for those) | unchanged | `failed` |

Each event is applied once. A bounce's diagnostic is kept without the address in it.

## Failover

A tenant can have several integrations of a channel; the dispatcher uses them in order of `priority`. When one fails with a provider fault
(refused credentials, an unverified sender, an outage past the retries), a delivery to a person's address or an external address moves to the
next one, keeping its id (phase-4-plan I38).
