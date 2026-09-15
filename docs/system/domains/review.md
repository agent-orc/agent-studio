# Review Domain Map

Version: 2026-09-15
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
percent of one core within `RUNNER_REVIEW_NO_CPU_PROGRESS_SECONDS` (default
900, `0` disables). Such a command is evidenced with signal `no-progress` and
reported as `ReviewInfra/NoCpuProgress` - checked before baseline comparison, so
a hang is never parsed into test failures and graded as a product regression.
On a host without `/proc` the sampler returns nothing and the watchdog stays
inert rather than guessing a kill.

Safe parallelism and the procedure for raising it live in
[linux-runner-host.md](../../operations/setup/linux-runner-host.md#review-parallelism-and-build-server-isolation).

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

## Key code and tests

- `runner/RemoteReviewWorkspace.cs`: exact checkout, integration-ref fetch,
  merge-base, bounded review material, aspect parsing, executor-side citation
  downgrade, and the per-attempt process environment.
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
