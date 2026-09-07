# CLI Domain Map

Version: 2026-09-01
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
  package is absent or its required `.cmd` command shim disappeared. It selects
  a plain install or forced relink from that state and persists repair and
  nearby npm-activity evidence to
  `<TaskRepository>/logs/cli-self-heal.jsonl`.
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
- CLI skills are required reading before changing the matching driver.
- Prompt-template edits are behavior changes. String-render tests are not enough
  because the adapter can still hand a bad shape to the live CLI.
- Sandbox and permission behavior must be explicit per CLI. Do not hide a
  permission block behind a generic failure.
- Quota probes are observability surfaces. Preserve stable event names and
  useful error context when editing nearby code.
- Quota reads are cache-only request paths. `GET /api/cli/quota` must never
  await CLI startup or PTY parsing. Failed refreshes retain the last good
  values and expose `probeFailedAt`, `cliVersion`, and the probe error so the UI
  can show an attributable stale marker.
- Claude and Codex version changes are checked after startup and periodically.
  Keep the structured `CLI version changed` log line when editing version or
  self-heal behavior.
- Local CLI repair handles two recognized global npm states: a truly absent
  configured package receives a plain install, while a present package with an
  absent Windows `.cmd` command shim receives a forced relink so an unchanged
  package version still regenerates bin shims. Custom executable paths and
  present-but-broken command shims remain outside this policy. Repair verifies
  npm itself with `npm --version` from an explicit active-Node, APPDATA, or PATH
  location before install, then verifies both the `.cmd` shim and CLI
  `--version`. It is limited to one persisted attempt per CLI per hour. The
  runner-status projection contains only active failures: a successful repair
  or later healthy probe clears the entry, and the durable resolved journal row
  prevents restart rehydration from restoring a stale alarm.
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
  task's configured CLI or model.
- A model catalog is the union of registry knowledge and live CLI discovery.
  A registry model that the installed CLI does not report remains visible and
  disabled with an availability note. Generation age is separate from
  deprecation: advertised older models remain selectable under `Older models`.
- A quota fallback is run-scoped and must never be silent. Keep the
  `quota_fallback_activated` timeline event, task chat note, task-card badge,
  and status-bar warning aligned. When the primary is below its cap again, the
  next run uses it automatically. Cross-CLI fallback starts a fresh session.
- Admission is algorithmic and pre-launch (AGT-2055). Before a card is admitted
  the scheduler evaluates the cached quota snapshots for its target CLI - a
  strict cap check plus a burn-rate projection over the 5-hour and 7-day windows
  (`QuotaAdmissionPlanner` / `QuotaWindowProjection`; caps in `cli-quota-caps.json`,
  default 95%). It decides purely from data, without spawning anything, to launch
  on primary, pre-emptively switch to the AGT-2040 fallback, throttle parallel
  admissions, or wait quietly for the next reset - never a burned launch or a
  reissue-budget charge on an exhausted quota (environmental, per the AGT-1944
  taxonomy). Every load-steering decision (switch / throttle / wait) is
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

## Verification

- Driver changes need focused unit tests for frame parsing, session capture,
  error classification, and command construction.
- Prompt or execution-path changes need the matching live probe, such as
  `claude-hello-world.spec.ts` or the equivalent for the affected CLI.
- Quota/model UI changes need frontend tests plus Playwright when behavior or
  rendering changes.
- For Codex changes, re-check current CLI behavior before relying on older
  recovery heuristics.
