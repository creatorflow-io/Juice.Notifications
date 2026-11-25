# Phase 4c acceptance record

**Status:** 25 November 2026, all gate checks passed. It records the M42 gate (`phase-4c-plan.md` §5 M42, §6): each proof point with the
test or browser journey that shows it, the rest of the suite still green, and the demo. Milestones M36–M41 (commits `7b4e26e` … `79f6fa1`),
each with its review rounds (the plan's as-built notes); the library's templates page removed from `@juice-js/notifications` in the
juice-layout working tree, to ship in its next release (p4c-d5).

## 1. Proof points

The .NET suite runs against the local stack with the App container stopped (`dotnet test Juice.Notifications.slnx`, 25 Nov 2026): 887 unit
and 374 integration tests pass. The journeys (`console/e2e`, `KEYCLOAK=… node e2e/<name>.mjs`) drive the built image on the local stack
(Edge, headless), signed in through Keycloak as the realm's people: `tess` (tenant admin of `…a1`), `paul` (platform admin), `ann` (no
admin role). All eleven pass on the same image, 297 checks. The three that claim "every page" (proof points 1, 2 and 6) first check their list against the Console's own route list (`uncoveredTenantRoutes`: 23 tenant routes, none missing or extra), so a page added later fails them until it is walked.

| # | Proof point | Test | Notes |
|---|---|---|---|
| 1 | A tenant admin sees and changes only their tenant: another tenant's URL shows nothing of it | `tenant.mjs` (72 checks); `AccessTests.A_tenant_admin_is_offered_only_their_tenant` | cara's tenant (`…b2`) gets rows of its own (a webhook integration, a notification with a correlation and its delivery, a local user). tess opens all 23 tenant pages there, detail pages by those ids: each is the no-access page, shows none of the rows, and no call names the tenant. Her token is refused (403) every read those pages start from, and a settings change, a route, a replay and a retry; paul is answered (200) every one of those reads, so the refusals are about the tenant, not the paths. Her own tenant's pages, 72 answers read (none left unread), never name cara's rows. |
| 2 | Someone signed in without an admin role gets no tenant data on any page | `no-access.mjs` (64 checks); `AccessTests.Someone_without_an_admin_role_is_offered_nothing_and_no_token_gets_401` | ann at the home, a deep link and a menu link (M36), and now at every tenant page (detail pages by real ids) and both applications pages: the no-access page each time with its reason, no tenant shown, nothing refused in the browser. Her token is refused (403) every read those pages start from, and the applications; paul is answered each. **Differs from the plan's wording** ("each page must make a refused call"): the guards send ann away before any page loads, so no page makes a call; the refused read is asked with her token instead, which shows the same thing (a page that never loaded can't pass). |
| 3 | "Why didn't they get it?" is answered in the Console: a muted, opted-out and deferred recipient each shows why, with no delivery | `why-not.mjs` (10 checks); `NotificationRecipientsTests.A_muted_opted_out_and_deferred_recipient_each_shows_why_with_no_delivery` | M37; deferred #36 resolved. |
| 4 | A failed delivery is found, inspected and retried with the same id; a bulk replay from a filter replays exactly what the filter shows | `deliveries.mjs` (12 checks) | M37, with review round 1's bound (the replay stops at the latest delivery shown). |
| 5 | Delivery set up entirely in the Console works end to end: an email and a signed webhook arrive | `configure.mjs` (26 checks) | M38: SMTP to Mailpit and a webhook to a receiver on this machine, its signature checked. |
| 6 | No secret appears in the Console: not on a page, not in an API response it reads | `secrets.mjs` (10 checks) | The secrets are every password, client secret and key in `deploy/local/.env` and the tests' webhook signing secret, which a seeded integration names by reference and has signed a delivery with. tess walks every tenant page (detail pages by the seeded ids), paul every one of them and the applications: every page's text, every field's value, every answer and what the hubs push (676 for paul: 113 from the API, 26 pushed) searched for each secret as written, JSON-escaped (`+`) or URL-encoded, none found; every answer's body is read before the next page opens and before the search, and the check fails on any API answer left unread. The integration's page shows its reference as given. |
| 7 | The App serves the Console's files as files, its routes as the index page, and API paths never as the Console | `ConsoleHostingTests.Console_files_routes_and_api_paths_are_told_apart` | M36. |

Every page of §4 is walked by at least one journey (I56): the four above and `signin.mjs` (12), `correlations.mjs` (10), `content.mjs` (28),
`people.mjs` (31) and `platform.mjs` (22).

## 2. Demo

`deploy/local/scripts/demo-console.sh`, against the rebuilt `app` container on 25 Nov 2026. It sets up a story through the API and prints
the walk; walked in the browser as tess and paul:

1. **Overview:** the last 24 hours' deliveries by status and channel, and the failing integration `demo-hooks`.
2. **Why not:** an alert to ann and ben; its notification page shows ann `muted` ("a mute matched (their own, or the tenant's)") with no
   delivery, and ben's deliveries.
3. **A failed webhook:** the failed list filtered to `demo-hooks`; the delivery's attempt shows the receiver's 404; Retry sends it again
   under the same id (a second attempt).
4. **Correlation:** the alert's correlation acknowledged, then resolved (`fixed`).
5. **Settings:** the storm threshold set to 50 shows as 50.
6. **Applications (paul):** `sample` with its client id, broker user and its two tenants.

**Found by the demo:** the stack's ports are bound to loopback, which the service never sends to, so a webhook needs a receiver on the
machine's own address; the script runs one (answering 404) and keeps it up until the walk is done (Enter), so the retry reaches it too.
Persons are named by their identity (Keycloak's id), as each reads it on `me`, not by user name.

## 3. Open items

- **Before the Console's first release** (`release.yml`, tags `console-<v>` and `api-<v>`): a nuget.org trusted publishing policy for
  `creatorflow-io/Juice.Notifications` and `release.yml`, the `NUGET_USER` and `NPM_TOKEN` secrets, and the npm scope's rights for the first
  publish of `@juice-js/notifications-admin`.
- **juice-layout:** the templates page's removal from `@juice-js/notifications` (M39) ships with that library's next release.
- **Not walked:** the home for a platform admin when no tenant has a linked application (the stack always has one); the no-access page for
  someone granted every tenant without the platform admin role (a host's own `ITenantAccess`; the stack has no such user).
- **The side menu is the same for everyone** (juice-layout's menu can't follow the access read), so the applications are reached from the
  tenant bar and the home, not the menu.
- Carried in `docs/deferred.md`: #48 (a tenant's settings have no version).
- `design.md` updated (§4.2, §15, §16, decision 23 for the Console; p4c-g3), left for the owner with their own changes to it.
