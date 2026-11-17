# Phase 2 plan: people and inbox

**Status:** accepted by the owner, 15 November 2026, with the recommendations in §2 and the choices in §3. It builds on `design.md` (section
numbers like §6.4 refer to it) and continues `phase-1-plan.md`: implementation choices go on from I14, milestones from M10, and each milestone names
the build-board items it closes (`p2-01` …).

**Goal:** the service knows people. Requests can name users, groups and topics; each person becomes one subscriber with contact points, device
registrations, preferences and quiet hours; the mandatory policy (§6.4) decides who hears what; and every person has an inbox, grouped by the
tenant's display groups, live over SignalR, with an Angular library for the Juice apps.

**Gate:** the six phase 2 proof points (§16), each shown by a named test (§6 below), plus a local-stack demo and an acceptance record.

---

## 1. Scope

**In**

- Subscribers by `(Source, ExternalId)`, with aliases linked explicitly (D23).
- Per-tenant resolver configuration (`recipient_resolvers`) and three new resolvers: local (users and local groups), topic, and identity.
- Resolver bounds (10,000), group expansion caching (5 minutes), and resolver failure handling (`ResolverUnavailable`).
- Contact points (email now; phone, Slack and Teams users stored for phase 4) with verification and health, encrypted at rest.
- Device registrations for push (§9.2). The push providers themselves are phase 4.
- Preferences per category and channel, quiet hours, tenant mutes, and the mandatory policy (§6.4).
- The display group catalog with tenant customization (D12).
- The inbox: a built-in channel, an API for the signed-in person, and a SignalR hub.
- `@juice-js/notifications` in `juice-layout`: an inbox bell and panel, a preferences page and a contact points page.

**Out** (later phases)

- Inbox actions as interactions, correlation state on inbox items, and events to producers (phase 3), including
  `notification.recipient.unresolved`. Phase 2 records and counts an unresolved recipient; phase 3 publishes it.
- Push, SMS, Slack and Teams providers, and the Console's Web Push service worker helper (phase 4). Phase 2 stores device registrations and
  phone or chat contact points, but sends nothing to them.
- On-call and external directory resolvers (later).

---

## 2. Owner decisions

Both recommendations below were accepted by the owner on 15 November 2026. For p2-d1, a user belongs to a tenant through a `platform_tenant` user
attribute in Keycloak, issued as the token's `platform_tenant` claim by a user-attribute mapper (the local stack's clients issue it as a hard-coded
claim today).

| # | Question | Decision | Why |
|---|---|---|---|
| p2-d1 | **Identity resolver contract.** What the Juice identity/tenant service exposes for users, groups and roles, and whether its emails are trusted. | `juice-tenants` exposes tenants and their settings only; no users, groups or roles. The identity source is the OIDC provider. Build the resolver against an `IIdentityDirectory` abstraction, with one implementation over the **Keycloak admin REST API** (users, groups, realm and client roles), reading as a service account with `view-users` only. Trust an email only when Keycloak marks it `emailVerified`. Users are scoped to a tenant by the same attribute that issues the `platform_tenant` claim (§19 decision 9). | It's the provider the stack already runs, it carries a verified-email flag, and the abstraction leaves room for Entra ID or SCIM later (the external directory resolver). The owner must confirm that Keycloak is the source for every deployment, and how a user belongs to a tenant there. |
| p2-d2 | **Who links subscriber aliases:** tenant admins only, or a sync a resolver declares? | **Tenant admins only in phase 2**, through the API, audited. One automatic link is allowed: the signed-in person's own subscriber (I16). A resolver-declared sync waits until an external directory resolver needs it. | Linking merges two people's inboxes and preferences; a wrong automatic link leaks one person's notifications to another. Admin linking is the smallest safe rule, and D23 already rules out matching by email. |

---

## 3. Implementation choices

| # | Choice | Why |
|---|---|---|
| I14 | **Resolvers return people and destinations.** `ResolveResult` gains `People` (`ResolvedPerson`: source, external id, name, locale, time zone, and the contact points the source vouches for), as §6.1 sketches. The planner maps each person to a subscriber and stores **one recipient per subscriber**, `NamedAs = subscriber:{id}`, with the names that reached them (`group:sre`, `user:adam`) in a new `Via` column. | Deduplication becomes the existing unique `(notification, step, NamedAs)` key, so a person named twice can't get two recipients even under concurrent planning (proof point 3 still holds). `Via` keeps "why did I get this?" answerable. |
| I15 | **Subscribers are created on first resolution,** with one `INSERT … ON CONFLICT (tenant_id, source, external_id) DO UPDATE … WHERE` changed, before the planner's step saves (a stage step holds no transaction while it runs, §11), then a read. An identity linked as an alias reaches its subscriber first; a subscriber linked into another (`MergedInto`) reaches that one. Contact points from a source are upserted the same way in M13. | Two planners resolving the same new person at once end with one subscriber. The upsert is safe to repeat: a step that fails or loses its lease leaves at most subscribers a later plan would make anyway, and only rows whose name, locale or time zone changed are written. |
| I16 | **The signed-in person is a subscriber of the source `oidc`,** with `ExternalId` = the token's `sub`. When the identity resolver's source is that same provider, the two are one source (`identity` resolves to `oidc:{sub}`), so no alias is needed. A tenant using only local users links each local user to its `oidc` id once (p2-d2). | The inbox and preferences API must find "me" without guessing. Reusing the token's subject is identity, not address matching, so D23 still holds. |
| I17 | **The inbox is a channel in the pipeline,** registered by the core (`inbox`, address kind `None`, audience People, `RepeatsSafely`), with a built-in provider that inserts the `inbox_items` row in the dispatcher's transaction. A type with no `inbox` template gets a default: title = the type's name, body empty, link = `links.self`. | The inbox gets history, retries, idempotency (`inbox:{subscriberId}` destination key) and redaction from the machinery that already exists, and a mandatory type always has something to show (§6.4). |
| I18 | **SignalR is told after commit.** The inbox provider queues a change, the dispatcher's post-commit hook sends it to the person's group (`tenant:{id}:sub:{id}`). Read and archive go through the API, which notifies the same group after its commit. Clients reload on reconnect. Several app replicas use the **Redis backplane** (`Inbox:SignalR:Redis`); without it one replica serves everything. | A notification can't appear before it exists, and a missed message costs only a reload. Redis is the backplane Microsoft supports; the local stack gets a Redis container for the two-replica test. |
| I19 | **Contact addresses are encrypted with AES-GCM** under a key named by a secret reference (`Encryption:KeyRef`, D15), with the key id stored per row for rotation, plus an HMAC of the normalized address (`AddressHash`) for uniqueness and lookups. Device tokens are handled the same way (`TokenHash`, §12). *As built (M13):* the HMAC key is a second reference (`Encryption:HashKeyRef`) that never rotates, since the hashes are the unique keys; rotated keys stay readable under `Encryption:PreviousKeyRefs`. The associated data binds a value to its tenant, person and channel (a device token to its tenant, platform and app). Both keys default to the variables `NOTIFICATIONS_CONTACT_KEY` and `NOTIFICATIONS_CONTACT_HASH_KEY`, and the host doesn't start without them. A delivery's snapshot names the contact point and its masked address (stored on the point when added, so planning never decrypts); only the dispatcher decrypts, at send time, and keeps the address out of the error text a provider returns. | §13.2 asks for encryption at rest; the hash keeps `UNIQUE (subscriber, channel, address)` and bounce lookups possible without decrypting. |
| I20 | **Email verification goes through the pipeline itself:** a built-in type `notifications.contact.verify` (mandatory, external recipient, email template) carries a six-digit code, valid 15 minutes, five tries, stored hashed. Phone verification waits for SMS (phase 4); until then a phone contact point stays unverified and unused. *As built (M13):* the service provisions its own application (`notifications`, never a producer, refused by the application API) and type at start; the person asks for a code (`POST me/contact-points/{id}/verification`), at most once a minute per address and 10 times an hour per person, and the tenant needs an enabled integration for the channel. The code is submitted through intake naming the contact point (recipient kind `contact-point`, accepted from the service's own requests only), so it shows in history while the address appears nowhere but the encrypted contact point; intake refusing it gives the code and the hourly allowance back. A wrong try counts even if nothing else is saved. Its `redact` path isn't masked yet (deferred #29). | No second sending path to secure; the delivery shows in history like any other. |
| I21 | **Preferences are evaluated twice.** The planner applies tenant mutes, category opt-out and personal mutes (outcome `Muted` or `OptedOut`, mandatory bypass per §6.4, recorded in `Bypassed`). The delivery planner applies channel turn-off, verification and declared channels, and always keeps the inbox for a mandatory type. **Quiet hours hold personal channels, not the inbox:** the inbox delivery is planned at once; the others are planned with `NextAt` = the end of quiet hours in the person's time zone (outcome `Deferred` until then). | Whether someone hears belongs to planning; how they hear belongs to destination choice (§6.2). An inbox item appearing silently during quiet hours disturbs no one. |
| I22 | **Resolver failure keeps the notification in `Planning`.** A resolver throwing `ResolverUnavailableException` (timeout, 5xx, connection) rolls the plan back; the notification is retried with backoff (1 s doubling, capped at 30 s) until `Resolvers:WaitLimit` (5 minutes) after its first attempt. Then it is planned with what resolved, and each failed named recipient is stored with outcome `ResolverUnavailable`, logged at Warning and counted (`recipients_unresolved`). *As built (M12):* the first failure is kept on the notification (`ResolverWaitSince`); each wait is as long as the wait so far (1, 1, 2, 4 … s, then 30 s), and the last try falls on the limit. A failed name is a marker recipient: `NamedAs` is what was named (`group:ops`), no subscriber, never due. | Planning stays one transaction, and a resolver outage delays notifications instead of silently dropping people, as §6.1 requires. |
| I23 | **Caching:** the identity resolver caches group and role expansions per tenant for `Resolvers:CacheSeconds` (300), in memory, keyed by resolver and name. Local and topic resolvers read the database in the planner's transaction and are never cached. Bounds: a named recipient expanding past the tenant's `MaxRecipients` (10,000) keeps the first 10,000 by external id, stores outcome `Truncated` on a marker recipient, and is audited. *As built (M12):* `Resolvers:MaxRecipients` is service-wide until tenant settings exist (deferred #28); the marker and a Warning log are the audit trail. The cache (`ResolverCache`) is also keyed by the resolver configuration's definition hash, so a changed configuration starts afresh. Resolvers read one past the bound and no more; the local and topic resolvers order by byte order (`COLLATE "C"`). | Local data is already consistent and cheap to read; only remote calls need a cache. Ordering by id makes truncation repeatable on a retry. |
| I24 | **Display groups are seeded lazily:** the first read or write of a tenant's catalog inserts `approval`, `system`, `general` (`BuiltIn`). An inbox item stores its group key when delivered; moving a type rewrites the open items' group in the same transaction. | No migration step has to know every tenant, and a move shows at once in every inbox (proof point 6). |
| I25 | **The person's API lives under `/api/v1/tenants/{tenantId}/me/…`,** authorized by the token's tenant (`ITenantAccess` gains `IsMember`). §9.2's `/api/v1/me/devices` moves there. | The same tenant routing as every other endpoint, and a token can't reach another tenant's inbox by changing a header. |
| I26 | **`@juice-js/notifications`** is a new library in `juice-layout/projects/juice-js`, following the `auth` and `tenant` libraries: an `InboxService` (REST plus `@microsoft/signalr`), a bell with unread count, a panel with group tabs and an "actionable and open" filter, and preferences and contact points pages. Released with the next `juice-layout` batch. | The apps already take auth and tenant context from those libraries. |

---

## 4. Data model (phase 2 tables)

New tables, all tenant-owned with composite keys (§12):

| Table | Notes |
|---|---|
| `subscribers` | `Source`, `ExternalId` (unique per tenant), `Name`, `Locale`, `TimeZone`, `QuietHours` (jsonb: days, start, end), `CreatedAt`, `LastResolvedAt` |
| `subscriber_aliases` | `SubscriberId`, `Source`, `ExternalId` (unique per tenant), `LinkedBy`, `LinkedAt` |
| `recipient_resolvers` | as §12; `ManagedBy` config or API (I12 provisioning extended) |
| `local_groups`, `local_group_members` | `Key` (unique per tenant), `Name`; members by subscriber, of any source; a local user is the subscriber `local:{id}` |
| `topics`, `topic_subscriptions` | `Key` (unique per tenant, as requests name it), `Name`, `SelfService` (people may follow it themselves through `me/topics`; otherwise an admin adds them); subscriptions by subscriber, unique per topic |
| `contact_points` | `SubscriberId`, `Channel`, `Address` (encrypted), `AddressHash`, `KeyId`, `VerifiedAt`, `VerifiedBy` (`self` or a source), `Health` (`ok`, `bouncing`, `failed`), `HealthReason`; unique `(SubscriberId, Channel, AddressHash)` |
| `contact_verifications` | `ContactPointId`, `CodeHash`, `ExpiresAt`, `Tries` |
| `device_registrations` | as §12, plus `KeyId` |
| `preferences` | `SubscriberId`, `Category` (or `*`), `Channel` (or `*`), `Mode` (`on`, `off`, `digest`; digest acts as `on` until phase 3) |
| `mutes` | as §12; tenant mutes, plus personal mutes and snoozes with `SubscriberId` set |
| `display_groups`, `type_display_groups` | as §12 |
| `inbox_items` | as §12; `CorrelationId` stays null until phase 3 |

Changed: `notification_recipients` gains `SubscriberId` (composite FK), `Via` (jsonb), `Bypassed` (jsonb: which rules a mandatory type bypassed), and the
outcomes `muted`, `opted_out`, `deferred`, `truncated`, `resolver_unavailable`; `notifications` gains `ResolverWaitSince` (M12); `notification_types` gains `Actions` (validated at declaration,
used by phase 3).

---

## 5. Milestones

Each milestone ends with its review loop (code-review rounds, then a confirmation) and a build-board update, as in phase 1.

### M10: subscribers and the resolver contract (`p2-01`, `p2-02`)

- `ResolvedPerson`, `ResolveResult.People` (I14); the route and address resolvers unchanged in behavior.
- `subscribers`, `subscriber_aliases`, the planner's subscriber mapping (I15) and per-subscriber recipients with `Via`.
- `recipient_resolvers`: the API and provisioning, and the planner picking the resolver per kind (and `"resolver": "<key>"`, §5.2).
- Alias linking and unlinking for tenant admins (p2-d2), audited; unlinking never splits recipients already planned.
- Tests: concurrent planning of one new person makes one subscriber; a person reached through a group and directly is one recipient with two `Via`
  entries (proof point 3 of phase 2); a resolver the tenant hasn't enabled refuses the request at intake (422 with a pointer).

### M11: local and topic resolvers (`p2-03`, `p2-04`)

- `LocalRecipientResolver` (`user`, `group`) over `local_groups`; `TopicRecipientResolver` (`topic`).
- Tenant admin API for local users, groups and members; topics; the person's own topic subscriptions (`me/topics`).
- Tests: group membership changes affect only notifications planned afterwards; a topic with no subscribers plans no one and says so in the
  recipient's outcome.

### M12: resolver bounds, caching and failure (`p2-06`)

- I22 and I23: `ResolverUnavailableException`, the wait limit, truncation, the cache, metrics `recipients_unresolved` and `recipients_truncated`.
- A fault-injecting test resolver (test project only) to drive outages.
- Tests: proof point 4 (an outage shorter than the limit plans everyone; a longer one plans the rest and records `ResolverUnavailable`); truncation is
  repeatable across a retry.

### M13: contact points and device registrations (`p2-07`, new `p2-12`)

- Contact points for the person (`me/contact-points`: add, verify, remove) and from sources; I19 encryption; I20 verification.
- Health: the SMTP provider's permanent recipient failures (5.1.x) mark the contact point `failed`; the delivery planner skips failed points.
- Device registrations (`me/devices`, §9.2): re-registration moves a token to the current person; registrations unseen for 60 days are removed by
  a daily sweep.
- Tests: proof point 2 (an unverified or failed contact point is never planned, even for a mandatory type); a code works once and expires; a token
  registered by a second person leaves the first.
- *As built:* `me/contact-points` (list, add, remove, `…/{id}/verification` to send a code, `…/{id}/verify`) and `me/devices` (list, register,
  remove). A person has up to 20 contact points and 50 devices (registering past that drops the longest unseen). Contact points are email only
  until other channels register (deferred #30); a person's own include those of an identity linked into them. A source's addresses
  (`ResolvedPerson.Contacts`) are upserted with the subscribers; a source can verify an address but never un-verify one. The delivery planner
  plans a person to each verified, working contact point on a channel the type has a template for; the dispatcher reads the point again when
  sending, so one removed, unverified or failed since isn't sent to. A provider's `SendResult.AddressGone` (SMTP 5.1.1, 5.1.6 or 5.1.10: the mailbox itself is wrong) fails the point
  (`notifications.contact_points_failed`); verifying it again restores it. The daily sweep (`Devices:Unseen`, 60 days) also removes codes a
  day past expiry. Schema: `contact_points`, `contact_verifications`, `device_registrations`, and the person's hourly code count on `subscribers`.

### M14: preferences, quiet hours, mutes and the mandatory policy (`p2-08`)

- `preferences`, quiet hours, tenant and personal mutes, the API for each (`me/preferences`, tenant `mutes`).
- I21: both evaluation points, every row of the §6.4 table, `Bypassed` recorded; tenant mutes on a mandatory type only when they name it exactly,
  flagged in the mute's response.
- Tests: proof point 1 (a mandatory type reaches a person who opted out of its category, at least in the inbox); one test per §6.4 row; quiet hours
  across a time zone's DST change.
- *As built:* `me` (`GET`: the person, with their quiet hours), `me/preferences` (`GET`, `PUT` replaces all, up to 200), `me/quiet-hours`
  (`PUT`, `DELETE`) and `me/mutes` (`GET`, `POST`, `DELETE` cancels; up to 100 in force or to come), and the tenant's `mutes` (the same, up to
  500, `?all=true` lists ended and cancelled ones). §7's `PATCH me` became `me/quiet-hours`: name, locale and time zone come from the source
  and its next resolution would overwrite them, so quiet hours carry their own time zone (else the person's, else UTC). A preference names a
  type's category or `*` and a channel (`inbox`, one reaching people, or `*`); the category's choice wins over `*`. A mute's match is a
  route's (type patterns, labels, minimum priority; `{}` is everything, a snooze when it has an end); it is cancelled, never deleted. The
  planner applies tenant mutes (to every kind of recipient), then category opt-out (`channel = *`), then personal mutes; a mandatory type is
  held only by a tenant mute naming it exactly (`RouteMatch.ExactTypes`, flagged as `appliesToMandatory`) and records each other rule it
  passed in `Bypassed` (`tenant-mute`, `opt-out`, `personal-mute`, `quiet-hours`). The delivery planner drops channels turned off (a person
  left with none is `OptedOut`) and, in quiet hours, plans the inbox now and defers the rest (`Deferred`, due at the end, computed in wall-clock
  time: an end the clocks skip is when they jump, one shown twice is the first still to come). Schema: `preferences`, `mutes`,
  `subscribers.quiet_hours`, `notification_recipients.bypassed`. A recipient is `Planned` once it has any delivery on record, as in phase 1 (history says how each ended); one deferred whose request expired or that is given up stays `Planned` if its inbox item is on record, else `Cancelled` or `Failed`. Half a surrogate pair on its own (U+D800) in stored JSON (data, a schema, templates, a match, settings) is now a field error, not a 500 (`CanonicalJson.Unstorable`). Deferred #36–#38.

### M15: display groups and the inbox (`p2-09`, `p2-10`)

- Display group catalog API (I24): add, rename, reorder, delete (not the three defaults), move a type.
- The inbox channel and provider (I17), `inbox_items`, `me/inbox` (list by group, unread counts, read, read all, archive), the "actionable and open"
  filter (actionable only in phase 2), and the SignalR hub (I18) with the Redis backplane.
- Tests: proof point 5 (read state syncs across two sessions on two replicas); proof point 6 (a type moved to a custom group shows there, open items
  move with it, and its actions are still listed); a person reads only their own items.
- *As built:* first deferred #27: `me` and the hub refuse a client's own token (`not_a_person`), and the local realm gained person accounts
  (`ann`, `ben`, password grant through the public client `people-test`, `PEOPLE_TEST_PASSWORD`; `scripts/keycloak-update.sh` adds them to an
  existing stack). The core always hosts `inbox` (a package can't define it): audience people, no address, content `Item` (a first line `Title: …`
  gives the title, else the type's name; the rest is the body; the link is `links.self` when http(s)); a type without an inbox template gets that
  default. Every person recipient gets an inbox destination (`inbox:{subscriberId}`) through the tenant's own integration `notifications.inbox`
  (kind `inbox`, `ManagedBy = service`), inserted on first use: every delivery names an integration, so history, retries and dead-letter retry
  work unchanged; the API lists it and refuses to change, delete or target it (`managed_by_service`), and refuses a new `inbox` integration.
  The inbox provider writes the item in its own transaction, as a send is, idempotent on the delivery (`ix_inbox_items_delivery`), not in the
  dispatcher's step transaction as I17 said: a recorded attempt can't then say "delivered" without the item. It resolves the group when it
  writes (in the insert, under the tenant's catalog lock taken shared; a move or a group's delete takes it exclusive) (the tenant's choice for the type, else the declared group if the tenant has it, else `general`) and copies the content and the type's
  actions. Types declare `actions` (up to 5 `{ id, label }`, hashed only when present, so earlier declarations keep their hash); an item with
  actions is `actionable`, and "actionable and open" is that and not archived. Display groups: `GET …/display-groups` (seeded on first read),
  `PUT/DELETE …/display-groups/{key}` (up to 50; built-ins renamed and reordered, never deleted, `display_group_built_in`), `PUT/DELETE
  …/types/{typeKey}/display-group` (a move, or back to the declared group), each rewriting open items in its transaction; deleting a group sends
  its types back to their declared groups with their items. The person's inbox: `GET me/inbox` (`group`, `unread`, `actionable`, `archived`,
  `before`, `limit` ≤ 100), `GET me/inbox/counts` (each group in order, unread, actionable and open), `GET me/inbox/{id}`, `POST/DELETE
  …/{id}/read`, `POST/DELETE …/{id}/archive` (archiving reads it; restoring puts it in its type's current group) and `POST me/inbox/read-all`
  (`group`). The hub `/hubs/inbox?tenant=` (token as `access_token` in the query for WebSockets) has one event, `inboxChanged` `{ kind, items }`,
  with `added`, `changed` or `reset` (reload: many items at once, or a tenant-wide move): one event instead of §7's three, since a client
  reloads its counts on any of them; a session closes when its token expires and reconnects with a fresh one. Where a type's items go is one database function, `notifications.inbox_group(tenant, type)` (the tenant's choice, else the group the type's latest version declares if the tenant has it, else `general`), used to write, restore and re-home items. Catalog changes and restores are services with their own transactions (`DisplayGroupEditor`, `PersonInboxEditor`), not mediator commands: a change and its items' rewrite are one transaction holding the tenant's catalog lock (an advisory lock: exclusive to change the catalog, shared to write or restore an item), and a command's transaction opens only after its handler. Adding a group re-homes items of types that declare it. The Redis backplane is `Inbox:SignalR:Redis`; the local stack gained Redis (`localhost:6380`). Schema:
  `display_groups`, `type_display_groups`, `inbox_items`. Deferred #39–#41.

### M16: identity resolver (`p2-05`, after `p2-d1`)

- `IIdentityDirectory` and the Keycloak implementation (or the owner's choice); `IdentityRecipientResolver` (`user`, `group`, `role`), trusting only
  verified emails.
- Local stack: Keycloak seeded with users, groups and roles across two tenants.
- Tests: a group expands to its members within its tenant only; an unverified email arrives as an unverified contact point; Keycloak stopped mid-plan
  follows I22.
- *As built:* the directories are the deployment's, by name (`Identity:Directories:{name}`: `BaseUrl`, `Realm`, `ClientId`,
  `ClientSecretRef`, `Source` = `oidc`, `TenantAttribute` = `platform_tenant`, `PageSize` 100, `MaxScanned` 10,000, `Timeout` 10 s per request and `NameBudget` 20 s per name, each at most 5 minutes). The planner gives resolvers a step deadline
  (`ResolveContext.StepDeadline`, two thirds of the stage's lease from the step's start; `StageContext.Lease`), so a slow Keycloak or several
  large listings can't hold a step past its lease, where another worker would take it over and repeat it: when the name's budget or the
  step's deadline runs out, a listing keeps the people read so far and is truncated, and a name with nothing read yet is unavailable), like
  SMTP servers: a tenant's resolver (kind `identity`) names one in its settings (`{ "directory": "keycloak" }`, nothing else), so the API can't
  send the service account's credentials to a host of an admin's choosing. `user` is a user id (the token's `sub`), `group` a group path
  (`ops` or `/ops`, `eng/sre`; direct members only), `role` a realm role (direct holders only, not through a group or a composite role). A person counts
  only when enabled and their `platform_tenant` attribute is exactly this tenant; groups and roles span tenants, so the directory reads every member up to
  `MaxScanned` (not only one past the planner's bound: the planner keeps the first by id, and Keycloak lists by username), and the name is
  truncated only when someone is left past the limit. People come as source `oidc` with the directory's id, so
  the person reached and the person signed in are one subscriber (I16); their email is a contact point, verified only when Keycloak says
  `emailVerified`. A timeout, a refused connection, a 5xx or 429, and a refusal (401 after renewing the token once, 403, a 400 from the token
  endpoint: logged at Error once a minute per directory, resource and status, since it's the account) make the directory unavailable (I22); a 404, or a 400 for a name Keycloak won't take, is
  no one; a directory removed from the deployment makes the resolver
  unavailable too. User lookups are cached with the expansions (I23), per tenant: each tenant naming one group reads it (deferred #44). Only a missing first
  page means no one; a later page that is missing or not a list, or a scan-limit check that isn't one, makes the directory unavailable, so
  people are never dropped silently. **Roles:** the service account needs realm-management `view-users` and
  `view-realm` (a realm role's holders), not `view-users` alone as p2-d1 said; client roles would need `view-clients`, which reads every
  client's secret, so they aren't resolved (deferred #43). Local stack: the `platform_tenant` attribute declared admin-only in the realm's user
  profile and issued by `people-test` from the person (it was hard-coded); people in two tenants, the group `ops`, the realm role `on-call`,
  and the client `notifications-directory` (`NOTIFICATIONS_DIRECTORY_CLIENT_SECRET`); the app's directory `keycloak`.

### M17: `@juice-js/notifications` (`p2-11`)

- I26, with unit tests in the library and a demo page in the `juice-layout` app against the local stack.

### M18: proof points and acceptance (`p2-g1` … `p2-g6`)

- All six proof point tests green; phase 1's proof points and load check still green with subscriber recipients in the mix; a local-stack demo;
  `docs/phase-2-acceptance.md`.

---

## 6. Proof points and their tests

| # | Proof point (§16) | Test (integration project) |
|---|---|---|
| 1 | A mandatory type reaches a person who opted out of its category, at least in the inbox | `Mandatory_type_reaches_an_opted_out_person_in_the_inbox_and_records_the_bypass` |
| 2 | An unverified contact point is never used | `Unverified_or_failed_contact_points_are_never_planned_even_for_mandatory_types` |
| 3 | A person reached through a group and directly gets one notification | `A_person_named_directly_and_through_two_groups_gets_one_recipient_and_one_delivery_per_destination` |
| 4 | An unavailable resolver is retried, then recorded and reported, never silent | `A_short_resolver_outage_delays_planning`, `A_long_resolver_outage_records_resolver_unavailable_and_counts_it` |
| 5 | Inbox read state syncs across two sessions | `Reading_in_one_session_updates_the_other_across_two_replicas` |
| 6 | A type moved to a custom group shows there, and its actions still work | `A_type_moved_to_a_custom_group_shows_there_with_its_actions` |

---

## 7. API surface (phase 2 additions)

| Who | Endpoint |
|---|---|
| Tenant admin | `GET/PUT/DELETE /api/v1/tenants/{tenantId}/resolvers`, `…/subscribers` (list, view, `…/aliases`), `…/groups` and `…/groups/{key}/members`, `…/topics`, `…/mutes`, `…/display-groups`, `PUT …/types/{key}/display-group` |
| Signed-in person | `GET …/me`, `…/me/quiet-hours` (M14), `…/me/contact-points` (+ `/{id}/verify`), `…/me/devices`, `…/me/preferences`, `…/me/topics`, `…/me/mutes`, `…/me/inbox` (+ `/{id}/read`, `/read-all`, `/{id}/archive`, `/counts`) |
| Signed-in person | SignalR hub `/hubs/inbox?tenant={tenantId}` (event `inboxChanged`: `added`, `changed`, `reset`; M15) |

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| Planning a 10,000-person group in one transaction is slow and holds locks | Measure in M12 with the load check's harness; if needed, split subscriber mapping into batches inside the transaction (one `INSERT … ON CONFLICT` per 1,000) before anything more complex. |
| A wrong alias link exposes one person's inbox to another | Admin-only linking (p2-d2), audited, shown on both subscribers; no automatic linking beyond the signed-in person's own id (I16). |
| A deployment's directory isn't Keycloak, or doesn't carry `platform_tenant` | Only M16 depends on it, behind `IIdentityDirectory`; local and topic resolvers cover the inbox and preferences work, and the resolver contract is fixed in M10. |
| SignalR across replicas adds Redis to the stack | Redis is optional: one replica works without it; the two-replica test runs only when the local stack's Redis is up. |
| Encryption keys lost or rotated badly | Key id per row; rotation re-encrypts in the background; a missing key fails that contact point as `failed` with a clear reason, never the whole plan. |
| Quiet hours and DST | Time zones are IANA ids evaluated with `TimeZoneInfo`; tests cover both DST transitions. |

---

## 9. Changes made to `design.md` (15 November 2026)

- §6.1: recipients are per subscriber with `Via` (I14); resolver failure keeps planning in one transaction (I22).
- §6.4: quiet hours hold personal channels, not the inbox (I21).
- §9.2: device registration endpoints move under `/api/v1/tenants/{tenantId}/me/devices` (I25).
- §12: `contact_points` and `notification_recipients` columns, `contact_verifications`; the other tables in §4 above were already listed.
- §19: decisions 10 and 11; §20: no open questions.

## 10. Build board changes

- Add `p2-12` (device registrations), which §16 lists for phase 2 and the board lacks.
- Add the two §16 gates the board lacks: "an unverified contact point is never used" and "inbox read state syncs across two sessions".
- Note on `p2-08` that tenant mutes are part of it (§6.1 step 4 needs them).
