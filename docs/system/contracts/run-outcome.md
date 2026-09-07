# Run Outcome Contract

The runner classifies a completed CLI invocation once, then every consumer reads that same classification.

## Remote execution outcome adapter

Remote coding and Remote review use the shared
`TaskServer.Contracts.ExecutionOutcomeAdapter` (`execution-outcome/v1`). Its
input is the complete immutable fact envelope: attempt kind and id, provider
terminal event, raw final assistant output, bounded stdout and stderr, exit code
or signal, timeout, OOM, cancellation, host shutdown, lease state, transport
state, provider session state, and durable output state. A terminal sentinel and
an exit code are evidence, not independent routing authorities.

The adapter emits a typed outcome, confidence or ambiguity, one recovery
action, and an optional detail. Authentication, quota, invalid
model/configuration, launch failure, CLI crash, timeout, OOM, transport loss,
host shutdown, lease loss, invalid session, explicit blocker, successful
completion, and protocol-inconclusive are distinct. `ExplicitAgentBlocker`
preserves the exact `TASK_BLOCKED` or `TASK_NEEDS_INPUT` reason in `Detail`, so
the Task Server timeline and board consumers can show the blocking reason.
`ProtocolInconclusive` remains visible and never aliases a product defect.

Review infrastructure recovery is constrained by an immutable
`RepositoryIdentity + ResultSha|ArtifactDigest` subject and can only select
`RetryReviewAttemptOnSameSubject`. It never invokes the coding model. Coding can
select one same-session resume when the runner has provider-specific resume
arguments and a captured session id. A rejected session can select one fresh
attempt from durable salvage; exhausted chains terminate visibly. Infrastructure
outcomes set every product-defect, completion, and coding-rework budget flag to
false.

### Repository identity

`RepositoryIdentity` is the materializable Git repository identity, not the
task-board project handle. `PROJ-016` identifies a project and is never a valid
repository identity in a new RunAttempt, result envelope, ReviewSubject, or
review workspace proof.

The shared `RepositoryIdentityContract.FromUrl` function is the single
derivation rule. It trims the resolved repository URL, removes trailing `/`,
lowercases it invariantly, hashes the UTF-8 value with SHA-256, and prefixes the
lowercase hex digest with `repo_`. Repository resolution uses the registry
`repo` URL when present. Otherwise it reads the `origin` URL from the project's
configured repository path. Both project shapes therefore produce the same
kind of URL-based identity before the coding lease is acquired, and completion
copies that identity unchanged into the result and review subjects.

For already persisted ReviewAttempts created with a project handle, the review
plane resolves the subject's repository URL, or the current project registry
binding when the URL is absent, and publishes the canonical URL-based identity
to the Review Executor. Reports are checked against that same resolved identity.
A `ReviewInfra` report still creates a new ReviewAttempt on the exact persisted
subject and never creates a coding attempt, so incident retries can recover
without weakening the Result-SHA or subject fences.

Provider terminal frames are normalized before classification. In particular, a
Claude-style `type=result` frame with `is_error=true`, an `error_*` subtype, or
an error status is failure evidence, never provider completion. When several
terminal frames are present, the last terminal state wins. A session rejected
by the provider cannot select `ResumeSameSession` again.

An exit-zero invocation with any explicit terminal sentinel (`TASK_DONE`,
`TASK_NOOP`, `TASK_BLOCKED`, or `TASK_NEEDS_INPUT`) suppresses infrastructure
regex matches found only in diagnostic narrative. Authoritative facts such as
`OomKilled`, lease loss, timeout, transport loss, invalid session state, or
launch failure still win over the sentinel.

Fresh-attempt recovery requires a published salvage reference whose
durable state is `Published` or `Acknowledged`. A host-local worktree path is
diagnostic evidence only and cannot authorize cross-attempt recovery. The Task
Server rejects a completion whose legacy outcome string contradicts the typed
decision, so event replay, run status, and routing cannot split.

Protocol v1 persists the whole decision as an idempotent, fenced
`execution.outcome.classified` event before completing the run. The event is the
Task Server API and timeline source for raw process facts, classifier version,
confidence/ambiguity, recovery action, and RunAttempt or ReviewAttempt identity.
Completion retries replay the same event and outcome; a mismatched attempt id,
payload, or fence fails closed. Task detail consumers read the ordered projection
from `GET /api/v1/projects/{projectId}/tasks/{taskIdentity}/attempts`; direct
event replay remains available from `GET /api/v1/runs/{runId}/events`.

## Contract

`TerminalRunOutcomeClassifier` maps the deterministic agent outcome plus process status to:

| Field | Used by |
|---|---|
| `Kind` | API/UI wire value: `success`, `failed`, `noop`, `blocked`, `needs-input`, `interrupted`, `committed-partial`, `unknown`. |
| `ProtocolResult` | The `status.md` `- Result:` line. |
| `ShouldMoveToReview` | `ProjectRunner` lane routing from `3-progress` to `4-auto-review`. |
| `ShouldShowFailureToast` | Frontend failure modal/toast gating. |

Hard sentinel matches win over process exit code. This is load-bearing on Windows: a killed or odd Codex process can report `exitCode=-1`, but if the agent emitted `[[TASK_NOOP]]`, the run outcome is `noop`, not `failed`.

## Consumer Rules

- Lane routing must call `RunCompletionPolicy.ShouldMoveToReview(TerminalRunOutcome)`.
- Summary generation must enforce `ProtocolResult` after the Haiku summary is produced.
- UI failure surfacing must use the `runOutcome` field when present and fall back to legacy `execution.status === 'failed'` only when it is absent.
- Raw process status and exit code remain visible for diagnostics, but they do not override a terminal sentinel.
- **UI outcome precedence.** Task detail derives one current-run presentation in `protocol-verdict.ts` with strict precedence `failed > needs-decision > unclear > succeeded`. A live run excludes stale terminal records and remains `Running`. Runner issues, terminal execution, `status.md`, Activity, pipeline, review, and lane are raw inputs, never independent head states.
- **One UI projection.** The task-detail parent passes that same presentation object to the protocol banner, Result chip, and final Pipeline verdict. No consumer reclassifies it. Raw inputs remain available only in the collapsed `Why this status?` disclosure. The separate verdict chain, outcome-issue chip, Activity outcome banner, and Overview FAILURE row are not primary status surfaces.

## Expected Cases

| Agent signal | Process status / exit | Kind | Protocol result | Lane | Failure toast |
|---|---:|---|---|---|---|
| `[[TASK_DONE]]` | any | `success` | `Success` | `4-auto-review` | no |
| `[[TASK_NOOP]]` | any | `noop` | `NoOp` | `4-auto-review` | no |
| `[[TASK_BLOCKED:...]]` | any | `blocked` | `Blocked` | `4-auto-review` | no |
| `[[TASK_NEEDS_INPUT:...]]` | any | `needs-input` | `NeedsInput` | `4-auto-review` unless auto-mode intercepts it first | no |
| no terminal signal, no commits | `failed` | `failed` | `Failed` | stays in `3-progress` | yes |
| no terminal signal, committed work | `failed` (exit `-1`) | `committed-partial` | `Partial` | `4-auto-review` | no |
| deliberate stop | `stopped` | `interrupted` | `Failed` | stays in `3-progress` | no |

`Partial` is reserved for runs that reached review but could not be classified confidently: a completed-but-unrecognized run (`unknown`), or a run that committed real work yet exited non-zero without a sentinel (`committed-partial`).

### Committed-partial

When a run exits non-zero **without** a terminal sentinel but `git rev-list HeadShaBefore..HeadShaAfter` shows it committed at least one change, the non-zero exit is almost always a killed downstream step (classically the watchdog terminating a post-commit test run, which on Windows reports `exitCode=-1`) rather than a genuine crash. Hard-failing such a run would discard a real commit, re-loop the card via reissue, and trip the auto-failure circuit breaker that flips the runner to manual mode. Instead the classifier routes it to `4-auto-review` as `committed-partial` (`ProtocolResult: Partial`, no crash toast). A no-sentinel card in `4-auto-review` is left for a human by `ReviewDecisionOrchestrator` — it is never auto-reissued — so auto-continuous mode is preserved. A failed run with **zero** commits is unaffected and still hard-fails.

## Post-processing outcome taxonomy

Beyond the terminal-outcome wire value, the gate / escalation path classifies a
run that did NOT sign off cleanly into one of five buckets
(`PostProcessingOutcomeTaxonomy`, AGT-1944). The bucket - not the raw exit code -
decides what happens next:

| Bucket | Meaning | Routing |
|---|---|---|
| `success` | clean terminal verdict (DONE / NOOP) | accept |
| `code-defect` | a self-reported build / compile / test failure, or an agent process violation | normal reissue / human review as a code problem |
| `environmental` | failed on the host / provider / CLI, not the change | transient members retry with backoff; every member escalates flagged `environmental` |
| `inconclusive-with-results` | no terminal verdict, but files present in `results/` | human review WITH a "partial work to inspect" hint |
| `inconclusive-empty` | no terminal verdict and nothing in `results/` | the bare `5e-escalated` park |

**Environmental retry-with-backoff.** A *transient* environmental fault clears on
its own, so the orchestrator retries it with exponential backoff before
escalating instead of parking it on first detection:

- `EnvironmentalTransient` - a host file lock (the MSBuild `MSB3021` / `MSB3026`
  / `MSB3027` copy-lock family, "the process cannot access the file … because it
  is being used by another process"), a network glitch (DNS failure,
  `ECONNRESET` / `ETIMEDOUT` / `EAI_AGAIN`, a 502/503/504 gateway blip), or a
  torn-down `/tmp` mount (`MSB1025`, `SocketException (99)`, or a NuGet
  `mkdtemp("/tmp/.dotnet.` `ENOENT` - a `PrivateTmp=true` unit restart deleting
  the mount out from under a still-running detached worker, AGT-2750). Retried
  up to `PostProcessingOutcomeTaxonomy.DefaultMaxEnvironmentalRetries` (2) with a
  30s / 120s / 300s backoff, then escalated with the `environmental` category.
- `CliLaunchFailed` - the agent CLI could not launch or its `--resume` target was
  rejected (a dead session after a backend restart). Gets one automatic
  fresh-start retry (rebuild from disk), then escalates with the
  `cli-launch-failed` category. It no longer parks straight to `5e` on the first
  launch failure.

Non-retryable environmental members (`ModelInvalid`, `ContextOverflow`,
`QuotaExhausted`, `EnvironmentBlocker`, `AuthRefreshFailed`) still escalate on
first detection with their own honest categories (AGT-1941) - re-running would
hit the same wall.

- `AuthRefreshFailed` (AGT-2066 WÄCHTER / breaker) - the agent CLI could not
  launch because its OAuth session expired and the token refresh failed ("OAuth
  session expired and could not be refreshed"). Because every parallel clean-
  context run shares the one operator credential, this fails *every* launch
  identically until the credential is re-authenticated centrally - the incident
  that drained 17 cards in minutes on 2026-07-10. Classified *before* the generic
  `CliLaunchFailed` so it is NOT retried; escalates on first detection with the
  `auth-refresh-failed` category and a re-auth instruction. Pairs with the
  primary fix (credentials shared by link, not copied - see
  `docs/system/cli/supported-clis.md`).

Environmental cycles never accrue toward the per-task no-progress quarantine
streak (`RunQuarantineBreaker.CountsAsNoProgressFailure`), and a transient
environmental fault does not raise the crash toast while it is being retried.

**Per-attempt-epoch reissue budget.** The shared reissue budget (spent by the
completion gate, evidence gate, build/test gate, lint gate, and reissue-loop
breaker) belongs to an operator-owned attempt epoch. Legacy journal rows are
epoch 0. An explicit human move out of `5-human-review` or `5e-escalated` into a
work/review lane appends an `OperatorRequeue` decision and increments
`.metadata/review-attempt.json`; only rows in that new epoch count afterward.
Automatic verdicts and automatic lane moves never increment the epoch, including
`Escalate` and `AcceptAsDone`, so agent loops cannot replenish their own
anti-churn budget. Historical rows remain append-only and readable.

The operator requeue also rotates active verdict residue (`status.md` when it is
an escalation summary, aspect/code-review outputs, pipeline/lifecycle state,
post-step outputs, and the old follow-up) into
`results/history/review-epoch-NNNN/`. That history is audit evidence and is
excluded from the active `ResultsInventory`. A requeue directly into
`4-auto-review` then enters Post Processing and queues the full gate plus aspect
path. Assessments in the new epoch do not supply the pre-requeue `status.md` or
the consumed prefix of the append-only CLI log as decision evidence;
deterministic gates and aspects must create the first decision-capable evidence
in the new epoch before another escalation can be emitted.

**Escalation categories.** The system-initiated escalation funnel
(`HumanReviewEscalationCategories`) records WHY a card was parked. AGT-1944 adds
`environmental`, `cli-launch-failed`, and `inconclusive-with-results` to the
existing set; AGT-2066 adds `auth-refresh-failed` (a failed OAuth-session refresh
breaker); an inconclusive run picks `inconclusive-with-results` over the bare
`orchestrator-inconclusive` / `infra-crash` category when its `results/` dir is
non-empty.

`environmental-load` is reserved for support-agent calls affected by sustained
host CPU saturation. It is handled before a reviewing OneShot can become a gate
verdict: dispatch queues until cooling, uses a 3x timeout after the load phase,
and retries one timeout. It must not be reported as
`orchestrator-inconclusive` or charged to the card's reissue budget.

## Regression cover

`backend.Tests/RunOutcomeContractTests.cs` locks the contract end to end: each terminal sentinel drives lane, `status.md` `ProtocolResult`, and the failure toast from one classification, and `CodexExitMinusOne_WithSentinel_ClassifiesIdenticallyAcrossAllConsumers` feeds a rendered `cli-output.log` whose exit line reports the Windows kill artifact (`status=failed, exitCode=-1`) and asserts the sentinel still wins for every consumer. This is the case the divergence bug was named after. `FailedRunThatCommitted_RoutesToReviewAsCommittedPartial` and `FailedRunWithZeroCommits_StaysHardFailure` pin the commit-aware branch: a failed, sentinel-less run routes to review as `committed-partial` only when `commitsDuringRun > 0`, and still hard-fails at zero commits.
