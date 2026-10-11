# Batch gate delivery state

Decision D1 is option A: keep model review per card, run preparation, build,
lint, and affected tests per code card, then run the complete suite once on
the combined candidate before publication. This follows the operator direction
of 2026-10-11. The project setting defaults off and to documentation scope.

## Delivered

- Code scope and look-ahead settings are validated per project. Queued review
  attempts are replanned against their immutable result refs when settings change.
- The real completion path gives the review planner the run envelope's SHA and
  immutable ref before the subject file exists. Full-suite selections stay on
  the per-card route.
- The look-ahead planner sizes a batch within configured bounds from risk and
  expected suite runs, waits for compatible near-term arrivals, and honors
  deadline, urgent tags, operator close-now, and overlap order. Decisions are
  persisted for the state API.
- The bounded advisor can approve, adjust, or veto membership. It cannot change
  tests, and errors or timeouts use the deterministic plan.
- Batch runs request the full test level and use the configured timeout, default
  150 minutes. The gate runner performs its one targeted failed-test rerun.
  A red candidate matching the red base fingerprint is recorded as pre-existing.
- Full-suite execution runs outside the 30-second planning tick. One batch runs
  per project. A documentation-only base movement queues a successor manifest
  for replay and another full gate. Publication pushes the tested SHA to origin
  and advances a checked-out local branch with a fast-forward merge.
- `GET /api/projects/{project}/batch-gate/state` exposes plan, pending, batches,
  and member state. The existing report includes observed batch green rate.

## Open

- A remote, independently claimable gate host remains a separate architecture
  step. The current worker uses the local gate runner.
- The D10 historical proxy is not a real combined-tree pass rate. Compare the
  state and report with actual batches before increasing the size bounds.
- The advisor uses the registered one-shot CLI on the backend host. Its quality
  and quota behavior need live pilot evidence before using `enforce` mode.

## Rollout and rollback

Enable one project with `scope: code`, `planner: look-ahead`,
`minimumSize: 2`, `maximumSize: 6`, `deadlineMinutes: 20`,
`lookAheadMinutes: 45`, `riskPolicy: balanced`, `overlap: adjacent`,
`reviewTestLevel: work-package`, `gateTimeoutMinutes: 150`, and
`advisor.mode: advise`. Roll back with `scope: documentation` or
`enabled: false`; pending members use the per-task gate on their immutable
result SHA. The setting does not authorize a project-wide rollout.
