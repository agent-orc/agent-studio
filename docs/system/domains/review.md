# Review Domain Map

Version: 2026-09-16
Status: System-of-record map for Remote Review material, semantic verdicts, and grading.

Use this when a change touches ReviewSubject preparation, aspect prompts,
review-report verdicts, reissue or escalation decisions, the isolation of
concurrent review attempts, or the evidence shown for a remote grade.

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

## Aspect budget contract

`contracts/TaskServer.Contracts/ReviewAspectBudgetPolicy.cs` derives one aspect
call's wall-clock budget as `base(cli) * weight(model) * weight(thinking level)
+ material`, clamped to 60..7200 seconds. Each term answers a question an
operator can check: which toolchain, how much thinking the routed model does,
and how much there is to read. The material term is bounded so one enormous diff
cannot buy an unbounded budget.

The plan builder freezes a budget derived from the authored prompt;
`RemoteReviewWorkspace` re-derives it once it has appended the authoritative
diff and runs the larger of the two, because the frozen prompt does not yet
carry that material. The executed budget, never the frozen one, is what the
command evidence and any violation report.

A review that runs out of budget is an infrastructure fact about a model on a
host, not a verdict about the change. The failure is classified `AspectTimeout`,
and its sentence names the model and the limit
(`violated review-command budget on model '<model>'`). A command that produces
no output at all for its silence window
(`RUNNER_COMMAND_SILENCE_WATCHDOG_SECONDS`, default 600 s, engaged only when it
is strictly tighter than the command budget) is killed as `CommandStalled`
rather than holding its review slot for the rest of the budget. That watchdog and
the no-CPU-progress watchdog below are separate detectors and neither subsumes
the other: silence catches a command that keeps burning CPU without ever
producing a line, no-CPU-progress catches a tree blocked on a host-shared
handle.

A blocked tree can trip both watchdogs. `StallDetection` in
`runner/RemoteReviewWorkspace.cs` lets whichever detector actually reaches its
own threshold first claim the kill (an atomic first-write-wins record), so the
report always names the detector that fired - `detector=silence` or
`detector=no-cpu-progress` - together with its effective window and what was
measured (AGT-2851). Earlier the silence catch always took priority whenever
its own poll loop had independently observed silence, which happened to match
reality only because the silence window defaulted tighter than the
no-CPU-progress window; an operator raising the silence window past the
no-CPU-progress window stopped changing which detector actually ended the
command, and the report kept saying "silence" regardless.

`contracts/TaskServer.Contracts/ReviewPlanResourcePolicy.cs` caps every
`dotnet build` and `dotnet test` in a frozen plan at `-maxcpucount:2` and starts
them with `-nodeReuse:false`. The cap is deterministic rather than derived from
measured host load: the plan is frozen before an executor claims it, so a
load-derived cap would make the fenced command depend on when it was built, and
host load is already a separate admission gate
(`runner/ReviewSlotAdmissionPolicy.cs`).

## Attempt isolation contract

One host runs several fenced ReviewAttempts at once. Isolation is what makes
that safe, and it has three layers.

**Filesystem and network.** `RemoteReviewWorkspace` roots every writable path
under the attempt directory (`HOME`, `TMPDIR`, `XDG_CACHE_HOME`,
`NUGET_PACKAGES`, `npm_config_cache`, `PIP_CACHE_DIR`, `CARGO_HOME`,
`GRADLE_USER_HOME`, `DOTNET_CLI_HOME`). The lease supplies the attempt's
`ResourceNamespace` for containers and databases and an eight-port window from
`PortBase`. The baseline worktree gets its own sub-root inside the same attempt,
so candidate and baseline never share a cache either.

**Build servers.** Path isolation does not cover them. On Linux, Roslyn's
`VBCSCompiler` listens on `/tmp/<pipename>` derived from the compiler directory
and the user name, and reusable MSBuild worker nodes listen on
`/tmp/MSBuild<pid>`. Both are host-global for one service account and one SDK,
and both keep the working directory of whichever attempt started them, so an
attempt that has been cleaned up leaves a server answering from a deleted tree
(AGT-2831). `runner/ReviewBuildServerIsolation.cs` therefore removes the shared
servers rather than renaming them: `MSBUILDDISABLENODEREUSE=1`,
`DOTNET_CLI_USE_MSBUILD_SERVER=0`, `UseSharedCompilation=false`, plus an
attempt-local `MSBUILDDEBUGPATH`. It is applied last to the candidate, baseline
and preparation environments, so an immutable plan can never re-enable a
host-shared build server. The attempt's `ReviewEnvironmentDto` carries
`buildServers=per-attempt` as proof.

**Progress.** A command that blocks on a host-shared handle consumes no CPU,
prints nothing and never exits, so a wall-clock budget cannot tell it apart
from a slow build. `runner/CommandProgressWatchdog.cs` samples the command's
whole process tree from `/proc` and kills it when the tree fails to burn one
percent of one core within its effective no-CPU-progress window. That window is
never smaller than `RUNNER_REVIEW_NO_CPU_PROGRESS_SECONDS` (default 900, `0`
disables) but also never smaller than half the executing command's own budget
(`RemoteReviewWorkspace.NoCpuProgressWindow`, AGT-2851): a healthy integration
suite run with `ParallelizeTestCollections=false` can sit near 0% CPU for long
stretches between test classes, and a fixed 900 s floor killed reviews on this
host that would otherwise have finished in 20-25 minutes. Such a command is
evidenced with signal `no-progress` and reported as `ReviewInfra/NoCpuProgress`
- checked before baseline comparison, so a hang is never parsed into test
failures and graded as a product regression. On a host without `/proc` the
sampler returns nothing and the watchdog stays inert rather than guessing a
kill.

`ReviewPlanResourcePolicy` also adds `--logger "console;verbosity=normal"` to
every review `dotnet test` invocation, so the silence watchdog above has real
per-test progress lines to reset its clock against through a long quiet suite
instead of relying only on the default console logger's start/end output.

Safe parallelism and the procedure for raising it live in
[linux-runner-host.md](../../operations/setup/linux-runner-host.md#review-parallelism-and-build-server-isolation).

## Review-plane parallelism

`AdaptiveReviewParallelismAdvisor` owns the review plane's recommended
parallelism. The recommendation rides the minutely capability advertisement as
`RoleMaxParallelism`, and `TaskServerClient.RoleMaxParallelism` is the ceiling
`RemoteReviewDaemon` actually claims against - the review counterpart of the
coding runner's central capacity. A lowered ceiling stops new claims; it never
cancels an active slot.

`RUNNER_MAX_PARALLELISM` is deprecated as the live review control. It seeds the
ceiling until the first advertisement is answered and remains the fallback for
an older server; a later file change does not replace the recommendation. It
still bounds the recommendation from above: the capability-advertisement
endpoint clamps `AdaptiveReviewParallelismAdvisor`'s one global number to each
executor's own registered bootstrap before answering `RoleMaxParallelism`, so
one host's declared capacity is never exceeded (AGT-2848). The advisor's input
is the combined backlog of the legacy post-processing queue and current
attempt-authority ReviewAttempts in state Pending (`GET
/api/runner/auto-review-queue`), and a raised
`AutoReviewQueueAdaptiveParallelism:BaselineParallelism` is adopted on the next
refresh once the queue is non-empty, without a backend restart. The
operator-facing form of this is in
[docs/operations/remote-hosts.md](../../operations/remote-hosts.md) and
[docs/operations/setup/linux-runner-host.md](../../operations/setup/linux-runner-host.md#how-the-review-ceiling-is-derived).

## Infrastructure retry scheduling

A `ReviewInfra` outcome (`AspectTimeout`, `ToolUnavailable`, `BaselineUnavailable`,
a workspace failure, ...) does not mint its linked retry attempt in the same
instant as the failure report. `AttemptAuthorityService.ScheduleReviewInfrastructureRetry`
records a due time instead: bounded backoff of 1, 3, then 9 minutes, indexed by
how many linked retries the chain has already spent
(`AttemptAuthorityService.ReviewInfrastructureRetryBackoff`). The card timeline
gets a `review_infrastructure_retry_scheduled` entry naming the retry number,
the budget of three, the delay, and the failure reason at schedule time; the
card stays in `4-auto-review` with no successor to claim until the delay
elapses. `ReviewInfrastructureRetryScheduler`, a background service, polls
`AttemptAuthorityService.DueReviewInfrastructureRetries` and mints the
successor once it is due, reusing the failed attempt's exact ReviewSubject
(same Result-SHA, same commands) unless the failure was `PreparationFailed`, in
which case it rebuilds the plan the same way the endpoint's inline retry used
to (AGT-2831 stale checkouts get a fresh preparation profile). This closes the
AGT-2841 gap where a `ReviewInfra` verdict left a card sitting in Auto Review
with no automatic next attempt until an operator issued `POST /move`.

Once `AttemptAuthorityService.ReviewInfrastructureRetryBudget` (three) linked
retries have all failed, no further retry is scheduled and the card is parked
in `5e-escalated` with a named reason exactly as before this change - the
budget-exhaustion and repeat-diagnosis behavior in
[`ReviewInfrastructureRepeatPolicy`](../../../backend/Features/Runner/ReviewInfrastructureRepeatPolicy.cs)
is unaffected by the scheduling delay.

## Baseline verify result cache

A candidate verify failure is not a verdict by itself: the same command runs
again on the merge-base to separate new failures from failures the integration
branch already had. That second run costs what the first cost - the backend
suite is roughly twenty minutes - and several attempts on one integration
branch resolve the same baseline SHA within the hour, so before AGT-2843 a card
paid for the identical baseline run once per attempt.

`runner/ReviewBaselineResultCache.cs` stores each baseline result once per host
under `$RUNNER_REVIEW_WORKDIR/.baseline-cache`, keyed by the inputs the grade
already records: repository, resolved baseline SHA, verify command line, and a
toolchain fingerprint over the `runtime`, `git`, and per-step executable
identities in `ReviewEnvironmentDto.Toolchain`. An entry holds the parsed
failure list plus the complete stdout and stderr, so a reused result is
re-attached as this attempt's baseline command evidence and cites the same
artefacts a fresh baseline run would have produced.

Three rules keep a hit honest:

- **A hit never replaces a candidate run.** The lookup happens only after the
  candidate command has already failed in this attempt's own workspace, and the
  flake retry still re-runs the candidate.
- **Entries expire.** A result older than 24 hours is dropped and re-executed,
  and every baseline SHA the freshly fetched integration ref no longer contains
  is pruned before the first lookup of an attempt.
- **Reuse is visible.** The verdict summary, the Markdown grade
  (`baselineReused`/`baselineReuse` frontmatter plus a `Baseline` column in the
  command evidence table), and `ReviewProjectionView.Attempts[].BaselineReused`
  all carry the same sentence, worded once by
  `ReviewBaselineReuse.Citation`: `baseline result reused from attempt <id>
  (<age>)`. A baseline this attempt executed itself reads `baseline executed in
  this attempt`.

Operating the cache, including where it lives and how to clear it, is in
[linux-runner-host.md](../../operations/setup/linux-runner-host.md#baseline-verify-result-cache).

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
  merge-base, bounded review material, aspect parsing, executor-side citation
  downgrade, and the per-attempt process environment.
- `runner/ReviewBaselineResultCache.cs`: baseline result key, bounded lifetime,
  integration-branch pruning, and the atomic per-entry store.
- `runner/ReviewBuildServerIsolation.cs` and
  `runner/CommandProgressWatchdog.cs`: the per-attempt build namespace and the
  CPU-progress hang watchdog.
- `backend/Features/Runner/RemoteReviewPlanBuilder.cs`: freezes task context,
  aspect templates, CLI, model, thinking level, and integration ref.
- `contracts/TaskServer.Contracts/ReviewContracts.cs`: verdict wire shape and
  shared citation policy.
- `task-server/TaskServerReviewStore.cs` and
  `backend/Features/Runner/V1ReviewPlaneEndpoints.cs`: authoritative report
  admission and acceptance-rail protection.
- The rail's integration truth and its one-refusal-per-state-change rule live
  in the pipeline domain:
  [pipeline.md](./pipeline.md#acceptance-rail-one-integration-truth-one-refusal).
- `orchestrator-engine/OrchestrationStageHandlers.cs`: Council records an
  uncited downgrade as a concern without turning it back into a reissue.
- `runner.Tests/RemoteReviewWorkspaceTests.cs`: exact docs-diff visibility and
  explicit truncation coverage.
- `runner.Tests/ReviewConcurrentWorkspaceIsolationTests.cs`: four concurrent
  attempts of one subject, and the reap-and-classify path for a command blocked
  on a host-shared handle.
- `runner.Tests/ReviewBuildServerIsolationTests.cs` and
  `runner.Tests/CommandProgressWatchdogTests.cs`: the two decision matrices.
- `scripts/review-build-server-isolation-probe.sh`: operator probe that measures
  host-global build servers left behind by concurrent attempts.
- `backend.Tests/ReviewGradingPolicyTests.cs`: uncited-block downgrade and cited
  block preservation.
- `backend/Features/Runner/AttemptAuthorityService.cs`
  (`ScheduleReviewInfrastructureRetry`, `DueReviewInfrastructureRetries`,
  `ClearScheduledReviewInfrastructureRetry`) and
  `backend/Features/Runner/ReviewInfrastructureRetryScheduler.cs`: bounded
  backoff scheduling and firing of linked `ReviewInfra` retries.
- `backend.Tests/AttemptAuthorityServiceTests.cs`
  (`Schedule_review_infrastructure_retry_defers_the_successor_by_one_backoff_step`,
  `Schedule_review_infrastructure_retry_uses_widening_backoff_and_stops_at_the_cap`)
  and `backend.Tests/RemoteRunnerEndToEndTests.cs`
  (`Monolith_v1_review_plane_schedules_a_named_retry_for_a_fake_aspect_timeout_without_an_operator_move`):
  the backoff schedule, the three-retry cap, and the
  `review_infrastructure_retry_scheduled` timeline entry.
- `contracts/TaskServer.Contracts/ReviewAspectBudgetPolicy.cs` and
  `runner.Tests/ReviewAspectBudgetPolicyTests.cs`: the derived per-aspect budget.
- `contracts/TaskServer.Contracts/ReviewPlanResourcePolicy.cs` and
  `runner.Tests/ReviewPlanResourcePolicyTests.cs`: CPU cap and node-reuse switch
  for every .NET command in a frozen plan.
- `backend/Features/Runner/AdaptiveReviewParallelismPolicy.cs` and
  `backend.Tests/V1ReviewPlaneDiagnosticsEndpointTests.cs`: the recommendation
  and the advertisement that delivers it to the review runner.
- `backend/Features/Runner/RemoteReviewReportEvidence.cs` and
  `backend/Features/Review/ReviewProjection.cs`: the grade's baseline-reuse
  citation and the card projection that reads it back, covered by
  `backend.Tests/RemoteReviewReportEvidenceTests.cs`.
- `runner.Tests/RemoteReviewWorkspaceTests.cs`: baseline cache hit, miss,
  expiry, pruning, and a new-failure classification against a reused baseline.
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
