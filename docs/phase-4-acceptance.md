# Phase 4 acceptance record

**Status:** 18 November 2026, all gate checks passed. It records the M35 gate (`phase-4-plan.md` §5 M35, §6): each proof point with the test
that shows it, phases 1 to 3 still green, the load check, and the demo. Milestones M25–M34 (commits `21548ea` … `e9f1cc2`), with the
library's parts in `juice-layout` on `dev/notifications-inbox` (`a4012ae`, `13ef05b`, `3d19c60`, `bbbbdd5` + `8b519a8`). By owner decision
(17 Nov) these milestones had no review per milestone: one review loop over phases 3 and 4 follows this record. SQL Server, and proof point 5
with it, is phase 4b (decision p4-d4).

## 1. Proof points

Every test runs against the local stack in `dotnet test Juice.Notifications.slnx` and passes: 347 integration tests (the load check run
separately, below) and 884 unit tests, 18 Nov 2026. The library's 25 tests pass (`ng test @juice-js/notifications`).

| # | Proof point | Test | Notes |
|---|---|---|---|
| 1 | A provider outage fails over without losing a delivery or changing its id | `FailoverTests.A_provider_outage_fails_over_to_the_next_integration_with_the_same_delivery_id` | One delivery, an attempt through each integration, one failover. Also: retries used up then failover, an unreadable vault secret failing over (M34). |
| 2 | A destination failure does not fail over | `FailoverTests.A_destination_failure_fails_the_delivery_without_trying_another_integration` | Never after rate limiting or an unknown outcome either, and never for a target. |
| 3 | A gone push token removes its registration and is never retried | `PushFlowTests.A_gone_push_token_deletes_its_registration_without_retry_or_failover` | Through the real host: three devices, three deliveries; FCM's `UNREGISTERED` fails one after one attempt, a second FCM integration is never tried, the registration is deleted, the others stay. |
| 4 | A 1,000-notification storm reaches each route as digests | `StormTests.A_storm_of_a_thousand_reaches_each_route_as_digests_and_nothing_is_lost` | Two routes at the default threshold (100 a minute): each target's deliveries carry all thousand requests, each once, in fewer than 400 deliveries. |
| 5 | The claim store passes the same concurrency suite on SQL Server | phase 4b | Decision p4-d4. |

The rest of phase 4, by milestone (as built: `phase-4-plan.md` §5):

- **M25** callbacks (`CallbackTests`) and the provider contract kit, which every provider below passes against a fake of its API.
- **M26** SES and SendGrid (`EmailProviderFlowTests`: sent through each, SNS and signed-webhook callbacks applied, forged ones refused).
- **M27** Slack and Teams (`ChatFlowTests`: linking an account with a code, a Slack button acting on the person's inbox item, posting to a
  channel); **M28** Telegram (`TelegramFlowTests`: webhook registration, linking with `/start`, a group target).
- **M29** Twilio (`SmsFlowTests`: a phone number verified by a code sent by SMS through the pipeline, its receipt by signed callback).
- **M31** bulk replay (`StormTests.Failed_deliveries_are_replayed_in_bulk_under_their_ids`); **M32** template editing
  (`TemplateEditingTests`); **M33** redaction and retention (`RetentionTests`); **M34** vault secrets (`SecretResolverTests`).

## 2. Phases 1 to 3 still hold

Their proof point tests are in the same run and pass. Four older tests changed with the channels phase 4 registers: tests that used `slack`
or `sms` as an unknown channel now use a made-up one, and the load check turns storm protection off for its tenant (it measures one delivery
per request).

**Load check** (`LoadCheckTests`, `NOTIFICATIONS_LOAD=1`, against the rebuilt compose `app` container), run alone:

| Run | Accepted → first attempt | Refused | Lease lost | Deliveries |
|---|---|---|---|---|
| Phase 2 record (17 Nov) | p50 0.05 s, p95 0.10 s, p99 0.29 s | 0 | 0 | 4,667 of 4,667 |
| Phase 4 (18 Nov) | p50 0.05 s, **p95 0.11 s**, p99 0.28 s, max 0.46 s | 0 | 0 | 4,667 of 4,667 (667 to inboxes); 4,000 webhook sends, none twice |

Per stage at p95: planner 0.02 s, delivery planner 0.05 s, dispatcher 0.05 s. **Passed:** no change from phase 2.
**Measured wrongly first:** two earlier runs passed a test filter the runner ignored, so the whole integration suite ran beside the load on
the same machine and database server (p95 9.6 s and 13.7 s, 4 leases lost each). Those numbers measure the shared machine, not the service;
the filter is `--filter "FullyQualifiedName~LoadCheckTests"`. Both runs attempted all their deliveries; nothing was left to clean up.

## 3. Demo

`deploy/local/scripts/demo-phase4.sh`, against the rebuilt `app` container on 18 Nov 2026 (emails in Mailpit):

1. **Storm:** the tenant's threshold set to 5 a minute; 40 alerts to a route with an email target reach it as 7 emails: the first ones each,
   the rest as digests listing their alerts. `GET …/storms` shows the storm: peak 40 a minute, 35 sent as digests.
2. **Template editing:** a preview with the schema's sample data (`[ACME] Alert 7`), the tenant's template saved (version 1), the next email
   arrives as `[ACME] Alert 99`; removing it restores the application's.
3. **Failover:** an SES integration first, whose secret isn't an access key (refused before any call to AWS), then the local SMTP: the
   delivery is accepted through `mail` with 2 attempts and 1 failover, under one id.
4. **Bulk replay:** with SMTP disabled, three alerts fail on SES; with it enabled again, `POST …/deliveries/replay` (integration `ses-demo`)
   replays 3, which fail over and arrive.
5. **Settings:** retention set to 30 and 7 days (defaults 90 and 14), then back to the defaults.

**Found by the demo:** with no enabled integration at all, the planner fails the recipient without a delivery, so there is nothing to
replay (by design since phase 1: history shows the recipient's reason); the demo makes its failures with a misconfigured integration instead.

## 4. Open items

- **The review loop over phases 3 and 4** (M17–M34) runs next (owner decision, 17 Nov).
- **Not yet built, recorded in the plan's as-built notes:** a reply in Slack or Telegram after a button or a link, and the Slack message
  updated once decided; notification actions as push buttons; inbound SMS opt-outs (Twilio's own refusal, 21610, fails the number); SendGrid
  `dropped` and Twilio failures that aren't the address's leave the delivery `Accepted`; the Teams bot (decision p4-d2); voice (p4-d3).
- **Phase 4b:** SQL Server (p4-d4) and proof point 5.
- Carried in `docs/deferred.md`: #42–#45.
