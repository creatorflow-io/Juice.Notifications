# Juice.Notifications: design

**Status:** revised draft, 10 November 2026. Revised after review (`feedback.md`) and the owner's decisions; §18 shows where each review item is
answered, §19 records the owner's answers. Phase 1 is being built (`phase-1-plan.md`). In the decisions table, *Owner* rows are the owner's. *Review* rows come from the
review and are accepted. *Mine* rows are proposals nobody has objected to; they stand unless changed.

**In one line:** a multi-tenant service that any Juice product calls to say "this happened". The service works out who should hear about it, on which
channels, in what words and when, then sends it, retries it and records what happened to each delivery.

**Where it comes from:** Juice.Observability built a solid alert-notification pipeline. It has an outbox, idempotent deliveries, claim-based dispatch,
signed webhooks, SSRF protection, Slack, Teams and email, grouping, silences and escalation. But all of it assumes an alert. The first non-alert source,
Phase 8 action requests, needed nullable foreign keys and a second routing mechanism. Notifications are their own product, and every Juice service
needs them.

---

## 1. Goals and non-goals

**Goals**

- A shared notification platform for Juice products. Each tenant has one inbox, one set of preferences and contact points per person, and one
  delivery history.
- Producers state *what happened*. Recipients, destinations, channels, wording, timing, preferences, retries and escalation belong to this service.
- Enterprise channels: email, SMS, Slack, Microsoft Teams, signed webhooks, push (mobile and browser) and an in-app inbox. Channels and providers
  are both pluggable: a new channel is a package, not a change to the core (D24, §9).
- People-aware: recipients resolved through pluggable resolvers (identity, local groups, topics, later on-call and external directories), contact
  points, preferences, quiet hours, and an inbox with display groups (three by default, customizable per tenant).
- Workflows per notification type: digest, delay, throttle and escalate-until-acknowledged. Interactions go back to the producer.
- Guarantees written down per hop (§10), with every attempt recorded and failed deliveries replayable.

**Non-goals**

- Not a marketing or campaign tool.
- Not a user directory, and not tied to one. People come from **recipient resolvers** (D20, §6.1); this service keeps subscribers' contact points,
  preferences and inbox only.
- Not an on-call system. On-call is a future recipient resolver backed by an external tool (D21).
- Not the place for domain decisions. Whether an alert is silenced, or whether a request needs approval, is decided by the product that owns it.
- Not a general workflow engine. Notification workflows are a fixed, small set of steps (§7).
- Not exactly-once delivery to external systems. No sender can promise that (§10).

---

## 2. Decisions

| # | Decision | By |
|---|---|---|
| D1 | **Observability moves to Juice.Notifications only after phases 1–4 are complete and accepted** (phase 5). Until then it keeps its own pipeline. | Owner, 9 Nov |
| D2 | **Centralized hosting is the default.** `Juice.Notifications.App` serves every product of a deployment. The core stays host-agnostic, so a product can **embed** it where it must not depend on another service. An embedded instance offers shared destinations only (§4.3). | Owner, 9 Nov |
| D3 | **Observability embeds** for its alert path and mirrors to a central instance when one exists (§4.3). Its alerts must not depend on another service being up. | Owner, 9 Nov |
| D4 | **Producers send everything templates need.** A request carries `data` validated against the pinned type version's schema. The service never calls back into a producer to render. | Mine |
| D5 | **Idempotency and correlation are scoped to tenant and application:** `UNIQUE (TenantId, ApplicationId, IdempotencyKey)` and `UNIQUE (TenantId, ApplicationId, CorrelationKey)`. There is no correlation across applications until a real case needs one. That would come with an explicit scope and its own authorization. | Review |
| D6 | **A delivery is one send of one destination on one channel.** It links to the one or more notification recipients it carries (more than one only for a digest). One recipient reaches one destination on one channel at most once (§3, §11). | Review, refined |
| D7 | **A shared destination needs no subscriber.** Routes and integrations name Slack channels, Teams webhooks and webhook URLs directly. | Review |
| D8 | **A provider timeout is an *unknown* outcome, never a success.** What happens next depends on the channel (§10.3). Failover happens only for provider failures, never for destination or content failures. | Review, refined |
| D9 | **Mandatory is a policy separate from priority.** Mandatory types get the behavior in §6.4: they bypass category opt-out and quiet hours, respect a channel the person turned off, and always reach the inbox. Priority affects ordering and channel choice only. | Review; §6.4 confirmed by Owner, 9 Nov |
| D10 | **Versions are pinned.** A request stores the type version and schema hash it was accepted under. A delivery stores the template version, locale and renderer version that produced its content. | Review |
| D11 | **Workers claim rows with a version column as the fencing token.** Every result is written conditionally on the claimed version, so a stale worker's write affects nothing. Each database has its own claim store (§11). | Review, refined |
| D12 | **Display groups are a per-tenant catalog, seeded with `approval`, `system` and `general`.** They're separate from `Category`. A type declares its default group (falling back to `general` if the tenant lacks it). Tenant admins can add, rename and reorder groups, and move a type to another group. Applications can't create groups. The three defaults can be renamed but not deleted. A group's behavior never depends on its name: actionable, correlation-following items come from the **type** (§7.3). | Owner, 9 Nov |
| D13 | **Domain suppression stays with the producer.** Observability silences and maintenance windows stay in Observability. This service offers only generic mutes: user preferences, quiet hours and tenant mutes. | Mine |
| D14 | **Webhook signing keeps the Observability contract**: `webhook-id`, `webhook-timestamp`, and `webhook-signature: v1,{base64 HMAC-SHA256 of id.timestamp.body}`. Receivers must deduplicate on `webhook-id` (§10.4). | Mine |
| D15 | **Secrets are references, never values** (`env:`, `file:`, later `vault:`/`keyvault:`), read at send time. Chat webhook URLs count as secrets. | Mine |
| D16 | **The SSRF contract (§13.1) is ported from Observability with its tests**, not designed again. | Mine |
| D17 | **PostgreSQL only until phase 4.** A SQL Server claim store and migrations come in phase 4, with their own concurrency tests. | Mine |
| D18 | **Workflow conditions are matchers, not expressions.** They test correlation state and labels, nothing else (§7.2). Templates run in a configured Scriban sandbox (§8.2). | Review, refined |
| D19 | **A digest delivery is a delivery with several items.** There is no "digested" status, and no notification is sent twice through a digest. | Mine |
| D20 | **Recipients come from pluggable resolvers (`IRecipientResolver`), not from a dependency on one identity service.** Built in: route, static address, local, topic, and identity (phase 2). Later: external directory and on-call. A tenant configures which resolvers it uses (§6.1). | Owner, 9 Nov |
| D21 | **No on-call in the notification system now.** Escalation steps name groups, people or routes directly. On-call comes later as an optional `OnCallRecipientResolver` backed by an external tool (PagerDuty, Jira Service Management). PagerDuty and Jira Service Management are not channels either. | Owner, 9 Nov |
| D22 | **Templates are Scriban text, edited as text through phase 4; a WYSIWYG editor may come later.** Each template records its `Engine` (`scriban` only for now). A future WYSIWYG editor saves a block model that compiles to Scriban, so the renderer, sandbox and version pinning stay one engine. | Owner, 9 Nov |
| D23 | **A subscriber's identity is `(Source, ExternalId)`.** People from different resolvers are one subscriber only through explicit links (`subscriber_aliases`), made by an admin or a sync, never by matching email addresses. Addresses from a tenant-configured resolver count as verified by their source; addresses a person adds must be verified. | Mine |
| D24 | **Channels come from a registry, not a fixed list.** Each channel is a *channel definition*: key, the address kind it needs, whether it reaches people, shared targets or both, its rendered shape and limits, escaping, and its unknown-outcome policy. The core validates types, templates, contact points and integrations against the registered definitions. A new channel (WhatsApp, Telegram, Zalo, Discord, voice) is one package: definition, renderer and provider (§9.1). | Mine; accepted by Owner, 10 Nov |
| D25 | **Push is one channel over device registrations, with FCM, APNs and Web Push (VAPID) as providers.** A person has many registrations (one per app install or browser), each with its own platform and token. They're registered by the client app, not verified like an email address, and removed when the push service reports them gone. Registrations come in phase 2 with subscribers; the providers in phase 4 (§9.2). | Mine; accepted by Owner, 10 Nov |

---

## 3. Concepts

```text
Application ─── NotificationType (versioned: schema, category, display group, mandatory, templates, workflow)

Request (immutable input, pinned to a type version)
  └── Notification (processing state for the request; one per request)
        └── NotificationRecipient (a person, a route match or an external address; carries its escalation step)
              └── DeliveryItem ──► Delivery (one destination, one channel, one integration, rendered content)
                                      └── DeliveryAttempt (one execution through a provider)
```

| Concept | Meaning |
|---|---|
| **Tenant** | The data and authorization boundary. Everything below is tenant-owned except the application and type catalog. |
| **Application** | A registered producer, such as `observability` or `workflow`. It owns a namespace of types (`observability.*`) and serves one or more tenants. |
| **Notification type** | Key, version, category, **display group**, schema, priority, mandatory flag, templates and workflow. Declared by its application. |
| **Category** | A grouping for subscription, preferences, filtering and analytics (`alerts`, `security`, `payments`). Applications define their own. |
| **Display group** | Where the inbox shows it: one group from the tenant's catalog, `approval`, `system` and `general` by default (D12). |
| **Request** | One "this happened" from a producer, stored exactly as accepted: type and version, idempotency key, recipients, labels, data, correlation key, actor, links, expiry. |
| **Notification** | The request after planning: its workflow state and correlation. One per request. |
| **Recipient** | Who the request (or a route, or an escalation step) addresses, resolved into recipients of one of three kinds: a **subscriber** (a person), a **route** (shared destinations), or an **external** address the type allows. |
| **Recipient resolver** | Turns what a request names (a user, group, role, topic, route, address, later an on-call schedule) into subscribers and destinations (D20, §6.1). |
| **Subscriber** | A person known to the service, identified by `(Source, ExternalId)` from the resolver that first produced them, with any linked aliases (D23): locale, time zone, contact points, preferences, inbox. |
| **Contact point** | A person's verified channel endpoint: email address, phone, Slack or Teams user. The inbox needs no contact point, and push uses device registrations instead. |
| **Device registration** | One app install or browser that can receive push for a person: platform (`fcm`, `apns`, `webpush`), token or subscription, app id, last seen (D25). A person has any number. |
| **Channel definition** | What a channel is to the core: key, address kind, audience (people, shared targets), rendered shape and limits, escaping, unknown-outcome policy. Registered by a package (D24). |
| **Integration** | A tenant's configured provider connection for one channel: SMTP, SES, a Slack app, a Teams webhook, a webhook URL. |
| **Integration target** | A shared destination reached through an integration: a Slack channel, a Teams webhook, a webhook URL. It belongs to no person (D7). |
| **Destination** | The actual target a delivery addresses: a contact point, an integration target, an external address, or a subscriber's inbox. Stored on the delivery as a canonical **destination key** plus a snapshot of the address. |
| **Delivery** | One send to one destination on one channel through one integration, with its rendered content. Its identity is its id, and it stays the same across retries and failover. |
| **Delivery item** | Links a delivery to a recipient it carries. One item normally; several for a digest. `UNIQUE (RecipientId, DestinationKey, Channel)` stops a recipient being sent anything twice. |
| **Delivery attempt** | One execution through a provider, with its own id, integration, outcome and timings. |
| **Correlation** | The lifecycle of a correlation key: `Open`, `Acknowledged`, `Resolved`, `Cancelled`. Escalation, cancellation and inbox state follow it. |
| **Interaction** | Something a recipient did: read, clicked, acknowledged, approved or rejected through a button. Recorded and published to the producer. |

**Destination keys** are canonical strings, so uniqueness doesn't depend on which table a destination lives in:

| Destination | Key |
|---|---|
| Contact point | `cp:{contactPointId}` |
| Device registration | `dev:{registrationId}` |
| Integration target | `it:{integrationTargetId}` |
| External address | `ext:{channel}:{normalized address}` (email lowercased, phone in E.164) |
| Inbox | `inbox:{subscriberId}` |

---

## 4. Architecture

### 4.1 Processing stages

```text
Producers ──HTTP / Juice.EventBus──►
 1. Intake          authenticate application · pick type version · validate schema · idempotency
                    · store Request + its Notification (due now) in one transaction · 202
 2. Planner         resolve recipients through IRecipientResolver (routes, addresses, users, groups, topics)
                    · mutes · preferences · mandatory policy · Notification + NotificationRecipients
 3. Workflow        per notification: delay · digest window · throttle · condition · escalation
                    · correlation lifecycle · inbox item state
 4. Delivery planner per due recipient: choose destinations and channels · integration · template
                    + locale · render and store content · Delivery + DeliveryItems
 5. Dispatcher      claim due deliveries · provider call · classify outcome · retry / failover
                    · DeliveryAttempt · status callbacks
Callbacks           provider receipts · Slack/Teams actions · signed email links · inbox actions
                    → Interaction → correlation + events to producers
```

Each stage reads rows the previous stage committed, and writes its own results in its own transaction. Only the dispatcher talks to the outside world.
Stages 2–4 touch only the database, so a crash leaves either nothing or a complete step (§11).

### 4.2 Hosting

| Mode | Host | Database | Intake | Features |
|---|---|---|---|---|
| **Centralized** (default, D2) | `Juice.Notifications.App` | its own | HTTP and Juice.EventBus | everything |
| **Embedded** | a product's host (`services.AddJuiceNotifications(...)`) | a `notifications` schema in the product's database | in-process `INotificationClient`, in the product's transaction | route and external recipients only: integrations, routes, mutes, workflow, history. No subscribers, preferences or inbox. |

Producers code against `Juice.Notifications.Abstractions` (`INotificationClient`) in both modes. **Moving between modes is not just configuration:**
the client changes by configuration, but data moves through the migration in §4.4.

### 4.3 Who owns what

| Data | Centralized | Embedded |
|---|---|---|
| Types (for the embedding product) | central | embedded |
| Integrations, integration targets, routes, mutes, escalation policies for the product's types | central | embedded |
| Requests, notifications, deliveries, attempts, interactions for the product's types | central | embedded |
| Subscribers, contact points, preferences, inbox | central | **never**; mirrored to central when one exists |
| Destination allowlist | central | embedded |

**Mirroring** (phase 5): an embedded product with a central instance available also publishes each request to central, through its outbox, with the
same idempotency key. Central handles only the **subscriber** recipients (inbox, personal channels). The embedded instance handles routes and
externals. Because the two sets don't overlap, nothing is delivered twice. If central is down, mirrored requests wait in the outbox. The embedded
alert path doesn't wait for them.

### 4.4 Embedded to centralized migration

1. **Configuration:** integrations, targets, routes, mutes and policies are exported as declarative configuration (`ManagedBy = config`) and loaded
   into central. Secrets stay references, so they have to be resolvable on the central host too.
2. **Drain:** embedded intake stops accepting, and its due work (workflow steps, deliveries) runs until empty or is cancelled.
3. **History:** an idempotent export/import copies requests, notifications, deliveries, attempts and interactions with their original ids (UUIDv7,
   globally unique) under the same tenant and application. A re-run skips rows it has already copied.
4. **Idempotency window:** imported requests keep their idempotency keys, so a producer retry after cutover is still recognized.
5. **Switch:** the product's `INotificationClient` configuration points at central; then the embedded schema is dropped.

---

## 5. Producer API

### 5.1 Declaring types

`PUT /api/v1/applications/{app}/types`, idempotent. A declaration with a new schema or templates becomes a **new version**. Older versions stay, so
requests accepted under them can still be validated, rendered and replayed (D10).

```json
{
  "key": "observability.alert.firing",
  "name": "Alert firing",
  "category": "alerts",
  "displayGroup": "system",
  "priority": "high",
  "mandatory": false,
  "externalRecipients": false,
  "schema": { "$ref": "schemas/alert.json" },
  "correlation": "required",
  "redact": ["data.subject"],
  "workflow": [
    { "step": "digest", "windowSeconds": 30, "by": "route" },
    { "step": "send" },
    { "step": "escalate", "policyFrom": "labels.escalationPolicy" }
  ],
  "templates": { "email": "…", "slack": "…", "teams": "…", "webhook": "passthrough", "inbox": "…" }
}
```

### 5.2 Sending

`POST /api/v1/tenants/{tenant}/notifications`, or the integration event `NotificationRequested` with the same body.

```json
{
  "type": "observability.alert.firing",
  "typeVersion": 3,
  "idempotencyKey": "transition:0193f0c2-…",
  "correlationKey": "alert:0193f0b9-…",
  "recipients": [
    { "kind": "route" },
    { "kind": "group", "id": "sre" },
    { "kind": "topic", "key": "observability.service:billing" }
  ],
  "actor": { "kind": "system", "id": "rule-evaluator" },
  "priority": "critical",
  "labels": { "rule": "http-5xx", "service": "shop/billing", "environment": "prod", "severity": "critical" },
  "data": { "…": "validated against version 3's schema" },
  "links": { "self": "https://console…/alerts/0193f0b9-…" },
  "expiresAt": "2026-11-09T12:00:00Z"
}
```

- Each recipient's `kind` selects a resolver (§6.1). `"resolver": "<key>"` picks a specific one when the tenant has several for the same kind,
  for example two directories.
- `typeVersion` is optional. Without it, the latest version is used, and the request records which one (D10).
- **Labels** are flat strings that routes, mutes, throttles, digests and conditions match on. **Data** is for templates only.
- `{ "kind": "route" }` means "whoever the tenant's routes say".
- `expiresAt`: no delivery starts after it; deliveries still due are `Expired`.
- Responses:
  - `202 { id, status: "accepted" }`;
  - a duplicate gets `200` with the original;
  - an invalid schema gets `422`, with the validation errors.

### 5.3 Lifecycle by correlation

The path is `/api/v1/tenants/{tenant}/correlations/{key}`, always under the caller's application (D5). Another application's key is reported as not
found.

| Call | Effect |
|---|---|
| `POST …/acknowledge` `{ "by" }` | Sets `Acknowledged`, stops escalations that stop on acknowledgement, and updates inbox items. |
| `POST …/resolve` `{ "outcome"? }` | Sets `Resolved`, cancels every recipient and delivery not yet started, and updates inbox items (an Approval item shows the outcome, e.g. "approved by adam"). |
| `POST …/cancel` | Like resolve, with no outcome shown. |
| `GET …` | Notifications, recipients, deliveries and interactions for the key. |

Each call is also an event (`NotificationCorrelationAcknowledged`, …) a producer can publish from its outbox.

### 5.4 Events to producers

`notification.interaction` (read, acknowledged, action with its id), `notification.delivery.failed` (every destination of a recipient failed) and
`notification.escalation.exhausted`. Each carries the event id, application, tenant and correlation key. Delivery is at least once, so producers
deduplicate on the event id.

---

## 6. Recipients, destinations, routes and preferences

### 6.1 Resolving recipients (planner)

The service doesn't depend on any one identity system (D20). What a request names is resolved through resolvers:

```csharp
public interface IRecipientResolver
{
    string Key { get; }                        // "identity", "local", "topic", "route", "address", later "oncall-pagerduty", "directory-ldap"
    IReadOnlySet<string> Kinds { get; }        // what it resolves: "user", "group", "role", "topic", "route", "address", "schedule"
    ValueTask<ResolveResult> ResolveAsync(RecipientSpec spec, ResolveContext context, CancellationToken ct);
}

/// <summary>People (by source and external id, with the contact points the source vouches for) and shared or external destinations.</summary>
public sealed record ResolveResult(IReadOnlyList<ResolvedPerson> People, IReadOnlyList<ResolvedDestination> Destinations, bool Truncated);
```

| Resolver | Resolves | Phase |
|---|---|---|
| `RouteRecipientResolver` | `route`: the tenant's matching routes, as route recipients | 1 |
| `StaticAddressResolver` | `address`: external addresses, only for types with `externalRecipients: true` | 1 |
| `LocalRecipientResolver` | `user`, `group`: subscribers and groups kept in this service, for tenants or embedded hosts with no identity service | 2 |
| `TopicRecipientResolver` | `topic`: a topic's subscribers | 2 |
| `IdentityRecipientResolver` | `user`, `group`, `role`: from the Juice identity/tenant service | 2 |
| `ExternalDirectoryResolver` | `user`, `group`: from a customer directory (LDAP, SCIM, Entra ID) | later |
| `OnCallRecipientResolver` | `schedule`: whoever is on call now, from PagerDuty or Jira Service Management (D21) | later |

Each tenant configures which resolvers it uses (`recipient_resolvers`) and which one handles each kind.

**Steps:**

1. Resolve each named recipient with its resolver. Then add one **route recipient** per matching route. A route matches on type pattern, label
   matchers (`=`, `!=`, `=~`, `in`) and minimum priority. Routes add recipients; they never remove ones the request named.
2. Map each resolved person to a subscriber by `(Source, ExternalId)` or a linked alias, creating the subscriber the first time (D23). Contact points
   the source vouches for are stored as verified, marked with that source, and refreshed on each resolution.
3. Deduplicate: a person named twice (directly and through a group, or through two linked sources) is one recipient.
4. Apply tenant **mutes** and then **preferences** per §6.4. A muted or opted-out recipient is kept, with its outcome (`Muted`, `OptedOut`), so
   "why didn't I get it?" can be answered.

**Rules:**

- **When:** at planning time. The result is stored on the recipients (resolver, what was named) for audit. Escalation steps resolve when the step
  runs, so a future on-call resolver returns whoever is on call then.
- **Bounds:** one named recipient expands to at most 10,000 people (a tenant setting). Beyond that the result is truncated, and the recipient is
  recorded as `Truncated` and audited.
- **Failure:** a resolver that's unavailable is retried with backoff, with planning waiting for it, for up to 5 minutes (a setting). After that,
  planning continues with what did resolve. The failed recipient is recorded as `ResolverUnavailable`, and `notification.recipient.unresolved` is
  published to the producer. A failure is never silent.
- **Caching:** group and role expansions may be cached for up to 5 minutes per tenant. On-call results are never cached.

### 6.2 Choosing destinations (delivery planner)

| Recipient | Destinations |
|---|---|
| Subscriber | the inbox, plus each type channel the person has a verified contact point for and hasn't turned off, in the person's preferred order; for push, each active device registration (one delivery per device) |
| Route | the route's integration targets |
| External | the address on its channel, through the tenant's default integration for that channel |

### 6.3 Route example

```json
{
  "key": "prod-critical-to-ops",
  "match": { "type": "observability.alert.*", "labels": { "environment": "prod", "severity": "in(high,critical)" } },
  "targets": [ "slack-ops", "pagerduty-sre" ],
  "digestSeconds": 30,
  "enabled": true
}
```

### 6.4 Mandatory policy (D9)

| Rule | Normal type | Mandatory type |
|---|---|---|
| Category opt-out | respected | **bypassed** |
| Quiet hours | respected (deferred until they end) | **bypassed** |
| Personal mute or snooze of the type | respected | **bypassed**, and recorded on the recipient |
| A channel turned off by the person | respected | **respected**, but the inbox can't be turned off for mandatory types, so the person always gets at least the inbox |
| Unverified contact point | blocked | **blocked** |
| Channel the type doesn't declare | blocked | **blocked** |
| Tenant mute | respected | respected only if the mute names the type explicitly (no wildcards); the Console flags it |
| Throttle and digest | applied | applied: mandatory decides *whether* someone hears, not how often |

Priority (`low` … `critical`) is separate. It orders the dispatcher queue and can select channels (for example, SMS for `critical` only, by tenant
setting). A critical type isn't automatically mandatory, and a mandatory type needn't be critical.

---

## 7. Workflows

### 7.1 Steps

A type's workflow runs per notification. Its state (current step, next due time, version) is kept on the notification, and on the recipient for steps
that wait per person.

| Step | Does |
|---|---|
| `send` | Marks the notification's current recipients due for the delivery planner. |
| `delay` | Waits a fixed time. |
| `digest` | Holds recipients that share a `by` key (route, subscriber or correlation) for a window. When it closes, the delivery planner makes **one delivery per destination with one item per recipient** (D19), capped at 100 items listed as "and N more". |
| `throttle` | At most N per window per key. The excess waits for the next window, so nothing is silently lost. |
| `condition` | Continues only if a matcher holds (§7.2). Otherwise the notification ends as `ConditionNotMet`. |
| `escalate` | Runs an escalation policy: "after N minutes, if the correlation is still `Open`, add these recipients", with the step number on each new recipient. Ends on resolve or cancel, and on acknowledge if the policy stops on acknowledgement. A step that reaches nobody is audited and the next one runs at once. |

### 7.2 Conditions (D18)

There is no general expression language. A condition is one of:

- `correlation in (Open, Acknowledged, …)`;
- a label matcher with the same grammar as routes;
- `all` or `any` of those, nested at most 3 deep.

It can read only labels and correlation state, never `data`. A label that's missing doesn't match (except `!=`). Conditions are validated when the type
is declared, so nothing is evaluated that wasn't checked first.

### 7.3 Correlation and the inbox

An inbox item belongs to a delivery to `inbox:{subscriberId}`. It is shown in the group the tenant maps its type to (D12), and it shows its
correlation's state. The behavior comes from the **type**, never from the group's name, so it works in a renamed or custom group:

- A type that declares **actions** (Approve and Reject, with ids) makes **actionable** items. While the correlation is `Open`, the item shows "waiting
  for you" with its actions. Once the correlation is resolved, every recipient's item shows the outcome, wherever the decision was made.
- **Inbox actions** become interactions sent to the producer. The producer decides, then resolves the correlation. The inbox never decides anything
  itself.
- The inbox can filter on "actionable and open" across groups. That's what a default `approval` group shows. A tenant can still move action types
  elsewhere.

---

## 8. Templates and rendering

### 8.1 Model

- One template per type version, channel and locale. A tenant can override any of them for its own tenant. Lookup order: tenant override, then the
  application's default, then the `en` locale.
- Each template records its `Engine` (D22). Only `scriban` exists now. Templates are edited as text, with preview, through phase 4. A later WYSIWYG
  editor would save a block model (`Source`) and compile it to Scriban (`Body`). The renderer only ever runs the compiled Scriban, so the sandbox
  (§8.2) and version pinning stay the same.
- Each channel's output shape comes from its channel definition (D24, §9.1). The built-in ones:
  - email: subject, HTML and text;
  - Slack: Block Kit JSON;
  - Teams: an Adaptive Card;
  - SMS: 160 characters, truncated with a link;
  - push: title, body, an action URL, an optional image, and a small `data` map for the app; the provider maps it to FCM, APNs or Web Push (§9.2);
  - inbox: title, body, an action URL and actions.
- The webhook channel defaults to `passthrough`: the producer's `data` inside a fixed envelope (`id`, `type`, `typeVersion`, `occurredAt`, `tenant`,
  `correlationKey`, `data`, `links`).
- Text is escaped per channel (Slack mrkdwn, Teams markdown, HTML email), carried over from Observability's `ChannelFormatter`. Escaping belongs to
  the channel definition, so a new channel brings its own.
- **Rendered once, before the first attempt** (by the delivery planner), and stored on the delivery with the template version, locale and renderer
  version (D10). Retries and failover send the same bytes, so signatures stay valid.

### 8.2 Scriban sandbox (D18)

- No template loader, so no `include` or `import`. Only the built-ins on an explicit allowlist are available; nothing that touches dates from the host
  clock or the environment.
- The model is read-only: `data`, `labels`, `links`, recipient name and locale, digest items.
- Limits: `LoopLimit` 1,000, `RecursiveLimit` 20, template size 64 KB, output size 256 KB (email) or the channel's own limit, and render time 200 ms
  (cancellation).
- A missing field renders empty, never an error.
- Templates are parsed and checked when declared or overridden. A template that fails at render time fails that delivery as `PermanentFailure`
  (`render_failed`), without failover.

---

## 9. Channels and providers

| Channel | Providers (phase) | Notes |
|---|---|---|
| Webhook | built-in (1) | Signed (D14), SSRF contract (§13.1) |
| Email | SMTP (1); SES, SendGrid (4) | Bounce and complaint callbacks mark a contact point unhealthy |
| In-app inbox | built-in (2) | API plus SignalR; `@juice-js/notifications` for Angular |
| Slack | incoming webhook, Slack app (4) | The app adds per-user DMs and interactive buttons |
| Teams | workflow webhook, bot (4) | |
| SMS / voice | Twilio (4) | Enabled per tenant, by priority |
| Push | FCM, APNs, Web Push (4); device registrations (2) | One channel for mobile and browser; see §9.2 |
| Others | (later) WhatsApp, Telegram, Zalo, Discord, voice | Not scheduled. Each would be a channel package (§9.1) |

### 9.1 Channel registry (D24)

The core knows no channel by name except `inbox`, which it hosts. Everything else is registered:

```csharp
public sealed record ChannelDefinition(
    string Key,                    // "email", "push", "whatsapp": lowercase, unique
    AddressKind Address,           // Email, Phone (E.164), Url, ChatUser, Device, None
    ChannelAudience Audience,      // People, SharedTargets, or both
    RenderShape Shape,             // the fields a rendered delivery has (subject/html/text, title/body/data, card JSON…)
    ChannelLimits Limits,          // output size, field lengths, truncation rule
    IChannelEscaper Escaper,       // how template values are escaped
    UnknownOutcomePolicy OnUnknown // §10.3: RetrySameKey, RetryOnceIfMandatory, Fail
);

public interface IChannelRegistry
{
    ChannelDefinition? Find(string key);
    IReadOnlyCollection<ChannelDefinition> All { get; }
}
```

- A channel package registers its definition, its renderer and one or more providers (`services.AddNotificationChannel<…>()`).
- The core validates against the registry: a type declaring templates for an unregistered channel is refused; so are an integration, contact point
  or route target on one. External addresses are normalized by the channel's `AddressKind`.
- A channel that is registered but has no enabled integration for a tenant simply produces no deliveries there, and the planner records why.
- Phase 1 registers `webhook` and `email` this way, so the registry is proven before any third channel exists.

### 9.2 Push (D25)

- **One channel, three platforms.** `push` has providers `fcm` (Android, and iOS or web through Firebase where a tenant uses it), `apns` (iOS and
  macOS directly) and `webpush` (browsers, VAPID per RFC 8030/8291, no Firebase needed). Each device registration names its platform, and the
  delivery planner picks the tenant's integration for that platform.
- **Device registrations** (phase 2): `POST /api/v1/me/devices` from the client app with the platform, token or Web Push subscription
  (endpoint, `p256dh`, `auth`), app id and locale; `DELETE` on sign-out. Re-registering the same token moves it to the current person, so a shared
  device never pushes to its previous user. Registrations unseen for 60 days (a setting) are dropped.
- **Not verified like an address.** A registration proves itself by being created from an authenticated session of that person. The "unverified
  contact point" rule (§6.4) doesn't apply to it.
- **One delivery per device.** Destination key `dev:{registrationId}`. Preferences turn push on or off per person, not per device.
- **Gone tokens.** FCM `UNREGISTERED`, APNs 410, Web Push 404/410 are a `Destination` failure: the registration is deleted, with no retry and no
  failover.
- **Payload.** Visible notifications by default (title, body, action URL); `data`-only on request for apps that render themselves. TTL is the
  request's expiry, capped by the platform. The delivery id is the collapse key (`apns-collapse-id`, FCM `collapse_key`, Web Push `Topic`), so a
  retried push replaces itself on the device. Badge counts come from the person's unread inbox items.
- **Web Push in the Console.** `@juice-js/notifications` gets a small service worker helper that subscribes with the tenant's VAPID public key and
  registers the device, so the Angular apps get browser notifications without Firebase.

### 9.3 Provider contract

In `Juice.Notifications.Abstractions`:

```csharp
public interface INotificationProvider
{
    string Channel { get; }
    string Kind { get; }
    /// <summary>Whether the provider accepts our delivery id as an idempotency key, which makes retrying after an unknown outcome safe.</summary>
    bool Idempotent { get; }
    ValueTask<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken ct);
}

public sealed record SendResult(AttemptOutcome Outcome, FailureCategory? Category, string? ProviderMessageId, int? StatusCode, string? Error);

public enum AttemptOutcome { Delivered, Accepted, RetryableFailure, PermanentFailure, Unknown }

public enum FailureCategory
{
    Provider,     // provider down, 5xx, auth or quota of our integration: another integration may work
    Destination,  // invalid address, unknown channel, 404/410 from a webhook: no integration will help
    Content,      // rejected payload, too large, render failed: no integration will help
    Policy,       // blocked by the destination policy (SSRF) or the allowlist
    RateLimited,  // 429 or provider throttle: retry later on the same integration
}
```

---

## 10. Delivery guarantees

### 10.1 Per hop

| Hop | Guarantee | How |
|---|---|---|
| Producer → intake | at least once; **effectively once** as logical work | Producer outbox; `UNIQUE (TenantId, ApplicationId, IdempotencyKey)`. Embedded: in the producer's own transaction. |
| Intake → planner | effectively once | The request and its notification (due now) commit together, and `Notification` is unique on `RequestId`. The planner claims it like any due row (§11). |
| Planner → workflow → delivery planner | effectively once | DB-only steps, conditional on version (§11); `UNIQUE (RecipientId, DestinationKey, Channel)` on delivery items. |
| Delivery → provider | **at least once**; duplicates possible after an unknown outcome | Stable delivery id, passed as the idempotency key where the provider supports it (§10.3). |
| Provider callbacks → us | at least once, deduplicated | `UNIQUE (Provider, ProviderEventId)`. |
| Us → producers (interactions, failures) | at least once | Outbox; event ids for deduplication. |

**Exactly once to an external system is not promised.** Where the receiver deduplicates on the delivery id, duplicates have no effect.

### 10.2 States

**Delivery**

```text
Pending ──claim──► Sending ──► Delivered                     (final success the channel can report)
                      ├──────► Accepted ──callback──► Delivered | Bounced
                      ├──────► Retrying ──due──► Sending      (retryable, or unknown where retry is safe)
                      ├──────► Failed {category}              (permanent, attempts exhausted, or unknown where retry is unsafe)
Pending | Retrying ──► Expired (request expiry) | Cancelled (correlation resolved or cancelled, admin)
```

- `Accepted` means the provider took it: SMTP 250, an SES message id, a push service's acceptance. It is **not** proof the person received it.
  `Delivered` from a callback is the strongest evidence a channel gives. For webhooks, a 2xx is `Delivered`, since the receiver is the final party.
- `Read` and `Acknowledged` are interactions, not delivery states.

**Attempt:** `Delivered`, `Accepted`, `RetryableFailure`, `PermanentFailure` or `Unknown`, with the category, integration, status code, error and
duration. Every attempt has its own id. The delivery id never changes.

### 10.3 Unknown outcomes and failover (D8)

An outcome is **unknown** when the request may have reached the provider but no answer came back: a timeout after the request was sent, a connection
reset mid-response, or SMTP dropping after `DATA`.

| Channel | After an unknown outcome |
|---|---|
| Webhook | Retry the same integration with the same `webhook-id`. Receivers must deduplicate (§10.4). |
| Provider with `Idempotent = true` (SES, SendGrid, Twilio with idempotency) | Retry with the delivery id as the key. |
| Push (FCM, APNs, Web Push) | Retry with the delivery id as the collapse key: a duplicate replaces the first on the device (§9.2). |
| Non-idempotent provider (plain SMTP, Slack and Teams webhooks) | Mandatory types: retry once (a duplicate is better than silence). Others: `Failed {Provider}` with `unknown_outcome`, no failover; visible and replayable. |

Failover to the tenant's next integration for the same channel happens **only** for `Provider` failures, or when attempts are exhausted on retryable
provider failures. It never happens for `Destination`, `Content` or `Policy` failures, and never after an unknown outcome on a non-idempotent provider.
A `RateLimited` failure waits and retries on the same integration.

Retry: exponential backoff with jitter, 10 s base, 10 min cap, 8 attempts by default, configurable per channel.

### 10.4 Webhook receiver contract

Receivers:

- verify `webhook-signature` against `{webhook-id}.{webhook-timestamp}.{body}` in constant time;
- reject a timestamp outside 5 minutes;
- **deduplicate on `webhook-id`** for at least 24 hours, which covers the retry window;
- answer 2xx once the message is stored, not after processing it.

This is published with sample verifiers (C#, Node, Python), as Observability's is today.

---

## 11. Concurrency (D11)

Every row a worker processes (notifications, recipients and deliveries due) has `Version`, `ClaimedBy`, `LeaseUntil` and `NextAt`.

1. **Claim:** a batch of due rows is claimed atomically:
   - PostgreSQL: `UPDATE … SET ClaimedBy = @me, LeaseUntil = now() + @lease, Version = Version + 1 WHERE Id IN (SELECT Id … WHERE NextAt <= now() AND (LeaseUntil IS NULL OR LeaseUntil < now()) ORDER BY NextAt LIMIT @n FOR UPDATE SKIP LOCKED) RETURNING Id, Version`.
   - SQL Server (phase 4): `UPDATE TOP (@n) … WITH (UPDLOCK, READPAST, ROWLOCK) … OUTPUT inserted.Id, inserted.Version`.
2. **Execute** one step.
3. **Persist** the result with `WHERE Id = @id AND Version = @claimedVersion`, setting `Version + 1` and clearing the lease. If no row was affected, the
   worker lost its claim to lease expiry. It discards its result. For the dispatcher, the attempt is still recorded, since the outside world saw it.
4. **Crash:** the lease expires and the row is due again.

**Re-running a step is safe:**
- Planner and workflow steps write only database rows, in the same transaction as the state change. Unique keys reject a second set.
- The dispatcher's only outside effect is the send. Re-sending after a crash is exactly the unknown-outcome case in §10.3, handled the same way.

Claims sit behind `IClaimStore`, with one implementation per database and concurrency tests for each: N workers, injected crashes, lease expiry, and
stale writes.

Lease length: the dispatcher's covers connect + request timeout + 5 s; the workflow's is 30 s. Lease lengths are settings, not part of correctness:
correctness comes from the version check.

**Every worker can process any row.** There is no singleton runner. Leases can still be partitioned by tenant hash for fairness, but that's tuning.

---

## 12. Data model (initial)

Schema `notifications`. Every tenant-owned table has `TenantId`, and **foreign keys between tenant-owned tables are composite `(TenantId, Id)`**, so a
row can't reference another tenant's row whatever the application code does. EF Core query filters are a convenience, not the isolation boundary.
Workers always pass `TenantId` explicitly. Ids are UUIDv7.

| Table | Key columns and constraints |
|---|---|
| `applications` | `Key` (unique), name, credentials (hashed), `Enabled` |
| `application_tenants` | `ApplicationId`, `TenantId`: which tenants an application may send for |
| `display_groups` | `Key` (unique per tenant), `Name`, `Order`, `Icon`, `BuiltIn` (the three defaults: renamable, not deletable) |
| `type_display_groups` | `TypeKey`, `DisplayGroupKey`: a tenant admin's move of a type to another group (unique per tenant and type) |
| `notification_types` | `ApplicationId`, `Key`, `Version` (unique together), `Category`, `DisplayGroup` (default key), `Priority`, `Mandatory`, `ExternalRecipients`, `Schema`, `SchemaHash`, `Workflow`, `Redact`, `Actions` |
| `templates` | `TypeId` (a version), `Channel`, `Locale`, `TenantId` (null for the default), `Version`, `Engine` (`scriban`), `Source` (null until a WYSIWYG editor exists), `Body` |
| `recipient_resolvers` | `Key`, `Kind` (resolver implementation), `Handles` (recipient kinds), `Settings`, `SecretRef`, `Enabled`, `ManagedBy` |
| `subscribers` | `Source`, `ExternalId` (unique per tenant together), `Name`, `Locale`, `TimeZone`, quiet hours |
| `subscriber_aliases` | `SubscriberId`, `Source`, `ExternalId` (unique per tenant): another source's id for the same person, linked explicitly |
| `local_groups`, `local_group_members` | groups kept here for `LocalRecipientResolver` |
| `contact_points` | `SubscriberId`, `Channel`, `Address` (encrypted), `VerifiedAt`, `Health` |
| `device_registrations` | `SubscriberId`, `Platform` (`fcm`, `apns`, `webpush`), `AppId`, `Token` (encrypted; for Web Push the subscription JSON), `TokenHash` (**unique per tenant, platform and app**), `Locale`, `LastSeenAt` |
| `preferences` | `SubscriberId`, `Category`, `Channel`, `Mode` (on, off, digest); quiet hours on the subscriber |
| `topics`, `topic_subscriptions` | `Key`; `TopicId`, `SubscriberId` |
| `integrations` | `Key`, `Channel`, `Kind`, `Settings`, `SecretRef`, `Priority` (failover order), `Enabled`, `ManagedBy` |
| `integration_targets` | `IntegrationId`, `Key`, `Address` or `SecretRef` (Slack channel, webhook URL) |
| `routes` | `Key`, `Match`, `Targets`, `DigestSeconds`, `Enabled`, `ManagedBy` |
| `mutes` | `Match`, `StartsAt`, `EndsAt`, `Reason`, `CreatedBy`, `CancelledAt` |
| `escalation_policies` | `Key`, `Steps`, `StopOnAcknowledge` |
| `destination_allowlist` | `Destination`, `Ports`, `ManagedBy` |
| `requests` | `ApplicationId`, `IdempotencyKey` (**unique with tenant and application**), `TypeId`, `TypeVersion`, `SchemaHash`, `CorrelationKey`, `Priority`, `Labels`, `Data`, `Actor`, `Links`, `ExpiresAt`, `AcceptedAt` |
| `notifications` | `RequestId` (unique), `WorkflowStep`, `State`, `NextAt`, claim columns |
| `notification_recipients` | `NotificationId`, `Kind` (subscriber, route, external), `SubscriberId`/`RouteId`/`Address`, `Resolver`, `NamedAs` (what the request or step named, such as `group:sre`), `EscalationStep`, `Outcome` (pending, muted, opted_out, deferred, planned, cancelled, truncated, resolver_unavailable), `NextAt`, claim columns |
| `correlations` | `ApplicationId`, `Key` (**unique with tenant and application**), `State`, `AcknowledgedBy`/`At`, `ResolvedAt`, `Outcome` |
| `escalations` | `CorrelationId`, `PolicyId`, `NextStep`, `NextAt`, `State`, `StopReason`, claim columns |
| `deliveries` | `Channel`, `IntegrationId`, `DestinationKey`, `DestinationSnapshot`, `TemplateVersion`, `Locale`, `RendererVersion`, `Content`, `Status`, `FailureCategory`, `Attempts`, `NextAt`, `ProviderMessageId`, claim columns |
| `delivery_items` | `DeliveryId`, `RecipientId`, `DestinationKey`, `Channel`; **unique `(RecipientId, DestinationKey, Channel)`** |
| `delivery_attempts` | `DeliveryId`, `Number`, `IntegrationId`, `At`, `Outcome`, `Category`, `StatusCode`, `Error`, `DurationMs` |
| `provider_events` | `Provider`, `ProviderEventId` (unique together), `DeliveryId`, `Kind`, `At` |
| `interactions` | `DeliveryId`, `CorrelationId`, `Kind`, `ActionId`, `By`, `At` |
| `inbox_items` | `SubscriberId`, `DeliveryId`, `DisplayGroupKey` (resolved at delivery; re-mapped if the tenant moves the type), `Actionable`, `CorrelationId`, `ReadAt`, `ArchivedAt` |

Audit records go to `services/audit` through its messaging integration.

---

## 13. Security and compliance

### 13.1 Outbound HTTP (D16)

Ported from `Juice.Observability.Webhooks` (`DestinationPolicy`, the dispatcher's client), with its tests:

- **Always blocked**, allowlisted or not: loopback, link-local (cloud metadata), unspecified, multicast and reserved ranges.
- **Blocked unless allowlisted:** private, CGNAT (`100.64.0.0/10`) and benchmark (`198.18.0.0/15`) ranges.
- IPv4-mapped IPv6 is normalized before checking.
- **Checked at connect time**, against the addresses actually resolved (a `SocketsHttpHandler.ConnectCallback` that resolves, checks, then connects to
  the checked address). That defeats DNS rebinding.
- **Redirects disabled.** No proxy.
- Ports: 443 only, unless an allowlist entry names others. HTTPS required, except plain HTTP to allowlisted private destinations when the deployment
  enables it.
- **One HTTP client per tenant, replaced when its allowlist changes**, so a pooled connection never outlives a policy change.
- Allowlist changes are admin-only and audited. The same rules apply to every outbound call: webhooks, chat webhooks, and API providers whose host is
  configurable.

### 13.2 The rest

- **Producer authentication:** application credentials, scoped to its type namespace and the tenants in `application_tenants`. Embedded mode calls
  in-process.
- **Admin and subscriber APIs:**
  - tenant admins manage integrations, targets, routes, mutes, policies, templates and the allowlist;
  - subscribers manage their own contact points and preferences;
  - every change is audited.
- **Secrets (D15):** never in logs, API responses or the Console.
- **PII:** contact addresses are encrypted at rest. Paths named in the type's `redact` are masked in stored request data after rendering, and in
  logs.
- **Contact verification:** email and phone are verified before use. External addresses are only for types that declare them.
- **Tenant isolation:** composite foreign keys (§12), a per-tenant HTTP client (§13.1), per-tenant rate limits, and tests that a cross-tenant
  reference is rejected.
- **Retention:** 90 days for requests, deliveries and attempts, and 14 days for rendered content. Both are configurable per tenant.

---

## 14. Operability

- **Metrics** (`Juice.Notifications` meter):
  - `requests` by type and outcome;
  - `recipients` by outcome;
  - `deliveries` by channel and status;
  - `attempts` by outcome and category;
  - `unknown_outcomes` by channel;
  - `failovers`;
  - `dispatch_delay`, from accepted to first attempt, with a target p95 ≤ 30 s;
  - `provider_latency`;
  - `due_backlog` by stage;
  - `lease_lost`;
  - `escalations` by outcome.
- **Traces:** carried from the producer through each stage to the provider call.
- **Health:** readiness needs the database (and the bus if used). A provider being down shows as a degraded integration, not as an unready service.
- **Storm protection** (phase 4): above N deliveries a minute to one route, the route switches to digest-only until the rate falls, and admins are
  told once.
- **Dead letters:** failed deliveries can be queried with their attempts. `POST …/deliveries/{id}/retry` and bulk replay by filter are admin-only,
  audited, and keep the delivery id.

---

## 15. Project layout

```
services/notifications/
├── Juice.Notifications.sln
├── docs/design.md, docs/feedback.md
├── src/
│   ├── Juice.Notifications.Abstractions/        INotificationClient, INotificationProvider, contracts
│   ├── Juice.Notifications/                     intake, planner, workflow, delivery planner, dispatcher (host-agnostic)
│   ├── Juice.Notifications.EF/                  NotificationsDbContext (schema "notifications"), IClaimStore contract
│   ├── Juice.Notifications.EF.PostgreSQL/       migrations, PostgreSQL claim store
│   ├── Juice.Notifications.EF.SqlServer/        (phase 4) migrations, SQL Server claim store
│   ├── Juice.Notifications.Messaging/           event contracts and handlers (Juice.Messaging, Juice.EventBus)
│   ├── Juice.Notifications.Providers.Webhook/   signed webhook, outbound HTTP policy
│   ├── Juice.Notifications.Providers.Email/     SMTP (SES, SendGrid in phase 4)
│   ├── Juice.Notifications.Providers.Chat/      (phase 4) Slack, Teams
│   ├── Juice.Notifications.Providers.Push/      (phase 4) push channel: FCM, APNs, Web Push
│   ├── Juice.Notifications.Providers.Sms/       (phase 4) SMS and voice: Twilio
│   ├── Juice.Notifications.Channels.*/          (later) further channels, each a definition, renderer and provider (§9.1)
│   ├── Juice.Notifications.Inbox/               (phase 2) inbox API, SignalR hub
│   ├── Juice.Notifications.Resolvers.Identity/  (phase 2) IdentityRecipientResolver; route, address, local and topic resolvers live in the core
│   ├── Juice.Notifications.Resolvers.*/         (later) external directory, on-call (PagerDuty, Jira Service Management)
│   ├── Juice.Notifications.Api.Contracts/
│   ├── Juice.Notifications.Api/                 producer, admin, subscriber and callback endpoints
│   ├── Juice.Notifications.Client.Http/         INotificationClient over HTTP
│   └── Juice.Notifications.App/                 centralized host
└── test/
```

The Angular inbox library lives in `juice-layout` as `@juice-js/notifications` (phase 2).

---

## 16. Phases

Each phase ends with its tests, a mutation check on its core rules, a local-stack demo and an acceptance record, as Observability's phases do.

| Phase | Delivers | Gate |
|---|---|---|
| **1. Core** | Applications, versioned types, HTTP and event intake, schema validation, outbox/inbox, idempotency; `IRecipientResolver` with the route and static-address resolvers, integration targets, routes; channel registry (§9.1) with webhook and email registered through it; webhook and SMTP; Scriban sandbox; delivery planner with stored rendering; dispatcher with the PostgreSQL claim store, outcomes, categories, retry, unknown-outcome policy; delivery history; dead-letter retry; centralized host | The six proof points below |
| **2. People & Inbox** | Identity, local and topic resolvers; subscribers by `(Source, ExternalId)` with aliases; resolver bounds, caching and failure handling; contact points with verification; device registrations for push (§9.2); preferences, quiet hours, mandatory policy (§6.4); display group catalog with tenant customization; inbox with SignalR; `@juice-js/notifications` | A mandatory type reaches a person who opted out of its category (at least in the inbox); an unverified contact point is never used; a person reached through a group and directly gets one notification; an unavailable resolver is retried, then recorded and reported, never silent; inbox read state syncs across two sessions; a type moved to a custom group shows there, and its actions still work |
| **3. Workflow** | Digest, delay, throttle, conditions, correlation lifecycle, acknowledge/resolve/cancel, escalation policies, inbox items that follow correlation state, inbox actions, events to producers | Resolving a correlation cancels everything still due and updates every recipient's actionable item; acknowledging stops an escalation that stops on acknowledgement; a digest of N notifications is one delivery with N items, and none is sent twice |
| **4. Enterprise** | Slack and Teams (webhooks and apps with interactive actions), SMS, push through FCM, APNs and Web Push (with the Console's service worker helper), SES and SendGrid with bounces, provider failover, storm protection, text template editor (Scriban) with preview, bulk replay, PII redaction, Vault/Key Vault secrets, SQL Server | A provider outage fails over without losing a delivery or changing its id; a destination failure does not fail over; a gone push token removes its registration and is never retried; a 1,000-notification storm reaches each route as digests; the claim store passes the same concurrency suite on SQL Server |
| **5. Observability** | Embedded host, mirroring (§4.3), Observability's types, migration tool, dry run, cutover (§17) | Observability's notification acceptance tests (its phases 4–8) pass on the new pipeline; the dry run shows the same deliveries as the old pipeline over an agreed soak period |
| **Later** (not scheduled) | `OnCallRecipientResolver` (PagerDuty, Jira Service Management), `ExternalDirectoryResolver` (LDAP, SCIM, Entra ID), WYSIWYG template editor (D22), further channels as packages (WhatsApp, Telegram, Zalo, Discord, voice; D24) | Set when scheduled |

**Phase 1 proof points** (no new channel is added before these hold):

1. An accepted request is not lost when the process crashes right after `202`.
2. A duplicate request (same tenant, application and idempotency key) creates no new work.
3. Planning is safe under retries and with several workers at once.
4. A worker crash never permanently loses a claimed delivery, and a stale worker's write is discarded.
5. A provider timeout follows the unknown-outcome policy, and no automatic failover happens for destination or content failures.
6. Delivery history shows every attempt with its outcome, category and integration, and the delivery id never changes.

---

## 17. Juice.Observability (phase 5)

**Until phase 5 (D1),** Observability keeps its own pipeline, `Juice.Observability.Webhooks`.

### What maps where

| Today in Observability | Becomes |
|---|---|
| `WebhookEndpoint`: destination | an integration and an integration target |
| `WebhookEndpoint`: `Events`, `RuleKeys`, `Environments`, `MinimumSeverity` | a route (one per endpoint, migrated losslessly) |
| `WebhookEndpoint.GroupWindowSeconds` | the route's `digestSeconds` |
| `WebhookEndpoint.Recipients` (email) | external recipients on an `observability.*` type that allows them, or subscribers once mirrored |
| `WebhookAllowlistEntry` | `destination_allowlist` |
| `EscalationPolicy`, `AlertEscalation` | `escalation_policies`, `escalations` on correlation `alert:{id}` |
| `WebhookDelivery`, `WebhookAttempt` | `deliveries` + `delivery_items`, `delivery_attempts` (history copied by the migration tool) |
| `NotificationPlan.Payload` | the `data` of `observability.alert.*`, passed through to webhooks |
| `ChannelFormatter` | default Slack, Teams and email templates |
| `WebhookSigner`, `DestinationPolicy`, SSRF client | already ported in phase 1 (§13.1) |
| `Actions:NotifyEndpoints`, action-request deliveries | `observability.action.request.*` types (default display group `approval`) and routes |
| **Silences and maintenance windows** | **stay in Observability** (D13): nothing is published while silenced; the current state is published when the silence ends |
| Alert acknowledgement | stays in Observability, which calls `acknowledge(alert:{id})`; acknowledgements from Slack or the inbox come back as interactions |
| Alert resolve | Observability publishes `observability.alert.resolved` and calls `resolve(alert:{id})` |

### Types

| Type | Category | Display group |
|---|---|---|
| `observability.alert.firing`, `.resolved` | alerts | `system` |
| `observability.action.request.created` (declares Approve and Reject actions) | actions | `approval` |
| `observability.action.request.approved`, `.rejected`, `.cancelled`, `.expired` | actions | `general` |
| `observability.agent.offline`, `observability.remediation.not_verified` (later) | agents | `system` |

These are the types' default groups; a tenant can move them (D12).

### Cutover

1. Observability embeds Juice.Notifications (D3). The migration tool copies endpoints, the allowlist and policies across.
2. Both pipelines run for a soak period. The old one sends; the new one runs in **dry run** (planned and rendered, never sent). A comparison job
   reports differences per transition.
3. Switch over. `/api/v1/tenants/{id}/webhooks` stays as a thin alias onto integrations and routes for one major version.
4. Copy the history, then remove `Juice.Observability.Webhooks`.

---

## 18. Review traceability

| Review item | Answered in |
|---|---|
| P0.1 Hosting model | D2, D3, §4.2–4.4 |
| P0.2 Recipient, destination, delivery, attempt | D6, D7, §3, §6.2, §12 |
| P0.3 Guarantees and ambiguous outcomes | D8, §9.3 (contract), §10 |
| P1.1 Tenant and application scope | D5, §5.3, §12 (constraints, composite keys) |
| P1.2 Leasing and concurrency | D11, §11 |
| P1.3 Mandatory policy | D9, §6.4 |
| P1.4 Pinned versions | D10, §5.1, §8.1, §12 |
| 4.1 Display group | D12 (customizable, by owner's decision), §3, §7.3, §12, §17 |
| 4.2 Constrained expressions and template sandbox | D18, §7.2, §8.2 |
| 4.3 SSRF | D16, §13.1 |
| 4.4 Claim per database | D11, D17, §11 |
| §5 Phase 1 scope and proof points | §16 |
| §7 Decisions to record | §2; the owner's answers are in §19 |

Refinements of the review: unknown outcomes are an **attempt** outcome with a per-channel policy rather than a delivery state (§10.3); the version
column is the fencing token, so there is no separate token (§11); conditions are matchers, not an expression language (§7.2); a digest is one delivery
with several items (D19), which the review's one-parent tree didn't cover.

---

## 19. Owner's decisions, 9 November

| # | Question | Decision | Applied in |
|---|---|---|---|
| 1 | Which service is the source of truth for users, groups and roles? | None in particular. Recipients come from a resolver abstraction (`IRecipientResolver`): identity, local, external directory, topic, on-call, static address. | D20, D23, §3, §6.1, §12, §15, §16 |
| 2 | On-call: build schedules here, or integrate? | The notification system stays independent and has no on-call now. On-call is an optional recipient resolver, integrated later (PagerDuty or Jira Service Management). Neither is a channel. | D21, §1, §6.1, §9, §16 |
| 3 | Centralized by default, with Observability embedding? | Confirmed. | D2, D3 |
| 4 | Does mandatory respect a channel the person turned off? | Confirmed: it respects it, and always reaches the inbox. | D9, §6.4 |
| 5 | Template editing? | Scriban, edited as text, through phase 4; be ready for a WYSIWYG editor later. | D22, §8.1, §12, §16 |
| 6 | Display groups? | Three by default (`approval`, `system`, `general`), customizable. Tenant admins add, rename, reorder and move types; applications choose a default; the three defaults can't be deleted. | D12, §3, §7.3, §12, §16, §17 |
| 7 | Web Push, Firebase, and new channels later? (10 Nov) | Yes: push covers FCM, APNs and Web Push over device registrations; channels come from a registry so new ones are packages. | D24, D25, §1, §3, §8.1, §9, §10.3, §12, §15, §16 |
| 8 | How does a RabbitMQ message prove its application? (11 Nov) | Broker-validated `user_id`: each producer connects with its own RabbitMQ user and stamps `user_id`, which RabbitMQ checks against the connection. The service maps that user to the application and refuses messages without one. Needs Juice.EventBus to set `user_id` when publishing, pass it to handlers, and keep it across its retry path. | phase-1-plan I5, M2 |
| 9 | How does a token say which tenants its holder administers? (11 Nov) | Behind an abstraction (`ITenantAccess`), so the answer can come from elsewhere later (juice-tenants membership, local grants). Today a platform admin administers every tenant, and the `notifications-tenant-admin` role administers the one tenant the token's `platform_tenant` claim names by id, as in Juice.Observability. | §13.2, phase-1-plan I11 |

## 20. Open questions

None blocking phase 1. To settle before phase 2:

1. **Identity resolver contract.** What the Juice identity/tenant service exposes for users, groups and roles (API or events), and whether it already
   provides email addresses the tenant trusts (D23).
2. **Subscriber linking.** Who links aliases across sources: tenant admins in the Console only, or a sync that a resolver can declare?

