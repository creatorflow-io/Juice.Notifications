# Local tracking (to copy into the build board)

The build board ([artifact](https://claude.ai/artifact/FxoB3GVpdwJtRwYSTgRF4p#tracking)) keeps its state in two collections, `tasks` and
`milestones`. This file mirrors them so the board can be updated by hand. Each row below is one board document; copy the values into the
board's fields (status, review, note). Item titles are taken from `phase-1-plan.md` §5; where the board already has a title, keep the board's.

**Values the board accepts**

- task `status`: `todo` · `doing` · `blocked` · `done`
- milestone `review`: `none` · `r1` · `r2` · `confirm` · `clean` · `owner`

## Milestones (collection `milestones`)

| num | name | items | review | note (as of 13 Nov 2026) |
|---|---|---|---|---|
| M1 | Skeleton and persistence | p1-01, p1-02, p1-06 | clean | Reviewed in three rounds; Juice `UnitOfWork` fix: bumped to Juice 10.13.0 on 13 Nov 2026 (deferred #2 closed). |
| M2 | Types and intake | p1-03, p1-04, p1-05 | confirm | Commit 007f690; RabbitMQ intake (p1-05) built 13 Nov 2026 (f3c7137), rounds 1 and 2 fixed; confirmation next. |
| M3 | Claim store and worker host | p1-07 | clean | Commit 43f1cea. |
| M4 | Planner and routes | p1-08, p1-09 | clean | Commit 0e7d238. Audit records and configuration provisioning (I12) still open. |
| M5 | Rendering and delivery planning | p1-10, p1-11 | clean | Commit fbb4684. Round 2: 6 bugs fixed. Confirmation: 2 fixed (renders on a 128 MB stack of their own, depth limit 250; invariant culture). Re-confirmation: no crash or wrong-outcome bugs; 2 low ones fixed (a slow-unwinding template error taken for a timeout; thread start failure taken for a template failure). Deferred #10, #12. |
| M6 | Providers | p1-12, p1-13 | clean | Round 1: 13 findings; fixed SMTP 4xx/timeout/QUIT/DATA classification and settings checks, webhook sent-or-not detection and body drain, 443-only for public hosts, NAT64/6to4/IPv4-compatible addresses, http only to private, allowlist validation, URL-free traces, Verify range, client cache disposal. Deferred #13–#15. Tests: DestinationPolicyTests, SmtpProviderTests, WebhookProviderTests (M6) / DispatcherTests (M7). Round 2: 4 fixed (SMTP drop mid-body retryable, legacy settings editable, punycode sender, NAT64 local-use at every RFC 6052 position; allowlist CIDRs judged as a whole). Confirmation: 2 fixed (sender checked by building it as the send does; allowlist refuses embedded blocked ranges). Re-check (e8740d4): 3 low fixed (stale attempts outside the retry budget; producers get no provider message id; tenant-template render errors hidden from producers). |
| M7 | Dispatcher | p1-14 | clean | Round 1: 6 bugs fixed (priority claim order, sends not cancelled by stopping, retry over unapplied attempts, mandatory once per budget, backoff from the send end, missing address/secret handling). Tests: DestinationPolicyTests, SmtpProviderTests, WebhookProviderTests (M6) / DispatcherTests (M7). Round 2: 4 fixed (claim index on integer priority, refusal beats stale unknown, 45 s shutdown + 50 s stop grace, 60 s dispatcher lease floor). Confirmation: 1 fixed (a refusal outranks an unknown only when newer); options copied, not mutated. Re-check (e8740d4): 3 low fixed (stale attempts outside the retry budget; producers get no provider message id; tenant-template render errors hidden from producers). |
| M8 | History, dead letters, operations, host | p1-15, p1-16, p1-17 | clean | Round 1: 9 findings; fixed retry over unapplied attempts, offset times, producer sees target not address, single-status parsing, earliest expiry, metrics after commit, backlog gauge per running host, stale fields on retry, plan and delivery-planning spans. Deferred #16. Round 2: 6 fixed (producer-safe error texts, https MetadataAddress checked at start, no System.Net.Http meter, stale failure after a retry ignored (ReplayedAt), planner failures counted, sample exit code). Re-check (e8740d4): 3 low fixed (stale attempts outside the retry budget; producers get no provider message id; tenant-template render errors hidden from producers). |
| M9 | Proof points and acceptance | p1-g1 … p1-g6 | clean | Commits a685af5, b6e6804 and the next. All six proofs have passing tests; mutation testing and load check done (docs/phase-1-acceptance.md). Review: no production bugs; three test-harness fixes (child host never leaked, port race retried, load check reports transport failures). |

## Items (collection `tasks`, phase `p1`)

| id | kind | title | ref | status | note |
|---|---|---|---|---|---|
| p1-01 | task | Solution, packages, build props, CI | M1 | done | |
| p1-02 | task | Local stack: PostgreSQL, RabbitMQ, Keycloak, Mailpit | M1 | done | |
| p1-06 | task | DbContext, tables, migrations; applications admin | M1 | done | |
| p1-03 | task | Type declarations (versioned schema, templates) | M2 | done | |
| p1-04 | task | HTTP intake with idempotency; notification read | M2 | done | |
| p1-05 | task | RabbitMQ `NotificationRequested` intake | M2 | doing | Built: `Juice.Notifications.Messaging`, applications' `brokerUser`, broker-side retry; `RabbitMQIntakeTests`. In review. |
| p1-07 | task | Claim store, stage worker, concurrency suite | M3 | done | |
| p1-08 | task | Recipient resolvers and planner stage | M4 | done | |
| p1-09 | task | Tenant configuration API; provisioning; audit | M4 | doing | API done; provisioning (I12) and audit not built. |
| p1-10 | task | Template sandbox and channel content | M5 | done | Review clean. |
| p1-11 | task | Delivery planner stage | M5 | done | Review clean. |
| p1-12 | task | Channel registry; webhook provider | M6 | done | Review clean. |
| p1-13 | task | SMTP provider (MailKit); secret references | M6 | done | Review clean. |
| p1-14 | task | Dispatcher: outcomes, backoff, unknown-outcome policy | M7 | done | Review clean. |
| p1-15 | task | Delivery history API | M8 | done | List (status, channel, integration, type, request, time; cursor paging) and detail with items and attempts. Review clean. |
| p1-16 | task | Dead-letter retry of one delivery | M8 | done | Same id, attempts kept, fresh budget (migration DeliveryReplays). Audit record waits on the audit work (p1-09). Review clean. |
| p1-17 | task | Metrics, traces, health; App host, Dockerfile, samples | M8 | done | Meter (all §14 metrics but failovers and escalations, which are phase 4), traces producer → provider, OTLP export, Dockerfile, compose `app` profile, `samples/` producer (email + verified signed webhook). Review clean. |
| p1-g1 | gate | Proof 1: accepted request survives a crash after 202 | M9 | done | `CrashTests` (child-process host killed after 202). |
| p1-g2 | gate | Proof 2: duplicate request creates no new work | M9 | done | `ProducerApiTests.Concurrent_identical_requests…` |
| p1-g3 | gate | Proof 3: planning safe under retries and concurrency | M9 | done | `PlannerConcurrencyTests` |
| p1-g4 | gate | Proof 4: no lost claims; stale writes discarded | M9 | done | `DispatcherTests.Dispatcher_killed_after_send…`, `ClaimConcurrencyTests` |
| p1-g5 | gate | Proof 5: unknown-outcome policy; no failover | M9 | done | `DispatcherTests`, `WebhookProviderTests`, `SmtpProviderTests`; 400 → Content pending owner decision (deferred #15). |
| p1-g6 | gate | Proof 6: history records every attempt; stable id | M9 | done | `HistoryTests.Delivery_id_is_stable…`, `TelemetryTests` |

## Log

Newest first. One line per change to copy to the board.

| date | change |
|---|---|
| 2026-11-13 | M2 → confirm: p1-05 round 2 fixes (trace kept from the producer, StartTimeout bounds start, broker health Degraded, update race order, refusal test waits past the retry); deferred #21 (shutdown drain, Juice). |
| 2026-11-13 | M2 → r2: p1-05 round 1 fixes (topology mismatch fails at once, data numbers as doubles, race answer order); deferred #19 (prefetch, Juice), #20 (no inbox). |
| 2026-11-13 | p1-05 → doing (RabbitMQ intake built: 565 tests green); M2 → r1 for it. Deferred #17, #18 recorded (Juice tasks). |
| 2026-11-13 | Juice 10.13.0 bump (deferred #2 closed; migration `OutboxIndexFilterQuoting`; 546 tests green). p1-05 → todo (unblocked by `PublisherId`). |
| 2026-11-12 | M9 → clean (review: harness fixes only). Phase 1 gate passed; open: p1-05 (RabbitMQ), p1-09 (provisioning, audit), owner decisions #13, #15. |
| 2026-11-12 | p1-g1 … p1-g6 → done; M9 acceptance record written (mutation scores, load p95 0.71 s); M9 code review next. |
| 2026-11-12 | M6, M7, M8 → clean (re-check fixes committed); p1-12 … p1-17 review clean. Next: M9. |
| 2026-11-12 | M6, M7 confirmation fixes and M8 round 2 committed; M8 confirmation and M6/M7 re-check running. |
| 2026-11-12 | M6, M7 → confirm (round 2 fixes committed); M8 round 2 review running. |
| 2026-11-12 | M6, M7, M8 → r2 (round 2 reviews started). Round 1 is commit 99e0cbe. |
| 2026-11-12 | M6, M7, M8 round 1 fixes and tests committed (504 tests green); round 2 next. |
| 2026-11-12 | M6, M7, M8 → r1 (round 1 reviews started). |
| 2026-11-12 | p1-17 → done (recipients and due_backlog metrics, compose app profile, samples producer). M8 part 1 is commit f73ee0a. |
| 2026-11-12 | M5 committed (fbb4684); M8 work committed after it. |
| 2026-11-12 | M5 review → clean (re-confirmation fixes in); p1-10, p1-11 notes → review clean. |
| 2026-11-12 | M5 confirmation: 2 bugs fixed; deferred #12 added; re-confirmation running. |
| 2026-11-12 | p1-17: metrics and traces added; p1-g6 note: trace test green. |
| 2026-11-12 | M5 review → confirm (round 2 fixes in); deferred #10, #11 added. |
| 2026-11-12 | p1-17 note: Dockerfile added and smoke-run healthy. |
| 2026-11-12 | p1-15, p1-16 → done (history API, dead-letter retry); p1-g6 → doing (proof 6 test green); M8 note updated. |
| 2026-11-12 | Tracking file created from the repository state (commits up to 0e7d238 and the M5 working tree). |
