# Queued-But-Unpickable Cards (Pickup Holds)

Status: implemented (AGT-2818). Owner surfaces:
`backend/Shared/Models/PickupHold.cs`, `backend/Features/Tasks/PickupHoldSweep.cs`,
`frontend/src/app/components/pickup-hold/`.

A card sitting in a pickup lane that the pickup gate skips carries a
**pickup hold**: the mechanism holding it, the specific reason, when the hold
started, how long it has been true, and what an operator would have to do. The
projection is derived from the same facts the runner admission gate consults, so
the board and the admission decision cannot disagree.

Sibling: [parked-card recall](./parked-card-recall.md) covers the escalated lane
and the `parkedBlocker` projection. This document covers the pickup lanes and the
waits-on and dispatch projections.

## Why this exists

Operator observation, 2026-09-14. The board reported two cards as queued in
`2-ready` while neither could ever be picked, and in both cases the reason was
already recorded in the product and simply not shown.

- **AGT-2373** sat in `2-ready` from 2026-08-11, a month, behind
  `dependsOn: [{ key: "AGT-2372", releaseGate: true }]`. AGT-2372 is in
  `7-archive` and carries no release flag, and `WaitsOnEvaluator.Evaluate`
  computes `fulfilled = terminal && (!releaseGate || target.Released)`. The card
  said `waits for release: AGT-2372` and nothing else: not that the target is
  archived, not that no run will ever clear it, not how long it had been
  waiting, not what to do about it.
- **AGT-2738** sat in `2-ready` from 2026-09-06 with a `remoteDispatchRejection`
  recorded by `agent-runner-01` (`capability-mismatch`,
  `task-server:connectivity` unavailable). No template under
  `frontend/src/app` read that field. The card looked queued and was silently
  skipped on every tick.

Both were found by reading `task.json` from disk, which is why the lane read as
"the system is standing".

## The four parts

### 1. An unsatisfiable gate is a configuration error, like a cycle

`WaitsOnItem` distinguishes three things that used to be one: `fulfilled`,
`waitingForRelease`, and now `unsatisfiable` with an `unsatisfiableReason`.
A `releaseGate` edge whose target is terminal, unreleased and **archived** is
unsatisfiable: no run is left that could set the flag. `WaitsOnStatus` raises
`unsatisfiableGate` for the card.

`unsatisfiable` is additive to `waitingForRelease` on purpose. The way out is
still an explicit release, so every affordance keyed on that flag keeps working,
including the dependent-side release button in `release-gate-section`.

`ProjectRunner.IsBlockedByWaitsOn` already raised a once-per-card structured
warning for a cycle and only a `LogDebug` for an ordinary wait. An unsatisfiable
gate now earns the cycle's warning, deduplicated per card in its own set so a
card can move between the two classifications without losing either report. What
the pickup gate admits is unchanged: the card was skipped before and is skipped
now.

### 2. The card names the state, not only the wait

`TaskInfo.PickupHold` is a read-time projection built by `PickupHoldPolicy`, a
pure decision matrix whose check order mirrors
`ProjectRunner.IsReadyPickupCandidate`:

| Mechanism | Holds because |
|---|---|
| `pickup-policy` | human agent, fixture, human-decision marker, or intake has not passed the card |
| `epic-container` | an epic came to rest in a pickup lane; epics never execute |
| `crash-backoff` | the last run crashed immediately and the cooldown has not expired |
| `dependency-gate` | a `dependsOn` edge is open, unknown, cyclic, or unsatisfiable |
| `dispatch-rejection` | a runner refused the offered card and recorded why |

The dispatch rejection is checked last: a card the local gate already refuses was
never offered to a runner, so a leftover refusal must not claim to be what is
holding it.

Each hold carries `sinceUtc` (the refusal instant for a refused dispatch, the
lane entry otherwise), `heldForSeconds`, `unsatisfiable`, and `resolutions[]`.

### 3. The ways out are offered, never taken

For the archived-gate case the two resolutions are `release-target`
(`PUT /api/tasks/{id}/release`) and `drop-release-gate` (remove the edge through
`PUT /api/tasks/{id}/references` and re-plan the dependent). Both are presented;
neither is applied. Releasing a validation gate states that the validation no
longer has to happen, which is an operator decision, so no sweep, projection or
background service in this feature performs a write.

### 4. A one-off sweep for the existing backlog

`PickupHoldSweep.RunOnce()` runs at boot and reports every card currently held in
a pickup lane across all projects, with mechanism, reason and age, so the backlog
that accumulated while the state was invisible becomes visible once. The same
evaluation is served live by `GET /api/pickup-holds`
(`?project=`, `?unsatisfiableOnly=true`), which returns totals, a per-mechanism
breakdown, and the held cards ordered oldest hold first. It is report-only, for
the same reason as part 3.

The sweep deliberately does not consult the rapid-crash cooldown: that deadline
lives in a live runner's memory and expires within minutes, so reporting it would
be reporting noise rather than a standstill.

## Where it renders

- **Board card and task detail**: `<app-pickup-hold>` inside
  `<app-task-live-status>`, which is the single surface that owns the card's one
  current-state sentence. The card variant shows headline, mechanism, reason and
  age; the detail variant adds the "Ways out" list. The one exception is the
  refused-dispatch reason on a board card: the card renders the durable record
  itself immediately below, in full, so the block does not repeat the sentence.
- **Dependency chip**: a `blocked` tone with `gate cannot open: KEY`, distinct
  from the `open` wait tone and from the `cycle` tone.
- **The refusal record**: `<app-remote-dispatch-rejection>` now also names the
  refusal `code` and the instant, not only the runner and the reason. The board
  card renders that shared component instead of its own partial inline copy, so
  one record has one rendering on the card and in the detail header.

## Regression coverage

- `backend.Tests/WaitsOnEvaluatorTests.cs` - archived and unreleased is blocked
  and unsatisfiable, archived and released is fulfilled, completed and unreleased
  still waits for release, an unknown key stays unresolved.
- `backend.Tests/WaitsOnPickupGateTests.cs` - the unsatisfiable gate is warned
  about once across three ticks, not per tick, and does not halt the lane.
- `backend.Tests/PickupHoldPolicyTests.cs` - the mechanism matrix, including the
  precedence between a dependency gate and a stale dispatch rejection.
- `backend.Tests/PickupHoldSweepTests.cs`,
  `backend.Tests/PickupHoldEndpointsTests.cs` - both reported cards, end to end.
- `frontend/src/app/components/pickup-hold/pickup-hold.component.spec.ts`,
  `.../remote-dispatch-rejection/remote-dispatch-rejection.component.spec.ts`,
  `frontend/e2e/board/pickup-hold-visibility.spec.ts` (both themes).
