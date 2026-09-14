# Review Domain Map

Version: 2026-09-14
Status: System-of-record map for Remote Review material, semantic verdicts, and grading.

Use this when a change touches ReviewSubject preparation, aspect prompts,
review-report verdicts, reissue or escalation decisions, or the evidence shown
for a remote grade.

## Review material contract

`runner/RemoteReviewWorkspace.cs` materializes the immutable delivery
Result-SHA in a disposable candidate checkout. Before the first semantic aspect
runs, it freshly fetches the frozen plan's integration ref and resolves the Git
merge-base between that ref and the Result-SHA. It then appends the same
authoritative material to every semantic aspect prompt:

- the integration ref, resolved merge-base SHA, and delivery Result-SHA;
- the changed-file list from `git diff --name-only <merge-base> <result-sha>`;
- the unified delivery diff from
  `git diff --find-renames --unified=3 <merge-base> <result-sha>`.

The semantic prompt must not rely on the review agent discovering this material
with tools. The frozen template created by
`backend/Features/Runner/RemoteReviewPlanBuilder.cs` supplies task and status
context; `RemoteReviewWorkspace` supplies Git evidence from the exact candidate
checkout at execution time. A semantic plan therefore requires an integration
ref even when no build command uses baseline comparison.

The prompt budget is 200 changed files and 12,000 unified-diff lines. Within
that budget the list and diff are complete. When either limit is exceeded, the
prompt contains a visible `REVIEW_DIFF_TRUNCATED` marker stating the shown and
total file and line counts plus the configured budget. Truncation is never
silent.

## Verdict citation contract

Every semantic aspect sentinel supplies these fields:

```text
[[ASPECT_VERDICT: status=<pass|concerns|block>; summary=<sentence>; evidence_checked=<files or sections>; missing=<exact gap, or none>]]
```

`evidence_checked` names the files, diff sections, tests, or result artifacts
the reviewer used. `missing` names the exact absent file, acceptance item,
branch, or contract for a block; pass and concern verdicts use `none` when there
is no missing gap.

`contracts/TaskServer.Contracts/ReviewContracts.cs` owns the shared
`ReviewVerdictCitationPolicy`. A semantic block without both meaningful
`evidence_checked` and `missing` values becomes `concerns` with classification
`block-without-citation`. The standalone Task Server and the monolith-compatible
review endpoint both apply this policy at report admission, so an old or
non-conforming executor cannot bypass it. If no cited block remains, the review
grades as Pass with concerns and the acceptance rail does not reissue or
escalate on the uncited refusal. `orchestrator-engine/CouncilLoop` still records
that verdict in its finding count but explicitly excludes the
`block-without-citation` classification from its blocker count.

Deterministic build and test verdicts are not model opinions. Their command,
baseline SHA, and exact new failures provide their citation and retain normal
blocking behavior.

## Failure attribution contract (AGT-2819)

A failing verification command is attributed before it is graded. Every
deterministic gate in the frozen plan carries `CompareToBaseline: true`, so when
it fails on the delivery the executor runs the same command on the merge base and
records that run's exit code as `BaselineExitCode` alongside the baseline SHA.
`contracts/TaskServer.Contracts/ReviewFailureAttributionPolicy.cs` then names the
owner:

| Gate failed on delivery | New failure names | Merge base | Owner |
|---|---|---|---|
| yes | any | not measured | `Delivery` (fails closed) |
| yes | one or more | any | `Delivery` |
| yes | none | red | `IntegrationBranch` |
| yes | none, exit-status gate | green | `Delivery` |
| yes | none, failure-name gate | green | `Tolerated` (flaky retry) |
| no | - | - | `None` |

Two comparison modes exist because the evidence differs by gate. A test gate uses
`ReviewBaselineModes.TestFailures`: failure names are diffed, so a new failure
inside an already-red suite still blocks the card. A lint or build gate uses
`ReviewBaselineModes.ExitStatus`: there are no names to diff, and synthesising an
`<unparsed failure in verify-N>` marker for one was exactly the bug - it made a
gate that was already red on the branch look like a brand-new product failure on
every card.

The terminal follows the attribution. Any `Delivery` owner grades
`ProductFailure`. Otherwise, one or more `IntegrationBranch` owners grade the
distinct terminal `ReviewTerminalOutcome.IntegrationBranchDefect`, which:

- is `AttemptLifecycleState.Completed`, not `Failed` - the review reached a
  verdict and it was not against the card;
- is admissible for integration (`RemoteDeliveryIntegrationPolicy`), because the
  delivery does not make the branch worse. Refusing the card for branch debt is
  what stalled the whole board in Human Review with "Remote Review ended with
  'ProductFailure', not Pass" while the deliveries were merged by hand;
- appears as a per-gate `integration-branch-defect` status in the orchestration
  gate projection rather than `passed`, so the branch defect is never hidden;
- carries an operator-feed alert and a card timeline entry, see below.

A report that records a `BaselineSha` without a `BaselineExitCode` is
`ReviewInfra/BaselineEvidenceInvalid`: incomplete evidence, not a product
finding.

## Integration-branch gate health (AGT-2819)

Because each gate is measured on the merge base on every delivery, the review
plane is also the integration branch's health monitor.
`backend/Features/Pipeline/IntegrationBranchGateReporter.cs` reads each settled
report, and `IntegrationBranchGateHealthPolicy` decides what is news:

- `TurnedRed` is the only alerting transition, so a branch that stays broken for
  weeks costs one operator-feed message (`topic: integration-branch-gate-red`),
  not one per card that passes through it.
- The alert names the step, its command, the merge-base commit the gate is red
  at, and - when an earlier green measurement exists - the
  `<last green>..<red>` range the breaking commit lies in. It never claims a
  single culprit commit, because a merge-base measurement cannot prove one
  without a bisect.
- Only a merge-base run moves the named commits. A gate that simply passed on a
  delivery clears a stale red flag without recording a branch commit, so a later
  regression alerts again.

State lives in
`<TaskRepository>/logs/integration-gate-health/<project>.json`. Losing that file
costs one repeated alert, never a missed one.

## Key code and tests

- `runner/RemoteReviewWorkspace.cs`: exact checkout, integration-ref fetch,
  merge-base, bounded review material, aspect parsing, and executor-side
  citation downgrade.
- `backend/Features/Runner/RemoteReviewPlanBuilder.cs`: freezes task context,
  aspect templates, CLI, model, thinking level, and integration ref.
- `contracts/TaskServer.Contracts/ReviewContracts.cs`: verdict wire shape and
  shared citation policy.
- `task-server/TaskServerReviewStore.cs` and
  `backend/Features/Runner/V1ReviewPlaneEndpoints.cs`: authoritative report
  admission and acceptance-rail protection.
- `orchestrator-engine/OrchestrationStageHandlers.cs`: Council records an
  uncited downgrade as a concern without turning it back into a reissue.
- `runner.Tests/RemoteReviewWorkspaceTests.cs`: exact docs-diff visibility and
  explicit truncation coverage.
- `backend.Tests/ReviewGradingPolicyTests.cs`: uncited-block downgrade and cited
  block preservation.
- `contracts/TaskServer.Contracts/ReviewFailureAttributionPolicy.cs` and
  `backend.Tests/ReviewFailureAttributionPolicyTests.cs`: the attribution matrix.
- `backend/Features/Pipeline/IntegrationBranchGateHealthPolicy.cs`,
  `IntegrationBranchGateHealthStore.cs`, `IntegrationBranchGateReporter.cs` with
  `backend.Tests/IntegrationBranchGateHealthPolicyTests.cs` and
  `backend.Tests/IntegrationBranchGateReporterTests.cs`: red-transition alerting.
- `runner.Tests/RemoteReviewWorkspaceTests.cs` and
  `task-server.Tests/RemoteReviewAuthorityTests.cs`: a lint gate red on the merge
  base settles as `IntegrationBranchDefect`; red only on the delivery still
  settles as `ProductFailure`.
