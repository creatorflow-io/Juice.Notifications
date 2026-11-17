# Phase 3 acceptance record

**Status:** 17 November 2026, all gate checks passed. It records the M24 gate (`phase-3-plan.md` §5 M24, §6): each proof point with the
test that shows it, phases 1 and 2 still green, and the demo. Milestones M19–M23 (commits `3309d8b` … `9d2b898`, fixes from the first stack
run in `61e87ba`). By owner decision (17 Nov) these milestones had no review per milestone: one review loop over phases 3 and 4 runs after
phase 4.

## 1. Proof points

Every test runs against the local stack in `dotnet test Juice.Notifications.slnx` and passes (315 integration tests with the one added for
M24 below, the load check run separately; 700 unit tests), 17 Nov 2026.

| # | Proof point | Tests | Notes |
|---|---|---|---|
| 1 | Resolving a correlation cancels everything still due and updates every recipient's actionable item | `CorrelationTests.Resolving_cancels_every_waiting_recipient_delivery_and_escalation_and_updates_each_actionable_item` | Two people hold the approval and a second notification of the correlation waits in its workflow. Resolve cancels the waiting notification, its recipients and deliveries still `Pending` or `Retrying`, and the escalation; each person's item shows the outcome and is no longer actionable. A `Sending` delivery finishes and is recorded. |
| 2 | Acknowledging stops an escalation that stops on acknowledgement | `EscalationTests.Acknowledging_stops_an_escalation_that_stops_on_acknowledgement_and_not_one_that_doesnt`; also `EscalationTests.Resolving_stops_an_escalation_before_its_step` | A policy with `stopOnAcknowledge: false` keeps escalating after an acknowledgement. |
| 3 | A digest of N notifications is one delivery with N items, none sent twice | `DigestTests.A_digest_of_N_is_one_delivery_per_destination_with_N_items_and_none_twice` | One delivery per destination, an item per notification; delivery items stay unique per recipient, destination and channel. |

The rest of the workflow, by milestone:

- **M19 workflow runner:** `WorkflowRunnerTests`: send-only fast path, delay, condition (labels and the correlation's state when the step
  runs), throttle across workers, expiry while waiting.
- **M20 correlations:** `CorrelationTests`: acknowledging, a request on a resolved correlation reopening it (decision 14), an action recorded
  once and refused after the decision, validation.
- **M23 events:** `ProducerEventRowsTests` (an event is written only when events are on and the application has a broker user) and, added
  for this gate, `ProducerEventBrokerTests.An_application_hears_its_own_events_and_the_broker_refuses_binding_to_anothers`: on a broker
  virtual host with topic permissions, the application binds `sample.#` and receives `correlation.resolved` with routing key
  `sample.notification.correlation.resolved`; another application's user binding `sample.#`, and this one binding `#`, are refused (403).
  The plan's risk table says the permissions were tested in M23; only the rows were, so this closes that.

## 2. Phases 1 and 2 still hold

Phase 1's and phase 2's proof point tests are in the same run and pass. The load check with people in the mix is in
`phase-2-acceptance.md` §2: it runs the phase 3 code, since the `[send]` fast path (I27) keeps a type without a workflow out of the runner.

## 3. Demo

`deploy/local/scripts/demo-people.sh` (`phase-2-acceptance.md` §3), against the rebuilt `app` container on 17 Nov 2026:

1. The application declares `sample.spend.approval` (correlation required, actions Approve and Reject, workflow `send` then `escalate` with
   the tenant's policy `spend`: after a minute, the Keycloak role `on-call`) and `sample.build.finished` (a 10-second digest per person).
2. The approval goes to the group `ops`; ann's inbox shows it open, with its actions.
3. Ann approves: one `action` interaction and her first `read`, nothing decided.
4. The producer resolves the correlation (`Approved by ann`): ann's item now shows `resolved` with the outcome, and the escalation is
   `Stopped` with reason `resolved` before its first step (checked in the database).
5. Three builds finish within the digest window: Mailpit has one email, "3 builds finished", to ann@example.com.

**Found by the demo:** the inbox library compared the correlation state and contact point health in PascalCase; the API writes camelCase.
Fixed in `juice-layout` (`phase-2-acceptance.md` §3). **Found while recording this gate:** the `deploy/local/README.md` section on events
had been mangled when M23 wrote it (a shell expansion pasted half the file into the middle of a command); rewritten.

## 4. Open items

- The review loop over M17–M23 (and phase 4) runs after phase 4 (owner decision, 17 Nov).
- Carried in `docs/deferred.md`: #42–#45.
