# Phase 1 plan: core

**Status:** draft for review, 9 November 2026. It builds on `design.md`, and section numbers like §10.3 refer to it. Implementation choices made here are
labelled I1, I2, …; they are mine, for review. Each milestone names the tracking items it closes (`p1-01` …) on the build board.

**Goal:** a centralized Juice.Notifications service that accepts notification requests over HTTP and RabbitMQ, resolves routes and external addresses,
renders templates, and delivers through signed webhooks and SMTP. Every attempt is recorded, and each delivery guarantee in §10 holds under crashes,
retries and concurrent workers.

**Gate:** the six proof points (§16), each shown by a named test (§6 below), plus a local-stack demo and an acceptance record.

---

## 1. Scope

**In**

- Applications, application credentials and the tenants each application may send for.
- Versioned notification types: schema, category, default display group, mandatory flag, priority, templates, `externalRecipients`, `redact`, and a
  workflow of `send` only.
- Intake over HTTP and `NotificationRequested` events, with schema validation and idempotency.
- `IRecipientResolver`, with `RouteRecipientResolver` and `StaticAddressResolver`.
- Integrations, integration targets, routes and the destination allowlist, through the API and configuration provisioning.
- Delivery planning: destination keys, delivery items, rendering stored before the first attempt.
- The Scriban sandbox; templates with `Engine = scriban`.
- The channel registry (`design.md` §9.1, D24), with `webhook` and `email` registered as channel definitions; providers: signed webhook, SMTP.
- Dispatcher: the PostgreSQL claim store, attempt outcomes and failure categories, backoff, the unknown-outcome policy.
- Delivery history API; dead-letter retry of one delivery.
- Metrics, traces and health; the centralized host; a local stack.

**Out** (later phases): subscribers, contact points, preferences, mandatory enforcement, display group catalog, inbox (phase 2); digest, delay,
throttle, conditions, correlations, escalation, events to producers (phase 3); chat, SMS and push providers, failover, storm protection, bulk replay,
SQL Server (phase 4); embedded host and mirroring (phase 5).

The model is shaped for those features from the start: recipients have a `Kind`; correlation keys and display groups are stored; notifications carry a
workflow step. Later phases add behavior without reshaping tables that already hold data.

---

## 2. Implementation choices

| # | Choice | Why |
|---|---|---|
| I1 | **.NET 10, C# latest, central package management.** Juice packages at the versions Juice.Observability uses (`Juice.EF`, `Juice.MediatR`, `Juice.MediatR.Behaviors`, `Juice.Messaging.*` 10.12.0; bumped to 10.13.0 on 13 Nov 2026 for the commit-failure fix, deferred #2), Npgsql EF Core 10. | Same toolchain and framework versions as the first producer, so phase 5 has no version gap. |
| I2 | **One `NotificationsDbContext`, schema `notifications`, PostgreSQL migrations in `Juice.Notifications.EF.PostgreSQL`.** Tenant isolation through `TenantId` columns and composite foreign keys (§12). No Finbuckle tenant resolution in the core: the tenant comes from the route (`/tenants/{tenantId}`) or the event, and is authorized against `application_tenants`. | The core stays host-agnostic for embedding. Tenant ids are the Juice tenants service's GUIDs; nothing here needs its configuration store. |
| I3 | **Stages hand off through due rows, not messages.** Intake writes the `Request` and its `Notification` (state `Planning`, due now) in one transaction. The planner, delivery planner and dispatcher each claim due rows (§11). After a commit, an in-process nudge (`Channel<Guid>`) wakes the next stage, so latency doesn't wait for the poll (1 s). | One mechanism, claims, gives every internal hop its guarantee (§10.1) and its crash recovery. The outbox is used where a message leaves the process: events to producers (phase 3) and the RabbitMQ intake's inbox. `design.md` §4.1 and §10.1 are updated to match (§9). |
| I4 | **Commands through `Juice.MediatR` with the transaction behavior** (`AcceptNotification`, `DeclareTypes`, `ChangeRoute`, …), as Observability does. Workers call the stage services directly inside their own claim transaction. | The Juice convention for anything that changes state through the API. |
| I5 | **RabbitMQ intake** via `Juice.EventBus.RabbitMQ`: a `NotificationRequested` consumer with inbox idempotency, sending the same `AcceptNotification` command. A duplicate is caught by the request's unique key either way. The application is the message's broker-verified publisher (`user_id`, Juice 10.13.0 `PublisherId`) matched to its `brokerUser`; retries go through the broker's own dead-lettering, which keeps `user_id` (built 13 Nov 2026). | Producers that already publish through Juice messaging don't need an HTTP client. |
| I6 | **Claims in raw SQL** (Npgsql) behind `IClaimStore`; everything else through EF Core. | `FOR UPDATE SKIP LOCKED … RETURNING` and the conditional version write must be exact. EF's generated SQL can't promise that. |
| I7 | **JSON Schema 2020-12 validation with `JsonSchema.Net`.** Schemas are compiled and cached per type version. | Supports 2020-12 and reports errors with JSON pointers for the `422` body. |
| I8 | **Scriban** with the §8.2 limits, through one `TemplateRenderer` that the sandbox tests target. | Owner decision D22. |
| I9 | **SMTP through MailKit**, not `System.Net.Mail.SmtpClient`. MailKit reports which SMTP stage failed, which the unknown-outcome rule needs: a failure before `DATA` completes is retryable; a dropped connection after it is unknown. | Microsoft recommends against `SmtpClient` for new code, and it can't tell those cases apart. |
| I10 | **Webhook provider ported from `Juice.Observability.Webhooks`**: `WebhookSigner`, `DestinationPolicy`, the per-tenant `SocketsHttpHandler` with `ConnectCallback`, and their tests, adapted to `INotificationProvider`. | D16. A copy for now; Observability's own module is deleted in phase 5. |
| I11 | **Authentication by OIDC JWT bearer.** Producers use client credentials, and `applications.ClientId` maps a token to an application. Tenant admins need the `notifications-admin` role for that tenant. The local stack uses Keycloak, as Observability's does. | The pattern the Juice stack already runs. |
| I12 | **Configuration provisioning** (`ManagedBy = config`) for integrations, targets, routes and the allowlist, loaded at start, as Observability's `WebhookProvisioner` does. API changes to a config-managed row are refused. | Deployments keep notification setup in their repository. |
| I13 | **Tests:** xUnit v3. Integration tests run against real PostgreSQL, RabbitMQ and Mailpit from `deploy/local`, and are skipped when it isn't up (as Observability's `LocalStack` does). Webhook receivers are in-process Kestrel servers the test controls. | Concurrency and crash properties can't be shown against an in-memory provider. |

---

## 3. Solution layout (phase 1)

```
services/notifications/
├── Directory.Build.props, Directory.Packages.props, nuget.config, Juice.Notifications.slnx
├── deploy/local/                                  docker compose: postgres, rabbitmq, keycloak, mailpit; .env generated by a script
├── src/
│   ├── Juice.Notifications.Abstractions/          INotificationClient, IRecipientResolver, INotificationProvider, contracts, enums
│   ├── Juice.Notifications/                       commands, intake, planner, delivery planner, dispatcher, renderer, resolvers (route, address)
│   ├── Juice.Notifications.EF/                    entities, NotificationsDbContext, transaction behavior, IClaimStore contract
│   ├── Juice.Notifications.EF.PostgreSQL/         migrations, PostgresClaimStore
│   ├── Juice.Notifications.Messaging/             NotificationRequested consumer (RabbitMQ), event contracts
│   ├── Juice.Notifications.Providers.Webhook/     signer, destination policy, HTTP client factory, provider
│   ├── Juice.Notifications.Providers.Email/       MailKit SMTP provider
│   ├── Juice.Notifications.Api.Contracts/         request and response DTOs
│   ├── Juice.Notifications.Api/                   producer, admin and history endpoints; problem details
│   ├── Juice.Notifications.Client.Http/           INotificationClient over HTTP
│   └── Juice.Notifications.App/                   host: wiring, workers, health, OpenTelemetry
└── test/
    ├── Juice.Notifications.Tests/                 unit tests (pure rules)
    └── Juice.Notifications.IntegrationTests/      PostgreSQL, RabbitMQ, Mailpit, Kestrel receivers; proof-point tests
```

---

## 4. Data model (phase 1 tables)

A subset of §12, created in the first migration:

| Table | Notes for phase 1 |
|---|---|
| `applications`, `application_tenants` | `ClientId` (unique), `Key` (unique), `Enabled` |
| `notification_types` | full columns; `Workflow` holds `[{"step":"send"}]` only |
| `templates` | `Engine`, `Source` (null), `Body`; tenant overrides through the admin API |
| `integrations`, `integration_targets` | webhook and SMTP kinds; `Priority` stored but not used for failover yet |
| `routes`, `destination_allowlist` | `ManagedBy` |
| `requests` | unique `(TenantId, ApplicationId, IdempotencyKey)`; `CorrelationKey` stored, not acted on |
| `notifications` | unique `RequestId`; `State` (`Planning`, `Planned`, `Expired`, `Failed`), claim columns |
| `notification_recipients` | `Kind` (`route`, `external`), `Resolver`, `NamedAs`, `Outcome`; claim columns |
| `deliveries`, `delivery_items`, `delivery_attempts` | as §12; unique `(RecipientId, DestinationKey, Channel)` on items |

Claim columns on every claimable row: `Version int`, `ClaimedBy text`, `LeaseUntil timestamptz`, `NextAt timestamptz`. There's a partial index on
`NextAt` where the row is still open. All tenant-owned tables have the key `(TenantId, Id)`, and foreign keys use both columns.

Left out until their phase: `subscribers` and everything under them, `display_groups`, `mutes`, `escalation*`, `correlations`, `interactions`,
`inbox_items`, `provider_events`, `recipient_resolvers` (phase 1 resolvers need no configuration).

---

## 5. Milestones

Each milestone ends with its unit and integration tests green and its board items set to done. The order follows dependencies. M6 can run alongside
M4–M5.

### M1: skeleton and persistence (`p1-01`, `p1-02`, `p1-06`)

- Solution, packages, `Directory.Build.props` (nullable, warnings as errors, analyzers), CI build and test.
- `deploy/local`: PostgreSQL, RabbitMQ, Keycloak (realm with a producer client and a tenant admin), Mailpit.
- `NotificationsDbContext` with the §4 tables, composite keys and the first migration.
- Applications and `application_tenants` admin endpoints (platform admin only).
- **Tests:** the migration applies to an empty database; a row that references another tenant's row is rejected by the database (the composite FK),
  for every tenant-owned relationship.

### M2: types and intake (`p1-03`, `p1-04`, `p1-05`)

- `PUT /api/v1/applications/{app}/types`: declare a type; same content returns the existing version; a changed schema or template adds a version.
  Validates the schema, workflow (`send` only in phase 1) and templates (they parse under the sandbox, and each names a registered channel).
- `POST /api/v1/tenants/{tenantId}/notifications`, implementing `AcceptNotification`:
  - authenticate the application and check `application_tenants`;
  - pick the type version and validate `data`;
  - reject external recipients unless the type allows them;
  - insert the request and its notification (`Planning`, due now) in one transaction;
  - return `202` with the id.
  A unique violation on the idempotency key returns `200` with the original request.
- RabbitMQ `NotificationRequested` consumer with inbox idempotency, sending the same command.
- `GET …/notifications/{id}`: the request, its notification state and its deliveries so far.
- **Tests:**
  - schema errors return `422` with JSON pointers;
  - the type version is pinned;
  - an application can't send for a tenant it isn't linked to, or a type outside its namespace;
  - 20 concurrent identical requests produce one request and one notification (**proof 2**).

### M3: claim store and worker host (`p1-07`)

- `IClaimStore` with `ClaimAsync(table, batch, lease)` and `CompleteAsync(id, claimedVersion, …)`; `PostgresClaimStore` using
  `FOR UPDATE SKIP LOCKED … RETURNING Id, Version`, and conditional updates on `Version`.
- `StageWorker<T>`: a background service that claims, runs one step per row in its own transaction, persists conditionally, records `lease_lost`, and
  wakes on the nudge channel or every second.
- A fault-injection seam (`IStageFaults`, used only by tests) that can throw or pause between claim, execute and persist.
- **Concurrency suite**, reused for SQL Server in phase 4:
  - 8 workers on 1,000 due rows: each row completes exactly once;
  - a worker paused past its lease: its write affects nothing, and another worker completes the row;
  - a worker killed after claiming: the row is due again after the lease.

### M4: planner and routes (`p1-08`, `p1-09`)

- `IRecipientResolver` and the resolve pipeline (§6.1). Phase 1 runs:
  - `RouteRecipientResolver`: route matching on type pattern, label matchers (`=`, `!=`, `=~`, `in`) and minimum priority, one route recipient per
    matching route;
  - `StaticAddressResolver`: addresses normalized (email lowercased; webhook URLs canonicalized), with `NamedAs` recorded.
- The planner stage claims `Planning` notifications, writes recipients (`Planned`, due now) and sets the notification to `Planned`, all in one
  transaction. A request past `expiresAt` becomes `Expired` with no recipients.
- Admin API for integrations, integration targets, routes and the allowlist (`/api/v1/tenants/{tenantId}/…`), audited to `services/audit`.
  Configuration provisioning (I12).
- **Tests:**
  - matcher table tests;
  - a route never removes a named recipient;
  - planning the same notification twice, concurrently and after a fault, produces one set of recipients (**proof 3**);
  - config-managed rows refuse API changes.

### M5: rendering and delivery planning (`p1-10`, `p1-11`)

- `TemplateRenderer` (Scriban) with the §8.2 limits: no loader, built-in allowlist, loop and recursion limits, size and time caps, missing fields
  rendered empty. Channel escaping for HTML email.
- Webhook `passthrough` envelope: `id`, `type`, `typeVersion`, `occurredAt`, `tenant`, `correlationKey`, `data`, `links`.
- Delivery planner stage:
  - claims `Planned` recipients;
  - picks destinations (route targets; external addresses through the tenant's default integration for the channel);
  - looks up the template (tenant override, then the application default, then `en`);
  - renders and stores the content with `TemplateVersion`, `Locale` and `RendererVersion`;
  - inserts deliveries and items (due now) in one transaction.
  A render failure creates the delivery as `Failed {Content, render_failed}`, so it shows in history.
- **Tests:**
  - sandbox: a loop bomb, deep recursion, an oversize output and a slow template are each stopped; `include` is unavailable;
  - the stored body is byte-identical across retries;
  - one recipient can't get two items for the same destination and channel.

### M6: providers (`p1-12`, `p1-13`)

- **Channels:** `ChannelDefinition` and `IChannelRegistry` in Abstractions; `webhook` and `email` registered by their provider packages, with
  their render shape, limits, escaping and unknown-outcome policy. `Channels.All` in `Vocabulary.cs` becomes the registry; the core keeps only
  `inbox` as a known name. Test: a type declaring an unregistered channel is refused.
- **Webhook:**
  - port and adapt the signer, destination policy and per-tenant client (I10), with Observability's tests;
  - map responses: 2xx → `Delivered`; 408/429/5xx → retryable (429 → `RateLimited`); 404/410 → `Destination`; other 4xx → `Content`;
  - map local failures: a blocked destination → `Policy`; a timeout or reset after the request was sent → `Unknown`; failing to connect → retryable
    `Provider`.
- **SMTP** (MailKit):
  - before `DATA` completes: a connect or auth failure → retryable `Provider`; recipient rejected → `Destination`;
  - `250` after `DATA` → `Accepted`;
  - connection lost after the body was sent → `Unknown`.
- `Idempotent` is `false` for both. Webhook receivers deduplicate on `webhook-id` (§10.4), which the dispatcher relies on.
- Secret references (`env:`, `file:`), read at send time.
- **Tests:**
  - SSRF suite (ported);
  - a signature verifies with the published sample verifier;
  - the outcome mapping table, including a Kestrel receiver that accepts and never answers, and a Mailpit proxy that drops after `DATA`.

### M7: dispatcher (`p1-14`)

- The dispatcher stage:
  - claims due deliveries in priority order, then by `NextAt`;
  - calls the provider through its integration, records a `DeliveryAttempt` with its own id;
  - applies the outcome: `Delivered`/`Accepted`, `Retrying` with backoff (10 s base, 10 min cap, jitter, 8 attempts by default, per-channel
    settings), or `Failed {category}`;
  - closes deliveries still due past the request's `expiresAt` as `Expired`.
- Unknown-outcome policy (§10.3) in phase 1:
  - webhook: retry with the same `webhook-id`;
  - SMTP: `Failed {Provider, unknown_outcome}` for normal types; one retry for mandatory types.
- No failover in phase 1 (phase 4). The failure category is recorded now, so phase 4 can decide on it.
- If the persist after a send loses its claim, the attempt is still written (the world saw it), and the delivery's state is left to the worker that
  holds the claim.
- **Tests:**
  - backoff schedule;
  - a timeout on a webhook retries with the same id, and the receiver sees the id twice (**proof 5**);
  - an SMTP drop after `DATA` doesn't retry (normal type) and retries once (mandatory type);
  - a 400 from a webhook fails as `Destination` after one attempt;
  - a dispatcher killed after sending and before persisting: the delivery is re-sent after its lease with the same id (**proof 4**).

### M8: history, dead letters, operations, host (`p1-15`, `p1-16`, `p1-17`)

- History API:
  - `GET /api/v1/tenants/{tenantId}/deliveries`, filtered by status, channel, integration, type, request and time;
  - `GET …/deliveries/{id}`, with items and attempts (outcome, category, integration, status code, duration).
  Producers see their own; tenant admins see all.
- `POST …/deliveries/{id}/retry`: a failed delivery goes back to `Pending` with the same id and its attempt count kept. Admin only, audited.
- Metrics (§14) and OpenTelemetry traces from the request's `traceparent` through every stage to the provider call. Health: `/health/live`,
  `/health/ready` (database, plus RabbitMQ when intake is enabled).
- `Juice.Notifications.App`, a Dockerfile, `deploy/local` wiring, and a `samples/` producer that declares a type and sends to a webhook and to Mailpit.
- **Tests:**
  - a delivery's id is the same in every attempt, in history, and after a dead-letter retry (**proof 6**);
  - history filters;
  - a trace links the request to the provider call.

### M9: proof points and acceptance (`p1-g1` … `p1-g6`)

- The crash test for **proof 1**:
  - the host runs as a child process with workers disabled;
  - a request is accepted, and the process is killed (`Kill(entireProcessTree)`, no graceful stop) right after `202`;
  - a new host process with workers delivers it to the receiver.
- Mutation testing (Stryker.NET) on the core rules: route matching, outcome classification, the unknown-outcome policy, backoff, destination keys,
  destination policy, idempotency handling. Surviving mutants are fixed or explained in the acceptance record.
- Load check: 1,000 requests a minute to two webhook routes on the local stack, p95 accepted-to-first-attempt ≤ 30 s, no lease storms.
- `docs/phase-1-acceptance.md`: each gate case with its test, the mutation score, the load result and the demo steps.

---

## 6. Proof points and their tests

| # | Proof point | Test (integration project) |
|---|---|---|
| 1 | An accepted request survives a crash right after `202` | `Accepted_request_is_delivered_after_the_host_is_killed_right_after_202` |
| 2 | A duplicate request creates no new work | `Concurrent_identical_requests_make_one_request_one_notification_and_one_delivery_per_destination` |
| 3 | Planning is safe under retries and concurrent workers | `Planning_the_same_notification_concurrently_or_after_a_fault_yields_one_set_of_recipients` |
| 4 | A worker crash never loses a claimed delivery; stale writes are discarded | `Dispatcher_killed_after_send_resends_after_lease_with_the_same_id`, `Worker_past_its_lease_cannot_overwrite_newer_state` |
| 5 | Timeouts follow the unknown-outcome policy; no failover on destination or content failures | `Webhook_timeout_retries_with_the_same_webhook_id`, `Smtp_drop_after_data_is_not_retried_for_a_normal_type`, `Webhook_400_fails_as_destination_without_retry` |
| 6 | History shows every attempt; the delivery id never changes | `Delivery_id_is_stable_across_attempts_history_and_dead_letter_retry` |

---

## 7. API surface (phase 1)

| Who | Endpoint |
|---|---|
| Platform admin | `POST/GET /api/v1/applications`, `PUT /api/v1/applications/{app}/tenants/{tenantId}` |
| Application | `PUT /api/v1/applications/{app}/types`, `GET …/types/{key}/versions` |
| Application | `POST /api/v1/tenants/{tenantId}/notifications`, `GET …/notifications/{id}` |
| Tenant admin | `GET/PUT/DELETE /api/v1/tenants/{tenantId}/integrations`, `…/integrations/{key}/targets`, `…/routes`, `…/allowlist` |
| Tenant admin | `PUT/DELETE …/templates/{type}/{channel}/{locale}` (tenant overrides), `POST …/templates/preview` |
| Application, tenant admin | `GET …/deliveries`, `GET …/deliveries/{id}`; tenant admin: `POST …/deliveries/{id}/retry` |
| Anyone | `/health/live`, `/health/ready` |

Errors are problem details, with the error codes listed in `Api.Contracts`.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| Polling load from several stage workers on one database | One partial index per claimable table on open `NextAt`, batch claims, nudges after commit so polling can stay at 1 s; measured in the M9 load check. |
| Porting the SSRF code drifts from Observability's | Port the tests first, then the code; keep the file-level structure so later fixes can be compared side by side until phase 5 removes the original. |
| `JsonSchema.Net` or Scriban behavior differs from what producers expect | Validate type declarations at `PUT` time, with examples in `samples/`; the template preview endpoint shows the exact output. |
| Crash tests are flaky on CI | Run them against `deploy/local` in a dedicated CI job, with process-level kill and explicit waits on database state, never sleeps. |
| Tenant ids come from outside (Juice tenants service) | Phase 1 trusts the tenant id an authorized application sends, checked against `application_tenants`; a tenant directory check can be added later without schema change. |

---

## 9. Changes made to `design.md`

- §4.1: "store Request + outbox row" is now "store Request + its Notification (due now)" (I3).
- §10.1, intake → planner: the guarantee now comes from that same transaction and the unique `RequestId`, with no outbox involved.
- 10 Nov, on the owner's question about push: D24 (channel registry) and D25 (push over device registrations with FCM, APNs and Web Push); §9
  is split into §9.1 registry, §9.2 push, §9.3 provider contract. Phase 1 builds the registry; push itself stays in phases 2 and 4.
