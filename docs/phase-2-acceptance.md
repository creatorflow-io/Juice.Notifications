# Phase 2 acceptance record

**Status:** 17 November 2026, all gate checks passed. It records the M18 gate (`phase-2-plan.md` §5 M18, §6): each proof point with the
test that shows it, phase 1's proof points and load check with people in the mix, and the demo. Milestones M10–M17 (commits `195266c` …
`96421f5`). By owner decision (17 Nov) the review loop for M17–M23 runs once, after phase 4; M10–M16 were reviewed milestone by milestone.

## 1. Proof points

Every test runs against the local stack (`deploy/local`, with Keycloak for the identity resolver) in `dotnet test Juice.Notifications.slnx`
and passes: 314 integration tests (1 skipped: the load check, run separately below) and 700 unit tests, 17 Nov 2026.

| # | Proof point | Tests | Notes |
|---|---|---|---|
| 1 | A mandatory type reaches a person who opted out of its category, at least in the inbox | `PreferenceTests.Mandatory_type_reaches_an_opted_out_person_in_the_inbox_and_records_the_bypass` | The bypass is recorded on the recipient (D9): category opt-out and quiet hours are bypassed, a channel opt-out is respected, the inbox always receives it. |
| 2 | An unverified contact point is never used | `ContactPointTests.Unverified_or_failed_contact_points_are_never_planned_even_for_mandatory_types`; `IdentityResolverTests.A_group_reaches_its_enabled_members_of_the_tenant_and_an_unverified_email_is_never_sent_to` | Also for addresses from the directory: dora's unverified Keycloak email gets no email, only her inbox item. |
| 3 | A person reached through a group and directly gets one notification | `PeoplePlanningTests.A_person_named_directly_and_through_two_groups_gets_one_recipient_naming_all_three` | One recipient whose `NamedAs` lists all three names; one delivery per destination (items unique on recipient, destination, channel). The plan's name for the test was shortened when written. |
| 4 | An unavailable resolver is retried, then recorded and reported, never silent | `ResolverFailureTests.An_outage_shorter_than_the_wait_limit_stores_nothing_meanwhile_then_plans_everyone`, `ResolverFailureTests.An_outage_past_the_wait_limit_plans_the_rest_and_records_the_name_as_unavailable`; against a real directory: `IdentityResolverTests.Keycloak_stopped_for_less_than_the_wait_limit_delays_planning_then_everyone_is_planned`, `IdentityResolverTests.Keycloak_stopped_past_the_wait_limit_records_its_names_as_unavailable_and_plans_the_rest` | Keycloak is cut off with a TCP gate in front of it. A short outage delays planning; a long one plans everyone else, records each name as `resolver_unavailable` on the notification and counts it (`notifications.recipients_unresolved`). |
| 5 | Inbox read state syncs across two sessions | `InboxSyncTests.Reading_in_one_session_updates_the_other_across_two_replicas` | Two hosts on one database and the Redis backplane, a SignalR session on each; reading on one reaches the other. |
| 6 | A type moved to a custom group shows there, and its actions still work | `InboxTests.A_type_moved_to_a_custom_group_shows_there_with_its_actions` | Group catalog per tenant (D12); actions are taken through the item, whatever its group. |

## 2. Phase 1 still holds, with people in the mix

Phase 1's six proof point tests (`phase-1-acceptance.md` §1) are in the same run and pass.

**Load check** (`LoadCheckTests`, `NOTIFICATIONS_LOAD=1`): since M18, every third request also names one of 50 local users through a `local`
resolver, so a third of the notifications plan a subscriber recipient and an inbox delivery beside the two webhook routes. 1,000 requests a
minute for two minutes; default stage settings.

| Run | Accepted → first attempt | Refused | Lease lost | Deliveries |
|---|---|---|---|---|
| In-process host | p50 0.10 s, **p95 1.23 s**, p99 1.75 s, max 2.25 s | 0 | 0 | 4,667 of 4,667 (667 to inboxes); 4,000 webhook sends, none twice |
| Compose `app` container | p50 0.05 s, **p95 0.10 s**, p99 0.29 s, max 0.52 s | 0 | 0 | 4,667 of 4,667 (667 to inboxes); 4,000 webhook sends, none twice |

Per stage at p95 (in-process): planner 0.03 s, delivery planner 1.01 s, dispatcher 0.28 s. Target: p95 ≤ 30 s, no lease storms. **Passed.**
Compared with phase 1 (p95 0.71 s), the delivery planner's share grew (0.59 → 1.01 s), with a third more deliveries to plan, each
with a person's preferences behind it.

**What the load check found:** the container run had never been possible since webhooks must be signed (deferred #13): the tests set
their signing secret only in their own process, so the `app` container failed every webhook step (`env:NOTIFICATIONS_SECRET_TEST_HOOK`
not set) and no webhook was attempted. The local `app` service now has the tests' public signing secret. Before that, a second run started
by mistake overlapped the first; both were stopped, their leftover deliveries cancelled, and the run above was made alone.

## 3. Demo

```bash
cd services/notifications/deploy/local
scripts/init.sh && scripts/keycloak-update.sh                  # once
docker compose --profile app up -d --build --wait
scripts/demo-people.sh                                          # phases 2 and 3 (needs node)
```

`demo-people.sh` sets up the `sample` application, an SMTP integration (Mailpit) and an identity resolver `people` over the deployment's
Keycloak directory, then sends an approval to the Keycloak group `ops`. Ann (verified email) gets the email and an inbox item; Dora
(unverified email) gets the inbox item only. Ann's own token reads her inbox, where the item shows its actions; the rest of the script is
phase 3 (`phase-3-acceptance.md` §3). Run on 17 Nov 2026 against the rebuilt `app` container: every step answered as expected.

The browser side: the `juice-layout` demo app with `ng serve -c notifications` (client `juice-layout-demo`, sign in as ann) shows the bell,
the inbox panel, preferences and email addresses from `@juice-js/notifications`, live over SignalR.

**Found by the demo:** the library expected the correlation state and contact point health in PascalCase (`'Resolved'`, `'Failed'`), but the
API writes them camelCase (`resolved`, `failed`): a resolved item kept its buttons and a failed address showed as verified. Fixed in
`juice-layout` (`models.ts` and the two templates); the library's tests now use the API's values.

## 4. Open items

Carried in `docs/deferred.md`; none blocks the gate. The review loop over M17–M23 runs after phase 4 (owner decision, 17 Nov).
