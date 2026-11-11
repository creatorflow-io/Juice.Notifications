# Feedback: Juice.Notifications Design Review

**Source:** `design.md`  
**Review date:** 9 November 2026  
**Purpose:** Actionable feedback for revising the design before implementation.

## 1. Overall assessment

The proposed design has a strong foundation and is a credible direction for a shared notification platform across Juice products.

Keep these decisions:
- Producers describe what happened; the notification platform owns processing and delivery.
- Notification types have versioned schemas and templates.
- Intake is synchronous and lightweight; planning and delivery are asynchronous.
- Producer-side Outbox and platform-side idempotency are part of the reliability model.
- Routes add destinations without removing explicitly named recipients.
- Domain-specific suppression, such as Observability silences, remains in the owning product.
- Workflow execution and message dispatch are separate responsibilities.
- Providers are pluggable, with independent retry and rate-limit behavior.
- Webhook signing, SSRF protection, secret references, and tenant isolation are first-class concerns.
- Observability should be migrated incrementally, with dry-run comparison before cutover.

**Recommendation:** Do not start implementation directly from the current draft. Resolve the P0/P1 issues below first.

---

## 2. Required design changes

### P0.1 — Resolve the hosting model

**Current design:** D1 supports embedded and standalone hosting, describes embedded as the default, while the goals describe one place where all Juice products send notifications and one delivery history per tenant.

These are different deployment and data-ownership models.

**Risk:** If each product embeds its own module, users may end up with separate inboxes, preferences, contact points, delivery histories, integrations, and routes. That may conflict with the goal of a unified notification center.

**Required change:**
1. Keep `Juice.Notifications` core host-agnostic.
2. Make centralized `Juice.Notifications.App` the default architecture for shared notification capabilities.
3. Retain embedded hosting as an optional mode for products that genuinely need it.
4. Define which data is local to an embedded deployment and which data is shared.
5. Specify how embedded data/history migrate if a customer later moves to a centralized instance.

Do not claim that configuration alone moves a product between hosting modes. The transition also involves data migration, identity mapping, subscriptions, contact points, integrations, and history.

**Acceptance criteria:** The design identifies the recommended deployment mode, defines ownership of inbox/preferences/contact points/integrations/history, and documents embedded-to-centralized migration.

### P0.2 — Clarify Recipient, Destination, Delivery, and Attempt

**Current design:** The concepts distinguish Recipient, Subscriber, ContactPoint, Integration, Notification, and Message. The data model has a `messages` table with channel, integration, destination, rendered content, status, and attempts.

**Risk:** A user, email address, Slack channel, Teams webhook, and generic webhook are not the same kind of recipient. A shared Slack channel may not belong to a subscriber. One logical recipient may also have several channel destinations.

**Required change:** Define entities and relationships explicitly:

```text
Request
  └── Notification
        ├── Recipient
        └── Delivery
              ├── Channel
              ├── Destination
              ├── Integration
              ├── RenderedContent
              └── DeliveryAttempt
```

| Entity | Responsibility |
|---|---|
| Recipient | Logical person or target named by the request |
| Subscriber | Person known to the notification service |
| ContactPoint | Verified personal endpoint, such as email or phone |
| Destination | Actual target addressed by a delivery |
| Integration | Tenant-configured provider connection |
| Delivery | One send to one destination on one channel |
| DeliveryAttempt | One execution attempt through a provider |

A destination may be a subscriber contact point, shared Slack/Teams destination, integration endpoint, or explicitly permitted external address.

Specify whether `Message` remains the name for the current delivery-level entity or is renamed to `Delivery`. Avoid using the same term for both a logical notification and a channel-specific send.

**Acceptance criteria:** Every delivery has one unambiguous destination and channel; shared destinations do not require a fake subscriber; attempts are separate records; unique constraints match the chosen identity model.

### P0.3 — Specify delivery guarantees and ambiguous outcomes

**Current design:** The design promises at-least-once delivery, deduplication, retries, provider failover, and reuse of the message ID as a provider or webhook deduplication key.

**Risk:** The notification service cannot guarantee exactly-once external delivery by itself.

Example:
1. A worker sends a webhook.
2. The receiver processes it.
3. The response is lost or times out.
4. The sender cannot know whether the receiver processed it.
5. Retrying may cause a duplicate side effect.

Sending the same `webhook-id` does not force a receiver to deduplicate. The receiver must implement that contract. A provider's HTTP 2xx or SMTP acceptance also does not necessarily mean the final recipient received or read the message.

**Required change:**
- Document guarantees separately for producer-to-intake, intake-to-planner, planner-to-delivery, delivery-to-provider, and provider callbacks.
- Keep `DeliveryId` stable across retries and provider failover.
- Give every attempt a separate `AttemptId`.
- Pass `DeliveryId` as idempotency key when the provider supports it.
- Define the webhook receiver idempotency contract explicitly.
- Distinguish provider acceptance from final delivery.
- Represent ambiguous outcomes, or document their handling.
- Do not fail over automatically for every failure. Distinguish provider failures from destination/content failures.

Suggested state model:

```text
Pending
  |
  v
Sending
  |
  +--> Accepted
  |      +--> Delivered
  |      +--> Bounced
  +--> RetryableFailure
  +--> PermanentFailure
  +--> UnknownOutcome
```

The exact states may differ, but their meanings and transitions must be documented.

**Acceptance criteria:** Provider timeouts have a defined policy; the design does not claim exactly-once delivery to arbitrary providers; provider acceptance and recipient delivery are distinct; failover considers failure category; webhook receiver deduplication requirements are documented.

---

## 3. P1 — Resolve before or during the first implementation phase

### P1.1 — Scope idempotency and correlation by tenant and application

**Current design:** Requests are unique on `(Application, IdempotencyKey)`. Correlation keys are unique per tenant.

**Risk:** An application may serve multiple tenants. Different applications may also generate the same correlation key. Acknowledge, resolve, and cancel must not accidentally affect another application's work.

**Required change:**

If idempotency is tenant-scoped:

```text
UNIQUE (TenantId, ApplicationId, IdempotencyKey)
```

If correlations are application-scoped:

```text
UNIQUE (TenantId, ApplicationId, CorrelationKey)
```

If cross-application correlation is required, define an explicit correlation namespace/scope and authorization policy instead of assuming all applications share one namespace.

Also define tenant-safe relationships between records. EF Core global query filters alone should not be treated as the tenant-isolation boundary.

**Acceptance criteria:** Duplicate requests are scoped correctly; correlation lifecycle APIs cannot affect another application's records without explicit authorization; cross-tenant references are rejected.

### P1.2 — Define workflow leasing and concurrency precisely

**Current design:** One replica at a time per shard owns workflow timers. Dispatcher runs on every replica and claims messages.

**Risk:** One replica per shard may be a reasonable initial simplification, but should not become a permanent architectural requirement. A lease alone may not prevent a stale worker from writing after its lease expires and another worker takes ownership.

**Required change:** Specify atomic claim, lease expiration, ownership/fencing token, optimistic concurrency/state version, idempotency of each workflow step, and recovery if a worker dies between executing a step and persisting its result.

Suggested flow:

```text
Workflow Runner replicas
        |
        v
Claim due workflow rows atomically
        |
        v
Lease + ownership/fencing token
        |
        v
Execute one step
        |
        v
Persist result and next due time conditionally
```

A single replica per shard can be used initially if justified, but document it as an operational choice rather than a correctness invariant.

**Acceptance criteria:** Two workers cannot both successfully own the same step; a stale worker cannot overwrite newer state; crashed worker leases expire; re-executing a step is safe or has a documented recovery strategy.

### P1.3 — Make Mandatory policy unambiguous

**Current design:** D10 says mandatory notifications bypass preferences and quiet hours, but recipient resolution says mandatory notifications still use the channels the person prefers.

**Risk:** It is unclear what happens if a user disables every channel or mutes a category. `Mandatory` and `Critical` may also be conflated even though they represent different concepts.

**Required change:** Define mandatory policy independently from severity.

| Policy | Normal | Mandatory |
|---|---|---|
| Category opt-out | Respect | Bypass |
| Quiet hours | Respect | Bypass |
| User mute | Respect | Explicit policy |
| Unverified contact point | Block | Block |
| Unsupported channel | Block | Block |
| Channel preference off | Respect | Explicit policy |

Decide explicitly whether mandatory bypasses channel opt-out. Do not infer this from the word `mandatory`.

**Acceptance criteria:** Every preference/mute rule has defined mandatory behavior; mandatory and severity are separate; security controls such as contact verification are not silently bypassed.

### P1.4 — Persist type/schema version on each request

**Current design:** Types are versioned, but the request table does not clearly list the selected type/schema version.

**Risk:** A request accepted under an older schema may be replayed after the type declaration changes. Without a version snapshot, validation and rendering behavior become ambiguous.

**Required change:** Persist the selected version and schema identity:

```text
Request
  TypeKey
  TypeVersion
  SchemaVersion
  SchemaHash
  Data
```

Also persist the template and renderer version associated with rendered delivery:

```text
Delivery
  TemplateVersion
  Locale
  RendererVersion
  RenderedContent
```

**Acceptance criteria:** A request can be validated/replayed using its accepted contract version; rendered content can be traced to template and renderer versions; updates do not silently rewrite historical semantics.

---

## 4. Additional design improvements

### 4.1 — Add a presentation group separate from Category

The notification center requires exactly three tabs:

- `Approval`
- `System`
- `General`

Do not overload `Category` for this purpose. `Category` supports subscriptions, preferences, filtering, and analytics. UI grouping is a separate presentation classification.

Add a field to Notification Type:

```csharp
public enum NotificationGroup
{
    Approval,
    System,
    General
}
```

Alternatively, use an extensible string if applications must declare additional groups.

Example mapping:

| Type | Category | DisplayGroup |
|---|---|---|
| `workflow.approval.requested` | Workflow | Approval |
| `security.password.changed` | Security | System |
| `payment.completed` | Payment | General |
| `content.published` | Content | General |
| `observability.alert.firing` | Alerts | System |

This mapping is product policy, not an inherent rule. An alert could be General if that is the intended user experience.

Keep the three UI tabs stable while allowing categories and event types to grow.

### 4.2 — Constrain workflow expressions

The design allows conditions over labels, data, and correlation state.

Define a small expression language rather than executing arbitrary C#, JavaScript, or other general-purpose code.

Requirements:
- No filesystem or network access.
- No service-container access.
- Read-only access to declared data.
- Bounded expression depth and execution time.
- Explicit supported operators and functions.
- Predictable behavior for missing/null fields.

Apply equivalent sandbox/resource-limit requirements to Scriban templates. The fact that templates have no intended I/O is not, by itself, a complete sandbox specification.

### 4.3 — Expand SSRF requirements

D8 is directionally correct but needs an implementation-level security contract.

Specify:
- Block loopback, link-local, private, and reserved IP ranges by default.
- Validate resolved IP addresses at connection time.
- Protect against DNS rebinding.
- Revalidate every redirect, or disable redirects.
- Handle IPv4-mapped IPv6 addresses.
- Enforce allowed ports.
- Ensure connection pooling does not bypass destination policy checks.
- Audit private-destination allowlist changes.

### 4.4 — Define provider-specific claim implementations

PostgreSQL and SQL Server do not have identical locking/query semantics. Do not assume one LINQ query will provide equivalent claim behavior for both.

Introduce an abstraction:

```csharp
public interface IMessageClaimStore
{
    Task<IReadOnlyList<ClaimedMessage>> ClaimAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
}
```

Provide provider-specific implementations and integration tests.

Examples:
- PostgreSQL: `FOR UPDATE SKIP LOCKED` or an appropriately designed atomic update.
- SQL Server: appropriate locking hints/conditional updates and output of claimed rows.

Verify the implementation under concurrent load and worker failure.

---

## 5. Reconsider Phase 1 scope

The current Phase 1 includes type registry, intake, idempotency, routes, four channels, templates, dispatcher, retry/DLQ, delivery history, and both hosting modes. This is a substantial first release.

Recommended staged scope:

| Phase | Recommended scope |
|---|---|
| 1. Core | Type registry, intake, schema validation, Outbox/Inbox integration, idempotency, recipient/destination model, webhook and email, dispatcher, retry, delivery history |
| 2. People & Inbox | Subscriber, contact points, verification, preferences, quiet hours, three display groups, in-app inbox |
| 3. Workflow | Digest, delay, throttle, condition, correlation lifecycle, acknowledgement, escalation |
| 4. Enterprise | Advanced Slack/Teams, SMS, push, provider failover, on-call, storm protection, advanced template management |

The most important Phase 1 proof points are:

1. Accepted intake is not lost after a process crash.
2. Duplicate intake does not create duplicate logical work.
3. Planning is safe under retries and concurrent execution.
4. Worker crashes do not permanently lose claimed deliveries.
5. Provider timeouts have a defined ambiguous-outcome policy.
6. Delivery history accurately records attempts and outcomes.

Do not expand channel count before these guarantees are demonstrated.

---

## 6. Suggested revised architecture

Keep the current overall architecture, but make processing boundaries explicit:

```text
Producers
   |
   v
1. Intake
   - Authentication
   - Type/schema validation
   - Idempotency
   - Request persistence
   - Outbox
   |
   v
2. Planner
   - Recipient resolution
   - Route matching
   - Preferences
   - Mandatory policy
   - Mutes
   |
   v
3. Notification / Workflow
   - Notification lifecycle
   - Correlation
   - Digest
   - Delay
   - Escalation
   |
   v
4. Delivery Planner
   - Destination resolution
   - Channel
   - Integration
   - Template
   - Locale
   |
   v
5. Dispatcher
   - Atomic claim
   - Render
   - Provider call
   - Retry
   - Failover
   - Attempt tracking
```

Suggested conceptual data model:

```text
Application
  └── NotificationType
        ├── Category
        ├── DisplayGroup
        ├── TypeVersion / SchemaVersion
        ├── Mandatory
        ├── Templates
        └── Workflow

Request
  └── Notification
        ├── Recipient
        ├── WorkflowState
        └── Delivery
              ├── Destination
              ├── Channel
              ├── Integration
              ├── RenderedContent
              └── DeliveryAttempt
```

This is a conceptual model. The final relational model may use additional tables for normalized recipient resolution, route matches, workflow steps, and provider callbacks.

---

## 7. Decisions to record explicitly

Before implementation, update the design with decisions for these questions:

1. Is centralized hosting the default, with embedded mode optional?
2. Is correlation scoped by tenant and application, or can it intentionally cross applications?
3. What exactly is the identity of a Delivery?
4. Can a shared destination exist without a Subscriber?
5. What does a provider timeout mean, and when is retry/failover safe?
6. Does Mandatory bypass channel opt-out?
7. Which type/schema version is pinned to an accepted Request?
8. Which template/renderer version is pinned to a rendered Delivery?
9. What lease/fencing mechanism protects workflow and delivery workers?
10. Which three `DisplayGroup` values are exposed to the inbox UI?

If a question remains open, mark it as an explicit unresolved decision. Do not silently choose an implementation behavior.

---

## 8. Final recommendation

The design is directionally sound and worth pursuing. Its strongest aspects are the producer boundary, type-driven contracts, asynchronous processing, reuse of Juice messaging infrastructure, provider abstraction, and incremental Observability migration.

The main implementation risk is not the number of channels. It is ambiguity around identity, recipient/destination modeling, lifecycle, concurrency, and delivery guarantees.

**Recommended next step:** revise `design.md` to resolve P0 and P1 items, record the remaining decisions, and then produce implementation plans per phase. Do not ask Claude to implement the entire platform from the current draft without first updating these contracts.
