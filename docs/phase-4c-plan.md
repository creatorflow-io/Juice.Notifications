# Phase 4c plan: the Notifications Console

**Status:** accepted by the owner, 19 November 2026, with the recommendations in §2. It follows the owner's decision of the same day: the service is administered from a Console
of its own, as Juice.Observability is, and `@juice-js/notifications` in juice-layout is only for display (the bell, the messages and the
person's own services around them). It builds on `design.md` (section numbers like §13.2 refer to it) and continues `phase-4-plan.md`:
implementation choices go on from I48, milestones from M36, and each milestone names the build-board items it closes (`p4c-01` …).

**Goal:** a tenant's admin can run their notifications from a browser: find any delivery and see why it went or didn't, retry and replay,
configure integrations, targets, routes, mutes and escalations, edit templates with a preview, look after people, groups and topics, and set
retention; a platform admin can also register applications and link them to tenants. Nothing needs `curl` any more.

**Gate:** the proof points (§6), each shown by a named test or browser journey, plus a local-stack demo and an acceptance record.

---

## 1. Scope

**In**

- A Console app in this repository (`console/`), an Angular workspace like Juice.Observability's: the juice-layout shell (toolbar, menu,
  theme, profile), sign-in through the deployment's OIDC provider, a tenant switcher, and the admin pages as a library in the workspace.
- The App serving the built Console from its web root, beside `/api/v1`, `/hubs` and `/health`.
- The pages of §4 over the existing API, and the API additions they need (§3, I52): a notifications list and a recipients view (deferred #36),
  a correlations list, an overview summary, and what the signed-in user may administer.
- The template editor moved from `@juice-js/notifications` into the Console.
- The bell and inbox in the Console's own toolbar (from `@juice-js/notifications`), so admins see what the service tells them (storm notices,
  I43) where they work.
- Browser journeys over the built image (`console/e2e`), as Observability's.

**Out**

- Changes to what the service does: every page uses behaviour the API has today, plus the reads in I52.
- A person's own pages (inbox, preferences, quiet hours, contact points, push, chat linking): they stay in `@juice-js/notifications`, shown
  in each product's own UI.
- Dashboards and charts beyond the overview's counts; metrics stay in the deployment's telemetry (§14).
- A WYSIWYG template editor (D22, later); Console localization (English as written, as Observability's).

---

## 2. Owner decisions

All six recommendations were accepted on 19 November 2026.

| # | Question | Recommendation | Why |
|---|---|---|---|
| p4c-d1 | **Where does the phase go?** Phase 5 (Observability) and 4b (SQL Server) are planned. | **Now, before 4b and phase 5.** | Phase 5 hands Observability's webhook admins over to integrations and routes; without the Console they would lose their UI at cutover. 4b changes nothing anyone sees and can follow. |
| p4c-d2 | **Tenants in the Console.** Unlike Observability (one tenant per deployment), this service is central and multi-tenant (D2). | **The tenant in the URL** (`/tenants/{id}/…`), picked from a switcher listing what the user may administer (I50). A tenant admin has one and never sees the switcher; a platform admin sees every tenant with linked applications. | A link to a delivery names its tenant and works for anyone allowed to see it; the API already takes the tenant from the path and checks it (`ITenantAccess`). |
| p4c-d3 | **Who signs in?** | **Platform admins and tenant admins only.** Anyone else signed in sees a page saying they have no access here; their notifications are in their product's bell. | The Console is for administration (the owner's decision); people's own pages stay in the library. |
| p4c-d4 | **Where is it served?** | **By `Juice.Notifications.App`, from its web root** (one origin, no CORS), built into the image; `ng serve` with a proxy for development. | As Observability's API serves its Console; one thing to deploy. |
| p4c-d5 | **The library's templates page.** | **Removed from `@juice-js/notifications`** in its next release, once M39 ships the Console's; noted in its changelog. Hosts that link to it link to the Console instead. | The page is administration. It was added in M32 (phase 4) and has no users yet beyond the demo app. |
| p4c-d6 | **The overview's numbers.** | **From the database through the API** (I52): deliveries by status and channel in the last 24 hours, failing integrations, open storms, unfinished requests. No metrics backend. | The Console must work where nobody runs Prometheus or Grafana, as Observability's does without Grafana. |

---

## 3. Implementation choices

| # | Choice | Why |
|---|---|---|
| I48 | **Workspace**: `console/` with the app `notifications-console` and the library `@juice-js/notifications-admin` (`projects/juice-js/notifications-admin`, the pages and their API service), Angular 22 and `@juice-js/layout`, `auth`, `tenant`, `localize` ^2.2, as `Juice.Observability/console`. The library's routes are one `NOTIFICATIONS_ADMIN_ROUTES` list the menu is built from. | The same shell, sign-in and conventions as Observability's Console; pages in a library so another host could mount them later without a second codebase. |
| I49 | **Hosting**: `Console:Root` (default `wwwroot`) served by the App: files as files (hashed names cached, `index.html` not), any other path outside `/api`, `/hubs`, `/health` as `index.html`, a missing file never answered with the index (a stale chunk must fail), `X-Frame-Options: DENY` and a CSP that allows only this origin and the OIDC provider. The Dockerfile builds the Console in a Node stage and copies it in. Without the files the App runs as now. | Observability's rules (its `ConsoleHostingTests`), proven there. |
| I50 | **Access**: `GET /api/v1/access` (signed in) answers `{ platformAdmin, tenants: [{ id, name? }] }` from `ITenantAccess`: a tenant admin's one tenant; for a platform admin, every tenant with a linked application, named through juice-tenants when the deployment configures it (`Tenants:Directory`), by id otherwise. The Console builds its switcher and menu from it and never reads roles out of the token. | Decision 9 put tenant access behind `ITenantAccess` so the answer can come from elsewhere later; the Console must follow it, not the token's shape. |
| I51 | **Sign-in**: a public OIDC client with code and PKCE (`notifications-console` in the local realm, redirect to the App's origin and `http://localhost:4300` for `ng serve`). The API accepts its tokens as it accepts the other clients' (same issuer, same roles). | As the juice-layout demo client is set up for the library (`juice-layout-demo`). |
| I52 | **API additions** (reads only, tenant admin): `GET …/notifications` (list: type, correlation, state, time, cursor paging) and `GET …/notifications/{id}` for admins (today producers only), with `…/notifications/{id}/recipients` (each recipient's kind, named-as, outcome, bypassed rules, digest, escalation step and their deliveries' ids and statuses: deferred #36); `GET …/correlations` (list: state, key, application, time) beside the existing read; `GET …/overview` (p4c-d6's counts, each a bounded query over indexed columns, cached 30 s). | What the pages need and the API doesn't give: today a recipient held before any delivery is visible only in the database (#36). |
| I53 | **Secrets in forms**: integrations take a secret *reference* (`env:`, `file:`, `vault:`, `keyvault:`, within the API's prefixes), never a value; pages show references as given and nothing else. The API already never returns a secret (D15). | D15: never in logs, API responses or the Console. |
| I54 | **Changes and audit**: every change goes through the existing commands, so it is audited as API changes are (p1-09); a form shows the API's field errors (JSON pointers) on their fields, and a conflict (a stale definition hash) asks to reload. | One write path; the API's errors are already per field. |
| I55 | **Live updates**: only where the API pushes them today (the inbox over SignalR, in the bell). Lists refresh on demand and every 30 s while open. | No new push channel for admin pages in this phase. |
| I56 | **Journeys**: `console/e2e/*.mjs` drive the built image on the local stack (as Observability's): each page of §4 is walked by at least one journey, as a tenant admin, a platform admin, and a person without a role. | Observability's rule: every route walked, on the image that ships. |

---

## 4. Pages

Menu groups as Observability's latest menu (investigation first, the rest grouped). "New" marks an I52 read.

| Group | Page | Route under `/tenants/{id}` | API | Who |
|---|---|---|---|---|
| — | **Overview** | `/` | `GET …/overview` (new), `…/storms` | tenant admin |
| Investigate | **Deliveries**: list and filters (status, channel, integration, type, request, time), a delivery with its items and attempts, retry; **bulk replay** from the list's filter | `/deliveries`, `/deliveries/{id}` | `…/deliveries`, `…/{id}`, `…/{id}/retry`, `…/replay` | tenant admin |
| Investigate | **Notifications**: requests by type, correlation, state and time; one with its recipients, each outcome and why (muted, opted out, deferred, bypassed), and their deliveries | `/notifications`, `/notifications/{id}` | `…/notifications` (new), `…/{id}`, `…/{id}/recipients` (new) | tenant admin |
| Investigate | **Correlations**: open, acknowledged, resolved; one with its notifications; acknowledge, resolve, cancel | `/correlations`, `/correlations/{key}` | `…/correlations` (new), `…/{key}`, the three actions | tenant admin |
| Investigate | **Storms**: current and past | `/storms` | `…/storms` | tenant admin |
| Delivery | **Integrations** and their **targets**; register a provider's callbacks | `/integrations`, `/integrations/{key}` | `…/integrations`, `…/targets`, `…/callbacks` | tenant admin |
| Delivery | **Routes**, **Mutes**, **Escalation policies**, **Destination allowlist** | `/routes`, `/mutes`, `/escalation-policies`, `/allowlist` | as named | tenant admin |
| Content | **Types** (versions, schema, default group) and their **templates** with the preview beside them (moved from the library) | `/types`, `/types/{key}` | `…/types`, `…/types/{key}/templates/{channel}`, `…/templates/preview` | tenant admin |
| Content | **Display groups** | `/display-groups` | `…/display-groups`, `…/types/{key}/display-group` | tenant admin |
| People | **Subscribers** and their aliases; **local users** and **groups**; **topics** and their subscribers; **resolvers** | `/subscribers`, `/groups`, `/topics`, `/resolvers` | as named | tenant admin |
| Settings | **Tenant settings**: retention, storm threshold | `/settings` | `…/settings` | tenant admin |
| Platform | **Applications**, their broker user, their tenants | `/applications` (outside `/tenants`) | `/api/v1/applications` | platform admin |

The toolbar has the tenant switcher (I50), the bell and inbox from `@juice-js/notifications` for the signed-in admin, and the profile menu.

---

## 5. Milestones

Each milestone ends with its tests and journeys, a commit and a build-board update. The review loop runs per milestone, as in phases 1 and 2,
unless the owner says otherwise.

- **M36 Skeleton, hosting and access** (`p4c-01`, `p4c-02`): I48, I49, I50, I51; the shell, sign-in, the tenant switcher, the no-access page,
  the bell; `ConsoleHostingTests`, `AccessTests`; journeys `signin.mjs`, `no-access.mjs`.
  - *As built (19 Nov 2026):* **Hosting** in the App's `Program.cs` (not the API library: an embedding host serves no Console), from the
    standard `webroot` setting rather than a `Console:Root` of its own; an 8-character hash in a bundle's name (`main-FIXYNYJB.js`,
    `chunk-Ce0mZ_Dd.js`) is cached a year, everything else `no-cache`. A missing file falls to the API's signed-in policy (401), never the
    index page. The Dockerfile builds the Console in a Node stage. **Access**: `ITenantAccess.AdministeredAsync` (a default answering none);
    names are null for now (no tenant directory is configured anywhere yet). **Keycloak**: the client asks for `openid juice-notifications`
    (the local realm has no profile or email scope); people `tess` and `paul`. **Console** (`console/`, from Observability's workspace and
    lock file, Angular 22.2): the menu links tenant pages through `go/{page}` (a guard sends them to the tenant being worked in, kept in
    session storage), so one menu serves every tenant; the access read waits for sign-in, which the host must say how to know
    (`provideNotificationsAdmin({ signedIn })`, required: a cold observable of the signed-in user's subject; the Console's from its OAuth
    service; the library knows no sign-in library), so a guard beside sign-in never asks without a token; an answer is kept for that user,
    5 minutes if it grants something, 30 seconds if not, a failed read not at all, and read again when the user changes; `allTenants` lets
    a platform admin open any tenant's link. The no-access page says why (`reason`: none, tenant, unavailable) and keeps the link opened:
    "Try again" reads the access afresh and goes back to it.
    Switching tenant keeps the page and drops the query, fragment, matrix parameters and any row id. The token's allowed URLs include the
    API's origin: the bell calls it by its full URL (review round 1 found its calls went without a token). The first, signed-out page load
    is refused (the bell asks before the redirect); the journeys count a refusal once back from Keycloak if it carried a token, and any once
    the app has left its sign-in pages, by when the call was sent (a 401 on a call with a token only if the same call isn't answered within 3 s
    under a refreshed token; a page is closed only once those are decided). Cached for good: `.js`, `.mjs`, `.css` and `media/` fonts and images with an 8-character hash. The overview is a placeholder until M37's read. Journeys: `signin.mjs` (tess lands in her tenant with no switcher and the bell;
    paul switches between two tenants, linking the sample application to `…b2` first), `no-access.mjs` (ann at the home, a tenant's deep
    link and a menu link). Closed after four review rounds; the last round's small items were fixed without another (the
    media rule, the fallback user id from the access token's subject, "try again" dropping a read in flight, only the current read kept).
- **M37 Investigate** (`p4c-03`, `p4c-04`): the overview, deliveries (retry, bulk replay), notifications with recipients (closes deferred #36),
  correlations, storms; the I52 reads with their API tests; journeys `deliveries.mjs`, `why-not.mjs`, `correlations.mjs`.
  - *As built (19 Nov 2026):* **API** (`InvestigationEndpoints`, reader `History.Investigation`, tenant admins only): `GET …/notifications`
    (type, application, correlation, state, from, to; `before` cursor over version-7 ids), `…/notifications/{id}/recipients` (oldest first,
    `after` cursor; each with outcome, via, bypassed rules, escalation step, its digest, its deliveries, and a person's name),
    `…/correlations` (state, application, key), `…/overview` (deliveries created in the last 24 hours by channel and status; failed ones
    by integration with the latest error; open storms; notifications planning or running, over a new partial index
    `ix_notifications_unfinished`, migration `ConsoleReads`; kept 30 s per tenant in `IMemoryCache`). Beyond the plan's reads, two
    widenings the pages need: `GET …/notifications/{id}` answers the tenant's admins for any application (naming it, new field
    `application`), and a correlation's read and its acknowledge, resolve and cancel take `?application=` from an admin (keys are per
    application), who acts as themselves: `by` is their name from the token (`NotificationsAudit.Caller`), whatever the body says; a
    producer is unchanged. Owner decision (19 November): tenant admins keep acknowledge, resolve and cancel on the correlation page
    (not read-only). **Pages**: overview (tiles and tables linking to the lists they count, read again every 30 s), deliveries (filters
    in the URL; with the filter on failed, "Replay" sends the same filter capped at the moment the list was read, up to 10,000), a delivery
    (attempts, items, retry), notifications, a notification (each recipient's outcome and one sentence of why), correlations, a
    correlation (`correlations/{application}/{key}`; acknowledge, resolve with an outcome, cancel), storms (current, or past too). Styles
    in the library (`styles/_notifications-admin.scss`, `colors($theme)` as Observability's). **Journeys**: `deliveries.mjs` (a receiver
    on this machine's private address fails with 404, then accepts: one delivery found, opened and retried with the same id and body; a
    filter's three failed deliveries replayed and received, another type's left failed), `why-not.mjs` (ann's mute, ben's opt-out and
    tess's quiet hours, each put back afterwards; eli is a disabled account), `correlations.mjs` (a key with a slash, acknowledged and
    resolved by tess as herself). Review round 1 fixed the replay bound (the newest delivery shown, by the server's time, not the page's
    open time; then, at confirmation, the latest creation time among all rows shown, since the list is in id order), late "More" pages, and
    the under-way link (the list takes several states); closed after the confirmation round with that one fix, by the owner's go-ahead.
- **M38 Delivery configuration** (`p4c-05`): integrations and targets (secret references only, I53), routes, mutes, escalation policies,
  the allowlist; journey `configure.mjs` sets up the demo's email and webhook delivery in the browser and sends through it.
  - *As built (24 Nov 2026; review rounds 1–3: 10 findings each, all fixed):* **API**: one read beyond the plan, `GET …/providers` (each channel's provider kinds this
    deployment has, whether one `signs` and whether it `registersCallbacks`; never the inbox), so the integration form offers what can be
    saved; `PlanningApiTests.The_providers_list_names_what_the_deployment_can_send_through`. **Pages** (menu group "Delivery"):
    integrations (list, create), an integration (`integrations/{key}`: settings as JSON starting from the kind's example, secret reference
    only (I53), priority, enabled; targets by address or address reference; "Register callbacks" for kinds that ask; delete, refused with
    the API's reason once deliveries went through it), routes (type patterns, label matchers and minimum priority as fields; targets from every
    integration), mutes (current or all, paged by the last id; cancel; one that holds a mandatory type is flagged), escalation policies
    (steps of minutes and recipients: the route, or people through an enabled resolver handling their kind), the allowlist. Field errors show
    on their fields by JSON pointer, a `concurrent_change` asks to reload (I54), and rows from configuration or the service are read only.
    Creating with a key (or destination) that exists is refused in the page: the API's PUT would change that row instead. **Journey**
    `configure.mjs` (proof point 5): tess allows the receiver, creates an SMTP and a webhook integration with a target each (a missing sender
    shown on the settings field first) and a route to both; the email arrives in Mailpit and the webhook once, with a valid signature; a mute
    and an escalation policy are made and undone; the route deleted, the integrations disabled (deleting them is refused: they sent), the
    entry removed. Run against the rebuilt image (`Juice.MultiTenant.AspNetCore` 10.15.0 published), after round 3;
    `deliveries`, `why-not`, `correlations` and `no-access` pass on the same build.
- **M39 Content** (`p4c-06`): types, templates with preview, display groups; the templates page leaves `@juice-js/notifications` (p4c-d5).
  - *As built (24 Nov 2026; review round 1: 9 findings, all fixed):* **API**: one read beyond the plan, `GET …/types/{key}` (tenant admins): the latest version as declared (category,
    priority, mandatory, external recipients, correlation, schema), every version's hash and time, where its inbox items go in this tenant
    (`group`, `groupChosen`; `displayGroup` is the type's own; the database's `inbox_group`, so a declared group the tenant lacks reads
    `general`), its channels' templates by locale, and `serviceType` (the service's own: templates fixed);
    `TemplateEditingTests.A_types_detail_names_its_versions_its_group_here_and_its_templates_by_locale`. **Hosting**: every path under
    `tenants/` is a Console route (a type key `sample.build.done` or a correlation key `deploy.v1.2` read as a file name, so a deep link or a
    refresh answered 401); `ConsoleTests` checks both. **Pages** (menu group "Content"): types (find by key, name or application; which
    channels have a template, the tenant's own marked), a type (`types/{key}`: facts, display group (the type's own or a group of the
    tenant's; open items move at once), the template editor by channel and locale with the preview beside it, rendered by the API half a
    second after the last keystroke with the schema's sample (shown, to change) or typed data, an email's HTML in a frame sandboxed with
    nothing allowed; save as the tenant's own, revert, use the application's again; channel and locale locked while there are unsaved
    changes; versions; schema), display groups (add, rename, reorder; delete one not built in, its types back in their own). **Library**
    (p4c-d5): the templates page, its routes, API calls and models removed from `@juice-js/notifications` (juice-layout, working tree, not committed: nothing in M39 depends on it, the old page still works against the same API; it ships with that library's next release, a
    breaking change for its next release). **Journey** `content.mjs`: tess finds a fresh two-version type, previews the application's inbox
    template with the schema's example, sees a template that doesn't parse fail in the preview and on save, saves the tenant's own (ben's
    item renders with it), adds a display group and moves the type into it (ben's next item lands there), then puts both back. **Review round 1** (9 findings): a late preview no longer writes its sample into
    the data field (the sample is shown apart, for the type it was made for, so it sends no second preview either); unsaved text survives a
    Refresh or a change stored meanwhile (kept, with a notice; Revert puts back what is stored now); a save or move answered after the page
    moved to another type is dropped, and "saved" is cleared; an empty editor previews the template in effect in the locale picked
    (`PreviewBody.locale`, the default without it); the detail reads the group and the choice in one query and shares the per-channel
    template count with the types list; a `MovedType` model; the detail test cleans up in `finally` and checks the preview by locale;
    `content.mjs` checks the vi preview and that a Refresh keeps unsaved text.
- **M40 People** (`p4c-07`): subscribers and aliases, local users and groups, topics, resolvers.
- **M41 Settings and platform** (`p4c-08`): tenant settings; applications and tenant links for platform admins.
- **M42 Proof points and acceptance** (`p4c-g1` … `p4c-g3`): §6, `phase-4c-acceptance.md`, a demo script, and design.md updated (§4.2,
  §15 layout, §16 phases, §19 decision 21).

---

## 6. Proof points and their tests

| # | Proof point | Test |
|---|---|---|
| 1 | A tenant admin sees and changes only their tenant: another tenant's URL shows nothing of it | `tenant.mjs` (every page with the other tenant's id); `AccessTests.A_tenant_admin_is_offered_only_their_tenant` |
| 2 | Someone signed in without an admin role gets no tenant data on any page | `no-access.mjs` (every route and deep link; each page must make a refused call, so one that never loaded can't pass) |
| 3 | "Why didn't they get it?" is answered in the Console: a muted, opted-out and deferred recipient each shows why, with no delivery | `why-not.mjs`; `NotificationRecipientsTests` |
| 4 | A failed delivery is found, inspected and retried with the same id; a bulk replay from a filter replays exactly what the filter shows | `deliveries.mjs` |
| 5 | Delivery set up entirely in the Console works end to end: an email and a signed webhook arrive | `configure.mjs` (Mailpit and the sample receiver) |
| 6 | No secret appears in the Console: not on a page, not in an API response it reads | `secrets.mjs` (seeds known secret values, then searches every page's text and every response it saw) |
| 7 | The App serves the Console's files as files, its routes as the index page, and API paths never as the Console | `ConsoleHostingTests` |

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| The Console grows its own idea of who may do what | The menu and switcher come from `GET /api/v1/access` (I50), and the API refuses anyway; proof points 1 and 2 check both. |
| A page shows a secret | Forms take references only (I53); proof point 6 searches every page and response. |
| Large tenants make lists slow | Every list is cursor-paged over indexed columns as the history list is; the overview's queries are bounded and cached (I52). |
| Two UI codebases drift (the library and the Console) | The Console uses `@juice-js/layout` and the library's bell as packages; the admin pages live only in the Console (p4c-d5). |

---

## 8. Build board

| Item | Title | Milestone |
|---|---|---|
| p4c-01 | Console workspace, shell, sign-in, tenant switcher, bell | M36 |
| p4c-02 | App serves the Console; `GET /api/v1/access`; Keycloak client | M36 |
| p4c-03 | Overview, deliveries, retry and bulk replay pages | M37 |
| p4c-04 | Notifications list and recipients view (deferred #36); correlations; storms | M37 |
| p4c-05 | Integrations, targets, routes, mutes, escalation policies, allowlist | M38 |
| p4c-06 | Types, templates with preview, display groups; library templates page removed | M39 |
| p4c-07 | Subscribers, aliases, local users, groups, topics, resolvers | M40 |
| p4c-08 | Tenant settings; applications and tenant links | M41 |
| p4c-g1 | Proof points 1–7 green | M42 |
| p4c-g2 | Demo script and acceptance record | M42 |
| p4c-g3 | design.md updated (hosting, layout, phases, decision 21) | M42 |
