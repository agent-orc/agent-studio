# Runner Domain Map

Version: 2026-08-12
Status: System-of-record map for runner-side changes.

Use this when a change touches task pickup, active execution, post-run outcome
policy, reissue behavior, crash recovery, supervisor loops, or runtime runner
state.

## Entry Points

- Start with [docs/common-problems/](../common-problems) for recurring
  runner, CLI, permission, filesystem, and state-machine failures.
- Use [Services killed by a harness sweep](../../operations/common-problems/services-killed-by-harness-sweep/)
  when choosing whether a command is session-owned, OS-owned, or a child
  process that must remain owned by one coding run.
- Use [docs/system/contracts/run-outcome.md](../contracts/run-outcome.md) for the shared
  classification that drives lane routing, `status.md`, and frontend failure
  surfacing.
- Use [Runner provenance, host handoff, and continuation](../../concepts/completion-review-and-remote-runner-stability.html#provenance)
  when a change touches historical runner attribution, runner assignment during
  active work, cross-host continuation, attempt identity, or per-step placement.
  It defines the deferred-switch default, the no-hot-migration boundary, and the
  ordered runner route shown in Overview and task detail.
- Read [docs/concepts/orchestrator-drive-to-conclusion.html](../../concepts/orchestrator-drive-to-conclusion.html)
  before touching reissue, retry, CLI-crash, or classifier logic: it holds the
  target model (retry-with-cooldown, no classifier-unknown, honest human-review
  terminal) and a running case log. Append new crash incidents there.
- Use [docs/system/contracts/agent-task.md](../contracts/agent-task.md) for the boundary
  between the application-owned lifecycle and the CLI-owned task work.
- Use [docs/research/orchestrator-decision-protocol-2026-05.md](../research/orchestrator-decision-protocol-2026-05.md)
  and [docs/system/architecture/decisions/adr-archive.md](../architecture/decisions/adr-archive.md) for ADR-0002
  and runner decision rationale.

## Key Code

- `backend/Services/TaskRunnerService.cs`: project runner ownership and public
  start, stop, continue, and mode surface.
- `runner/TaskServerConnectivityMonitor.cs`, `DaemonIdleWatchdog.cs`,
  `RemoteRunnerDaemon.cs`, and `RemoteReviewDaemon.cs`: host-side Task Server
  route and loop liveness. Poll failures use bounded backoff and transition
  logging, while a slot-free process that stops starting polls for five minutes
  logs a fatal invariant and exits for service-manager replacement. Capability
  advertisement re-registers after the backend forgets its in-memory runner
  identity. The connectivity capability's three-minute freshness deadline is
  the remote alarm because a broken route cannot deliver its own failure
  telemetry.
- `deploy/windows/agent-runner-tunnel/tunnel-watchdog.sh` and its Scheduled
  Task registration: Windows-side one-minute supervision of the interim reverse
  tunnel. Two failed runner-side functional probes trigger targeted remote
  listener cleanup, a TunnelKeeper task restart, bounded verification, and an
  operator alarm after two failed heals. TunnelKeeper preserves OpenSSH output
  and eventual exit codes under its local state directory for incident root
  cause analysis. It has no automatic Scheduled Task retry, so it cannot race
  the watchdog recovery policy.
- `backend/Services/Runner/ProjectRunner.cs`: per-project pickup tick, active
  job latch, progress-first resume, dead-letter handling, and CLI spawn path.
- `backend/Shared/Runner/FollowUpAdmissionPolicy.cs`: the pure lane / phase /
  execution-location decision that gates whether a user follow-up may spawn a
  local process or has to be queued as a saved intent. See
  [Follow-ups: admission, queueing, preservation](#follow-ups-admission-queueing-preservation).
- `cli-hosting/TaskCleanContextStore.cs`,
  `backend/Features/Cli/Execution/CleanContextPreparation.cs`, and
  `runner/CarWorkerExecution.cs`: the shared local/remote clean-home path,
  task marker, seed, restart adoption, CAR environment bridge, and bounded
  retention contract. Continue planning resolves this stable home before Codex
  rollout viability is checked.
- `runner/CliProtocolNoveltyTracker.cs`,
  `contracts/TaskServer.Contracts/ProtocolNoveltyContracts.cs`, and
  `contracts/TaskServer.Contracts/ExecutionOutcomeContracts.cs`: version-aware
  provider-frame observation, scrubbed unknown-frame telemetry, provider
  terminal extraction, and the pure typed-outcome policy. The
  [CLI frame compatibility matrix](../cli/frame-compatibility-matrix.md) is the
  current vocabulary and replay-corpus system of record.
- `backend/Features/Runner/PromptEnrichmentService.cs`,
  `backend/Features/Tasks/LeaseEndpoints.cs`, and
  `runner/RemoteRunPrompt.cs`: shared pre-spawn prompt materialization. Local
  fresh runs call the service before the pickup lock and in-memory run claim.
  Remote claims materialize the same report before lease acquisition and lane
  movement, then compose the exact generated context into the existing
  `RunSpec.ModeFraming` value. The runner persists that single spec in its slot,
  keeping mode framing, results-directory guidance, and enrichment on one
  deterministic prompt path. The authored `prompt.md` is never rewritten. A
  missing or unwritable report is an admission failure, while selector failure
  may use the authored prompt only after a `fallback-unenriched` report has been
  persisted.
- `backend/Features/Runner/RunTimelineEventFactory.cs`: canonical projection of
  run execution context and terminal run facts into timeline events. Execution
  context preserves model, thinking level, source origin, and exact source
  members; zero MCP counts and the permanent permission mode are omitted.
- `backend/Features/Runner/WorktreeRunPolicy.cs`: pure always-worktree policy -
  whether a run must be worktree-isolated, the main-checkout guard condition, and
  the cwd-keyed session-resume gate (see ADR-0057). Every source-mutating run,
  including a single-slot run, requires an authoritative Git repository and its
  own task worktree. A non-Git project is rejected for mutating runs instead of
  falling back to in-place execution; read-only planning and research remain
  eligible to run in place.
- `backend/Features/Git/GitBranchRetention.cs`: host-owned recurring repository
  maintenance. The startup-and-daily pass fetches with prune, removes missing
  worktree registrations, and deletes only `task/*` and `runner/*` refs whose
  tip commit is older than the configured retention window and contained in
  both `develop` and `main`. Missing refs, active worktrees, changed tips, and
  origin refresh failures retain the branch. Remote deletes use an exact-tip
  force-with-lease.
- `backend/Services/Runner/AgentOutcomeAnalyzer.cs`: terminal sentinel and
  issue-kind classification.
- `backend/Services/Runner/RunOutcomePolicy.cs`: deterministic outcome action
  mapping.
- `backend/Features/Runner/RunTimeline.cs`: additive projection from durable
  `session-events.jsonl` rows, Attempt Authority, the unified timeline, and CLI
  output into `GET /api/tasks/{id}/runs`. New local finishes and accepted
  remote settlements close the matching session row with result, finish time,
  exit code, and duration. This is a display receipt only: the remote
  RunAttempt and its completion-envelope policy remain the terminal authority.
  A successor start closes an older open row as `superseded`; the legacy read
  projection first prefers a terminal RunAttempt, `agent_run_finished`, or a
  CLI exit, then uses the successor boundary and last bounded CLI activity.
  A terminal legacy row with no usable evidence is
  explicitly marked `legacy-missing` instead of being presented as an
  unexplained active or unknown run.
  Confirmed local process starts and remote claims persist the resolved model
  and thinking level on the session event; remote claims also persist their
  fenced Attempt id. Every new `RunRecord` carries those values independently
  of optional CLI init frames or token summaries.
- `backend/Services/Runner/OrchestratorChatLog.cs`: typed orchestrator messages
  written into `logs/cli-output.log`.
- `backend/Features/Tasks/CliOutputLogFile.cs` and
  `CliOutputLogMaintenanceService.cs`: the single bounded write boundary for
  local, remote, recovery, and orchestrator CLI-log output. The active log and
  one ignored rotation are capped at 10 MiB each; startup migrates oversized
  legacy logs before CLI reattachment and history readers run.
- `backend/Features/Runner/OrchestratorChat.cs` and `OrchestratorRunner.cs`:
  side-sheet chat dispatch. This operating mode accepts the effective model and
  reasoning choice from the live Codex catalogue, executes through the Codex
  one-shot registry, and rejects non-GPT models without a Claude fallback.
- `backend/Features/Runner/RemoteChatWorkBroker.cs`,
  `backend/Features/Tasks/LeaseEndpoints.cs`, and
  `runner/RemoteProjectChatRunner.cs`: assignment-aware remote side-sheet chat
  dispatch. The Runner claims and renews opaque chat work, prepares the
  project's dedicated chat checkout from its normal git cache, starts Codex
  there, and completes with the observed hostname, repository path, branch,
  and HEAD revision.
- Coding hosts advertise fresh `cli-execution:<cliType>` and
  `provider-auth:<cliType>` capabilities for every card CLI binary they can
  invoke. The primary `RUNNER_CLI_BIN` and the provider-specific
  `RUNNER_CLAUDE_CLI_BIN` / `RUNNER_CODEX_CLI_BIN` paths form that inventory;
  setup preserves both discovered paths even when Codex is selected as the
  primary. `LeaseEndpoints` adds the candidate card's normalized CLI keys to
  the existing required-capability set before repository preflight or lease
  acquisition. An incompatible card stays Ready. Fenced idempotent claim replay
  is evaluated first and always describes the already claimed run. Capability
  matching never rewrites the card's model or thinking selection; those remain
  governed by [the model-routing policy](model-routing-policy.md).
- Remote provider credentials use one protected host file,
  `/etc/agent-runner/provider-auth.env`, for every provider. It is installed as
  `root:agent` mode `640`, loaded by both Coding and Review units after their
  existing EnvironmentFile, and provisioned only through SSH stdin. Studio
  never stores the value. Provider probes use the process environment and CLI
  status as authentication authority. A metadata-only freshness monitor also
  reads the runner user's native Claude and Codex credential files when present,
  but returns only modification and expiry timestamps and never token values.
  Provider-specific environment files, including `claude.env`, are outside the
  contract.
- `backend/Features/Orchestrator/OrchestratorContextKey.cs`,
  `OrchestratorSessionRegistry.cs`, `OrchestratorSessionEndpoints.cs`, and
  `OrchestratorTurnService.cs`: context-keyed orchestrator sessions, the
  `/api/orchestrator/sessions/{contextKey}/turns` and `/park` API surface, and
  session turn dispatch through the existing orchestrator CLI runner. Four
  context kinds exist, in canonical rail order:

  | Kind | Key shape | Owns its own transcript | Implicit context |
  |---|---|---|---|
  | `global` | `global` | Yes (board-level) | Cross-project digest |
  | `project` | `project:<PROJ>` | Yes (permanent) | Project-scoped digest |
  | `workbench` | `workbench:<PROJ>/<DOSSIER-KEY>` | Yes (permanent, never hidden; AGT-2725) | Dossier descriptor + entrypoint excerpt only - no board/task digest |
  | `task` | `task:<PROJ>/<KEY>` | Yes (hidden, not deleted, after archive) | Task metadata, `prompt.md`, `status.md`, agent plan, last run outcome |

  Records persist under
  `<TaskRepository>/.metadata/orchestrator-sessions/<encoded>/`.
- `backend/Features/Orchestrator/OrchestratorContextDigestService.cs` and
  `OrchestratorContextEndpoints.cs`: ORCH-1 read context shared by side-sheet
  chat turns and session turns. The bounded digest folds board transitions,
  active lifecycle phases, cached quota, PUB-1 targets, backend/watcher health,
  and decision-journal excerpts according to the `global|project|task` key. A
  `workbench` context never reaches this digest builder - it is composed
  separately by `OrchestratorWorkbenchPromptContextComposer` from the Dossier's
  own `workbench.json` descriptor and entrypoint HTML
  (`WorkbenchCatalogueService`), deliberately excluding board/task state
  (AGT-2725). `POST .../refresh` is the explicit expensive path that re-probes
  quota first and applies to `global|project|task` only.
- `backend/Features/Registry/OrchestratorSettingsResolver.cs`: pure two-tier
  resolver for the workspace-shaped orchestrator knobs (model, thinking level,
  autonomy) - `project override → workspace default → platform constant` - plus
  the injectable `OrchestratorDefaultsProvider` the read sites call (ADR-0061).
  The autonomy read sites are `ProjectRunner` (orchestrator model override) and
  `OrchestratorPrepHostedService` / `IntakeHostedService` (autonomy level).
- `backend/Services/Runner/OrchestratorReplyParser.cs`: `{REPLY | STEER |
  BLOCK}` grammar.
- `backend/Services/Runner/RunQuarantineBreaker.cs`: no-progress failure streak
  circuit breaker.
- `backend/Services/Runner/EvidenceGate.cs`: visual and acceptance evidence
  gates before auto-accept.
- `backend/Services/Runner/CrashRecoveryService.cs`: recovery for orphaned run
  state after process failure.
- `backend/Services/Supervisor/*`: Layer 2 advisory loop, meta-cycle, and rare
  intervention primitives.
- `runner/*`: the standalone `agent-host` daemon. A dependency-free console
  process that runs as either a separately registered `coding` or `review`
  service. Coding continuously claims server-assigned projects with centrally
  managed bounded host slots (`RUNNER_MAX_PARALLELISM` is bootstrap/fallback
  only). The Coding daemon caches the last accepted version under
  `RUNNER_STATE_DIR/configuration/runtime-capacity.json`, reports the exact
  applied version on claim polls, and never treats registration delivery alone
  as an acknowledgement. The Task Server audits the first exact value/version
  confirmation. It then uses fenced leases + heartbeat, per-task linked git
  worktrees, log/artifact upload, and fenced normal completion into auto-review.
  Host project access is also versioned in the Task Server, but is enforced
  during central claim and permit selection rather than copied into the runner.
  Review claims one immutable ReviewSubject, creates a fresh disposable
  exact-SHA workspace, runs the server-supplied existing aspect command plan,
  and sends one fenced evidence report plus cleanup proof. The original `--task <key>`
  one-shot remains for diagnostics. It owns no task state. Its Git writes to
  origin are generation-scoped salvage and immutable result refs described below.
  Every coding and review registration reports the exact fenced attempts whose
  detached process generation or durable terminal result is still positively
  present on the host. A restarted Task Server compares attempt, task, lease,
  fence, epoch, executor, host, and original lease instance against its durable
  authority record before extending the lease. Registration also publishes the
  accepted active-slot count immediately. A renewal that first encounters the
  restarted server retries this exact registration once before treating a
  404/409 as lease loss; a stale or superseded authority still fails closed.
  Operator runbook:
  [docs/operations/setup/linux-runner-host.md](../../operations/setup/linux-runner-host.md).
- `runner/RemoteTaskRunner.cs` and
  `backend/Features/Diagnostics/ArtifactIngestionEndpoints.cs`: the compatibility
  coding result-return boundary. After the final log flush, the runner uploads
  every file below its external `JOB_RESULTS_DIR` recursively and requests the
  application-owned `status.md` projection from `SummaryGenerationService`.
  The server acknowledges the exact result-path set and summary state before
  worktree teardown. An incomplete or absent acknowledgement retains the
  worktree. A genuine summary failure is allowed through so the marked
  `TaskTransitionService` scaffold remains the honest terminal backstop.
- `runner/ReviewStateStore.cs`, `runner/ReviewSlotReconciler.cs`,
  `runner/DurableReviewProcess.cs`, `runner/RemoteReviewDaemon.cs`, and
  `runner/RemoteReviewExecutor.cs`: durable
  Remote Review handoff. The daemon persists the immutable ReviewAttempt,
  subject, lease/fence, workspace, phase, and worker PID generation below
  `RUNNER_STATE_DIR/reviews`. The detached worker persists command checkpoints
  and terminal evidence. At startup and once per minute, the daemon reconciles
  every record that is not already active. A record consumes a slot only when
  its PID start time and workspace cwd match, or when the Task Server still
  reports the exact unexpired attempt, fence, executor, and host lease. Records
  without either proof are marked cleaned and deleted; transient authority
  lookup failures retain the record without consuming a slot. A 24-hour safety
  limit also deletes any record that has no live PID, at startup or during the
  periodic reconciliation, and journals the purge count. A replacement adopts
  a positively matched PID, renews the original lease instance, and submits the
  deterministic `review-report:<attempt>:<fence>` terminal key. Report
  delivery retains the completed evidence and retries transport failures,
  ambiguous responses, HTTP 408/429, and ordinary 5xx responses with bounded
  backoff. A missing task (404 or legacy `task-not-found` 503), superseded
  authority, or another fenced 4xx response is a classified terminal delivery
  rejection: the disposable workspace is reaped locally and the slot record is
  deleted, or retained as visible `terminal-cleanup-pending` state if cleanup
  cannot complete. The daemon journals total persisted slots, pending report
  count, oldest pending-report age, and terminal-cleanup count at startup and
  once per minute. Successful local workspace removal deletes the slot record
  on Pass, ProductFailure, and ReviewInfra exit paths even when the cleanup
  acknowledgement cannot be delivered. Compatibility report projection uses
  the archive-inclusive task snapshot so a late idempotent report can record
  evidence without reopening an archived card. The report boundary rejects a
  missing authority record or an expired, non-replay lease immediately as
  HTTP 409 `LeaseExpired`, before task scanning, evidence writes, or delivery
  integration. A retry of an already settled report remains admissible under
  its exact fence and idempotency key so post-settlement compatibility work can
  converge after a process restart.
- `task-server/RemoteRunResultCollector.cs`,
  `contracts/TaskServer.Contracts/RemoteRunResultContracts.cs`, and
  [the remote run result contract](../contracts/remote-run-result.md): additive
  infrastructure-test evidence collection. Final Task Server authority is
  combined with Runner observations into one create-once scenario result. The
  collector owns no outcome, retry, lease, or task transition decision.
- `scripts/remote-runner-onboard.sh` and
  `scripts/agent-host-resource-governance.sh`: the current agent-host Linux
  install/update path and its role-specific systemd resource renderer. The
  renderer derives defaults from `nproc`, reads deliberate overrides only from
  `/etc/agent-host/profile.conf`, and adopts legacy resource drop-ins before the
  managed main unit replaces them. The target contract lives in
  [runner-host resource governance](../../operations/haertung-verteilte-ausfuehrung/target-architecture/resource-governance.md).
- `deploy/agent-host/agent-runner-deploy`, its configuration policy, and
  `scripts/harden-agent-runner-host.sh`: the root-owned least-privilege release
  and role-configuration boundary. Release promotion retains its fixed
  no-argument ingress. Before the atomic link flip, it validates the selected
  `agent-host.deps.json` runtime-assembly closure and runs the staged binary as
  the service user under a short timeout. It then restarts the fixed Coding and
  Review units and detects an immediate crash loop from systemd restart counters;
  failure output names the previous release and prints the operator rollback
  command. Role configuration accepts only Coding or Review
  `RUNNER_MAX_PARALLELISM` values from 1 through 6, updates an EnvironmentFile
  loaded by that unit, and relies on systemd's documented precedence where
  `EnvironmentFile=` overrides `Environment=`. It restarts only the mapped
  unit, waits for an active new MainPID, proves the value through that exact
  process's `/proc/<MainPID>/environ`, and journals or rolls back the result. The
  release checks add no sudo command or argument shape.
- `AttemptAuthorityService` + `RunLeaseService` + `AttemptAuthorityEndpoints`
  (AGT-2182): the Task Server's persisted control-plane authority for separate
  `RunAttempt`, `ReviewAttempt`, and immutable `ReviewSubject` records. The store
  owns stable attempt IDs, repository/task/source identity, leases, per-task
  monotonic fences, authority epoch, heartbeat, terminal facts, evidence digests,
  and task-and-operation-scoped idempotency. Remote completion carries an
  explicit immutable Result-SHA independently of optional salvage-branch
  metadata, and rejected late review reports remain non-authoritative attempt
  history. Authority epochs are claim generations. Rotation advances the epoch
  used by every newly minted run or review lease, while an already leased
  attempt keeps renewing, writing, and settling against its own older epoch.
  Its exact lease remains current until settlement, release, expiry, or a
  higher-fence takeover, including across a Task Server restart. Rotation does
  not supersede attempts or make their task appear unleased. Only an exact
  acquire-delivery replay is idempotent; a new acquire from the same executor
  cannot renew a live lease without the canonical attempt and fence. Replayed
  review settlements become superseded once another subject is current. It
  lives under `<TaskRepository>/.metadata/` and performs no
  checkout, build, test, provider CLI, vision, or semantic review work.
  Remote claim and completion lane facts carry the same attempt, fence, epoch,
  and idempotency tuple. Claim, standalone acquire, and completion are serialized
  at the Task Server mutation boundary, and canonical Remote completion suppresses
  the generic local auto-commit, drift, provenance, and post-processing queue
  path. Remote completion owns a separate attribution contract: it fetches the
  pushed `agent-studio/results/<attempt>/fence-<n>/<result-sha>` ref, verifies
  that its tip equals the fenced `ResultSha`, and writes every commit in the exact
  `merge-base..ResultSha` range to `commits[]` with `automatic` attribution.
  The range is rejected as a whole, left empty, and logged as a warning when
  the delivery branch belongs to another task or any commit subject explicitly
  names a different task key.
  `AgentSession` and process-holder identity remain continuity metadata only;
  neither can mint or recover attempt write authority. Failed authority-store
  persistence restores the last durable snapshot before the error escapes, so
  the live process cannot retain a fence, epoch, or attempt that restart would
  forget.
  Server restart recovery is cooperative rather than a takeover: durable leased
  records may be re-adopted after expiry only when the runner supplies every
  unchanged authority field. Re-adoption preserves the attempt and fence,
  reopens renewal and final-write delivery, restores the coding runner badge,
  and projects leased ReviewAttempts back into Auto Review activity. Omitted,
  mismatched, terminal, or superseded records are never reopened.
- `orchestrator-engine/`: the separate API-only flow executor. Its bounded
  ReviewDecision, Council, PostProcessing, GateDispatch, and CompletionJudge
  loops claim server-owned orchestration runs through
  `/api/v1/orchestration/*`. It references only the shared Task Server
  contracts and has no TaskScanner, task-folder, or store access.
- `task-server/TaskServerOrchestrationStore.cs`: durable flow definitions,
  orchestration runs, stage results, leases, fences, and restart recovery.
  Expired Engine leases return the same run to `pending`; a replacement Engine
  receives a higher fence and stale settlement is rejected. Canonical Remote
  Review cleanup creates the run atomically with the full RunAttempt,
  ReviewSubject, ReviewAttempt, Result-SHA, policy, and report-hash envelope.
  Task Server settlement applies the version-fenced reissue, escalation, or
  Human Review lane transition; the Engine and Agent Host cannot move lanes.
- `tools/remote-test-suite/`: repository-owned, isolated Remote Run
  infrastructure scenarios. The `reference-change` manifest drives the public
  v1 claim and attempt authority, durable immutable-result handoff, exact-SHA
  review, and reviewed fixture integration with stable-seed semantic
  acceptance. Its explicit `remote-integration` Compose profile adds the
  disposable Task Server, deterministic Agent Runner protocol process,
  production Studio UI, and two-link fault proxy used for remote-host rolling
  replacement and partition evidence. Phase hooks observe claim, run, gate,
  review, and integration without adding scheduler-only branches. It never
  targets stable or the managed task workspace. The `parallel-harness.mjs`
  workload adds two twelve-task passes over separate coding, gate, and review
  worker pools. It records slot admission, queue and execution timings, system
  pressure, exact-SHA proofs, idempotent delivery, deterministic integration
  collision decisions, and one controlled four-slot gate-worker loss with
  bounded redistribution. Its harness-only fault catalog covers
  bounded Task Server disconnects, gate watchdog timeouts, occupied worktree
  targets, and lost or interrupted terminal markers. Fault activation requires
  a checked-in manifest, an explicit enable flag, a run/root-bound
  acknowledgement, an unchanged safety marker, and a harness-owned isolated
  Task Server. Fault runs assert lane, lease/fence, process, worktree, outbox,
  Result-SHA, and incident terminals. The catalog is not referenced by
  production binaries and never targets stable or the managed task workspace.
  First-class historical replays also cover divergent salvage lineage,
  lease adoption across a real Runner daemon restart, and the external
  completion cycle. Every manifest binds chronicle incidents to an expected
  durable terminal, bounded recovery budget, and complete machine-assertion
  set.
  The acceptance run first holds two already-claimed slots through a
  configurable real Task Server partition,
  records useful-work and durable-outbox timelines, reconciles each exact fence
  before replay, and proves exact-once terminal delivery. The card-safe harness
  uses a 25-second outage by default; the release suite owns the separately
  marked MachineBound ten-minute invocation. Runner replacement and Task Server
  replacement remain separate later checks.
- `backend/Features/Runner/OrchestrationExecutionMode.cs`: transition switch
  for the legacy host. `Orchestration:ExecutionMode` accepts exactly
  `Monolith` or `Engine`; Engine mode omits the legacy review/post-processing
  hosted services from the monolith.
- Canonical Remote ReviewAttempts are excluded from the legacy
  `ReviewDecisionOrchestrator` scan. They remain visibly in Auto Review until a
  fenced Remote Review Executor claims them. This is the fail-closed bootstrap
  boundary: the Task Server never substitutes its checkout or local
  `session-events.jsonl` for the ReviewSubject. Legacy tasks without attempt
  authority continue through the established local compatibility path.
- ReviewAttempt claimability is also bound to the current task lane. A
  successful transition into `6-completed` or `7-archive` supersedes every open
  ReviewAttempt for that task before another claim can cross the lifecycle
  boundary. Each claim poll fails closed for attempts whose card is absent or
  no longer in `4-auto-review`, and writes a durable supersession journal fact.
  The compatibility store uses `review_attempt_superseded` in the task
  timeline; the canonical Task Server uses `review.superseded` in its audit
  journal. The startup sweep applies the same idempotent repair to authority
  records left by older binaries.
- `task-server.Tests/TopologyTests.cs`: release-blocking sibling-process
  harness for Studio detach, canonical history replay, renewal safety stop,
  Task Server restart quarantine, Runner replacement after positive
  no-overlap proof, and authenticated HTTPS event transport.
- `TaskRunnerService.ProjectRunnerBadge` + `TaskEndpointHelpers.WithRuntime`
  (AGT-2003, canonicalized by AGT-2182): read-time projection of the active
  persisted RunAttempt lease onto `TaskInfo.Runner`
  for `3-progress` cards, so the board can show which host executes a card
  (remote `Host · <runner-id>` from the lease owner vs a quiet `Local`
  in-process run).
  The projection includes canonical Attempt ID and authority epoch alongside
  the lease and fence. A remote runner acquires the run lease; the local
  in-process runner uses the disk pickup-lock and holds none, which is exactly
  the lokal-vs-remote signal.

## Invariants

- Lifecycle projections describe only the current attempt. A confirmed coding
  restart clears prior Post Processing checks, a recovery re-enqueue replaces
  them with checks timestamped no earlier than the recovery boundary, and every
  terminal Post Processing path closes active checks as `completed` or `failed`
  with `finishedAt`. Cards marked `fixture: true` are excluded from automated
  recovery, liveness, cron, health, and orchestration scans.
- Backend Git network processes (`fetch`, `push`, and `ls-remote`) have a hard
  30-second per-process boundary in addition to caller cancellation. Timeout or
  cancellation kills the full process tree, concurrently drains both output
  pipes, and bounds the final reap so a DNS or remote outage cannot accumulate
  Git children or inherited handles. Process-start resource exhaustion is a
  typed command failure and must not terminate the backend.
- Origin is a fenced side-effect channel. New Remote Run salvage refs include
  runner, task, attempt, fence, and SHA; immutable result refs include attempt,
  fence, and SHA. A newer generation never resumes or overwrites an older
  generation's moving card ref. Known lease-loss and unattributed crash debris
  publish only under `agent-studio/quarantine/...` and are never a delivery
  candidate. The Task Server accepts a result handoff only when its exact ref
  name matches the request's current run, fence, and result SHA. Integration
  continues to consume only the settled envelope ref.
- A coding result is delivered only after `git ls-remote` against the repository
  URL from the project registration resolves the published ref to the exact
  local result commit. The configured `origin` push URL is not delivery
  evidence. A missing or mismatched registered-repository ref retains the host
  worktree and routes the card to the visible Escalated failure state with
  hostname, worktree path, branch, cause, and a recovery recipe. "Completed
  out-of-band" is reserved for a missing terminal sentinel after this exact
  remote proof succeeds.
- Coding and review service identities are not interchangeable. A
  `review-executor` capability cannot claim coding work, mixed capabilities are
  rejected, and a registered identity cannot switch executor roles. Review
  claim, renew, report, and cleanup use a separate lease and monotonically increasing fence.
  Same-host placement is allowed only with the distinct service identity,
  instance, workspace root, cache, port block, container/database namespace,
  read-only credentials, and quota. A ReviewPlan may require a different host
  failure domain.
- Every review command records the expected Result-SHA and the actual HEAD
  immediately before process start. The Task Server accepts evidence only when
  the repository identity, expected SHA, tested SHA, tree, executable-digest
  toolchain, output-artifact, and containment facts match the immutable subject.
  Missing source is `ReviewInfra` /
  `SnapshotUnavailable`; wrong repository, wrong SHA, dirty-before, and
  mutated-after are typed infrastructure outcomes. They stay in Auto Review and
  consume no coding attempt.
- A Review Executor advertises dependency-preparation capability before it can
  claim a plan with preparation work. The immutable plan carries the build
  profile install command, lockfile scopes, and preserve globs. Candidate and
  baseline workspaces execute that preparation before verification using
  role-isolated instances of the shared gate dependency cache. Lockfile digest
  changes invalidate the cached dependency state. Preparation failure and a
  verification exit 127 are typed `ReviewInfra`; the report retains the exact
  failed command, exit code, budget, and complete stdout and stderr artifacts.
  `PreparationFailed` retries rebuild the plan from the current Git-tracked
  source, and a missing preparation directory is surfaced as the grade Detail.
- Draining closes review admission but not active renew, report, or cleanup.
  Safe shutdown and restore include unresolved ReviewAttempt authority, and
  a positively proven planned-restart adoption retains the ReviewAttempt,
  fence, lease instance, and containment namespace. A non-adoption takeover
  changes the durable fence and containment namespace only after the old
  process can no longer retain authority.
  A report is also rejected when its immutable subject no longer owns the task's
  Auto Review lifecycle.
- Review slot admission is host-load aware and immediate. Before each claim the
  Review Executor captures a fresh one-minute load sample and admits at most one
  new slot per poll only when `Load1 < CpuCores * ClaimMaxLoadPerCore`. Missing
  load or core evidence closes admission. The configured slot ceiling remains a
  hard upper bound, and an admission decision never cancels an active review.
  Immutable ReviewPlans normalize every direct or shell-wrapped `dotnet test`
  command to `-maxcpucount:2 -p:ParallelizeTestCollections=false` before storage
  or claim. Subject, baseline, retry, and fenced command evidence therefore use
  the same bounded command.
- Capability-aware Remote admission (AGT-2186) is Task Server authority, not a
  daemon-local slot reduction. Coding and review services publish versioned,
  expiring health for provider authentication, Git fetch/push, repository
  access, .NET, Node, Playwright, vision, disk, Task Server connectivity, and
  platform/toolchain identity. Claims persist their required set. One
  capability advances through `healthy -> suspect -> draining -> half-open ->
  healthy` with bounded thresholds, exponential cooldown, and one fenced
  canary. Matching new claims stop while unrelated capabilities and the
  configured parallelism continue. Active leases are never revoked by this
  admission decision. Disk full, invalid lease authority, host network
  isolation, repository filesystem corruption, and Task Server authority
  uncertainty use the separate automatic whole-host drain. Operator drain is a
  distinct API, audit action, persisted field, and UI label. Capability failure
  reports must bind the active coding or review claim and fence; stale and
  duplicate deliveries fail closed or replay idempotently.
- The monolith V1 Review compatibility mount accepts the Review service's
  `PUT /api/v1/runners/{runner-id}/capabilities` startup and refresh requests
  with the same advertisement and snapshot contracts as the standalone Task
  Server. It validates the registered Review runner and instance, schema,
  freshness, and generation before retaining the latest snapshot. The separate
  `review-executor` identity therefore remains on the V1 Review plane after
  registration instead of failing startup on a missing capability route. The
  authenticated `GET /api/v1/management/remote-hosts` route exposes the latest
  retained coding and review snapshots in both the monolith compatibility
  profile and the standalone Task Server profile. Each snapshot retains the
  process role's bootstrap parallelism, so Execution Hosts can show Review and
  Coding ceilings independently while grouping them by the shared host id.
  Runner registration advertises the deployment release directory selected by
  `/opt/agent-host/current` (or `RUNNER_RELEASE_ID`) rather than the generic
  assembly package version.
- Provider-auth advertisement changes are appended to the same bounded recovery
  history exposed by the management snapshot. Execution Hosts turns that data
  into per-CLI `OK`, `Retrying`, `Limited`, `Expiring`, `Unavailable`, and
  `Unknown` badges, transition notifications, 14-day expiry warnings when
  metadata is available, and Ready-card wait reasons. One shared classifier
  accepts only explicit provider-login signatures as authentication failures.
  Tool failures and indeterminate non-zero exits retain last-good; rate limits
  use the limited state. Two consecutive explicit failures are required before
  sign-in is blocked, and a later positive probe clears that provider circuit
  without a runner restart.
- Account-level provider session, usage, and rate limits are CLI capability
  state, not task outcomes. The local runner records `claude: limited until
  <time>` in runner status, persists the current card in provider-scoped
  `quota-waiting`, and excludes only Claude from claim admission. The queue
  health projection reports `limited` separately from `stalled`. At the reset
  time the runner runs one coalesced quota probe while keeping Claude closed.
  A healthy probe clears every matching wait marker and resumes the card
  automatically; an inconclusive or still-limited probe schedules another
  bounded check. Codex and other CLI claims remain eligible throughout.
  Provider limits do not create task failure records, human-review moves, or
  pickup-breaker failure counts. Operator mode changes remain manual.

- Coding-slot occupancy follows live CLI processes, not lane membership. A
  `3-progress` card in `loop-waiting`, `steer-pending`, `quota-waiting`, or post-processing keeps
  no execution seat; a continuation must pass admission again and remains
  visibly queued when no seat is free. A heartbeat-less `3-progress` card may
  survive the liveness grace only with one of the explicit waiting phases.

- `auto-single` reverts to `manual` only when the pickup queue is empty and the
  project has no run chain in flight. A claimed coding run, runner-side
  post-processing record, durable `post-processing-running` phase, or card in
  `4-auto-review` keeps the mode armed. A review reissue to `2-ready` therefore
  remains part of the same single-run chain and is picked automatically. Once
  the chain reaches a terminal lane and the pickup queue is empty, the normal
  revert applies.

- Remote host capacity reports centrally admitted workload and lease truth. RUN
  occupancy comes from every daemon claim poll (`ActiveSlots` plus
  `AvailableSlots`, whose sum is the configured host maximum). Canonical Remote
  review commands use Review Executor admission and ReviewAttempt authority;
  there is no process-local SSH gate workload class. Host CPU and load include
  both executor planes and unrelated processes, so neither is inferred from
  lane membership or CPU percentage. This keeps claim/lane drift visible
  instead of silently folding it into a slot count.

- `RuntimeCapacitySettingsService` in the Task Server owns the versioned host
  ceiling, target load, and ramp strategy. The first Runner registration seeds
  it from the bootstrap value; later registrations and every Coding claim
  inherit it. Admission counts active Coding RUN authority across every Runner
  process on that host. Capacity changes take effect without a daemon restart
  and never cancel already-running work. Projects consume the shared host
  ceiling and do not own independent capacity settings. Canonical Remote review
  work is admitted by the Review Executor and does not consume a Coding RUN
  slot.

- Linux host resource enforcement belongs to agent-host-managed systemd units,
  separately for Coding and Review. Host-level cgroups are the hard CPU and I/O
  boundary; AIMD slot admission reacts within that envelope. A slot count is
  never interpreted as CPU capacity. Windows Job Objects are not implemented.

- Remote pickup ownership lives in the project record (`executionRunner` plus
  `remoteExecutionEnabled`). The remote claim endpoint and local ProjectRunner
  consult the same record; assigned remote-capable projects are never locally
  auto-picked. Lease fencing is the hard split-brain guard below that policy.

- Side-sheet project and task chat follows the same remote pickup ownership.
  A remote-assigned project's chat work is claimable only by its assigned
  Runner and executes inside a host checkout from the same project git cache as
  card runs. A project without a remote assignment executes chat locally. Each
  response projects the actual local or remote hostname, repository path,
  branch, and HEAD; a reassignment invalidates a cached host context.
- A planned `agent-host` daemon restart is an execution handoff, not an attempt
  boundary. Coding persists lease, fence, Task Server run/instance, worktree,
  detached-worker PID/start time, and file-log progress below
  `RUNNER_STATE_DIR`. Review persists the equivalent ReviewAttempt, immutable
  subject, ReviewLease, exact workspace, command checkpoints, and terminal
  evidence below `RUNNER_STATE_DIR/reviews`. SIGTERM stops claims and exits
  without cancelling either worker type. A pre-launch slot marker plus a
  worker-written atomic identity closes the `Process.Start`-to-slot-save handoff
  window. The replacement renews authority only after PID generation and Linux
  `/proc/<pid>/cwd` match the persisted worktree or review repository. It then
  follows the coding JSONL output or the review checkpoint/result files and
  completes the same attempt under the original fence. Startup discovery and the
  attached-process poll consume one durable-process inspection verdict. If PID
  liveness fails, that inspection reads the atomic terminal result a second time
  before declaring the worker lost, so a result written during process exit
  cannot be released as a dead attempt. Reattachment uses the
  original persisted attempt instance, never the replacement daemon's process
  identity. Missing or mismatched coding processes are actively released and
  returned to Ready. A non-adoptable review is settled as `ReviewInfra` with
  classification `ExecutorRestarted`, the completed-command count and duration,
  the failed process proof, and the retry reason. DB lease presence alone is
  never process-liveness evidence. systemd must use `KillMode=process`.
- A Task Server restart is also not an attempt boundary. The server reloads the
  persisted RunAttempt and ReviewAttempt ledger; the surviving runner includes
  positively proven active slots in its next registration and the server
  extends only the exact matching leases. The registration acknowledgement
  includes one typed adoption result per reported attempt, and the server emits
  fresh active-slot telemetry from the accepted set. Coding and review terminal
  deliveries therefore continue under their original attempt, fence, epoch,
  lease, and lease instance instead of receiving a false unknown-attempt or
  Superseded response.
- A failed lease renewal consumes the last server-issued authority window. The
  default requested window is 15 minutes, with a durable stop-before boundary
  one renewal interval before expiry. The standalone Runner persists that
  boundary in the worker directory, continues the already-fenced process while
  time remains, and journals output and terminal evidence locally. It admits no
  new work and replays no event, artifact, result handoff, terminal report, or
  completion while authority is uncertain. Only a successful renewal of the
  exact lease and fence advances the boundary and opens replay. At stop-before,
  the Runner reaps and verifies the entire worktree process generation and
  retains an honest `authority-deadline-exhausted` record. A daemon restart
  applies the same persisted deadline before it can register or adopt work.
  Task Server restart records `process-unknown`; only positive containment or
  infrastructure-fencing proof permits a higher-fence replacement.
- A remote project clone is eligible only when the project registry contains a
  repository URL. On every new clone and refresh, the standalone runner sets
  both fetch and push URLs to that registry value and logs the effective pair.
  Host-level probe and one-shot fallback URLs never flow into project clones.
  A project without a registry URL stays Ready, is reported as not
  remote-capable, and creates no clone.
- The separated v1 Task Server resource contract does not yet carry project
  repository registration on a claim. In that isolated compatibility profile
  only, the claim adapter binds the configured `--git-remote` to its stable
  repository identity and configured base branch. This does not relax the
  registry-owned repository boundary for product project claims.
- Before the first card for one host/project pair is leased, the claim endpoint
  returns an unleased preflight offer containing the registered repository and
  a registration fingerprint. The host creates or refreshes the exact shared
  project clone, verifies that both `origin` fetch and push URLs equal the
  registration, fetches, and proves write access by creating and removing a
  temporary runner ref. It reports the result in the next claim request. Only a
  matching green result may cross `2-ready` to
  `3-progress`. The server persists the result on the host identity and reuses
  it for following cards without another offer roundtrip. Repository
  registration and integration-branch mutations invalidate every host's cached
  result for that project. A failure remains visible on the Execution Hosts card
  and project execution card.

- A fresh `2-ready` Epic is remotely claimable as an Epic planning run. It
  occupies a normal host slot and holds the same fenced lease, heartbeat,
  cancellation, drain, and telemetry contract as a coding task, but it is not a
  coding work item. The server renders `epic-decomposition.md`, and the remote
  host runs it in a detached disposable checkout. No task branch, salvage
  commit, or push is created. Only the children produced by the plan enter the
  coding pipeline. A valid plan completes the Epic into `5-human-review`, never
  `4-auto-review`: the run carries no Result-SHA, so the code-review lane would
  hold it against a ReviewAttempt that can never be minted.
  An interrupted assigned card whose lease is free is requeued
  to Ready inside the next atomic claim before a higher fence is issued. Before
  any `3-progress` to `2-ready` requeue, `TaskTransitionService` queries attempt
  authority. If the current RunAttempt is Completed and its immutable
  ResultEnvelope validates, the Ready move is suppressed: the existing review
  handoff is recreated idempotently, the card advances to `4-auto-review`, and
  `settled_run_recovered` records the original attempt and envelope digest.
  A Completed attempt that carries an invalid envelope or digest fails the
  requested move closed and remains in Progress for repair; it is never made
  claimable in Ready. Detector-specific retry state, such as a cleared session
  or staged steer answer, is applied only after authority confirms a genuine
  Ready transition.
  This guard is shared by operator moves, stale sweeps, and liveness or crash
  detectors. Review reissues from later lanes remain deliberate new delivery
  cycles and are outside this BP-09 guard.

- A terminal sentinel in the final agent reply is authoritative. Sentinel-shaped
  text in streamed tool output, diffs, file content, or stderr is not a verdict.
  When adding a sentinel, update
  [docs/system/contracts/agent-task.md](../contracts/agent-task.md) and
  `AgentOutcomeAnalyzer.SentinelRegex`.
- The agent classifies its run. The rule engine decides reissue, stop,
  escalation, and lane movement.
- Waits-on gate (AGT-2029): the ready-lane pickup gate
  (`ProjectRunner.IsReadyPickupCandidate`) skips a `2-ready` card whose
  `references.dependsOn` targets have not all reached `6-completed`/`7-archive`.
  A dependency object with `releaseGate: true` additionally requires the
  terminal target card to carry its explicit `released: true` flag. Completion
  does not infer release, and legacy string dependencies retain the existing
  terminal-state-only behavior.
  Fulfillment is resolved cross-project and archive-inclusive
  (`TaskReferenceIndex` built from `ScanAllJobsWithArchive()`, shared with the
  read-time card overlay via `WaitsOnEvaluator`). A skipped card falls out of the
  candidate list and the tick picks the next eligible card - blocking is visible
  (the card's waits-on chip, which distinguishes completion from release),
  never a silent deadlock. A `dependsOn` cycle is a
  configuration error: it is reported once per card (`waits-on-cycle` warning)
  and skipped, never deadlocked.
  `ProjectRunnerStatus.queuedJobIds` reuses this exact pickup-candidate order.
  It is the single source for runner queue counts and one-based positions, so a
  card held by a dependency or another pickup gate occupies no displayed slot
  and does not shift the positions of eligible cards.
- A re-open starts a new run. It must rerun pre steps, core, post steps, and
  append run history instead of flattening earlier evidence.
- Before any automatic auto-review follow-up is persisted,
  `ReviewDecisionOrchestrator` compares its whitespace- and case-normalized
  base text with every prompt under `orchestrator-follow-up-history/`. A match
  adds a diagnosis-first block that requires the exact failed check or missing
  evidence, the target artifact and verification, and continuation from the
  existing diff. The intervention count increases on every recurrence so the
  guard cannot become another identical prompt. The timeline records the
  intervention as `orchestrator_steered` with cause
  `reissue-prompt-repeat-guard`, and the enriched text is the text written to
  the canonical follow-up, history, and decision journal.
- Context overflow is non-retryable and routes to human review on first
  detection.
- Post-processing classifies every run that did not sign off cleanly into one of
  five outcome buckets (`success` / `code-defect` / `environmental` /
  `inconclusive-with-results` / `inconclusive-empty`,
  `PostProcessingOutcomeTaxonomy`), and the bucket - not the raw exit code -
  drives what happens next. Environmental faults are never the change's fault:
  a *transient* one (host file lock in the MSB302x copy-lock family, network
  glitch, or a CLI launch/resume failure) retries with exponential backoff before
  escalating; the retry budget is bounded per kind (`EnvironmentalTransient` 2,
  `CliLaunchFailed` one fresh-start), and every environmental member that does
  escalate is flagged `environmental` so a reviewer never reads an infra blip as
  a failed change. An inconclusive run with files in `results/` routes to human
  review with a "partial work to inspect" hint rather than a bare `5e` park. See
  the taxonomy section of
  [docs/system/contracts/run-outcome.md](../contracts/run-outcome.md).
- Post-step (aspect / code-review) verdicts extend the same taxonomy: a missing /
  unparseable verdict caused by the reviewing CLI dying is ENVIRONMENTAL, not the
  card's work (AGT-2021, belege AGT-1996). The step reruns once with the
  environmental backoff (`PostProcessingOutcomeTaxonomy.DecidePostStepVerdictRetry`,
  `MaxPostStepVerdictRetries` = 1); a second miss records an `InfraCrash` flagged
  `environmental` and escalates via a chain-ending `Escalate` decision, so the
  card's reissue budget is never charged. See the pipeline domain map for the
  aspect-runner / orchestrator wiring.
- Environmental cycles do not count against progress or budget: a transient
  environmental fault never accrues toward the no-progress quarantine streak
  (`RunQuarantineBreaker.CountsAsNoProgressFailure`). The shared reissue budget
  belongs to a review-attempt epoch. Only an explicit human move out of
  `5-human-review` / `5e-escalated` opens the next epoch and rotates stale
  verdict artefacts; automatic verdicts and moves retain the current epoch and
  cannot replenish the ceiling. `OperatorReviewRequeueService` owns the epoch
  boundary, history rotation, decision-journal row, and timeline event.
- The verdictless Human Review boot repair is subordinate to operator lane
  decisions. If the latest `lane_changed` row is attributed to `human` or
  `human:<client-id>`, the current lane is an operator verdict even when the
  legacy decision journal has no provenance verdict. The repair also excludes
  `6-completed` and `7-archive` unconditionally. Every repair move records its
  reason on the resulting `lane_changed` row.
- Host-load admission (AGT-2077) samples total system CPU every 15 seconds. A
  continuous minute above 90 percent activates `load-throttle`: existing runs
  continue, new slot picks are deferred with timeline and orchestrator-feed
  decisions, while support OneShots and build/test post-gates queue until
  cooling. Build/test post-gates are also serialized per Git repository before
  they enter host-load admission. Calls released after a load phase receive a
  3x timeout and one timeout retry after cooling. These failures are
  `environmental-load`, never a work-quality conclusion.
- No-progress failures count across auto-pickup and `UserContinue` reissues
  until progress, review, or quarantine resets the streak.
- Orchestrator session turns use the existing CLI session machinery. A context
  with a stored session id resumes that session; otherwise the first turn starts
  a fresh run and persists the captured session id. Active turns are capped by
  `Orchestrator:SessionTurns:ActiveLimit` with default `4`; overflow responses
  return `status: "queued"` and a one-based `queuePosition`. Posting `/park`
  cancels the active turn for that context and parks queued turns for the same
  context.
- Every orchestrator chat entry point receives the same ORCH-1 application
  digest. Global context may read all registered projects; project and task
  contexts are project-isolated, with task context adding only its focused task.
  Digest sections are capped and omit raw quota samples and full decision
  prompts/responses. Normal turns use cached quota; only the explicit refresh
  endpoint starts quota probes.
- Every task-scoped side-sheet message also carries the stable task key from
  the same synchronous active-tab projection shown in the composer footer.
  Prompt composition resolves that task through `TaskScannerService` on every
  turn and adds independently capped task metadata, `prompt.md`, `status.md`
  when present, and the latest recorded run outcome. This task block is
  independent of the ORCH-1 digest, so a failed digest lookup is logged and
  marked as degraded without silently erasing task substance. Each reply
  persists a context receipt naming the scope and included blocks; the chat UI
  shows the latest receipt below the answer transcript.
- Side-sheet chat sends one route-bound typed context envelope on every turn.
  Prompt order is scoped preamble, source ledger, automatic evidence, explicit
  attachments, bounded central transcript continuity, and the new user message
  last. The Task Server owns project/task chat contexts, turns, receipts,
  lifecycle visibility, usage, and short summaries. Project contexts are
  permanent; archived task contexts are retained with `hiddenAt` and omitted
  from the current list. Local orchestrator chat JSONL is migration input only.
- Project chat reference chips carry stable task, page, repository-file, or
  project-qualified commit ids only. The backend resolves them on the selected
  execution checkout after route and project validation. The UI exposes the
  resulting Task Server receipt through the Used context inspector instead of
  trusting browser-supplied source bodies.
- Side-sheet Orchestrator chat is GPT-only. Its selected model and reasoning
  level travel on every Board or Task context request. The backend may resolve
  an omitted model to the detected Codex default, but it must never route this
  mode to Claude. The composer therefore lists Claude and Gemini as disabled
  host-policy choices with an explicit GPT-only reason; their presence does not
  make them valid execution routes for this chat.
- Every coding run is worktree-isolated - single-slot resume/reissue included,
  not just parallel slots. The shared main checkout is read-only reference + the
  integration target; on a failed worktree prepare the run is deferred, never
  run in the main checkout, and a coding run that resolves to the main checkout
  is refused + escalated. Read-only (planning / research) and epic-planning runs
  run in-place. See
  [ADR-0057](../architecture/decisions/adr-archive.md#adr-0057---always-worktree-garantie-every-coding-run-is-worktree-isolated-including-single-slot-resumereissue-with-a-main-checkout-guard-2026-06-22).
- Steer-timeout (Run-Liveness Slice B): an auto-mode run that asks a steer /
  `[[TASK_NEEDS_INPUT]]` question the orchestrator cannot answer leaves a durable
  `steer-pending.json` marker + a visible `steer-pending` phase, and a bounded
  sweep (`SteerTimeoutMonitor` over `SteerTimeoutPolicy.Decide`) resolves the wait
  after `Runner:SteerTimeout:TimeoutSeconds` (default 120s): auto-answer from the
  branch state when the question is unambiguous, else a `steer-timeout` blocked
  escalation. A steered card never waits indefinitely. See
  [docs/concepts/run-liveness-and-slot-semantics.md](../../concepts/run-liveness-and-slot-semantics.md).

- Supervisor code is advice-first. Emergency primitives must call runner
  services, not poke task state directly.
- Teardown never drops uncommitted work. `WorktreeTaskLifecycle.TeardownIfIntegrated`
  snapshots any dirty/untracked worktree onto its `task/<id>` branch as a
  platform WIP safety commit before removing anything, and refuses teardown if
  that snapshot fails (AGT-1945). The merged-ancestor gate alone is not enough:
  a failed auto-commit leaves the branch tip at develop, which reads as "merged"
  and would force-remove the deliverable. Genuine auto-commit failures at
  integration are surfaced as a High `integration-error`, never silent.
- Remote teardown is also fail-closed. Protocol 2 coding runs journal logs,
  status, artifacts, Git facts, terminal facts, the immutable result envelope,
  and server acknowledgements under
  `$RUNNER_WORKDIR/outbox/<run-attempt-id>/`. `runner/GitWorkspace` first commits
  dirty work, preserves the moving salvage ref without force push, and publishes
  the exact result to
  `refs/heads/agent-studio/results/<run-attempt-id>/<result-sha>`. Cleanup then requires
  the Task Server acknowledgement for the matching canonical envelope digest.
  A process restart replays the original outbox before new claims and never
  starts the coding CLI. Transfer failure stays `transfer-recovery`, retains the
  worktree, and consumes no coding or completion budget.
- Compatibility remote teardown is fail-closed at the older artifact-upload
  boundary too. Git salvage protects source changes only; it does not collect
  the runner's external `JOB_RESULTS_DIR` or the Studio task folder. Therefore
  the final recursive result upload, exact path acknowledgement, and server-side
  `status.md` generation all precede `GitWorkspace` teardown. A transfer error
  retains the checkout instead of converting missing evidence into a normal
  completion.
- The compatibility Remote completion boundary also fails closed when an older
  Runner reports any coding terminal without a complete `ResultSha`, `BaseSha`,
  `ImmutableResultRef`, and `ArtifactManifestDigest` envelope. This includes
  `Blocked`, `NeedsInput`, and `Unknown`, not only `Done` and `NoOp`. The
  RunAttempt settles as `Failed` with terminal outcome `delivery-failed`; no
  ReviewAttempt or ReviewSubject is created. The first consecutive envelope
  failure appends an idempotent status note with the published salvage-fence ref
  and returns the card to Ready. The durable failure counter survives the lane
  move and process restarts. A second consecutive failure reaches Escalated with
  category `unverified-delivery`. A valid envelope or a non-coding completion
  resets the counter. Epic planning, report-only modes, and repository or
  environment preparation failures are exempt because they produce no coding
  delivery. A failed salvage-fence push is not eligible for this retry: the
  runner retains the worktree and keeps the existing `worktree-blocked`
  escalation.
- The July Marathon envelope repair restored best-effort emission of the trio,
  and the AGT-2250 delivery probe showed that valid envelopes could reach Review.
  It did not make the trio a teardown precondition: a legacy immutable-result-ref
  push failure deliberately omitted all three additive fields after salvage.
  The later server guard checked only `Done` and `NoOp` and escalated those cases
  immediately. Consequently AGT-2531's `Blocked` outcome bypassed the guard,
  while AGT-2541's `Done` outcome was detected but had no automatic recovery
  path. The expanded completion policy and two-attempt delivery-failure budget
  close both gaps.
- For every generation-fenced compatibility coding run, `GitWorkspace` commits
  local changes when present and publishes the generation-scoped salvage fence
  before attempting the immutable result ref and before removing the worktree.
  Clean runs publish the fence as well, so an envelope failure can always name a
  durable recovery ref. If the fence cannot be published and verified, teardown
  stops and retains the checkout.
  The canonical protocol 2 Task Server continues to reject completion until the
  matching envelope handoff is acknowledged.
- The Task Server stores one result envelope per RunAttempt with repository ID
  and URL, base and result SHA, immutable ref or source-bundle digest,
  artifact-manifest digest, and applicable submodule and LFS identities. Handoff and completion
  have idempotency keys plus monotonic host sequence numbers. A response lost
  after commit therefore returns the original acknowledgement and cannot repeat
  a lane transition. Protocol 1 cannot call the protocol 2 handoff or completion
  path.
- Result refs and manifests have an earliest deletion time of 30 days by
  default. Reaching Completed or Archive extends that time to at least 30 days
  after the terminal transition. The Task Server runs a periodic result-ref GC
  sweep. It deletes only the exact
  `refs/heads/agent-studio/results/<run-attempt-id>/<result-sha>` ref when the
  retention deadline passed, the card is in Completed or Archive, the matching
  review has a terminal non-infrastructure report with no active retry, and a
  newer result-bearing RunAttempt superseded the source attempt. The newest
  result-bearing RunAttempt for a card is always spared, including after
  acceptance. Missing credentials,
  malformed refs, active reviews, non-terminal reviews, and Git failures all
  fail closed. Successful deletions are recorded in the Task Server GC ledger
  and are not retried.
- Retained remote-runner worktree pickup reconciles the local and canonical
  salvage tips by ancestry before reuse. Equal and remote-ahead tips keep the
  canonical remote ref, and local-ahead tips advance it with a normal
  fast-forward push. Divergent tips never rewrite that ref: the runner publishes
  the local tip to
  `runner/<runner-id>/<task-key>-collision-<local-sha>-<remote-sha>`, verifies
  both exact SHAs, and recreates the checkout from the explicit canonical SHA so
  the CLI can start. The collision ref and both tips are recorded in typed runner
  completion and operator deliverables. Publishing retries at most three times;
  an exhausted or genuinely unrecoverable git failure retains the worktree and
  uses the existing `worktree-blocked` escalation with the preserved tips and
  next safe action (AGT-2177).
- Every moving salvage push passes a final card-scope allowlist at the Git
  mutation boundary. The only accepted targets are the exact
  `runner/<runner-id>/<task-key>` branch and collision refs derived from that
  branch. An integration or authoritative-base branch such as `main` or
  `develop`, a fully qualified base ref, and another card's runner ref all fail
  closed before `git push`; the worktree remains recoverable (AGT-2423).
- Worktree preparation records the actual repository base line as a full ref,
  such as `refs/heads/main` or `refs/heads/develop`. Completion persists that
  ref on the task and review subject. Integration status, review planning,
  merge, push, recovery, and provenance consume the recorded ref instead of
  reapplying a project-level branch assumption after the run.
- Acceptance resolves the delivery source from persisted card truth, never from
  the task folder slug alone. Resolution order is immutable result ref,
  attributed `commits[].branch`, fenced `runner/<runner-id>/<task-key>`, then the
  legacy local `task/<slug>` fallback. Remote sources remain fenced to the
  reviewed result SHA.
- Epic planning is the deliberate exception: its detached checkout is checked
  for mutations and discarded without salvage. Any mutation invalidates the
  plan and returns the Epic to Backlog because planning is source-read-only
  (AGT-2178).
- Remote `agent-host` delivery admission is repository-scoped. The startup
  probe still checks the configured fallback fetch URL and Git `pushurl`, but
  publishes that result as diagnostics only: it never grants or denies another
  project's claim. Before a project receives a lease, its delivery preflight
  requires the registered fetch and push URLs, an exact remote integration
  branch, and a real create/delete push of a temporary runner ref. Proofs expire
  after five minutes because branch and credential state can change without a
  settings write. A failed or unconfigured project stays Ready while unrelated
  projects assigned to the same host remain claimable. Execution Hosts and the
  project's Execution card surface the per-project target and failure reason.
- Workspace-shaped orchestrator settings (model, thinking level, autonomy)
  resolve `project override → workspace default → platform constant` through
  `OrchestratorSettingsResolver`, never read ad-hoc at a call site. The provider
  is tolerant: an unmapped project or an empty workspace tier collapses to the
  old project-only chain, so an empty workspace-settings store is byte-for-byte
  identical to pre-migration behaviour. The process-wide supervisor/orchestrator
  lifecycle flags stay platform-global in `OrchestratorConfigService` and are
  **not** workspace-shaped. See
  [ADR-0061](../architecture/decisions/adr-archive.md#adr-0061---orchestrator-settings-are-a-two-tier-config-project-override-wins-over-workspace-default-wins-over-platform-constant-2026-07-11).

## Live execution ownership projection

Every task read returns `executionLocation`, the canonical view of where a run
actually executes. A live local CLI process or a fenced run lease is the source
of truth. `ProjectSettings.executionRunner` is returned separately as
`configuredRunnerId` and explains routing only; it never replaces the actual
owner. This makes configured-vs-actual mismatches explicit during recovery or
operator intervention.

The projection distinguishes `local-running`, `remote-running`,
`remote-disconnected`, `queued-remote`, `recovering`, and
`no-active-execution`. Remote leases retain their last accepted heartbeat, so a
missed heartbeat becomes acute only after the stale window. A renewed lease
returns to healthy without a page reload through the normal task push/poll path.
Run-start session events capture the execution projection so finished run
history keeps its stable runner-id attribution with `historical: true` and renders
quietly. The wire contract is
[task-execution-location.schema.json](../schemas/task-execution-location.schema.json).
Run timeline context events retain exact source members so count disclosures
are inspectable. Terminal run events retain status and duration as structured
details; the frontend owns their compact, non-redundant sentence projection.

A remote claim refusal is durable task state, not log-only evidence. Claim
admission records the Runner identity, a stable reason code, readable detail,
and timestamp in `task.json.remoteDispatchRejection`. Task reads expose the
current Ready-lane value as `executionLocation.lastRejection`; the card and
detail header render it inline. Successful dispatch clears it, and a later lane
generation cannot inherit it. Missing repository registration is also projected
as a failed project preflight in Execution Hosts before a Runner polls.

The build-profile gate is evaluated inside the remote candidate loop, after a
Ready card is known to be routed to that Runner. A closed gate records the
stable `build-profile-gate` refusal on every affected current Ready generation
instead of filtering the card before rejection recording. The starvation
snapshot includes these cards immediately, even before the age threshold, and
publishes a separate build-profile-blocked count for the workspace and
Execution Hosts banner.

Build-profile validation runs only in the project's registered source checkout,
never in its task-store or Review workspace. A profile fingerprint is frozen in
each immutable Remote Review plan. A passing remote report validates the
current profile only when that fingerprint still matches, and records the
ReviewAttempt, executor, result SHA, and timestamp as the last green remote
verification. Editing a previously validated profile starts a three-run grace
window with revalidation pending. Successful coding claims consume the window;
a matching green local or remote validation clears it, while exhaustion closes
the gate loudly.

`RemoteQueueStarvationWatchdog` independently detects remotely routed Ready
cards while a live Runner reports free slots. A durable claim rejection is
acute evidence immediately. Without a rejection, a card must be older than
`RemoteQueueStarvation:ThresholdMinutes` (30 by default) and the live Runner
fleet's latest successful claim must also be at least that old. This claim
progress window debounces normal serial queue processing, including snapshots
between claim polls. The snapshot published by
`GET /api/runner/queue-starvation` includes the latest successful claim time,
the stalled-progress verdict, and whether any returned card has rejection
evidence. The board mentions a latest rejection only when that evidence exists.
The watchdog emits the rate-limited `remote-ready-starvation` warning event and
clears the acute signal when claim progress, the queue, or capacity recovers.

## Follow-ups: admission, queueing, preservation

A user follow-up (`POST /api/tasks/{id}/continue` in mode `continue`, `steer`,
`extend`, or `newTask`) and a manual `POST /api/tasks/{id}/start` are admitted
against the card **before** any process is spawned. The rule exists because
accepting a follow-up and then spawning a run that lane reconciliation kills a
second later destroys the operator's input silently: the response already said
`started`, the prompt only lives in the chat log, and no run ever reads it
(three steers lost on 2026-09-07, AGT-2743).

`FollowUpAdmissionPolicy` (`backend/Shared/Runner/FollowUpAdmissionPolicy.cs`)
is a pure decision over three facts: the card's lane, its lifecycle phase, and
the project's execution location resolved through `ProjectExecutionPolicy`
against this backend's own `RunnerIdentity`.

| Lane | Local follow-up run | Why |
|---|---|---|
| `0-backlog` | queued | never picked up; a follow-up belongs to the queue |
| `1-preparation` | queued | same |
| `1a-orchestrator-prep` | queued | same |
| `2-ready` | **starts locally** | the runner's own pickup moves it to `3-progress` before spawning |
| `3-progress` | **starts locally** | where a run belongs |
| `3a-failed-pickup` | queued | `RunPlanner` never moves it to `3-progress`, so a run there is killed by lane reconciliation |
| `3b-code-not-complete` | queued | same park-lane reason |
| `4-auto-review` | queued | a delivery is being judged; the auto-review worker owns the card |
| `5-human-review` | queued | delivery awaiting acceptance |
| `5e-escalated` | queued | intervention basin |
| `6-completed` | queued | terminal |
| `7-archive` | queued | terminal |

Two further gates apply to a card that is otherwise in a runnable lane:

- **Delivery under review.** Phases `post-processing-running`,
  `post-processing-blocked`, `awaiting-review`, and `integrating` mean the run
  is over and its delivery is being handled. A follow-up sent then races the
  auto-review worker and dies with `active job moved out of 3-progress`.
- **Remote execution.** When `ProjectExecutionPolicy.ResolveExecutionLocation`
  names a runner other than this backend, the follow-up belongs to that runner,
  not to a local process. Admission runs before the local CLI-availability
  check for exactly this reason: a card that will never run here must not be
  refused because this host lacks the CLI.

Precedence is lane, then delivery, then remote, so the `202` body always names
the most fundamental obstacle.

**Queueing is not a rejection.** A queued follow-up persists the prompt and
mode as `pending-intent.json`, appends the usual continuation note to
`prompt.md` (which is what a remote runner reads when it later claims the
card), promotes the card to the top of `2-ready` with transition detail
`follow-up-queued-<reason>`, writes a `follow_up_queued` ledger row, and answers
`202 {"status":"queued","queued":{"reason":"lane-not-runnable"|"delivery-under-review"|"remote-execution"|"project-busy"}}`.
A manual start carries no prompt, so nothing is persisted; the promotion alone
carries the intent.

**A kill never loses intent.** Every stop path that terminates a run still
holding its execution slot - `ClearActiveJobIfMatches` (lane reconciliation and
the move endpoint), the silence watchdog, and the quota-cap watchdog - calls
`ProjectRunner.PreserveUnconsumedFollowUp` **before** stopping the process. It
writes the follow-up back to `pending-intent.json` with reason
`run-stopped:<why>`, appends a `follow_up_preserved` ledger row, and raises a
supervisor advisory naming the card and the reason. A run whose CLI already
exited is skipped: the agent has seen the follow-up, and the trailing
post-processing lane move must not resurrect it.

**Consumption is provable.** When a run consumes a saved intent, the file is
renamed to `pending-intent.consumed.json` and kept, and a `follow_up_consumed`
row records the run id. Only the run that stashed an intent may roll it back on
spawn failure, so a later unrelated failure cannot replay a follow-up an agent
already acted on. The card detail shows an unconsumed intent as one quiet line,
"Follow-up waits for the next run."

**`started` is honest.** A run that had to change lanes is watched for
`Runner:FollowUpStartWindowMs` (1200 ms; `0` disables). If a preserved
`pending-intent.json` re-appears inside that window, the run did not survive and
the endpoint answers `409` with the reason while the intent stays persisted.
The preservation half is the detector, so a run that legitimately finishes in
under a second is never reported as lost.

## Verification

- Outcome and grammar changes need focused unit tests for analyzer, policy,
  reply parser, quarantine, and escalation paths.
- Pickup, dead-letter, active-run, and recovery changes need integration-style
  tests around `ProjectRunner` or the service that owns the transition.
- Prompt-template changes under `prompts/runtime/` require the matching live CLI
  probe, not only rendered-string tests.
