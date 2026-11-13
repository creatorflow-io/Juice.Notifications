# Phase 1 acceptance record

**Status:** 12 November 2026, all gate checks passed; M9 reviewed. It records the M9 gate (`phase-1-plan.md` §5 M9, §6): each proof point with the test that shows it, the
mutation score on the core rules, the load check, and the demo steps. Commit references are on `main`.

## 1. Proof points

Every test below runs against the local stack (`deploy/local`) in `dotnet test Juice.Notifications.slnx` and passes. Where a proof has more
than one part, each part has its own test.

| # | Proof point | Tests | Notes |
|---|---|---|---|
| 1 | An accepted request survives a crash right after `202` | `CrashTests.Accepted_request_is_delivered_after_the_host_is_killed_right_after_202` | The real host (Juice.Notifications.App) runs as a child process with workers off and is killed (whole process tree, no graceful stop) as soon as it answers `202`. The request is in the database; a new host process with workers delivers it, with the delivery id as `webhook-id`. |
| 2 | A duplicate request creates no new work | `ProducerApiTests.Concurrent_identical_requests_make_one_request_and_one_notification` | 20 concurrent identical requests: one request, one notification. Deliveries come only from the notification's recipients, one per destination and channel (unique key, `PlanningApiTests`, `DeliveryTests`). |
| 3 | Planning is safe under retries and concurrent workers | `PlannerConcurrencyTests.Planning_the_same_notification_concurrently_or_after_a_fault_yields_one_set_of_recipients` | Planner and delivery planner both write under the claim's version fence (M3). |
| 4 | A worker crash never loses a claimed delivery; stale writes are discarded | `DispatcherTests.Dispatcher_killed_after_send_resends_after_lease_with_the_same_id`, `DispatcherTests.Dispatcher_killed_after_an_accepted_send_applies_it_without_resending`, `ClaimConcurrencyTests.A_worker_paused_past_its_lease_writes_nothing_and_another_completes_the_row`, `ClaimConcurrencyTests.Rows_of_a_worker_that_dies_after_claiming_are_due_again_after_the_lease` | The attempt is written in its own transaction right after the send, so a worker that dies before saving its step leaves it recorded; the next owner applies it rather than sending blindly. |
| 5 | Timeouts follow the unknown-outcome policy; no failover on destination or content failures | `DispatcherTests.A_webhook_unknown_outcome_is_retried_with_the_same_delivery_id`, `DispatcherTests.A_non_idempotent_unknown_outcome_fails_as_unknown_outcome_without_a_second_send`, `DispatcherTests.A_mandatory_unknown_outcome_is_repeated_exactly_once`, `DispatcherTests.A_permanent_failure_fails_after_one_attempt_without_retry`, `WebhookProviderTests` (never answers → Unknown; 400 → Content; 404/410 → Destination), `SmtpProviderTests` (lost after the body → Unknown; partway through → retryable) | The plan names `Webhook_400_fails_as_destination_without_retry`; the code maps a 400 to `Content` as M6 specifies (open question: `docs/deferred.md` #15). Both are permanent, with no retry and no failover. |
| 6 | Delivery history records every attempt; the delivery id never changes | `HistoryTests.Delivery_id_is_stable_across_attempts_history_and_dead_letter_retry`, `TelemetryTests.A_trace_links_the_request_to_the_provider_call` | Same id in each attempt, in history, at the receiver and after a dead-letter retry; one trace from the producer's `traceparent` through every stage to the provider call. |

## 2. Mutation testing

Stryker.NET 5.1 (`dotnet tool run dotnet-stryker`) with the Microsoft Testing Platform runner; the VSTest runner can't switch mutants
under xUnit v3 (it scored 7 % with every test passing). Configurations: `test/stryker/unit.json` (unit tests) and
`test/stryker/integration.json` (integration tests against the local stack, without the crash and load tests, which start their own hosts).
Run from `src/Juice.Notifications`: `dotnet tool run dotnet-stryker --config-file ../../test/stryker/unit.json --output ../../build/stryker/unit`
(about 5 minutes), and the same with `integration` (about 25 minutes, with the local stack up).

| Core rule | File | Tests | Score | Without message strings |
|---|---|---|---|---|
| Route matching | `Planning/RouteMatch.cs` | unit | 84.6 % (77 killed, 14 survived) | 98.7 % |
| Destination policy | `Providers/DestinationPolicy.cs` | unit | 72.0 % (95 killed, 28 survived, 9 not covered) | 84.8 % |
| SMTP outcome classification | `Providers/SmtpProvider.cs` | unit | 62.8 % (58 killed, 25 survived, 10 not covered) | 73.8 % |
| Unknown-outcome policy, backoff, outcome precedence | `Delivering/Dispatcher.cs` | integration | 74.2 % (144 killed, 23 timed out, 52 survived, 6 not covered) | 82.3 % (also without telemetry and log calls) |
| Webhook outcome classification | `Providers/WebhookProvider.cs` | integration | 47.4 % (35 killed, 2 timed out, 30 survived, 11 not covered) | 55.2 % (see below) |
| Destination keys | `Delivering/DeliveryPlanner.cs` | integration | not run | |
| Idempotency handling | `Intake/Intake.cs` | integration | not run | |

**Survivors, explained.** Mutation testing first found untested boundaries, now covered: route limits (20 type patterns, 50 label
matchers, 10 regex matchers of 200 characters, 500-character values), `in(…)` without its closing parenthesis, single-address allowlist
entries (/32, /128), NAT64 and 6to4 prefix edges, and the 400/499/500 SMTP edges. What survives:

- **Message strings** (47 of 67 unit survivors): mutating a refusal's sentence to `""`. Tests assert the error pointer, code and category,
  not wording, on purpose.
- **Static initializers** (the blocked and private network lists in `DestinationPolicy`): Stryker can't switch a mutant inside a static
  field initializer per test run. Each list entry is covered by `DestinationPolicyTests.Blocked_ranges_are_blocked`, `Private_ranges_are_private`, `Other_addresses_are_public` and `IPv6_forms_that_carry_IPv4_are_judged_by_the_stricter_class`.
- **Socket connection code** (`DestinationPolicy.ConnectAsync`, not covered by unit tests): exercised by `WebhookProviderTests` against real
  sockets.
- **Equivalent mutants**: `Order()` → `OrderDescending()` on the list of server names in a refusal message; `Started |= …` → `^=` where the
  value can only go from false to true once; timeouts doubled or halved where the test's fake server answers well inside either.
- **The webhook provider's survivors** are mostly outside what its integration tests can observe: the signature check `WebhookSigner.Verify`
  (covered by the unit tests, which this pass doesn't run), the per-tenant client cache and its delayed disposal (resource lifetime, not
  outcomes), and handler settings that are equivalent on a machine with no proxy (`UseProxy`, `UseCookies`) or behind the provider's own
  deadline (`HttpClient.Timeout`). Three were real gaps, covered by tests added after this measurement (not re-scored): the 2xx upper edge
  (299, 300), the `charset=utf-8` in the Content-Type the signature relies on, and refusing schemes other than http and https.
- **Dispatcher survivors**: tenant filters next to a unique delivery id (equivalent), telemetry and log calls, and message strings. Tests for
  the give-up path, expiry, a disabled integration or missing provider, and running out of attempts were added because of the first pass
  (51.1 % → 74.2 %).
- **Not mutation-tested yet**: destination keys and idempotency handling (`DeliveryPlanner`, `Intake`). Both are covered by integration tests
  (proof 2, `PlanningApiTests`, `DeliveryTests`), but a pass over those files didn't run: Stryker's line-range filter matched nothing, and a
  whole-file pass is long. An open item for the next run.

## 3. Load check

`LoadCheckTests.A_thousand_requests_a_minute_reach_their_first_attempt_within_30_seconds_at_p95` (runs with `NOTIFICATIONS_LOAD=1`;
`NOTIFICATIONS_LOAD_API=http://localhost:5280` aims it at the compose `app` container instead of an in-process host): 1,000 requests a minute
for two minutes, each routed to two webhook receivers (4,000 deliveries). Default stage settings (one worker per stage).

| Run | Database | Accepted → first attempt | Refused | Lease lost | Sends |
|---|---|---|---|---|---|
| In-process host | fresh scratch database (cold: no statistics yet) | p50 0.05 s, **p95 0.71 s**, p99 0.97 s, max 1.27 s | 0 | 0 | 4,000 of 4,000, none twice |
| Compose `app` container | the stack's `notifications` database | p50 0.03 s, **p95 0.75 s**, p99 1.21 s, max 1.30 s | 0 | 0 (no duplicate sends) | 4,000 of 4,000, none twice |

Per stage at p95 (in-process): planner 0.03 s, delivery planner 0.59 s, dispatcher 0.10 s. Target: p95 ≤ 30 s, no lease storms. **Passed.**

**What the load check found** (fixed before the runs above):

- **Cold-start stall.** The first run took p95 47.8 s: deliveries made in the first minute all waited until about 60 s, then the dispatcher
  caught up. The dispatcher looked up a delivery's request through recipients and notifications; with new tables not yet analyzed
  (autovacuum's first pass comes within a minute), PostgreSQL planned that four-table join badly and it hit the 30 s command timeout, so steps
  failed and backed off. Delivery items now carry their request id (migration `DeliveryItemRequest`, backfilled), and the dispatcher, history
  and retry reach the request in one indexed step.
- **Recipients had no creation time.** The planner never set `NotificationRecipient.CreatedAt`, so every recipient read as created in year 1.

## 4. Demo

```bash
cd services/notifications
deploy/local/scripts/init.sh                                   # once
docker compose -f deploy/local/docker-compose.yml --profile app up -d --build --wait
dotnet run --project samples/Juice.Notifications.Samples.Producer
```

The sample registers the `sample` application, routes `sample.disk.*` to the Mailpit mailbox and to a signed webhook it receives itself,
declares the type, sends one notification, verifies the signature and prints the delivery history; it exits non-zero unless both deliveries
were made. The email is in Mailpit at http://localhost:8026. `deploy/local/scripts/demo.sh` does the email half with curl.

## 5. Open items

Not part of the gate; recorded in `docs/deferred.md` and `docs/tracking.md`:

- RabbitMQ intake (p1-05): built 13 Nov 2026 (`RabbitMQIntakeTests`), in review.
- Configuration provisioning (I12) and audit records (p1-09), including the audit of dead-letter retries (#11).
- Owner decisions: unsigned webhooks without a secret (#13), the category of a webhook 400 (#15).
