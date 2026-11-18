# Phase 4 plan: enterprise

**Status:** accepted by the owner, 17 November 2026, with the recommendations in §2 (design §19, decisions 15–20). SQL Server moved to a
phase of its own, 4b (p4-d4). It builds on `design.md` (section numbers like §9.2 refer to it) and
continues `phase-3-plan.md`: implementation choices go on from I38, milestones from M25, and each milestone names the build-board items it closes
(`p4-01` …).

**Goal:** the service reaches people and teams wherever they are (Slack, Teams, Telegram, SMS, push, and email through SES and SendGrid), survives a
provider's outage by failing over without losing or duplicating a delivery, protects routes from storms, lets tenants edit their templates, masks
what types mark as personal, reads secrets from Vault or Key Vault, and runs on SQL Server as well as PostgreSQL.

**Gate:** the phase 4 proof points (§16), each shown by a named test (§6), plus a local-stack demo and an acceptance record.

---

## 1. Scope

**In**

- Failover between a tenant's integrations of a channel (D8, §10.3), and the inbound callback endpoint providers report to (bounces,
  complaints, delivery receipts, interactive actions), with per-provider signature checks and `provider_events` deduplication.
- Email: SES and SendGrid, with bounce and complaint callbacks marking contact points (§9).
- Chat: Slack (incoming webhook; a Slack app for channels, direct messages to linked people, and interactive buttons) and Teams (workflow
  webhook with an Adaptive Card; the bot per p4-d2).
- Telegram (Bot API): group and channel chats as targets; a person's chat as a contact point, linked through the bot.
- SMS through Twilio (voice per p4-d3).
- Push: FCM, APNs, Web Push, gone-token handling (§9.2), and the service worker helper in `@juice-js/notifications`.
- Storm protection (§14) and bulk replay by filter (admin, audited).
- Template editing for tenants: overrides through the API, a preview that renders with sample data, and an editor page in the library (D22).
- PII: `redact` paths masked in stored request data and inbox items after rendering (deferred #29, #41); retention per tenant (§13.2).
- Secret references `vault:` and `keyvault:` (D15).

SQL Server (migrations, claim store, the concurrency suite; D17) is phase 4b, after this one (p4-d4).

**Out**

- A WYSIWYG template editor (D22, later); WhatsApp, Zalo, Discord, voice beyond p4-d3 (D24, later); on-call and external directory resolvers.

---

## 2. Owner decisions

All six recommendations were accepted on 17 November 2026.

| # | Question | Decision | Why |
|---|---|---|---|
| p4-d1 | **How are providers tested without their accounts?** | Against fakes of each provider's HTTP API (in-process, recorded from their documentation and error catalogues), with the contract tests every provider passes (outcomes, categories, unknown outcomes, idempotency). Optional live smoke tests run only when credentials are in the environment (`NOTIFICATIONS_LIVE_*`), never in CI. | No sandbox accounts are needed to build or test; a live run can confirm a provider when the owner has an account. |
| p4-d2 | **Teams: a bot with interactive actions, or workflow webhooks only?** | Workflow webhooks (Adaptive Cards) in phase 4; the Teams bot (Azure Bot registration, Microsoft app credentials, a public HTTPS endpoint, per-user chats and card actions) after phase 4, when a tenant needs it. Slack gets its app in phase 4. | The Teams bot needs an Azure registration per deployment and Bot Framework hosting; Slack's app needs only a token and a signing secret. Interactive actions still arrive from Slack and the inbox. |
| p4-d3 | **Twilio: SMS only, or voice too?** | SMS only; voice later with WhatsApp and the other channels. | Voice needs call flows (TwiML) and its own templates; nothing in phases 1–3 asks for it. |
| p4-d4 | **SQL Server: in phase 4, or a phase of its own?** | **A phase of its own, after phase 4.** Phases 2 and 3 grew PostgreSQL-specific code: advisory locks (catalog, throttles, digests), a SQL function (`inbox_group`), `SKIP LOCKED` (claims, the event relay), `xmin` row versions, `jsonb`, partial indexes, `ON CONFLICT`. Porting it is a refactor behind a dialect seam plus a second migration set and the full concurrency suite, about as large as a phase. | Done inside phase 4 it would crowd the provider work and share its review; alone, it gets its own plan and gate. The proof point "the claim store passes the same concurrency suite on SQL Server" moves with it. |
| p4-d5 | **Which vaults, and how does the service sign in?** | HashiCorp Vault (KV v2; token or AppRole) and Azure Key Vault (managed identity, or client credentials), configured per deployment (`Secrets:Vault`, `Secrets:KeyVault`); `vault:path#field` and `keyvault:name` references; values cached 5 minutes. | The two named in D15; per-deployment sign-in keeps credentials out of tenant configuration, as SMTP servers and identity directories are. |
| p4-d6 | **Where do providers' callbacks come in?** | One endpoint, `POST /api/v1/callbacks/{provider}/{integrationId}`, anonymous but verified per provider (SES SNS signatures, SendGrid signed webhooks, Slack signing secret, Twilio request signature, Telegram secret token). The deployment makes it reachable (`Callbacks:PublicUrl`); the local stack tests it by posting signed callbacks itself. | One place to secure and rate-limit; the integration id names the tenant and the secret to check with. |

---

## 3. Implementation choices

| # | Choice | Why |
|---|---|---|
| I38 | **Failover** (D8): a delivery keeps its id and its content; on a `Provider` failure, or retryable provider failures past the attempts, the dispatcher moves it to the tenant's next enabled integration for the channel (by priority), records the switch on the attempt, and starts its attempts afresh there. Never for `Destination`, `Content`, `Policy`, nor after an unknown outcome on a non-idempotent provider. Counted (`failovers`). | The id is what receivers deduplicate on; changing integrations must not change it (proof point 1). |
| I39 | **Providers as packages**: `Juice.Notifications.Providers.Email` (SES, SendGrid), `.Chat` (Slack, Teams), `.Telegram` (a channel package: definition, renderer, provider), `.Sms` (Twilio), `.Push` (FCM, APNs, Web Push), each registering its channel definitions and providers (D24). Each provider has a contract test suite run against its fake. | The registry was built for this in phase 1; the core stays free of provider SDKs. |
| I40 | **Callbacks** (p4-d6): `provider_events` (provider, provider event id unique together, delivery, kind, at) dedupes them; a bounce or complaint marks the contact point (`bouncing` after a soft bounce, `failed` after a hard one or a complaint), a delivery receipt moves a delivery from `Accepted` to `Delivered` or `Bounced`, an interactive action becomes an interaction (phase 3). | §9, §10.2: `Delivered` from a callback is the strongest evidence a channel gives. |
| I41 | **Linking chat identities**: a person links their Slack user or Telegram chat from the inbox: the service shows a one-time code; the person sends it to the bot (`/start <code>` in Telegram, a DM to the Slack app); the bot's inbound event creates a verified contact point (`slack`, `telegram`) for that person. Never matched by name or email (D23). | The bot hearing the code from that account is the proof, as a code sent to an address is. |
| I42 | **Push** (§9.2): `dev:{registrationId}` destinations, one delivery per device; collapse key = delivery id; gone tokens delete the registration (`Destination`, no retry, no failover); registrations unseen for 60 days dropped; VAPID keys per tenant integration; the library's service worker helper subscribes and registers. | As designed; the gone-token rule is a proof point. |
| I43 | **Storm protection** (§14): deliveries per route per minute are counted; above `Storm:Threshold` (default 100/min) the route switches to digest-only (its route recipients join a 1-minute digest, M21's machinery) until the rate stays below half the threshold for 5 minutes; the tenant's admins are told once per storm (an inbox item of a built-in type). | Reuses digests; nothing is dropped (proof point 3). |
| I44 | **Bulk replay**: `POST …/deliveries/replay` with a filter (status, channel, integration, time range, type), admin-only, audited, at most 10,000 per call, each replay keeping its delivery id (as the single retry does). | §14. |
| I45 | **Templates for tenants**: `PUT/GET/DELETE …/types/{key}/templates/{channel}` (a tenant override of the latest version, checked as declarations are), `POST …/templates/preview` (render with given or sample data, never stored); an editor page in `@juice-js/notifications` with the preview beside it. | D22: text with preview through phase 4. |
| I46 | **Redaction and retention**: after rendering, `redact` paths are masked in the stored request data (`"***"`), in inbox items' copied content where a path's value appears, and in logs; retention (§13.2) as a tenant setting with a daily purge job, in batches. | Deferred #29, #41. |
| I47 | **Vault secrets** (p4-d5): `ISecretResolver` behind `SecretReferences.Resolve`, with `env:`, `file:`, `vault:`, `keyvault:` resolvers; read at send time, cached 5 minutes; a failure is a `Provider` failure (fails over, retried). | D15. |

---

## 4. Data model (phase 4 tables)

| Table | Notes |
|---|---|
| `provider_events` | as §12: `Provider`, `ProviderEventId` (unique together), `DeliveryId`, `Kind`, `At` |
| `link_codes` | person, channel, code hash, expires, used: chat identity linking (I41) |
| `storms` | route, started, ended, deliveries counted |
| `tenant_settings` | retention days (requests, content), storm threshold override |

Changed: `deliveries` gains `Failovers` and `AttemptsAtFailover` (M25; each attempt already names its integration), and an index on the provider's
message id; `contact_points` gains channels `slack`, `telegram`,
`sms`; `device_registrations` gains `LastSeenAt` use for expiry.

---

## 5. Milestones

Owner decision (17 Nov 2026): no per-milestone review until phase 4 ends; one review loop over phases 3 and 4 then. Each milestone ends with its
tests, a commit and a build-board update.

- **M25 Failover and callbacks** (`p4-05`): I38, I40 foundation, provider contract test kit.
  - *As built (failover):* only deliveries whose address isn't the integration's own fail over: a person's contact point (`cp:`) or an
    external address (`ext:`). A shared target (`it:`: a webhook URL, a Slack channel) belongs to its integration and an inbox has only
    the built-in one. The next integration is the tenant's next enabled one for the channel after the current one by (priority, key), the order
    the planner picks the first by, so failover never comes back round. Triggers: a permanent `Provider` failure, retryable `Provider`
    failures past the attempts, a disabled integration or one no provider sends through. Never `RateLimited`, and never after an unknown
    outcome, even on an idempotent provider: the next provider can't deduplicate on the first one's key. The delivery records `Failovers` and
    `AttemptsAtFailover` (the new integration's budget counts from there) instead of a flag on the attempt; each attempt already names its
    integration, so history shows the switch. A stale worker's failure through the old integration, recorded late, doesn't answer the new one.
    A dead-letter retry stays on the integration the delivery is on. Counter `notifications.failovers`. Tests: `FailoverTests` (proof points 1
    and 2, plus retry budgets, rate limits, unknown outcomes, disabled integrations, targets, the end of the order, stale failures).
  - *As built (callbacks):* `ICallbackHandler` (Abstractions) per provider: `Provider` (the path segment), `Handles(kind)`, and
    `ReadAsync`, which verifies before reading and returns events or a refusal (plus a reply for providers that want one). `ProviderCallbacks`
    records each event once in `provider_events` (unique per tenant, provider, event id) and applies it in the same transaction. A receipt makes a
    delivery `Delivered` from `Accepted`, `Sending`, `Retrying` or a failed unknown outcome (it went out, so it isn't sent again; the version moves on, so a
    dispatcher holding it loses its save). A hard bounce makes it `Bounced` (`Destination`) and fails the contact point; a soft bounce marks a working point
    `Bouncing`; a complaint fails it. Interactions are recorded, and applied from M27. Contact points are marked with an update statement, as
    the dispatcher does, so the audit doesn't record the provider's report as someone's change; the endpoint is excluded from access records.
    Answers: `200` (or the handler's reply), `401` refused, `404` unknown integration or a provider it doesn't use, `503` its secret unset,
    `413` over 1 MB, `429` past `Callbacks:PerMinute` (1,200) per integration. `Callbacks:PublicUrl` gives providers their URL. Tests:
    `CallbackTests`.
  - *As built (contract kit):* `ProviderContract` and `FakeHttpApi` in the unit tests: eight cases (accepted, server error, rate limited, credentials
    refused, address refused, content refused, no connection, no answer) each with the outcome and category it must give, plus the idempotency
    key on every attempt. A provider's suite scripts its fake per case in its API's words. `ReferenceProviderTests` runs it over a minimal JSON
    provider, the template for M26; misclassifying a case fails the suite (checked by hand).
- **M26 Email: SES and SendGrid** (`p4-04`): providers, bounce and complaint callbacks.
  - *As built:* package `Juice.Notifications.Providers.Email` (`AddEmailProviders`, added by the App), over the providers' HTTP APIs with no
    SDK; one `EmailHttp` client (no redirects, pooled connections renewed every 5 minutes) that tests replace with a fake. Setup for operators:
    [`providers.md`](providers.md).
    **SES** (`ses`): API v2 `SendEmail`, SigV4 written here and checked against AWS's worked example; the secret is
    `{accessKeyId}:{secretAccessKey}`; the delivery id is the message tag `delivery_id`. Throttling (429, or `ThrottlingException` in a 400) is
    `RateLimited`; a used-up sending quota is a retryable `Provider` failure (another integration may have room); refused credentials, a
    suspended or paused account, an unverified sender or the sandbox's unverified recipient are permanent `Provider` (fail over); a malformed
    recipient is `Destination` with the address gone; another rejected message is `Content`. Callbacks come through SNS: the message's topic must
    be the integration's `settings.topicArn` (SNS signs any topic's messages, so the signature alone proves nothing about who published), the
    signature (v1 SHA1 or v2 SHA256) is checked against the certificate fetched only from `https://sns.{region}.amazonaws.com` and cached, and a
    subscription confirmation is confirmed by visiting its link, only on SNS's host. Bounce `Permanent` is a hard bounce, `Transient` and
    `Undetermined` soft; complaints and delivery receipts as named; others ignored.
    **SendGrid** (`sendgrid`): v3 `mail/send` with the API key, `region: eu` for the EU host, the delivery id as the custom argument
    `delivery_id`. 401/403 and a 400 on `from` are `Provider`; a 400 on the recipient's field is `Destination`, gone; other 400s and 413 are
    `Content`. Callbacks: the signed event webhook (ECDSA P-256 over timestamp + body) with `settings.eventWebhookKey`; `bounce` is hard unless
    `blocked`, `dropped` for a bounced or invalid address is hard, for a spam report or unsubscribe a complaint, as are `spamreport` and the
    unsubscribes. Neither API takes an idempotency key, so both are non-idempotent (no answer is an unknown outcome, not repeated). Diagnostics are
    kept with addresses masked. Neither handler checks timestamps for freshness: providers retry for hours or days, and each event applies once.
    Not yet: SendGrid `dropped` for other reasons (spam content) leaves the delivery `Accepted`; SES `Reject` and `RenderingFailure` are ignored.
    Tests: `SesProviderTests`, `SendGridProviderTests` (the contract kit plus what each sends), `AwsSigV4Tests`, `SesCallbackTests`,
    `SendGridCallbackTests`, and `EmailProviderFlowTests` in the real host (the dispatcher sends through each fake API; signed callbacks mark the
    delivery and the person's address; a forged or unsigned one is refused).
- **M27 Chat: Slack and Teams** (`p4-01`): Slack webhook and app (channels, DMs to linked people, buttons → interactions), Teams workflow
  webhook (Adaptive Card); Slack linking (I41).
- **M28 Telegram** (`p4-12`): channel package, chats as targets, linking.
- **M29 SMS: Twilio** (`p4-02`).
- **M30 Push** (`p4-03`): FCM, APNs, Web Push, the library's service worker helper.
- **M31 Storms and bulk replay** (`p4-06`, `p4-08`).
- **M32 Template editing** (`p4-07`).
- **M33 Redaction and retention** (`p4-09`).
- **M34 Vault and Key Vault** (`p4-10`).
- **M35 Proof points and acceptance** (`p4-g1` … `p4-g3`), then the review loop over phases 3 and 4.

---

## 6. Proof points and their tests

| # | Proof point (§16) | Test |
|---|---|---|
| 1 | A provider outage fails over without losing a delivery or changing its id | `A_provider_outage_fails_over_to_the_next_integration_with_the_same_delivery_id` |
| 2 | A destination failure does not fail over | `A_destination_failure_fails_the_delivery_without_trying_another_integration` |
| 3 | A gone push token removes its registration and is never retried | `A_gone_push_token_deletes_its_registration_without_retry_or_failover` |
| 4 | A 1,000-notification storm reaches each route as digests | `A_storm_of_a_thousand_reaches_each_route_as_digests_and_nothing_is_lost` |
| 5 | The claim store passes the same concurrency suite on SQL Server | moves with SQL Server (p4-d4) |

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| Providers behave differently from their fakes | Fakes follow each provider's documented errors; live smoke tests when credentials exist (p4-d1). |
| Callbacks forged | Each provider's signature check, the integration's secret, `provider_events` dedupe, rate limits on the endpoint. |
| Failover sends twice | Only after definite provider failures or idempotent providers; never after an unknown outcome on a non-idempotent one (D8). |
| Chat identity linked to the wrong person | Linking only by a one-time code the person's own chat account sends (I41). |
