# Phase 3 plan: workflow

**Status:** accepted by the owner, 17 November 2026, with the recommendations in §2. It builds on `design.md` (section numbers like §7.1 refer to it) and
continues `phase-2-plan.md`: implementation choices go on from I27, milestones from M19, and each milestone names the build-board items it closes
(`p3-01` …).

**Goal:** a notification is no longer one send. A type's workflow can wait, hold, throttle, check a condition and escalate; requests that share a
correlation key have a lifecycle (acknowledge, resolve, cancel) that stops what is still due and updates every inbox item; inbox actions reach the
producer as interactions; and producers hear what happened through events.

**Gate:** the three phase 3 proof points (§16), each shown by a named test (§6), plus a local-stack demo and an acceptance record.

---

## 1. Scope

**In**

- The workflow runner and its steps (§7.1): `send`, `delay`, `condition`, `throttle`, `digest`, `escalate`, validated when a type is declared.
- Conditions as matchers over labels and correlation state (§7.2, D18), never `data`.
- Correlations (§5.3): `correlations`, the acknowledge, resolve and cancel APIs, and cancelling what is still due.
- Digests (D19): one delivery per destination with one item per recipient, capped at 100 listed.
- Throttles: at most N per window per key; the excess waits.
- Escalation policies (tenant-owned, admin API and provisioning) and the `escalate` step.
- Inbox items that follow their correlation, inbox actions as interactions, and the "actionable and open" filter on correlation state.
- Events to producers (§5.4): `notification.interaction`, `notification.delivery.failed`, `notification.escalation.exhausted`,
  `notification.recipient.unresolved` (phase 2 recorded it; phase 3 publishes it), and the correlation events.
- `@juice-js/notifications`: action buttons and correlation state on items.
- The `digest` preference mode, which acted as `on` until now: a person's own digest (I33).

**Out** (phase 4 and later)

- Slack and Teams interactive actions (they become interactions the same way, with the apps in phase 4).
- Storm protection (a route switching to digest-only under load) is phase 4; it reuses the digest machinery built here.

---

## 2. Owner decisions

All three recommendations were accepted on 17 November 2026 (design §19, decisions 12–14).

| # | Question | Decision | Why |
|---|---|---|---|
| p3-d1 | **How do events reach producers, and how is one application kept from seeing another's?** | Over RabbitMQ, from the outbox (phase-1-plan I3 reserved it for this): one topic exchange `juice.notifications.events`, routing key `{applicationKey}.{event}` (`observability.notification.interaction`). An application's broker user may bind only to `{applicationKey}.#`: RabbitMQ topic permissions (`read` on the exchange with that routing-key pattern), set when the application is registered with a `brokerUser`, as intake already trusts it (§19 decision 8). An application without a broker user gets no events in phase 3; a signed webhook (D14) for those comes later if one needs it. | Observability, the first producer, already speaks Juice.EventBus over RabbitMQ; the outbox gives at-least-once delivery committed with the change that caused the event; topic permissions enforce the isolation in the broker, not by convention. |
| p3-d2 | **What does an inbox action do?** | Records an interaction (`kind = action`, the action id, the person, the item) and publishes `notification.interaction`; nothing else. The item shows "sent" for that person until the producer resolves the correlation; several people may act, and each action is one interaction (the same person and action twice is one). Only while the correlation is `Open` or `Acknowledged`; after that the API answers 409 `correlation_closed`. | §7.3: "the inbox never decides anything itself". The producer owns the decision, so an approval that two people race to make is the producer's to settle, once. |
| p3-d3 | **A request on a correlation already resolved or cancelled?** | Reopens it: state `Open`, `acknowledgedBy`/`resolvedAt`/`outcome` cleared, `reopenedAt` set, and every recipient of the new notification starts afresh. Earlier notifications stay as they ended. | An alert that fires again after it resolved is a new episode of the same thing; refusing the request would drop it, and a new key per episode would break "everything about alert X". |

---

## 3. Implementation choices

| # | Choice | Why |
|---|---|---|
| I27 | **The workflow runs as its own stage over notifications.** The planner resolves recipients as now, but they wait (`NextAt` null, outcome `Pending`) unless the workflow's next step is `send`. A new stage, `workflow`, claims notifications in state `Running` and executes steps from `WorkflowStep` until one waits (`NextAt` = when to go on) or the workflow ends (`Planned`). New notification states: `Running`, `ConditionNotMet`, `Cancelled`. A type whose workflow is `[send]` (every phase 1 and 2 type) skips the stage: the planner marks its recipients due and ends the notification `Planned`, so nothing gets slower. | One step at a time, claimed like every other hop (§11), crash-safe, and its position is on the row. The fast path keeps the phase 1 load check's numbers. |
| I28 | **Steps act on the notification's current recipients**: those of the plan and, after an `escalate`, those its policy added. `send` marks the recipients of the current escalation step due for the delivery planner. `delay` waits a fixed time (1 s to 7 days). `condition` evaluates its matcher; when it fails, the notification ends `ConditionNotMet` and its waiting recipients `Cancelled`. A step's input is checked at declaration: unknown steps, keys, bounds and matchers are 422 with a pointer, as templates are. | The workflow is data validated once; the runner never meets a step it can't run. |
| I29 | **Conditions are matchers** (§7.2): `{ "correlation": ["open", "acknowledged"] }`, `{ "labels": <route match grammar> }`, `{ "all": [...] }`, `{ "any": [...] }`, nested at most 3 deep. They read labels and the correlation's state at the moment the step runs. | The route matcher already exists and is tested; reusing it keeps one grammar (D18). |
| I30 | **Throttles count in fixed windows**: `throttle_windows` (tenant, type, key, window start, count), the key from the step's `by` (`type`, `correlation`, or `labels.<name>`). A notification over the limit waits for the next window (`NextAt` = its start) and counts there; nothing is dropped. The count is an `INSERT … ON CONFLICT … DO UPDATE … RETURNING`, so concurrent workers can't both take the last place. | Fixed windows are simple to reason about and to test; the excess waiting is what §7.1 asks for. |
| I31 | **Digests are rows**: `digests` (tenant, type, key, opens, closes, state) and `digest_members` (digest, recipient). A `digest` step adds the notification's recipients to the open digest for its key (`by`: `route`, `subscriber` or `correlation`), creating it if none is open, and the notification waits until it closes. When it closes, the delivery planner plans **one delivery per destination for the digest**, its delivery items one per member recipient (D19); the template gets `items` (the first 100, with `more` = how many past that). A member recipient's outcome is `Planned` through the digest delivery. Uniqueness: one open digest per key (partial unique index); a recipient in at most one digest. | The delivery item key `(RecipientId, DestinationKey, Channel)` already makes "none sent twice" a constraint, so a digest closed twice can't send twice. |
| I32 | **Correlations are rows** (§12): created at intake when a request has a key (`INSERT … ON CONFLICT DO NOTHING` then read, as subscribers are), `requests.CorrelationId` set. `acknowledge`, `resolve`, `cancel` are producer calls under the application (D5) and audited; each is one transaction that sets the state, cancels the correlation's waiting recipients (`Cancelled`), deliveries `Pending` or `Retrying` (`Cancelled`; one `Sending` finishes and is recorded), open digests' members (removed; an emptied digest is cancelled), waiting notifications (`Cancelled`) and escalations (`Stopped`), then tells the inbox hub. Reopen per p3-d3. | Cancelling in the same transaction as the state change is what makes "resolve cancels everything still due" a guarantee, not a race (proof point 1). |
| I33 | **The `digest` preference is a person's own digest**: a person who chose `digest` for a category and channel gets that channel's deliveries of the category in one digest per hour per person (`by: subscriber`, a fixed window), through the same rows as I31. The inbox is never digested. | It was promised in phase 2 (it acted as `on`); the machinery is the same. |
| I34 | **Escalation policies are tenant configuration** (`escalation_policies`: `Key`, `Steps` = `[{ "afterMinutes", "recipients": [recipient specs] }]`, `StopOnAcknowledge`, `ManagedBy`), with an admin API and provisioning like routes. The `escalate` step names one (`policy`) or reads its key from a label (`policyFrom: labels.x`); it needs a correlation. It creates an `escalations` row (correlation, policy, `NextStep`, `NextAt`); a new stage, `escalation`, claims due rows: if the correlation is still `Open` (or `Acknowledged` and the policy doesn't stop on acknowledgement), it resolves the step's recipients through the planner's resolvers, adds them to the notification with that `EscalationStep`, and marks them due; otherwise it stops with the reason. A step that reaches nobody is recorded and the next runs at once; after the last, `notification.escalation.exhausted`. | The policy belongs to the tenant (who is on the hook), the step to the type (that it escalates). Resolving through the same resolvers keeps people one subscriber each, so a person already reached isn't reached twice (the unique `NamedAs`). |
| I35 | **Inbox items show their correlation**: `inbox_items.CorrelationId`; the item view gains `correlation` (`state`, `outcome`, `acknowledgedBy`) and the person's own `interactions`. "Actionable and open" becomes: actionable, not archived, and its correlation `Open` or `Acknowledged` (or none). A correlation change tells every affected person's sessions (`changed` with their items). | §7.3: once resolved, every recipient's item shows the outcome wherever the decision was made. |
| I36 | **Interactions** (§12): `interactions` (delivery, correlation, kind `read`, `acknowledged` or `action`, action id, subscriber, at), unique per (delivery, kind, action id) so a double click is one. `POST me/inbox/{id}/actions/{actionId}` (p3-d2) and `POST me/inbox/{id}/acknowledge`. Reading an item records `read` once. Each interaction is published as `notification.interaction`. | One table answers "who did what", and the unique key makes the events idempotent at the source. |
| I37 | **Events to producers** go through the outbox (p3-d1): `NotificationEvent` integration events with an event id (UUIDv7, the dedup key for producers), application, tenant, correlation key and a type-specific body; a publishing policy routes them to the RabbitMQ events publisher. `notification.delivery.failed` is raised when a recipient's last destination fails; `notification.recipient.unresolved` with each `ResolverUnavailable` record. The local stack gets the exchange and a test consumer. | At-least-once, committed with the change; the outbox already exists and is tested. |

---

## 4. Data model (phase 3 tables)

New, all tenant-owned with composite keys (§12):

| Table | Notes |
|---|---|
| `correlations` | `ApplicationId`, `Key` (unique per tenant and application), `State` (`open`, `acknowledged`, `resolved`, `cancelled`), `AcknowledgedBy`, `AcknowledgedAt`, `ResolvedAt`, `Outcome`, `ReopenedAt`, `UpdatedAt` |
| `escalation_policies` | `Key`, `Steps` (jsonb), `StopOnAcknowledge`, `ManagedBy`, `DefinitionHash` |
| `escalations` | `CorrelationId`, `NotificationId`, `PolicyId`, `NextStep`, `NextAt`, `State` (`running`, `stopped`, `exhausted`), `StopReason`, claim columns |
| `throttle_windows` | `TypeKey`, `Key`, `WindowStart`, `Count`; unique together |
| `digests`, `digest_members` | as I31; one open digest per `(TypeKey, Key)` |
| `interactions` | as I36 |

Changed: `requests` gains `CorrelationId`; `notifications` gains the states of I27; `notification_recipients` gains `DigestId`; `inbox_items`
gains `CorrelationId` (column reserved in phase 2); `deliveries` gains `DigestId` and the status `Cancelled`; `applications` gains
`EventsEnabled`.

---

## 5. Milestones

Owner decision (17 Nov 2026): no per-milestone review in phases 3 and 4; one review loop over all of it after phase 4. Each milestone ends with its
tests, a commit and a build-board update.

### M19: the workflow runner (`p3-01`, `p3-03`, `p3-04`)

- I27–I30: workflow declaration and validation, the `workflow` stage, `send`, `delay`, `condition`, `throttle`, the `[send]` fast path.
- Tests: a delayed notification is sent after its delay and not before; a failed condition ends `ConditionNotMet` and sends nothing; a throttle of
  N lets N through per window and the rest wait for the next, none lost under concurrent workers; phase 1's load check unchanged.
- *As built:* the workflow runs inside the planner stage, not a stage of its own: the claim store hands out any due notification, so two
  stages over one table would take each other's rows; the planner claims `Planning` and `Running` and dispatches on the state
  (`WorkflowRunner`). A finished workflow ends `Planned` (no `Completed` state), so everything reading `Planned` as "sent on" keeps working;
  new states `Running`, `ConditionNotMet`, `Cancelled` (stored as text). A workflow's people wait as `Pending` (not due) and its `send` step
  applies the tenant's mutes and the people's preferences as they are then, as the planner does on the fast path. `delay` keeps when it began
  (`notifications.step_entered_at`); a nudge before it ends puts the notification back. `throttle` counts by `type`, `correlation` (types with
  `correlation: required`) or `labels.<name>` (not by route: a notification can match several), in `throttle_entries` (one row per
  notification and step, under an advisory lock per tenant, type and key, in its own short transaction; entries a window old are removed on the
  way); the window is fixed (epoch-aligned). A request that expires while its workflow waits ends `Expired`, its people cancelled. `digest` and
  `escalate` are refused at declaration until M21 and M22. Conditions read correlation state as `open` for a request with a key until M20.

### M20: correlations (`p3-05`, part of `p3-07`)

- I32, I35: intake creates and reopens correlations; acknowledge, resolve, cancel; cancellation of everything still due; inbox items follow.
- Tests: proof point 1; acknowledge keeps deliveries going; a request on a resolved correlation reopens it; another application's key is not found.
- *As built:* `correlations` (unique per tenant, application and key; PostgreSQL's `xmin` as the row version), `requests.correlation_id`,
  `inbox_items.correlation_id` (set by the inbox provider from the item's request). Intake upserts the correlation (committed on its own,
  as subscribers are), reopens it if closed, and touches it either way, so the request's save carries the row version: a resolve committed
  meanwhile fails the save, and the request is answered 409 `concurrent_change` (HTTP) or comes back (RabbitMQ). `CorrelationLifecycle`
  (not a mediator command: bulk statements in one transaction with the state change): acknowledge (open only; again is a no-op),
  resolve (`outcome`, `by`), cancel; closed ones answer 409 `correlation_closed`. Resolve and cancel cancel notifications still `Planning`
  or `Running`, recipients `Pending`, `Deferred` or `Planned` and not yet taken (`next_at` set), and deliveries `Pending` or `Retrying` no
  dispatcher holds, moving each row's version on so a worker mid-step loses its save; the dispatcher also cancels, rather than sends, a
  delivery whose correlation is closed (it may have been held as the resolve ran). Every person with an item of the correlation is told
  (`changed`). Items carry `correlation` (`state`, `outcome`, `acknowledgedBy`, `resolvedAt`); "actionable and open" (filter and count)
  needs the correlation open or acknowledged. `GET …/correlations/{key}` shows the state, the latest 100 notifications and the recipients'
  and deliveries' counts. A key with `/` is sent encoded. A disabled application may still acknowledge, resolve and cancel. Conditions
  read the correlation's state when their step runs. The tenant admin's read-only view (§7) waits for a need.

### M21: digests (`p3-02`)

- I31, I33: the `digest` step and the person's digest preference; one delivery per destination with N items.
- Tests: proof point 3; a digest closing while a worker crashes sends once; a resolved correlation's members leave its digest.
- *As built:* `{ "step": "digest", "windowSeconds", "by": "route" | "subscriber" | "correlation" | "type" }` must be followed by `send`, which
  it stands for. The runner puts each waiting recipient in the open digest for its key (a route recipient by its route, a person by
  themselves, the request's correlation, or one per type), creating it with `closes_at` = now + the window, in a short transaction per key
  under an advisory lock, committed at once; the notification goes on past the send. A new stage, `digest`, claims a digest when it closes:
  it marks it closed under the same lock (so nobody joins it afterwards), then plans the members still waiting in one step: mutes and
  preferences as at a send, the delivery planner's destinations (its destination, personal-rule and render code shared, not copied),
  then **one delivery per destination and channel** (`deliveries.digest_id`) with an item per member, its content rendered once with
  `items` (the first 100, by acceptance) and `more`; a passthrough webhook gets an envelope with `items`. The inbox still gets an item per
  notification. A person in quiet hours gets the digest's other deliveries when the hours end (the delivery's `next_at`; the digest
  isn't held). Running the close again plans only members still waiting; delivery items' unique key keeps any from going twice. A digest
  whose members all left (a resolved correlation cancels them) ends `Cancelled`. **Not built:** I33, a person's own hourly digest from a
  `digest` preference (deferred #45): that mode still acts as `on`.

### M22: escalation (`p3-06`)

- I34: policies (API, provisioning), the `escalate` step, the `escalation` stage.
- Tests: proof point 2; a step reaching nobody runs the next at once; the last step raises `escalation.exhausted`; resolve stops it.
- *As built:* `escalation_policies` (`Key`, `Steps` = `[{ "afterMinutes": 0–10080, "recipients": [1–20 specs] }]` up to 10, recipients a
  route or people through an enabled resolver of the tenant that handles the kind, checked when saved; `StopOnAcknowledge`, default true),
  `PUT/GET/DELETE …/escalation-policies/{key}` (mediator commands, audited). The step: `{ "step": "escalate", "policy": "key" }` or
  `"policyFrom": "labels.<name>"`, once per workflow, for types that can carry a correlation; without a correlation or with an unknown policy
  it does nothing (logged) and the workflow goes on. It adds one `escalations` row per notification (saved with the step). The `escalation`
  stage claims it when due: stops it if the policy is gone or the correlation closed or acknowledged (for a policy that stops on that);
  otherwise hands the step to the planner rather than resolving itself: the notification goes back to `Planning` with
  `escalation_step` and `escalation_specs`, and the planner resolves those names, numbers the new recipients with the step, leaves out
  anyone it reached already (by `NamedAs`), and sends them at once whatever the workflow; a step that reaches nobody new makes the
  escalation due at once. An escalation waits (5 s at a time) while its notification is still running its workflow or planning an
  earlier step. Acknowledging stops the escalations whose policy stops on it, in its transaction; resolving and cancelling stop all of
  them. `escalation.exhausted` is published with M23's events. Provisioning policies from configuration waits for a need.

### M23: interactions and events (`p3-07`, `p3-08`)

- I36, I37 and p3-d1, p3-d2: inbox actions and acknowledgement, interactions, events over RabbitMQ with per-application isolation;
  `@juice-js/notifications` action buttons and correlation state.
- Tests: an action is one interaction and one event however often it's clicked; an application's broker user can't bind another's events;
  `delivery.failed` and `recipient.unresolved` are published once.
- *As built:* `interactions` (item, delivery, correlation, person, kind `read` | `acknowledged` | `action`, action id; unique per item, kind
  and action): `POST me/inbox/{id}/actions/{actionId}` (an action the item has; it reads the item) and `POST me/inbox/{id}/acknowledge`,
  both refused with 409 `correlation_closed` once decided; the first read is an interaction too. Items list the person's interactions.
  Events: raised as `producer_events` rows saved with the change that raised them (a stage step's save; the correlation and inbox
  transactions), not through the outbox directly, since the outbox saves the whole context and a stage step's save is the stage's own;
  `ProducerEventRelay` moves them into the outbox in batches (`FOR UPDATE SKIP LOCKED`, so replicas share it), and the outbox publishes
  them on the `events` route: RabbitMQ, topic exchange `Events:Exchange` (`juice.notifications.events`), declared with the intake's
  topology on its connection; the routing key is the event's name, `{application}.notification.{event}` (Juice uses `IEvent.EventName`).
  `NotificationEvent` carries the event id (its message id, UUIDv7), application, tenant, correlation key, notification, request and type,
  and the event's own fields. Raised: `interaction`, `correlation.acknowledged|resolved|cancelled`, `escalation.exhausted`,
  `recipient.unresolved` (the planner's `ResolverUnavailable` names), and `delivery.failed` (a delivery failed and every other one of
  the recipient's ended without success). Only with `Events:Enabled` (which needs the RabbitMQ intake) and for applications with a
  `brokerUser`. Topic permissions are the deployment's (README: Events to producers). `@juice-js/notifications`: action buttons while
  the correlation is open, the chosen one marked, the outcome once decided (juice-layout e83bbfd).

### M24: proof points and acceptance (`p3-g1` … `p3-g3`)

- The three proof point tests green; phases 1 and 2 still green; a local-stack demo; `docs/phase-3-acceptance.md`.

---

## 6. Proof points and their tests

| # | Proof point (§16) | Test (integration project) |
|---|---|---|
| 1 | Resolving a correlation cancels everything still due and updates every recipient's actionable item | `Resolving_cancels_every_waiting_recipient_delivery_and_escalation_and_updates_each_actionable_item` |
| 2 | Acknowledging stops an escalation that stops on acknowledgement | `Acknowledging_stops_an_escalation_that_stops_on_acknowledgement_and_not_one_that_doesnt` |
| 3 | A digest of N notifications is one delivery with N items, none sent twice | `A_digest_of_N_is_one_delivery_per_destination_with_N_items_and_none_twice` |

---

## 7. API surface (phase 3 additions)

| Who | Endpoint |
|---|---|
| Producer | `POST /api/v1/tenants/{tenantId}/correlations/{key}/acknowledge`, `…/resolve`, `…/cancel`; `GET …/correlations/{key}` |
| Tenant admin | `GET/PUT/DELETE …/escalation-policies/{key}`; `GET …/correlations/{key}` (any application's, read-only) |
| Signed-in person | `POST …/me/inbox/{id}/actions/{actionId}`, `POST …/me/inbox/{id}/acknowledge`; items carry `correlation` and `interactions` |
| Events (RabbitMQ) | `{app}.notification.interaction`, `…delivery.failed`, `…escalation.exhausted`, `…recipient.unresolved`, `…correlation.acknowledged`, `…correlation.resolved`, `…correlation.cancelled` |

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| A new stage slows every notification | The `[send]` fast path (I27) skips it; the load check runs in M19 and M24. |
| Resolve races a dispatcher that already claimed a delivery | A `Sending` delivery finishes and is recorded (it may have gone out); only `Pending` and `Retrying` are cancelled, in the resolve's transaction. |
| Digest closing twice | Delivery items are unique per recipient, destination and channel; the close is a claimed step. |
| Events leaking across applications | Broker topic permissions per application user (p3-d1), tested in M23. |
| Escalation reaching someone twice | Recipients are unique per notification and `NamedAs`; a person already reached is not added again. |
