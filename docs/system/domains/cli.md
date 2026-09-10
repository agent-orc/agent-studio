# CLI Domain Map

Version: 2026-09-08
Status: System-of-record map for CLI adapter and quota changes.

Use this when a change touches Claude, Codex, Copilot, Gemini, prompt handoff,
stream parsing, session capture, quota probes, model catalogs, sandbox modes, or
CLI execution tests.

## Entry Points

- [docs/system/cli/supported-clis.md](../cli/supported-clis.md) defines the cross-CLI invocation
  contract.
- [docs/system/cli/skills/cli-overview.md](../cli/skills/cli-overview.md) covers adapter
  invariants.
- Per-CLI deep refs:
  [Claude](../cli/skills/cli-claude.md),
  [Codex](../cli/skills/cli-codex.md),
  [Copilot](../cli/skills/cli-copilot.md),
  [Gemini](../cli/skills/cli-gemini.md).
- [docs/system/cli/skills/sandbox-and-yolo.md](../cli/skills/sandbox-and-yolo.md) covers
  permission, sandbox, and effective-mode behavior.
- [docs/system/cli/audits/startup-cost-analysis-2026-05-09.md](../cli/audits/startup-cost-analysis-2026-05-09.md)
  is the spawn/probe/discovery cost analysis.
- [docs/system/cli/investigations/codex-runner-investigation.md](../cli/investigations/codex-runner-investigation.md) records
  the Codex stdin-via-`-` incident and regression guard.

## Key Code

- [Model Routing Policy](./model-routing-policy.md) is the canonical selection
  policy above the live model catalog and quota fallback machinery.
- `backend/Services/Cli/`: CLI drivers and shared execution base.
- `backend/Services/Cli/CliRouter.cs`: `cliType` routing.
- `backend/Services/Quota/*QuotaProbe.cs`: per-CLI quota probes.
- `backend/Services/Quota/QuotaService.cs`: aggregate quota surface.
- `backend/Features/Cli/Repair/LocalCliRepairService.cs`: Windows local-host
  detection and bounded repair when a configured Claude or Codex global npm
  package is absent, its required `.cmd` command shim disappeared, or its
  launcher binary is still the postinstall placeholder. It selects a plain
  install, a forced relink, or a postinstall re-run from that state and persists
  repair, detection, and nearby npm-activity evidence to
  `<TaskRepository>/logs/cli-self-heal.jsonl`.
- `backend/Features/Cli/Pty/CliEnvironment.cs`: `ProbeEnvironment()` is the one
  updater guard every PTY spawn passes as `extraEnv`.
- `backend/Features/Cli/CliEndpoints.cs`: sessions, versions, quota, and model
  endpoints. The CLI-session tool (AGT-2102) adds `GET /api/cli/{cliType}/session-detail`
  (lazy single-transcript parse: model, thinking, message count, first prompt)
  and a guarded `DELETE /api/cli/{cliType}/session` (cleanup refused for any path
  outside the CLI's own session store). Both resolve/parse in
  `SessionRegistry.cs`; the `/usage` list report stays body-free.
- `backend/Services/Runner/OrchestratorSession.cs` and
  `OrchestratorRunner.cs`: runner-to-CLI orchestration boundary.
- `backend/Features/Cli/Routing/OneShot/ClaudeOneShot.cs` and `CodexOneShot.cs`:
  central one-prompt adapters used by model-backed pipeline steps. Codex uses
  stdin plus the JSONL protocol, read-only sandboxing, final-agent-message
  extraction, and `turn.completed` usage parsing.
- `prompts/runtime/`: prompt templates handed to the CLIs.
- `frontend/src/app/features/cli/`, `frontend/src/app/features/tokens/`, and
  `frontend/src/app/components/cli-model-selector/`: CLI status, usage, quota,
  and model UI.

## Invariants

- Every driver must satisfy the same contract: start process, stream output,
  capture session identity when available, report completion, surface quota and
  permission issues, and preserve terminal sentinels.
- Log line caps never break a JSONL frame. Oversized structured payloads remain
  parseable and carry an in-frame truncation note and flag.
- CLI skills are required reading before changing the matching driver.
- Prompt-template edits are behavior changes. String-render tests are not enough
  because the adapter can still hand a bad shape to the live CLI.
- Sandbox and permission behavior must be explicit per CLI. Do not hide a
  permission block behind a generic failure.
- A CLI's model catalog is the known-model registry union live discovery. A
  model the registry knows for that CLI's vendor but the installed CLI does not
  offer stays in the catalog as unavailable with an attributable note, and the
  picker renders it disabled: known-but-unavailable is disabled, not hidden. A
  model only the CLI reports stays selectable. Reasoning ladders and default
  levels come from the CLI only for models explicitly onboarded for it
  (`ModelMetadataRegistry.UsesLiveDiscoveredThinkingLadder`, currently only
  `gpt-6-astra`); every other model, including the codex `gpt-5.6` family,
  always resolves through the static `CliThinkingLevels` table regardless of
  what the CLI reports for it, byte-for-byte, so onboarding a new model can
  never silently change an already-shipped model's default (AGT-2707).
- Quota probes are observability surfaces. Preserve stable event names and
  useful error context when editing nearby code.
- Quota reads are cache-only request paths. `GET /api/cli/quota` must never
  await CLI startup or PTY parsing. Failed refreshes retain the last good
  values and expose `probeFailedAt`, `cliVersion`, and the probe error so the UI
  can show an attributable stale marker.
- Claude and Codex version changes are checked after startup and periodically.
  Keep the structured `CLI version changed` log line when editing version or
  self-heal behavior.
- A quota probe or model discovery observes the installed CLI; it never mutates
  it. Every `PtySession.SpawnAsync` call passes `CliEnvironment.ProbeEnvironment()`
  as `extraEnv`, which disables the Claude and Codex self-updaters exactly as the
  run spawn path does. A probe that lets the CLI auto-update can leave a
  half-installed global package behind and break every later run on that host.
  `PtyProbeUpdaterGuardTests` pins the guard at each spawn site.
- Local CLI repair handles three recognized global npm states: a truly absent
  configured package receives a plain install, a present package with an absent
  Windows `.cmd` command shim receives a forced relink so an unchanged package
  version still regenerates bin shims, and a present package whose launcher
  binary is still the sub-4096-byte postinstall placeholder (or whose
  `--version` reports `native binary not installed`) receives a re-run of the
  package's own `install.cjs`, falling back to a version-pinned global install
  when that script is gone. Custom executable paths and present-but-broken
  command shims remain outside this policy. Repair verifies npm itself with
  `npm --version` from an explicit active-Node, APPDATA, or PATH location before
  install, then verifies both the `.cmd` shim and CLI `--version`. It is limited
  to one persisted attempt per CLI per hour. The runner-status projection
  contains only active failures: a launcher stub is journalled as `detected` on
  sight so a suppressed attempt still raises the alarm, while a successful
  repair or later healthy probe clears the entry, and the durable resolved
  journal row prevents restart rehydration from restoring a stale alarm.
- Codex Spark quota windows are independent windows. Keep their labels and burn
  percentages separate from the standard 5-hour and weekly windows; never fold
  a Spark-only snapshot into the main-window admission signal.
- Review-decision and supporting aspect calls default to Codex with
  `gpt-5.4-mini`. The configured `ReviewDecisionOrchestrator:Cli` must be passed
  through to `CliOneShotRegistry`; never replace it with an implicit Claude
  lookup. Project pipeline-step overrides and Token Economy recommendations may
  select another compatible GPT model explicitly.
- Workspace CLI Management owns the model-routing policy. Each CLI has one
  primary model and may have a fallback CLI, model, and thinking level in
  `cli-model-routing.json`. `CliQuotaFallbackService` resolves that policy
  against the latest quota snapshot for every new run; it must not rewrite the
  task's configured CLI or model. When the operator has configured no explicit
  fallback for a CLI, `CliQuotaFallbackService` derives one from
  `IModelEquivalenceCatalog` (AGT-2751) - an equal-strength model in the other
  CLI family, e.g. the documented Codex Sol/high <-> Claude Opus 5/high and
  Codex Mini/high <-> Claude Sonnet 5/medium pairs. The exhaustive, maintained
  version of that table is Token Economy's model migration catalogue
  (`model-migration-catalog-safe-auto-rules`); the shipped
  `ModelEquivalenceCatalog` is an interim table pending that catalogue, and an
  operator override in `cli-model-routing.json` always wins over the derived
  pair. `GET /api/cli/quota/model-routes` returns the effective row (configured
  or derived) for every CLI with an `isFallbackDerived` marker so the picker
  can label which is which.
- A model catalog is the union of registry knowledge and live CLI discovery.
  A registry model that the installed CLI does not report remains visible and
  disabled with an availability note. Generation age is separate from
  deprecation: advertised older models remain selectable under `Older models`.
- A quota fallback is run-scoped and must never be silent. Keep the
  `quota_fallback_activated` timeline event, task chat note, task-card badge,
  and status-bar warning aligned. When the primary is below its cap again, the
  next run uses it automatically. Cross-CLI fallback starts a fresh session.
- Admission is algorithmic and pre-launch (AGT-2055). `QuotaAdmissionService`
  is the application-wide boundary over `QuotaAdmissionPlanner`: local coding,
  remote coding claims, local review and aspect calls, remote review claims,
  pipeline post-steps, orchestrator preparation and decisions, project chat,
  and all other shared one-shot consumers resolve through that boundary
  immediately before execution (AGT-2751). The remote coding claim substitutes
  the resolved route into `RunSpecDto`. A review claim resolves every
  `agent-aspect` command against current quota, so an open attempt follows a
  provider switch without rebuilding its stored plan. A deferred review claim
  relinquishes its undelivered lease immediately. An idempotent coding-claim
  replay reads the persisted fallback marker and returns the same resolved
  route instead of reverting to the card's configured, capped provider. Remote
  project chat queues the effective route and retains the configured route as
  provenance, then the runner selects the matching CAR driver. The shared
  one-shot dispatcher calls the selected raw provider exactly once and never
  re-enters itself, preventing fallback cycles. Local, remote-claimed, and
  review-claimed task runs track an
  active fallback differently (in-memory active-run table versus a durable
  `quota-fallback.json` sidecar per job, mirroring `quota-wait.json`) because
  only the local run has a long-lived process to hold it in memory. The
  task-card badge reads whichever source is live for the current lane.
- Before a card is admitted the scheduler evaluates the cached quota snapshots
  for its target CLI - a strict cap check plus a burn-rate projection over the
  5-hour and 7-day windows (`QuotaAdmissionPlanner` / `QuotaWindowProjection`;
  caps in `cli-quota-caps.json`, default 95%). It decides purely from data,
  without spawning anything, to launch on primary, pre-emptively switch to the
  AGT-2040 fallback, throttle parallel admissions, or wait quietly for the next
  reset - never a burned launch or a reissue-budget charge on an exhausted
  quota (environmental, per the AGT-1944 taxonomy). A nearby-reset wait is
  additionally gated on the card being cheap (AGT-2751): an explicit
  high/xhigh/ultra/max reasoning pin switches to the equal-strength fallback
  immediately instead of sitting out the reset inside the configured wait
  threshold, since an expensive card waiting on a cap is the more expensive
  choice. Every load-steering decision (switch / throttle / wait) is
  documented, never silent: a `quota_admission_decision` timeline event carrying
  the projection numbers plus a `load-distribution` orchestrator-feed line (the
  data source for the load-distribution view). A healthy primary launch stays a
  log-only normal path. The planner reuses the AGT-2040 routing map; it does not
  duplicate "which model replaces which".
- Wait-on-quota is opt-in and bounded (CodingAgentRunner 0.6.0). The global
  policy lives in `cli-quota-wait-policy.json`; project settings may override
  enabled state and threshold independently. For a strictly capped primary,
  the decision order is nearby-reset wait, fallback model switch, then
  parallelism throttle. Unknown, suspicious, elapsed, or distant reset data
  cannot enter the nearby wait branch. Every branch emits a
  `quota_admission_decision`; library `QuotaWaitStarted` and `QuotaWaitEnded`
  events additionally maintain the visible `quota-waiting` task substate and
  durable `quota-wait.json` marker.
- Quota-window projection keeps the first trusted start of an active window as
  a persisted anchor (AGT-2107). A newly parsed `resetAt` cannot move that start
  while the anchored reset has not yet passed. Conflicting boundaries and
  projections above a 4x projected/used ratio in the first quarter of a window
  are ignored instead of steering admission. The admission warning records
  `resetAt`, assumed start, and elapsed fraction in the structured log, task
  timeline, and load-distribution feed.

## Quota admission coverage

[ADR-0069](../architecture/decisions/adr-archive.md#adr-0069---quota-admission-is-one-run-scoped-boundary-for-every-cli-execution-path-2026-09-08)
makes quota admission one run-scoped boundary. The requested CLI, model, and
thinking level remain configuration and provenance. Immediately before a CLI
can start, [QuotaAdmissionService](../../../backend/Features/Cli/Quota/QuotaAdmissionService.cs)
passes that route and the current cached quota facts to
[QuotaAdmissionPlanner](../../../backend/Features/Cli/Quota/QuotaAdmissionPlanner.cs).
Only the resulting effective route is dispatched. A running process is not
replanned or interrupted.

| Execution entry point | Admission and dispatch contract | Regression proof |
|---|---|---|
| Local coding | [ProjectRunner](../../../backend/Features/Runner/ProjectRunner.cs) checks admission while selecting a card and again at the launch boundary. It holds a wait or throttle without spawning, or passes the resolved route to the CLI session. | [QuotaAdmissionPlannerTests](../../../backend.Tests/QuotaAdmissionPlannerTests.cs) pins the decision matrix; the existing ProjectRunner quota suites pin local pickup and launch behavior. |
| Remote coding claim (`POST /api/runner/claim`) | [LeaseEndpoints](../../../backend/Features/Tasks/LeaseEndpoints.cs) resolves quota before matching Runner capability and writes the effective CLI, model, and thinking level into `RunSpecDto`. Idempotent claim replay reads the durable fallback marker and returns the same route. | [QuotaFallbackCrossPathTests](../../../backend.Tests/QuotaFallbackCrossPathTests.cs) covers capped Codex to Claude and capped Claude to Codex claims, immutable card configuration, markers, timeline evidence, and replay. |
| Local review and agent aspects | [AspectRunnerService](../../../backend/Features/Runner/AspectRunnerService.cs), [CodeReviewStepService](../../../backend/Features/Review/CodeReviewStepService.cs), and [ReviewDecisionOrchestrator](../../../backend/Features/Runner/ReviewDecisionOrchestrator.cs) submit task and step identity to the shared one-shot registry. The registry resolves once and calls the selected raw provider directly. | [QuotaAwareOneShotRegistryTests](../../../backend.Tests/QuotaAwareOneShotRegistryTests.cs) covers aspect, review-grade, visual-verdict, and task-scoped evidence paths. |
| Remote review claim (`POST /api/v1/runners/{runnerId}/review-claims`) | [V1ReviewPlaneEndpoints](../../../backend/Features/Runner/V1ReviewPlaneEndpoints.cs) resolves each `agent-aspect` command when the attempt is claimed. It returns an effective command copy while leaving the immutable stored plan unchanged. Deterministic tool commands are not routed through a coding-agent CLI. A deferred claim relinquishes its undelivered lease. | [QuotaFallbackCrossPathTests](../../../backend.Tests/QuotaFallbackCrossPathTests.cs) opens an attempt before the cap, claims it after the provider switch in both directions, and verifies that only the claimed command changes. [AttemptAuthorityServiceTests](../../../backend.Tests/AttemptAuthorityServiceTests.cs) pins immediate reclaim after deferral. |
| Pipeline post-steps | [DriftPostStepRunner](../../../backend/Features/Drift/DriftPostStepRunner.cs), [PostAbortReviewStepService](../../../backend/Features/Runner/PostAbortReviewStepService.cs), and the review/aspect services above provide the pipeline step id to [CliOneShotRegistry](../../../backend/Features/Cli/Routing/OneShot/ICliOneShot.cs). This includes requirement-fit, code-quality, review-grade, visual-verdict, drift, and post-abort model calls. | [QuotaAwareOneShotRegistryTests](../../../backend.Tests/QuotaAwareOneShotRegistryTests.cs) proves capped-Claude to available-Codex dispatch for representative pre-step, aspect, post-step, and generic one-shot sources. |
| Orchestrator decisions and preparation | [OrchestratorRunner](../../../backend/Features/Runner/OrchestratorRunner.cs) uses `CliOneShotRegistry` for new decisions and for the one-shot recovery path after a session rejection. Quota deferral returns a typed non-launch result instead of trying the capped provider. | [QuotaAwareOneShotRegistryTests](../../../backend.Tests/QuotaAwareOneShotRegistryTests.cs) proves the public orchestrator entry point resolves before dispatch and that an unsupported pinned floor waits without a fallback cycle. |
| Project chat | Local chat reaches the quota-aware `OrchestratorRunner`. [OrchestratorChat](../../../backend/Features/Runner/OrchestratorChat.cs) resolves a remotely placed turn before enqueue; [RemoteChatWorkBroker](../../../backend/Features/Runner/RemoteChatWorkBroker.cs) carries both effective and configured routes, and [RemoteProjectChatRunner](../../../runner/RemoteProjectChatRunner.cs) selects the matching provider driver. | [QuotaAwareRemoteChatTests](../../../backend.Tests/QuotaAwareRemoteChatTests.cs) proves resolved-family claim and configured-route provenance; [RemoteProjectChatRunnerTests](../../../runner.Tests/RemoteProjectChatRunnerTests.cs) pins cross-family response parsing. |
| Other shared one-shot consumers | Prompt enhancement, title generation, soft reasoning, and future callers registered through `CliOneShotRegistry` inherit the same admission boundary. Callers supply a stable `Source`, plus project and task identity when available, so decisions remain attributable. | [QuotaAwareOneShotRegistryTests](../../../backend.Tests/QuotaAwareOneShotRegistryTests.cs) proves the registry dispatches the selected raw provider exactly once and never recursively replans. |

### Strength, waiting, and return

- Automatic cross-family fallback is limited to exact pairs in
  [ModelEquivalenceCatalog](../../../backend/Features/Cli/Quota/ModelEquivalenceCatalog.cs).
  The current pairs are Codex Sol/high with Claude Opus 5/high and Codex
  Mini/high with Claude Sonnet 5/medium. When fallback is needed, an explicit
  requested model or thinking pin must match a known pair; otherwise admission
  waits instead of weakening the route. The
  [Model Routing Policy](./model-routing-policy.md#hard-floors) remains
  authoritative for correctness floors. An explicit operator fallback override
  remains possible and wins over the derived catalogue route.
- The planner compares the primary reset time, current and projected burn in
  the primary and fallback quota windows, fallback availability, occupied
  slots, the configured wait threshold, and the requested thinking level as
  the run's cost signal. A nearby reset may hold a cheap run. Expensive
  `high`, `xhigh`, `ultra`, or `max` work switches when an equal-strength
  fallback is available. A distant, elapsed, unknown, or suspicious reset does
  not enter the nearby-wait branch.
- The fallback is effective for one launch only. Task and pipeline-step
  settings are not rewritten. Once a fresh quota snapshot places the configured
  provider below its cap, the next launch uses that configured route again.

### Observability

[QuotaAdmissionRecorder](../../../backend/Features/Cli/Quota/QuotaAdmissionRecorder.cs)
tees notable decisions to the structured log, task timeline and chat when a
task exists, and the `load-distribution` orchestrator feed. Switches also emit
`quota_fallback_activated` and write `quota-fallback.json` for remotely claimed
work; waits write `quota-wait.json` and keep the card in the `quota-waiting`
substate until reset. Project-only chat has no task folder, so its decision is
recorded in the project load-distribution feed and returned with configured and
effective route provenance.

The UI reads those same projections: the
[CLI models panel](../../../frontend/src/app/features/cli/components/cli-models-panel/cli-models-panel.ts)
labels catalogue-derived fallbacks as automatic, the
[task-card view model](../../../frontend/src/app/features/board/components/task-card/task-card-view-model.ts)
shows the active fallback model and quota reason, and the
[status bar](../../../frontend/src/app/features/shell/components/status-bar/status-bar.html)
shows the active fallback warning. The reason includes the capped provider and
reset when known, so the visible state distinguishes waiting from switching.

## Verification

- Driver changes need focused unit tests for frame parsing, session capture,
  error classification, and command construction.
- Prompt or execution-path changes need the matching live probe, such as
  `claude-hello-world.spec.ts` or the equivalent for the affected CLI.
- Quota/model UI changes need frontend tests plus Playwright when behavior or
  rendering changes.
- For Codex changes, re-check current CLI behavior before relying on older
  recovery heuristics.
