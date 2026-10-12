# Batch gate delivery state

Decision D1 is option A: keep model review per card, run preparation, build,
lint, and affected tests per code card, then run the complete suite once on
the combined candidate before publication. This follows the operator direction
of 2026-10-11. The project setting defaults off and to documentation scope.

## Delivered

- Code scope and look-ahead settings are validated per project. Queued review
  attempts are replanned against their immutable result refs in the background
  when settings change. Claims may proceed while selection is computed.
- The real completion path gives the review planner the run envelope's SHA and
  immutable ref before the subject file exists. Full-suite selections stay on
  the per-card route.
- The look-ahead planner sizes a batch within configured bounds from risk and
  expected suite runs, waits for compatible near-term arrivals, and honors
  deadline, urgent tags, operator close-now, and overlap policy. A separate
  overlap policy excludes cards touching a chosen member's files. Decisions
  are persisted for the state API. Pending reviews appear in the look-ahead.
- The bounded advisor can approve, adjust, or veto membership. It cannot change
  tests, and errors or timeouts use the deterministic plan.
- Batch runs request the full test level and use the configured timeout, default
  150 minutes. The gate runner performs its one targeted failed-test rerun.
  A red candidate is compared on the server with a durable full-suite base
  verdict keyed by base SHA and gate profile. A matching red base is reported
  as pre-existing and pauses as `integration-branch-red`. It cannot publish or
  validate the build profile because the suite stopped at the first red step.
  Members stay pending until a new base SHA triggers reconstruction. The remote
  worker does not spend the batch budget on a
  clean-repeat diagnosis.
  The report counts batches currently held for a red integration branch.
- Full-suite execution runs outside the 30-second planning tick. One batch runs
  per project. A documentation-only base movement queues a successor manifest
  for replay and another full gate. Publication pushes the tested SHA to origin
  and verifies the remote SHA. Member release follows that verified remote
  publication; local checkout sync is best effort. Ejected members start their
  per-card gates after the batch releases its project reservation. A rebased
  member records its exact source-to-published-SHA mapping before lane release.
  A deferred `IntegrationBranchDefect` review releases on verified batch evidence.
  Recovery failures are logged per manifest, allowing later batches to proceed.
  A queued code push keeps its project batch reservation pending until origin
  verifies the merged SHA. The pending marker survives a backend restart.
  Before waiting and on each code-enabled planning tick, the batch gate clears markers
  whose approved SHA is already on origin, whose merge was superseded,
  rolled back, or left the integration path, or whose push ended with a
  permanent branch divergence, or a completed local-only push. A failed push can therefore
  remain visible without blocking later batches forever. State and report
  expose the oldest push wait and an age alert after 15 minutes by default;
  `BatchGate:PendingPushAlertMinutes` configures that limit.
  Persisted markers are loaded at startup. Reconciliation shares one integration-tip
  fetch per repository and branch in each pass, and a disabled or documentation-only
  batch gate does not reconcile code push markers.
  Disabling the push step keeps code batching on hold while the local merge
  is still unpublished.
- `GET /api/projects/{project}/batch-gate/state` exposes plan, pending, batches,
  and member state. The existing report includes observed batch green rate.

## Open

- A remote, independently claimable gate host remains a separate architecture
  step. The backend worker currently sends the gate through the remote runner
  transport without an independent gate-attempt claim.
- The D10 historical proxy is not a real combined-tree pass rate. Compare the
  state and report with actual batches before increasing the size bounds.
- The first red candidate on a new base SHA can require one separate full
  base run. Its cached verdict is reused for later comparisons with the same
  gate profile.
- The advisor uses the registered one-shot CLI on the backend host. Its quality
  and quota behavior need live pilot evidence before using `enforce` mode.
- The command-inventory change guard lists the built-in Agent Studio project,
  .NET, and Angular inventory files. Custom build profiles need declared
  inventory paths before the same guard can cover their commands.

## Rollout and rollback

Enable one project with `scope: code`, `planner: look-ahead`,
`minimumSize: 2`, `maximumSize: 6`, `deadlineMinutes: 20`,
`lookAheadMinutes: 45`, `riskPolicy: balanced`, `overlap: adjacent`,
`reviewTestLevel: work-package`, `gateTimeoutMinutes: 150`, and
`advisor.mode: advise`. Roll back with `scope: documentation` or
`enabled: false`; pending members use the per-task gate on their immutable
result SHA. The setting does not authorize a project-wide rollout.
