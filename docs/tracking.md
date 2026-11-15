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
| M2 | Types and intake | p1-03, p1-04, p1-05 | clean | Commit 007f690; RabbitMQ intake (p1-05) f3c7137, reviewed in two rounds, a confirmation and a re-check (72f0692, b878981, 567bd35, then the re-check's test). |
| M3 | Claim store and worker host | p1-07 | clean | Commit 43f1cea. |
| M4 | Planner and routes | p1-08, p1-09 | clean | Commit 0e7d238. p1-09 completed 13 Nov 2026 (provisioning I12, audit to services/audit, route_targets, deferred #8, #9, #11 closed): review round 1 (9 findings), round 2 (10), then four confirmations each finding narrower bugs, decided by the owner (secret rule moved to the point of use; stale route_targets rows released under a route lock); last round's 4 fixed, 1 deferred (#24). Not committed. |
| M5 | Rendering and delivery planning | p1-10, p1-11 | clean | Commit fbb4684. Round 2: 6 bugs fixed. Confirmation: 2 fixed (renders on a 128 MB stack of their own, depth limit 250; invariant culture). Re-confirmation: no crash or wrong-outcome bugs; 2 low ones fixed (a slow-unwinding template error taken for a timeout; thread start failure taken for a template failure). Deferred #10, #12. |
| M6 | Providers | p1-12, p1-13 | clean | Round 1: 13 findings; fixed SMTP 4xx/timeout/QUIT/DATA classification and settings checks, webhook sent-or-not detection and body drain, 443-only for public hosts, NAT64/6to4/IPv4-compatible addresses, http only to private, allowlist validation, URL-free traces, Verify range, client cache disposal. Deferred #13–#15. Tests: DestinationPolicyTests, SmtpProviderTests, WebhookProviderTests (M6) / DispatcherTests (M7). Round 2: 4 fixed (SMTP drop mid-body retryable, legacy settings editable, punycode sender, NAT64 local-use at every RFC 6052 position; allowlist CIDRs judged as a whole). Confirmation: 2 fixed (sender checked by building it as the send does; allowlist refuses embedded blocked ranges). Re-check (e8740d4): 3 low fixed (stale attempts outside the retry budget; producers get no provider message id; tenant-template render errors hidden from producers). |
| M7 | Dispatcher | p1-14 | clean | Round 1: 6 bugs fixed (priority claim order, sends not cancelled by stopping, retry over unapplied attempts, mandatory once per budget, backoff from the send end, missing address/secret handling). Tests: DestinationPolicyTests, SmtpProviderTests, WebhookProviderTests (M6) / DispatcherTests (M7). Round 2: 4 fixed (claim index on integer priority, refusal beats stale unknown, 45 s shutdown + 50 s stop grace, 60 s dispatcher lease floor). Confirmation: 1 fixed (a refusal outranks an unknown only when newer); options copied, not mutated. Re-check (e8740d4): 3 low fixed (stale attempts outside the retry budget; producers get no provider message id; tenant-template render errors hidden from producers). |
| M8 | History, dead letters, operations, host | p1-15, p1-16, p1-17 | clean | Round 1: 9 findings; fixed retry over unapplied attempts, offset times, producer sees target not address, single-status parsing, earliest expiry, metrics after commit, backlog gauge per running host, stale fields on retry, plan and delivery-planning spans. Deferred #16. Round 2: 6 fixed (producer-safe error texts, https MetadataAddress checked at start, no System.Net.Http meter, stale failure after a retry ignored (ReplayedAt), planner failures counted, sample exit code). Re-check (e8740d4): 3 low fixed (stale attempts outside the retry budget; producers get no provider message id; tenant-template render errors hidden from producers). |
| M9 | Proof points and acceptance | p1-g1 … p1-g6 | clean | Commits a685af5, b6e6804 and the next. All six proofs have passing tests; mutation testing and load check done (docs/phase-1-acceptance.md). Review: no production bugs; three test-harness fixes (child host never leaked, port race retried, load check reports transport failures). Mutation pass over `DeliveryPlanner` and `Intake` done 13 Nov 2026: 41.4 % → 74.4 % and 76.8 % → 82.5 % with the tests it called for (`DeliveryPlannerTests`, `IntakeScopeTests`, `IntakeValidationTests`, `RequestRulesTests`); survivors explained in the acceptance record. |

## Items (collection `tasks`, phase `p1`)

| id | kind | title | ref | status | note |
|---|---|---|---|---|---|
| p1-01 | task | Solution, packages, build props, CI | M1 | done | |
| p1-02 | task | Local stack: PostgreSQL, RabbitMQ, Keycloak, Mailpit | M1 | done | |
| p1-06 | task | DbContext, tables, migrations; applications admin | M1 | done | |
| p1-03 | task | Type declarations (versioned schema, templates) | M2 | done | |
| p1-04 | task | HTTP intake with idempotency; notification read | M2 | done | |
| p1-05 | task | RabbitMQ `NotificationRequested` intake | M2 | done | `Juice.Notifications.Messaging`, applications' `brokerUser`, broker-side retry, parking after `ParkAfter`, consumer readiness; `RabbitMQIntakeTests`. Juice consumer fixes from 10.13.1 (deferred #17–#19, #21). |
| p1-07 | task | Claim store, stage worker, concurrency suite | M3 | done | |
| p1-08 | task | Recipient resolvers and planner stage | M4 | done | |
| p1-09 | task | Tenant configuration API; provisioning; audit | M4 | done | Provisioning from `Provisioning:Path` (`ConfigurationProvisioner`, `ProvisioningTests`); audit records to services/audit over gRPC (`Audit:Endpoint`, `AuditTests`, `AuditServiceTests`; local host in deploy/local/audit); `route_targets` (`RouteTargetTests`). Deferred #22–#24. |
| p1-10 | task | Template sandbox and channel content | M5 | done | Review clean. |
| p1-11 | task | Delivery planner stage | M5 | done | Review clean. |
| p1-12 | task | Channel registry; webhook provider | M6 | done | Review clean. |
| p1-13 | task | SMTP provider (MailKit); secret references | M6 | done | Review clean. |
| p1-14 | task | Dispatcher: outcomes, backoff, unknown-outcome policy | M7 | done | Review clean. |
| p1-15 | task | Delivery history API | M8 | done | List (status, channel, integration, type, request, time; cursor paging) and detail with items and attempts. Review clean. |
| p1-16 | task | Dead-letter retry of one delivery | M8 | done | Same id, attempts kept, fresh budget (migration DeliveryReplays). Audited as an access record (p1-09, `AuditTests.A_dead_letter_retry_is_recorded`). Review clean. |
| p1-17 | task | Metrics, traces, health; App host, Dockerfile, samples | M8 | done | Meter (all §14 metrics but failovers and escalations, which are phase 4), traces producer → provider, OTLP export, Dockerfile, compose `app` profile, `samples/` producer (email + verified signed webhook). Review clean. |
| p1-g1 | gate | Proof 1: accepted request survives a crash after 202 | M9 | done | `CrashTests` (child-process host killed after 202). |
| p1-g2 | gate | Proof 2: duplicate request creates no new work | M9 | done | `ProducerApiTests.Concurrent_identical_requests…` |
| p1-g3 | gate | Proof 3: planning safe under retries and concurrency | M9 | done | `PlannerConcurrencyTests` |
| p1-g4 | gate | Proof 4: no lost claims; stale writes discarded | M9 | done | `DispatcherTests.Dispatcher_killed_after_send…`, `ClaimConcurrencyTests` |
| p1-g5 | gate | Proof 5: unknown-outcome policy; no failover | M9 | done | `DispatcherTests`, `WebhookProviderTests`, `SmtpProviderTests`; 400 → Content (owner decision 13 Nov 2026, deferred #15). |
| p1-g6 | gate | Proof 6: history records every attempt; stable id | M9 | done | `HistoryTests.Delivery_id_is_stable…`, `TelemetryTests` |

## Log

Newest first. One line per change to copy to the board.

| date | change |
|---|---|
| 2026-11-15 | M10 built (uncommitted): subscribers and aliases, `recipient_resolvers` (API and provisioning), person recipients at intake (`user`, `group`, `role` by id, `topic` by key, with the tenant's resolver), one recipient per subscriber with `Via`, alias link and unlink for tenant admins (versioned against chained links). Review: round 1 (10 findings, 7 fixed), round 2 (9, 7 fixed), round 3 clean. Integration run found one bug the review's version fix introduced (the planner's subscriber insert left `version` null; fixed). 529 unit and 244 integration tests green. |
| 2026-11-15 | Phase 2 plan accepted (`docs/phase-2-plan.md`): p2-d1 (identity from Keycloak behind `IIdentityDirectory`, verified emails only, tenant by `platform_tenant` user attribute) and p2-d2 (admin-linked aliases) decided; design §6.1, §6.4, §9.2, §12, §19, §20 updated. Board: p2-12 (device registrations) and gates p2-g5, p2-g6 added. |
| 2026-11-15 | Deferred fixes: #24 (a route Put removes only the targets it read, leaving stale rows to their release, so it no longer fails with 409 against a release in progress), #20 number half (a number, boolean, object or array where a broker message expects text is refused as `invalid_request` with field pointers instead of read as text; dates stay text), #12 (`array.sort` is ordinal, with exact number comparison), #1 documented (design §14 Alerting). #14 moved to Phase 4; #25 added (two latent gaps in the text check). Three review rounds; 527 unit and 235 integration tests green. |
| 2026-11-15 | Deferred #6 resolved: schema patterns (`pattern`, `patternProperties`) get a 100 ms match timeout instead of the library's fixed 5 s (`PatternTimeouts`); meaning and the producer contract are unchanged. The non-backtracking engine was tried and dropped (memory, and still slow on some patterns). |
| 2026-11-14 | Deferred cleanups past their milestones: #4 (a lost declaration race compares the hash the handler computed, kept in the scoped `DeclarationHash`), #10 and #14 in part (`ChannelDefinition` carries `Content` and `RepeatsSafely`; the planner and dispatcher read the definition, not the key), #12 in part (ordinal `starts_with`, `ends_with`, `index_of`, `equals_ignore_case`; `array.sort` still by culture). #1 tried and dropped (see deferred.md); #6 left for an owner decision (pattern engine). |
| 2026-11-13 | Mutation pass on destination keys and idempotency handling (M9 open item): `DeliveryPlanner` 74.4 %, `Intake` 82.5 % (79.9 % together, from 49.0 %); found untested: an idempotency key's application and tenant scope, destination key values, the planner's failure paths and template choice, intake's priority/expiry/data/link/correlation checks. 443 unit + 234 integration pass. |
| 2026-11-13 | p1-09 → done; M4 complete. Provisioning (I12): tenant setup from a JSON file at start, one tenant per transaction under an advisory lock, the API's own checks, takeover and hand-back; no path leaves provisioned rows alone. Audit: `NotificationsDbContext` on Juice `DbContextBase`, configuration rows `IAuditable`, `UseAudit()` for API changes (not producer requests), records sent to services/audit (Juice.Audit 10.6.1 gRPC client; local host in deploy/local/audit); records name the caller (`client:<id>` for client tokens, which carry no user name). `route_targets` with FKs (deferred #8), exact number normalization in configuration hashes (#9), retry audited (#11). Secrets only the deployment may name are never used for what the API sets. Load check after the DbContextBase move: p95 0.12 s. Local Postgres max_connections 300 (the suite's parallel hosts exhausted 100). Deferred #22–#24. |
| 2026-11-13 | Juice 10.13.1 released (tag on ef1f492; nuget.org). All `Juice.*` packages on 10.13.1; deferred #17–#19, #21 closed. CI reads the GitHub feed with `GIT_HUB_PAT` (GITHUB_TOKEN got 403); flaky `SmtpProviderTests` dispose fixed. |
| 2026-11-13 | Juice `release/10` pushed (6135290; CI green; alpha 10.13.0-alpha.323, then 10.13.1-alpha.324 after the version bump). Intake adopts it: parks a message still failing after `ParkAfter` (1 day) or unreadable on `notifications.requests.parked`; readiness reads the consumer (`AddRabbitMQConsumerHealthCheck`, Degraded), `BrokerHealthCheck` removed. Found and fixed: a number beyond a double (`1e400`) was accepted as `"Infinity"`; now unreadable and parked. Deferred #17–#19, #21 wait only on a stable Juice release. |
| 2026-11-13 | Juice fix for deferred #17, #18, #19, #21 committed in Juice `release/10` (6135290, unreleased, not pushed): prefetch before consume, start retry and restart after broker cancel or channel close, `RabbitMQConsumerStatus` + consumer health check, drain on shutdown, `ParkAfter` for broker-made retries, `throw-fake-exception` hook removed. One review round: 4 findings fixed with tests. Intake adoption waits for the package. |
| 2026-11-13 | Owner decisions: #13 webhooks require a signing secret (refused on save without `secretRef`; never sent unsigned; tests `PlanningApiTests`, `WebhookProviderTests.Nothing_is_sent_unsigned`); #15 a webhook 400 is `Content` (code unchanged; plan corrected). Deferred #13, #15 closed; p1-g5 note updated. |
| 2026-11-13 | p1-05 → done; M2 → clean (re-check: no bugs; added a test that readiness uses the intake's own broker check). |
| 2026-11-13 | p1-05 confirmation: one bug (broker health check still Unhealthy), fixed with its own check; checked live: broker stopped → /health/ready 200 Degraded, consumer back after restart. Re-check running. |
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
