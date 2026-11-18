# Providers

How to set up each provider integration a tenant can use. Every integration has a `kind` (the provider), a `channel`, `settings` (checked when
saved) and usually a `secretRef` (`env:` or `file:`, never the secret itself; design D15). Providers report back to
`POST /api/v1/callbacks/{provider}/{integrationId}`; set `Callbacks:PublicUrl` so the service can tell providers its address, and make that
path reachable from the internet. Callbacks are anonymous and checked per provider.

The phase 1 providers (`smtp`, `webhook`) are described in [`design.md`](design.md) §9.

## Secrets

A `secretRef` (and a target's `addressRef`) names where a secret is; the value is read when it is used, never stored:

| Form | Where | Through the API |
|---|---|---|
| `env:NAME` | an environment variable | names starting with `Delivery:ApiSecretEnvPrefix` (`NOTIFICATIONS_SECRET_`) |
| `file:/path` | a file (trimmed) | files directly in `Delivery:ApiSecretDirectory` |
| `vault:path#field` | HashiCorp Vault, KV v2 | paths starting with `Delivery:ApiVaultPrefix` (unset: none) |
| `keyvault:name` | Azure Key Vault | names starting with `Delivery:ApiKeyVaultPrefix` (unset: none) |

The deployment's own configuration (provisioning files) may name any. Vault values are kept 5 minutes (`Secrets:CacheFor`). A vault that
can't be read when sending is a provider failure: retried, then failed over to the tenant's next integration. A secret a vault doesn't have
is unset, as an empty variable is. The service's own keys (contact encryption, the identity resolver's client secret) stay `env:` or `file:`.

- **Vault** (`Secrets:Vault`): `Address`, `Mount` (`secret`), optional `Namespace`; sign-in with `TokenRef`, or AppRole with `RoleId` and
  `SecretIdRef` (its token kept until a minute before its lease ends).
- **Key Vault** (`Secrets:KeyVault`): `VaultUri`; the managed identity by default (`ClientId` for a user-assigned one), or client
  credentials with `TenantId`, `ClientId` and `ClientSecretRef`.

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

## Chat: Telegram (`telegram`)

Package `Juice.Notifications.Providers.Telegram` (`AddTelegram`). Channel `telegram` reaches group and channel chats (targets) and people's
own chats (linked).

| Field | |
|---|---|
| `secretRef` | names the bot's token, from BotFather |
| target `address` | a chat's id (`-100…` for a group or channel) or a public channel's `@name`; add the bot (an admin in a channel) |

Set `Callbacks:PublicUrl`, then register the bot's webhook: `POST /api/v1/tenants/{tenantId}/integrations/{key}/callbacks`. The service
calls `setWebhook` with its URL and a secret token derived from the bot token, and refuses updates without it. A person links their chat by
sending `/start CODE` to the bot. The library's contact points page shows the code, and opens the bot with it when `telegramBot` is
configured. Buttons press as in Slack; the bot answers in the chat.

In the library (`@juice-js/notifications`), `linkChannels: ['slack', 'telegram']` shows the linking controls.

Chat text is plain: Slack's `<!channel>`-style mentions and links, and Markdown links in Teams, are escaped. A type with no template for a
chat channel sends its name, its link and its actions.

## SMS: Twilio (`twilio`)

Package `Juice.Notifications.Providers.Sms` (`AddSms`). Channel `sms` reaches people's verified phone numbers and shared numbers (targets).
Numbers are E.164 (`+84901234567`); spaces, dashes, dots and parentheses are dropped. A type sends SMS only with an `sms` template; its
output is the text, up to 1,600 characters.

| Field | |
|---|---|
| `settings.from` or `settings.messagingServiceSid` | the sender: one of the account's numbers, or a Messaging Service (`MG…`) |
| `secretRef` | names `{accountSid}:{authToken}` |
| target `address` | a phone number |

With `Callbacks:PublicUrl` set, each message asks Twilio for status callbacks, checked with the auth token against that URL. A number
added by a person is verified with a code by SMS (the service's verification type gains an SMS template when the channel is registered). In
the library, `phoneNumbers: true` lets people add numbers. A number that replied STOP, an invalid one or not a mobile one fails its contact
point; `delivered` is a receipt, and errors 30005 and 30006 hard bounces, 30003 a soft one.

## Push: FCM, APNs, Web Push (`fcm`, `apns`, `webpush`)

Package `Juice.Notifications.Providers.Push` (`AddPush`). Channel `push` reaches the devices people's apps register
(`POST …/me/devices`): one delivery per device, through the tenant's first enabled `push` integration whose kind is the device's platform.
A type reaches devices when it has a `push` template (`Title: …` then the text, as a chat message). When a push service says a token is gone,
its registration is deleted at once: no retry, no failover. Registrations not seen for 60 days are swept.

| Kind | `settings` | `secretRef` names |
|---|---|---|
| `fcm` | `projectId` (optional: the key's project) | the service account's JSON key |
| `apns` | `teamId`, `keyId`, `bundleId`, `environment` (`production` or `sandbox`) | the `.p8` signing key (PEM) |
| `webpush` | `subject` (`mailto:` or `https:`), `publicKey` (VAPID, base64url) | the VAPID private key (base64url) |

Web Push sends only to the browsers' push services (Google, Mozilla, Microsoft, Apple). In the library, `enableWebPush` subscribes the
browser and registers it, and the packaged `juice-push-sw.js` shows the notification and opens its link.

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
