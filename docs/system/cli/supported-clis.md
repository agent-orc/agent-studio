# Supported CLIs

This document is the **contract** every CLI integration in the agent-orchestrator must satisfy. It describes:

1. What "supported" means.
2. The capabilities every supported CLI must provide.
3. The CLIs supported today and their known quirks.
4. The step-by-step procedure for adding a new CLI.

Whenever a CLI integration is added, changed, or audited, this file is updated **in the same PR**.

> **Language:** English. See [AGENTS.md](../../../AGENTS.md#documentation-language).

> **Operational knowledge** (frame catalogues, capture flows, known incidents, common tasks) lives in the per-CLI skills under [`docs/system/cli/skills/`](skills): [`cli-overview`](skills/cli-overview.md), [`cli-claude`](skills/cli-claude.md), [`cli-codex`](skills/cli-codex.md), [`cli-gemini`](skills/cli-gemini.md). This document is the contract; the skills are the working notes. Both must stay in sync.

---

## 1. What "supported" means

A "supported CLI" is a coding-agent CLI that Agent Studio can drive end-to-end. The current integrations are Claude Code, Codex, and Antigravity through its `agentapi` command. The persisted CLI type for Antigravity remains `gemini` for compatibility.

- The task processor can spawn a process for it from a job folder.
- It surfaces live output in the Activity Log.
- The user can pick a model from the side-sheet.
- The user can see plan + remaining quota in the right-hand quota side-sheet.
- The user can cancel a running job.
- A new run can either start fresh or, where the CLI supports it, resume an existing session.

If any of these capabilities are missing, the CLI is **partially** supported and the gap must be documented in section 3.

---

## 2. Required capabilities

Each capability has: a contract, the code that implements it, and the test that proves it.

### 2.1 Process lifecycle

**Contract.** Given a prompt, working directory, optional session id, optional model, the CLI driver must spawn a child process, stream stdout/stderr to the API consumer line-by-line, accept cancellation, and report final exit code + duration.

**Code.** [`GenericCliExecutionService`](../../../backend/Features/Cli/Execution/CliExecutionServiceBase.cs) is the Studio host adapter. [`BuiltInCliBehaviors`](../../../backend/Features/Cli/Execution/BuiltInCliBehaviors.cs) contains CLI-specific parsing and host behavior. Claude and Codex launch through CodingAgentRunner's `ICliDriver`; CAR owns their descriptors, argv, common normalization, process lifecycle, and typed events. Studio retains the output mirror, Activity Log rendering, session and usage capture, cancellation policy, active-job registry, and terminal classification.

The local engine is rollout-gated. Resolution order is the process environment override `RUNNER_EXEC_ENGINE`, then project override, then workspace default, then the platform default `car`. Each configurable tier accepts `car` or `legacy`; `legacy` is the explicit rollback until the migration chain removes it. Antigravity always selects its legacy adapter even when the effective setting is `car`, because its current `agentapi` protocol is not CAR-compatible.

The local backend does not reattach after a restart. `ReattachOnStartup` is intentionally an orphan reaper: it validates PID identity, kills a surviving process tree, and relies on the runner recovery path to demote or reissue the interrupted task. The remote runner's detached-worker reattach contract is separate.

**Test.** A backend xUnit test that spawns the CLI in a temp directory and asserts a non-empty output line + clean exit. An E2E `@billable` "hello world" Playwright spec that drives the UI.

### 2.2 Session model

**Contract.** The driver decides whether the CLI has a usable session concept. If it does, it must:

- Define `IsCompatibleSessionName(string?)` strictly. Reject identifiers from a different CLI and leftover slugs from the removed Copilot driver.
- Supply the compatible session id to the selected CAR request or legacy launch when `resumeSession=true`.
- Capture the session id from the raw or rendered output at the documented hook for that CLI.

If the CLI has no usable session concept, `IsCompatibleSessionName` returns `false` for everything and the UI Continue button stays disabled automatically.

**Session storage discovery.** [`SessionRegistry`](../../../backend/Features/Cli/Execution/SessionRegistry.cs) reads each CLI's on-disk session store to populate the Sessions side-sheet. Disk reads are best-effort: missing files mean "no sessions", not an error.

**Session loss is an expected state.** A previously captured session id can disappear between runs because of retention, pruning, upgrades, or a machine change. The product handles this through recovery, not as a hard failure. [`ProjectRunner.OnCliFinishedAsync`](../../../backend/Features/Runner/ProjectRunner.cs) detects a resume attempt with no newly captured id, clears the dead pointer, records one recovery decision, and lets the next follow-up rebuild context. Do not retain a known-dead session id or convert a normal session-not-found response into an unrelated hard failure.

**Stale sessions are a separate quality risk.** A session can still exist on disk and be accepted by the CLI while its useful context has degraded after a long idle period, provider-side cache eviction, prompt-harness changes, or a partially-applied resume optimization. The [April 23, 2026 Anthropic postmortem](https://www.anthropic.com/engineering/april-23-postmortem) is the reference incident: a Claude Code harness optimization for sessions idle over one hour accidentally kept clearing older thinking on every later turn, making resumed sessions forgetful and repetitive even though the model and API were fine. Our contract is therefore stronger than "resume command exits zero": a resumed run must still act on the user follow-up, preserve task intent, and produce useful evidence. When stale-session behavior is suspected, add tests at the runner/recovery layer first, then CLI-specific live probes for Claude and Codex.

### 2.3 Model selection

**Contract.** `GetModelCatalogAsync` returns a list of models the user can pick from. Acceptable sources, in preference order:

1. **Live discovery** — query the CLI for its current model list (a PTY-driven probe is the reference pattern).
2. **Static list with version** — hardcoded list keyed off the CLI version when discovery isn't feasible (Claude pattern).
3. **Empty list** — the CLI auto-picks; the user has no choice. Fine for v1, document it.

The frontend's model dropdown reads `/api/cli/{cliType}/models`. No CLI-specific UI code is needed if the JSON shape (`CliModelCatalog`) is honoured.

**Selected model.** Studio first qualifies the chosen model against its live catalog. Claude and Codex then pass the qualified model and thinking level to CAR, which applies its common normalization and descriptor flags. The legacy Antigravity adapter maps the model to its `agentapi` vocabulary.

**Latest-in-family defaults and migrations.** Supporting calls name a stable
family (`claude-haiku`, `claude-sonnet`, `claude-opus`, `gpt-mini`, or
`gpt-flagship`) instead of a concrete generation. `ModelFamilyResolver` picks
the newest available member from a fresh live catalog and falls back to the
registry when discovery is stale. A configured concrete model remains an
explicit pin.

Workspace CLI Management shows the active Token Economy migration catalog
version and the workspace switch for safe admission-time migrations. It also
lists an update offer for each superseded configuration pin, including the
cost-class and reasoning-ladder diff. The same offer appears on an explicitly
pinned task card and on a project pipeline-step override. Applying an offer is
always an operator action for explicit pins.

The workspace switch governs persisted, non-explicit task model ids. A
catalog rule is applied only when it is marked `safeAuto`, its target is
available on the executing host, it is a newer same-family generation with a
compatible ladder and the same or lower known cost class, and dated pricing is
available. The resulting `model_migrated` timeline event and operator-feed
entry carry the source, target, rule, and catalog version. Resolving a
family-backed supporting default is baseline selection rather than a persisted
model migration, so it does not rewrite a card or create a task timeline event.
Remote claims retain the stored model until runner capability advertisements
can prove the target model is available on that execution host.

### 2.4 Quota probe

**Contract.** A `QuotaProbeBase` subclass returns a `QuotaSnapshot` with:

- `Plan` — human-readable subscription tier ("Pro", "Plus", "Free", …) or `null`.
- `Windows[]`: one or more `QuotaWindow`s with `UsedPct` (0-100, may exceed when overage allowed), `ResetAt` UTC, and `ResetLabel` for display. When the CLI exposes a recognized quota surface but no numeric utilization, return an explicit window with `UsedPct = null`; consumers render `Unknown` and must not treat that as a probe error.
- `Source` — what the probe queried (`/usage`, `/status`, footer text, HTTP endpoint, …).
- `RawSample` — truncated raw output for debugging.

**Implementation pattern.** Most probes spawn the CLI in a scratch directory under `%TEMP%/agent-taskboard-quota/<cliType>` via a PTY, send slash commands, and scrape the rendered panel. See `ProbeWithStepsAsync` in [`QuotaProbeBase`](../../../backend/Features/Cli/Quota/QuotaProbeBase.cs).

**Aggregation.** [`QuotaService`](../../../backend/Features/Cli/Quota/QuotaService.cs) aggregates all registered `IQuotaProbe`s and serves `/api/cli/quota`. New CLIs register an `IQuotaProbe` in [`backend/Host/Program.cs`](../../../backend/Host/Program.cs).

**Quota fallback routing.** Workspace CLI Management persists one primary model
and an optional fallback CLI/model/thinking level per CLI in
`cli-model-routing.json`. Before each run, `CliQuotaFallbackService` evaluates
the primary against the cached `/api/cli/quota` snapshot and configured usage
caps. A blocked primary selects the fallback for that run only. The task's
stored CLI/model is not rewritten, so a later snapshot below the cap returns
new runs to primary automatically. Cross-CLI switches deliberately start a new
session because session identifiers are CLI-owned. Every switch emits the
`quota_fallback_activated` timeline event, a task chat note, an active task-card
badge, and a status-bar warning; fallback selection is never silent.

Before this routing was added, quota handling only rejected pickup with
`QuotaCapExceeded` and the watchdog stopped an in-flight run that crossed its
cap. Model catalog fallback referred only to discovery failure and did not
change the launch model. There was no quota-triggered model or CLI router.

**Refresh cadence.** Background refresh is automatic; the user can force a refresh per-CLI via the side-sheet button.

### 2.5 Logging & Activity Log

**Contract.** Output the user sees in the Activity Log must be:

- **Streamed,** not buffered until the run finishes.
- **Marker-line formatted** so the frontend's `activity-log.parser` can classify entries (Read/Search/Edit/Run/Todo/Task/Messages).
- **Free of ANSI escapes** in the persisted form (the base class strips them on write).
- **UTF-8 safe** so output is not corrupted by the host's legacy code page.

**Implementation.** Add or update an [`ICliOutputRenderer`](../../../backend/Features/Cli/Execution/Rendering/ICliOutputRenderer.cs) and wire it through the CLI behavior's `TransformReadLine` delegate. Claude and Codex render their native streams into `● Read /path`, `● Edit /path`, `● Run <cmd>`, and related marker lines. Antigravity currently retains an inline renderer for its `agentapi` JSON.

If the CLI emits plain text already in a parser-friendly shape, `TransformReadLine` can be the default identity transform.

**Frame compatibility and drift.** The remote Agent Host and CAC rendering
share the versioned, scrubbed provider-stream corpus under
`testdata/cli-fixtures/streams/<cli>/<version>/`. Runner replay tests must assert
events, terminal outcomes, recovery, and delivery state. CAC may add separate
render expectations but must consume the same provider bytes. An unknown
structured top-level frame or nested item type emits the typed
`runner.protocol.unknown-frame` event with scrubbed counters and a payload hash;
it is never silently dropped. The current per-version vocabulary and outcome
differences live in the
[CLI frame compatibility matrix](frame-compatibility-matrix.md).

### 2.6 Cancellation

**Contract.** A running job must be cancellable from the UI. CAR owns the Claude and Codex process lifecycle and tree kill. Studio records the stop reason, retains Windows task job-object cleanup, and maps the terminal result. The legacy adapter retains the equivalent cancellation behavior for rollback and Antigravity.

**CLI-specific cleanup.** If the CLI leaves orphaned PTY children, modal pickers, or background helpers, the driver is responsible for cleaning them up. Quota probes additionally send `<Esc><Esc>` before tearing down to close any open modal pickers — keep doing this for new CLIs.

### 2.7 Availability & authentication check

**Contract.** `TestCliPath()` returns `(Available, Version, ResolvedPath)`. Availability and quota probes remain Studio services because they also feed settings and routing surfaces outside an active CAR run.

**Authentication.** Studio-local CLIs may still authenticate out of band.
Remote hosts use the protected provider-auth provisioning flow. Environment
credentials live only in `/etc/agent-runner/provider-auth.env` on the selected
host, owned by `root:agent` with mode `640`. Studio sends a replacement through
SSH stdin and does not persist it. Both remote units load the file after their
normal runner EnvironmentFile. The probe uses the resulting process environment
and CLI status as authentication authority. A metadata-only freshness monitor
may read native Claude and Codex credential files for modification and expiry
timestamps, but never returns token values.

**Remote coding hosts.** The standalone host keeps one primary
`RUNNER_CLI_BIN` plus `RUNNER_CLAUDE_CLI_BIN` and `RUNNER_CODEX_CLI_BIN`.
Capability probing tests binary presence and provider authentication for each
configured provider before the first advertisement. On Linux, the status command
runs through `nice -n 10` with a 30-second timeout so review load does not look
like logout. The runner keeps the last advertised auth verdict when a probe times
out, returns empty output, cannot start, or reports an unsupported command. Two
consecutive probes must contain an explicit logout signal before a ready verdict
becomes unavailable. A later successful probe restores ready automatically,
without restarting the runner and clears the matching capability circuit.
Indeterminate observations emit
`runner-provider-auth-probe-degraded` for host diagnosis. A card requires the
matching `cli-execution:<cliType>` and `provider-auth:<cliType>` keys. The CAR
worker receives the matching provider path, so a Claude pin on a Codex-primary
host cannot fall through to `codex -m <claude-model>`. On headless Linux hosts,
both runner units load `/etc/agent-runner/provider-auth.env`; the Claude worker
explicitly admits `CLAUDE_CODE_OAUTH_TOKEN` from the process environment after
clean-context preparation. Credential paths are never an authentication source;
the optional native-file read is freshness metadata only.

Execution Hosts renders `OK`, `Retrying`, `Limited`, `Expiring`, `Unavailable`,
or `Unknown` for each advertised CLI and exposes the probe detail as a tooltip.
Provider-auth state changes are retained in capability recovery history. Only
two consecutive explicit sign-out results can create the blocking
`OK -> Unavailable` transition and operator notification. Generic non-zero and
tool exits retain last-good, while quota output becomes provider-scoped
`Limited`. Ready cards show a provider sign-in wait reason only for confirmed
sign-out. If credential metadata exposes an expiry, Studio warns during the
final 14 days.

### 2.8 Execution context (read-only observability)

**Contract.** Every CLI loads context beyond the prompt the runner hands it — a memory / instruction-file chain walked up from the working directory, a session/transcript store, a global config directory, and (Claude) wired-in MCP servers. `DescribeContextSources(string jobKey)` returns a `CliExecutionContext` describing those sources for the live (or just-finished, still-tracked) run, plus the scalar header (model, effective permission mode, cwd). This is a **read-only** surface (ASS-1739 / T1a): producing it must never change what the CLI loads — policy changes are a separate task (T1b). The base implementation derives everything from the adapter invocation plus each CLI's documented config-path conventions and sets `Source = "convention"`; a driver with a richer self-report (Claude's stream-json `init` frame) overrides `DescribeContextSources`, merges the init-frame model / permission mode / cwd / MCP list on top, and sets `Source = "init-frame"`. Returns `null` for an unknown run; the interface default is a no-op so test stubs and not-yet-wired drivers stay compilable.

**Code.**
- Model: [`CliExecutionContext` / `CliContextSource` / `CliContextSourceKinds`](../../../backend/Shared/Models/CliExecutionContext.cs).
- Contract + base/convention implementation: [`ICliExecutionService.DescribeContextSources`](../../../backend/Features/Cli/Execution/ICliExecutionService.cs) and [`CliExecutionServiceBase`](../../../backend/Features/Cli/Execution/CliExecutionServiceBase.cs) (`BuildConventionContext`).
- Pure convention builder (per-CLI path conventions, filesystem-probed): [`CliContextConventions`](../../../backend/Features/Cli/Execution/CliContextConventions.cs).
- Claude init-frame merge and parser: [`BuiltInCliBehaviors`](../../../backend/Features/Cli/Execution/BuiltInCliBehaviors.cs) and [`ClaudeInitContextParser`](../../../backend/Features/Cli/Execution/Adapters/ClaudeInitContextParser.cs).
- Persistence: the runner calls `DescribeContextSources` at run finish (while the per-run `ProcInfo` is still alive) and stamps the result onto the latest `SessionEvent` via [`TaskSessionLog.BackfillLatestSessionEventExecutionContext`](../../../backend/Features/Tasks/TaskSessionLog.cs); the run-detail "Execution Context" panel and a slim timeline marker read it back (`RunTimeline`).

**Test.** `CliContextConventionsTests` (pure path conventions), `ClaudeInitContextParserTests` (init-frame parse), `DescribeContextSourcesTests` (service wiring for the convention CLIs + untracked-run null), `SessionEventsTests.BackfillLatestSessionEventExecutionContext_*` (durable JSONL round-trip), and `RunTimelineBuilderTests.ExecutionContext_IsCarriedFromEventOntoRunRecord` (timeline projection) together cover the producer → persistence → projection data path.

**New CLI note.** The convention branch in `CliContextConventions.For` is the only thing a new sessionless/init-frame-less CLI needs; add its memory-file name and config-path conventions there. A driver that emits its own startup frame can override `DescribeContextSources` like Claude does.

### 2.9 Context mode: clean vs. shared

**Contract.** A run executes in one of two context modes:

- `clean`, the default for coding runs, isolates user-level memory, prior transcripts, and scratch configuration while retaining repository instruction files.
- `shared` reuses the operator's global CLI state and must be selected deliberately.

Clean context is implemented with a relocated config home, not a CLI flag. The home contains only authentication and base configuration. Repository instructions stay in the working directory.

| CLI | Support | Host environment | Seed policy |
|---|---|---|---|
| Claude | clean or shared | `CLAUDE_CONFIG_DIR` | Link `.credentials.json`; copy `settings.json`; exclude user memory and project history. |
| Codex | clean or shared | `CODEX_HOME` | Link `auth.json`; copy `config.toml`; exclude history and prior rollouts. |
| Antigravity, persisted as `gemini` | shared only | none documented for `agentapi` | No isolated-home claim is made. |

**Storage and resume boundary.** [`TaskCleanContextStore`](../../../cli-hosting/TaskCleanContextStore.cs) is the one path and seed implementation used by the local backend and standalone Agent Host. Windows homes live under `%USERPROFILE%\.atp\clean-context\`; Linux homes live under `$XDG_STATE_HOME/agent-studio/clean-context/`, falling back to `~/.local/state/agent-studio/clean-context/`. `AGENT_STUDIO_CLEAN_CONTEXT_ROOT` or backend `CleanContext:Root` may select another persistent, non-temporary root. Each CLI/task pair receives a SHA-256-keyed directory with an ownership marker. The task id is not exposed in the path, and another task cannot adopt a home whose marker does not match.

The home survives attempt teardown and backend or Agent Host restart. A continue pickup resolves the marker-validated home before Codex resume viability is planned, so the rollout written by the prior attempt is visible to `codex resume`. A seven-day inactivity retention window owns normal deletion. The backend sweeps at startup and every six hours; both hosts also sweep opportunistically during acquisition. Fresh homes from failed pre-adoption starts are removed immediately, and stale incomplete homes are covered by the same retention pass.

**CAR bridge.** CAR 0.7.0 owns a clean home for one process, while Studio needs the task boundary above. Both local and remote CAR consumers therefore acquire the shared host lease, ask CAR to run in `shared` mode, and pass `CLAUDE_CONFIG_DIR` or `CODEX_HOME` in `CliRunRequest.ExtraEnvironment`. CAR still owns the launch but does not create a second process-scoped home. The upstream public lease seam remains tracked in PROJ-011 as `public-clean-context-lease`; hosts must not duplicate the path composition while that package boundary remains.

Credential files are linked back to the operator home so a refresh writes through to the authoritative token. Base configuration remains an isolated copy. Link creation falls back to copying when the filesystem cannot support the link, with a warning that concurrent refresh protection is reduced for that run.

Context mode resolution remains task override, then project setting, then the `clean` default, narrowed to `shared` for Antigravity. Disposing an attempt lease refreshes the task home's last-use marker without deleting rollout state. Retention deletion is bounded, ownership-checked, best-effort per directory, and idempotent.

**Code and tests.** [`TaskCleanContextStore`](../../../cli-hosting/TaskCleanContextStore.cs) owns path composition, seeding, task identity, restart adoption, and retention. [`CleanContextPreparation`](../../../backend/Features/Cli/Execution/CleanContextPreparation.cs) is the backend observability adapter. [`BackendCarExecution`](../../../backend/Features/Cli/Execution/BackendCarExecution.cs) and [`CarWorkerExecution`](../../../runner/CarWorkerExecution.cs) apply the two CAR bridges. `TaskCleanContextStoreTests`, `CleanContextSessionStabilityTests`, `CliContextModesTests`, `CarWorkerExecutionTests`, and `CleanContextRetentionBreakerTest` cover Windows/XDG paths, real rollout discovery after a host restart, cross-task isolation, credential linking, failed-start cleanup, and retention.

### 2.10 CAR callback and launch seams

CAR 0.7.0 raises typed events before the matching raw-output callback. Studio must parse raw usage and session metadata before subscribers handle `TurnCompleted`, so [`CarCallbackBridge`](../../../backend/Features/Cli/Execution/BackendCarExecution.cs) buffers each typed batch, handles the raw line, and then publishes its events. This ordering is part of the token and cost ledger contract.

The old Studio-local `WindowsHandleScrubSpawner` no longer exists. CAR owns npm-shim healing for CAR-backed Claude launches. CAR 0.7.0 keeps its healer internal, so the existing Studio `NpmShimHealer` remains temporarily for the explicit legacy rollback and non-agent `ClaudeOneShot` only; T4 removes it with those paths. Studio uses the public `ICliProcessSpawner` seam only to attach host bookkeeping and the Claude rules-file overlay. The remaining public API gaps are tracked in PROJ-011 as `public-clean-context-lease`, `public-hardened-spawner-composition`, `public-cli-launch-overlay`, and `public-pre-spawn-health`. Do not solve CAR's internal Windows process helpers by copying them back into this repository.

---

## 3. Currently supported CLIs

### 3.1 Claude Code (`claude`)

| Aspect | Status |
|--------|--------|
| Execution engine | CAR 0.7.0 by default; explicit `legacy` rollback |
| Process lifecycle | CAR Claude descriptor through `ICliDriver`; stream-json output; permission mode supplied from Studio's resolved local run configuration |
| Session model | UUIDs only; resume through the CAR request; the CLI assigns the fresh session id |
| Model selection | Studio catalog and qualification, then CAR model and thinking normalization |
| Quota probe | ✅ `/usage` PTY probe - session + weekly buckets when reported; explicit unknown window for the Claude Code 2.1.202 tabbed API-billing view |
| Logging | Stream-json to typed CAR events and Studio marker lines |
| Cancellation | CAR process-tree stop plus Studio terminal classification and Windows task job object |
| Availability | ✅ `claude --version` |
| Session storage | `~/.claude/projects/<encoded-cwd>/<uuid>.jsonl` |

**Quirks.**
- The CAR path uses the CAR-A one-shot stdin prompt transport and closes stdin immediately after the full prompt is flushed. This keeps prompts of at least 200 KiB out of argv and process listings without leaving an interactive pipe open. The temporary `legacy` rollback retains the older argv transport from ADR-0014.
- CAR performs its built-in npm-shim healing before a CAR-backed Claude launch. The explicit legacy rollback and non-agent one-shot paths retain the pre-existing Studio healer until T4 because CAR 0.7.0 exposes no public repair API. CAR-backed agent runs never call the Studio healer.
- Studio adds its centrally managed rules file through the narrow launch decorator until PROJ-011 `public-cli-launch-overlay` is available.
- Claude Code 2.1.202 renders `/usage` as a tabbed `Settings / Status / Config / Usage / Stats` view. API-billed accounts can show only session cost and token counts there, with no subscription utilization percentage. The probe recognizes that exact PTY shape and returns `Quota: Unknown` instead of an empty/error snapshot. Older `Current session` and `Current week` text remains supported.
- Rate-limit frames accept both the original camelCase keys and forgiving snake_case aliases. Unknown fields and optional fields with unexpected types are ignored so telemetry drift cannot break the CLI output loop.

### 3.2 Codex CLI (`codex`)

| Aspect | Status |
|--------|--------|
| Execution engine | CAR 0.7.0 by default; explicit `legacy` rollback |
| Process lifecycle | CAR Codex descriptor through `ICliDriver`; JSON event stream with prompt on stdin |
| Session model | UUIDs only; captured from `thread.started`, with legacy `session_meta` accepted; resume through the CAR request |
| Model selection | Live `CodexModelDiscovery`, Studio qualification, then CAR model and thinking normalization |
| Quota probe | `/status` PTY probe with 5-hour and weekly buckets when reported |
| Logging | Typed CAR events plus Studio marker lines from `CodexOutputRenderer` |
| Cancellation | CAR process-tree stop plus Studio terminal classification and Windows task job object |
| Availability | ✅ `codex --version` |
| Session storage | `~/.codex/session_index.jsonl` + `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` |

**Quirks.**
- Codex's trust prompt has "1. Yes, continue" pre-selected and accepts a bare Enter. Sending `1<Enter>` leaves a stray `1` that prefixes the next slash command, so use `<Enter>` alone.
- Trust, welcome, and `/status` form a fragile multi-step probe. See [`CodexQuotaProbe`](../../../backend/Features/Cli/Quota/CodexQuotaProbe.cs).
- The CAR callback bridge must capture raw usage before publishing the matching `TurnCompleted` event.

### 3.3 Antigravity (`agentapi`, persisted as `gemini`)

| Aspect | Status |
|--------|--------|
| Execution engine | Legacy adapter only, including when the effective rollout setting is `car` |
| Process lifecycle | `agentapi new-conversation [--model=<id>] <prompt>` or `agentapi send-message <uuid> <prompt>` |
| Session model | UUID conversation id captured from `agentapi` JSON; resume with `send-message` |
| Model selection | Static `flash`, `pro`, and `flash_lite` mapping |
| Quota probe | No local numeric surface; reports that quota is managed by the IDE session |
| Logging | `agentapi` JSON rendered to the shared marker-line vocabulary; typed compatibility events use the existing adapter |
| Cancellation | Studio legacy process-tree cancellation |
| Availability | `agentapi --version`, accepting its usage response when that flag is not implemented |
| Context mode | Shared only |

**Quirks.**
- The public CLI type is still `gemini`; changing the persisted value is a separate compatibility migration.
- CAR 0.7.0 has an Antigravity descriptor, but it assumes a different stream and permission contract than Studio's current `agentapi` integration. T2 does not silently switch protocols.
- `agentapi` exposes no documented config-home override, so Studio reports shared context honestly.
- A future CAR migration requires recorded protocol fixtures for conversation creation, continuation, permission behavior, output framing, session capture, stop, and quota reporting.

---

## 4. Adding a new CLI: checklist

Use this as a PR template. Tick each box; missing items must be justified in section 3.

- [ ] Add the CLI type and descriptor to CodingAgentRunner first, or document why its protocol must remain an explicit Studio legacy adapter. Do not create another unstructured default launch path.
- [ ] Add the literal to the frontend type union in [`task.model.ts`](../../../frontend/src/app/models/task.model.ts).
- [ ] Add a [`CliBehavior`](../../../backend/Features/Cli/Execution/CliBehavior.cs) for Studio-owned rendering, session capture, context observation, and model discovery. CLI argv belongs in CAR for a CAR-backed integration.
- [ ] Register one keyed [`GenericCliExecutionService`](../../../backend/Host/Program.cs) and include it in [`CliRouter`](../../../backend/Features/Cli/Execution/CliRouter.cs).
- [ ] Update the CAR support decision in [`BackendCarExecution`](../../../backend/Features/Cli/Execution/BackendCarExecution.cs). An unsupported CAR protocol must fall back explicitly and visibly.
- [ ] Add session-store discovery to [`SessionRegistry`](../../../backend/Features/Cli/Execution/SessionRegistry.cs), or return no sessions if the CLI has no on-disk store.
- [ ] Add path conventions to [`CliContextConventions`](../../../backend/Features/Cli/Execution/CliContextConventions.cs).
- [ ] Default to shared-only context unless the CLI has a real config-home override and a credential-safe seed policy.
- [ ] Add a quota probe under [`backend/Features/Cli/Quota`](../../../backend/Features/Cli/Quota) and register it in `backend/Host/Program.cs`.
- [ ] Add backend contract tests for the Studio-to-CAR request, event ordering, output rendering, session capture, stop, and any deliberate legacy fallback.
- [ ] Add a `@billable` E2E spec `frontend/e2e/<cli>-hello-world.spec.ts`.
- [ ] Update **section 3** of this document with the real, observed behaviour and quirks.
- [ ] Update [README.md](../../../README.md) and [AGENTS.md](../../../AGENTS.md) if the new CLI changes user-visible product scope.

The order matters: establish the CAR protocol contract and deterministic fixtures before enabling a production rollout.
