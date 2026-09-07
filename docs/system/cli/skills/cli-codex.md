---
name: cli-codex
description: Deep operational reference for the OpenAI Codex CLI driver in this project. Use when touching backend/Services/Cli/CodexCliService.cs, the codex --json frame parser, Codex session_meta capture, CodexModelDiscovery, CodexQuotaProbe, or any code that consumes Codex output. Covers invocation (note positional resume!), frame model, session-UUID capture, model catalog (live), quirks (trust prompt, /status PTY fragility), and common tasks. Pair with cli-overview for cross-CLI context.
sentinel: TASKBOARD-CLI-SKILL-CODEX-2026
---

<!-- SENTINEL: TASKBOARD-CLI-SKILL-CODEX-2026 — pickup-tests assert any CLI driving the repo can echo this back. -->

# OpenAI Codex CLI (`codex`)

OpenAI's Codex CLI. Distributed as the npm package `@openai/codex`. Headless invocation is via `codex exec`; live discovery (`CodexModelDiscovery`) keeps the model list current.

> **Source:** [`backend/Services/Cli/CodexCliService.cs`](../../../backend/Services/Cli/CodexCliService.cs) (extends `CliExecutionServiceBase`).
> **Tests:** [`backend.Tests/CodexModelDiscoveryTests.cs`](../../../../backend.Tests/CodexModelDiscoveryTests.cs).
> **Contract:** [docs/system/cli/supported-clis.md §3.2](../supported-clis.md).

## Commit / push boundary

| Question | Answer |
|---|---|
| Does this CLI commit on its own? | **No.** The platform owns the commit boundary. |
| Does this CLI push on its own? | **No.** Push is the runner's job (today: a tracked gap). |
| What if it does? | Regression. Raise an issue and cite [docs/operations/git/commit-push-doctrine.md](../../../operations/git/commit-push-doctrine.md). |

Codex `exec` runs unattended and may inspect the working tree with `git status` / `git diff`. That is fine. What it must never do: `git commit`, `git push`, `git amend`, `git checkout`, `git reset --hard`, or any branch-mutating command. The runner records the commit on the `3-progress -> 4-review` transition; see [docs/operations/git/commit-push-doctrine.md](../../../operations/git/commit-push-doctrine.md) and [ADR-0019](../../architecture/decisions/adr-archive.md#adr-0019---platform-owns-the-commit-boundary-2026-05-04).

## Identity card

| | Value |
|---|---|
| Binary | `codex` |
| Config key | `CodexCli:Path` (override) |
| Version probe | `codex --version` |
| Output mode used | `--json` (NDJSON, one frame per line; **not** the same shape as Claude/Gemini's stream-json) |
| Session ids | UUID — strict |
| Resume flag | `exec resume <uuid>` (positional, before `--json`) |
| Session storage | `~/.codex/session_index.jsonl` + `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` |
| Quota probe | `/status` PTY probe — 5-hour + weekly buckets (Codex reports % left, we invert to % used) |

## Invocation reference

### Fresh run

```sh
codex exec --json [-m <model>] -    # then write prompt to stdin, close
```

Orchestrator-chat one-shots also pass `--skip-git-repo-check`. Chat runs are
read-only Q&A calls (`--sandbox read-only`) and may use a non-repository fallback
directory when project checkout metadata is incomplete. Other Codex one-shot
sources keep the default Git repository check.

The orchestrator passes `-` as the positional and pipes the full prompt
(system-prefix + rendered template) over the redirected stdin pipe, then
closes the pipe so Codex sees EOF. `-m` selects the model. `--json` makes
stdout machine-readable; without it we cannot extract the session UUID.

**Why stdin, not positional argv.** Two independent failures share this one
fix; reverting either half to positional-argv re-opens both:

1. **NOOP on every fresh job** (codex 0.130+): handing the prompt as the
   last positional argv made the model treat the entire block as system-side
   "initial instructions" and reply with `[[TASK_NOOP]]` ("no task
   provided"). See `docs/system/cli/investigations/codex-runner-investigation.md` for the forensic
   write-up.
2. **"The command line is too long." on Windows** (the 2026-05-11 reissue
   incident): codex on Windows is an npm `.cmd` shim launched through
   cmd.exe, whose command line is capped at 8191 chars. A rules-heavy
   prompt plus accumulated reissue framing crossed that cap after 2-3
   reissues, so the spawn failed with exitCode 1 and a 0s duration before
   the model ever ran (the orchestrator then logged three rapid-fire
   `[stderr] The command line is too long.` exits and auto-paused). Routing
   the whole prompt over stdin keeps the argv constant and tiny regardless
   of prompt size or reissue count, so the cmd.exe cap can no longer be
   reached. `BuildStartInfo_ArgvSizeDoesNotGrowWithReissuePromptLength` and
   `BuildStartInfo_LongPromptKeepsPromptOutOfArgvAndUsesStdin` lock this.

### Resume

```sh
codex exec resume <uuid> --json [-m <model>] -   # prompt via stdin
```

`resume` is a **subcommand of `exec`**, taking the UUID positionally. Don't pass it as `--resume=<uuid>` and don't pass it as `-r <uuid>` (that's Claude/Gemini). Codex's `exec resume <uuid>` is positional. The prompt itself goes over stdin via the same `-` switch as fresh runs.

### Anti-patterns

- **Don't** swap argument order. `codex exec --json resume <uuid> -` parses `resume` as the prompt sentinel.
- **Don't** pass a non-UUID session id. `IsCompatibleSessionName` rejects non-UUIDs to keep cross-CLI session names from leaking through.
- **Don't** revert to positional-argv prompt delivery. `BuildStartInfo_LongPromptKeepsPromptOutOfArgvAndUsesStdin` locks the stdin path; `docs/system/cli/investigations/codex-runner-investigation.md` records why.

### System-prompt prefix

Codex has no `--append-system-prompt` flag, so `CodexCliService.BuildSystemPromptPrefix` prepends a short orchestrator note to the stdin payload on every invocation (fresh runs and resumes). The prefix carries two prophylactic hints:

1. **Sentinel reminder.** Repeats the `[[TASK_DONE]] / [[TASK_BLOCKED:...]] / [[TASK_NEEDS_INPUT:...]] / [[TASK_NOOP]]` grammar. On a resume turn the fresh-start template is not re-rendered, so without this the agent regularly drops the terminal sentinel and the run lands in auto-review as "missing-terminal-sentinel".
2. **Windows no-shell hint** (only when `OperatingSystem.IsWindows()`). Tells Codex not to retry on `windows sandbox: runner error` / `CreateProcessAsUserW failed` and to surface `[[TASK_BLOCKED:windows-sandbox]]` instead. This is the preventive complement to `AgentEnvironmentDetector`'s reactive in-stream match.

Keep the prefix short — the length-guard test in `CodexCliServiceTests.BuildSystemPromptPrefix_StaysShort` enforces an upper bound because every Codex invocation pays this in tokens.

## `--json` frame model

Codex emits JSON Lines on stdout. `TransformReadLine` delegates to the pure
[`CodexOutputRenderer`](../../../src/AgentTaskboard.Runner/Cli/Rendering/CodexOutputRenderer.cs)
(the marker-line twin of `CodexEventAdapter`; see `cli-overview` § "Unified renderer
layer"), which maps each frame onto the **same** marker vocabulary Claude emits, so
a Codex run reads as cleanly as a Claude run in the Activity Log.

The runner and backend keep the 64 KiB physical-line memory guards, but those
guards never cut through a JSONL frame. They shorten large payload strings,
write the truncation note inside the frame, and set `item.truncated` so every
stored or returned Codex frame remains valid JSON.

| Frame | Marker out | Stream |
|---|---|---|
| `{"type":"thread.started","thread_id":"<uuid>"}` | `● Session <uuid>` | stdout |
| `{"type":"session_meta","payload":{"id":"<uuid>"}}` (legacy; `session_id` on root also accepted) | `● Session <uuid>` | stdout |
| `{"type":"turn.started"}` | *(suppressed)* | — |
| `{"type":"turn.completed","usage":{…}}` | `● Turn completed (tokens: <input+output>)` | stdout |
| `{"type":"turn.failed","error":{"message":…}}` | `● Turn failed: <reason>` | stderr |
| `{"type":"item.started",…}` | *(suppressed — `item.completed` renders the same item)* | — |
| `item.completed` `agent_message` | model text, multi-line split | stdout |
| `item.completed` `reasoning` | *(suppressed, like Claude's `thinking`)* | — |
| `item.completed` `command_execution` / `command_call` / `local_shell_call` | `● Run <cmd>` | stderr iff `exit_code != 0` |
| `item.completed` `file_change` | `● Edit <path>` | stdout |
| `item.completed` `web_search` | `● Search web <query>` | stdout |
| `item.completed` `update_plan` / `todo` | `● Todo update` | stdout |
| any other frame / item type | `● <type>` (never raw JSON) | stdout |

Codex also writes a tracing diagnostic such as
`codex_core::tools::router: error=Exit code: 1` to stderr for a non-zero shell
command. This is not a separate tool result. In AGT-2081, the Codex rollout
session records a completed `custom_tool_call_output` for `Get-Process
dotnet,vstest.console,testhost -ErrorAction SilentlyContinue`: it contains
usable `dotnet` and `testhost` rows but reports exit 1 because at least one
requested process name was absent. Codex continued the same turn and the CLI
run later completed successfully. This establishes a real command-level exit
1, not a Codex process failure or malformed tool response.

The following `item.completed.command_execution` frame is authoritative and
carries the command, output, and exit code. `CodexOutputRenderer` suppresses
only the duplicate router tracing line so the activity parser sees one complete
tool event. `RealCodexTranscript_ToolRouterExitDiagnostic_IsSuppressed` parses
the persisted AGT-2081 Windows transcript fixture, drives the authoritative
stdout frame through `CodexEventAdapter`, and asserts that it becomes an
error-valued `ToolCompleted` event while the orphan diagnostic produces no
rendered parser row. The negative test
`SimilarStderrWithoutCodexRouterPrefix_RemainsVisible` protects genuine stderr.
The host's `MapCodexFrame` normalization is required until the shared
CodingAgentRunner adapter projects `command_execution.exit_code` onto
`ToolCompleted.IsError`; without it, the package reports the valid completion
as a successful tool event even though the renderer correctly marks it failed.

AGT-2082 is a separate path. `CodexMapLineToRunEvents` calls
`CodexTryCaptureTurnUsage` on raw stdout before `CodexOutputRenderer` runs, and
`CodexTurnUsageBusEmitTests` verifies that a raw `turn.completed` usage frame
reaches the recorded-usage bus with Codex attribution. Consequently, the
AGT-2089 diagnostic caused noisy activity parser rows but could not remove
recorded model usage. A missing usage entry must be investigated in raw
`turn.completed` capture, parser registration, or bus emission instead.

**Deliberate equivalences (not byte-identical to Claude).** Codex's frame catalogue
differs from Claude's, so the marker *text* differs, but each maps to a verb the
frontend `classifyAction` already buckets: `● Session` (vs Claude `● Session init`),
`● Turn completed (tokens: N)` (Codex analogue of Claude's `● Result (success)`), and
`● Run`/`● Edit`/`● Search web`/`● Todo update` reuse Claude's verbs verbatim. The
AC's literal `"Tool: pwsh.exe"` shape was **not** used: it would classify as `other`,
not `command`. Frame→marker snapshots are locked in `backend.Tests/CodexOutputRendererTests.cs`.

## Session-UUID capture

Capture runs in `MapLineToRunEvents` (via `TryCaptureSessionId`), **not** in
`OnOutputLine`. The base class invokes `MapLineToRunEvents` on the **raw** stdout
line but `OnOutputLine` on the **transformed** line; now that `TransformReadLine`
rewrites `thread.started` → `● Session <id>`, the original `thread_id` payload is
gone by the time `OnOutputLine` fires. `MapLineToRunEvents` is the same raw-line hook
where `TryCaptureTurnUsage` already lives, so both telemetry captures read real JSON.

```csharp
protected override IEnumerable<CliRunEvent> MapLineToRunEvents(string jobKey, CliOutputLine line)
{
    if (line.Stream != "stdout") return Array.Empty<CliRunEvent>();
    if (_processes.TryGetValue(jobKey, out var info))
    {
        TryCaptureTurnUsage(info, line);
        TryCaptureSessionId(info, line);   // reads the raw thread.started / session_meta frame
    }
    return CodexEventAdapter.Map(line.Text, jobKey);
}
```

`TryExtractSessionId(string?)` (the pure parser, with 7 regression tests) is unchanged:
it accepts `thread.started.thread_id` (preferred) or legacy `session_meta` `payload.id` /
`session_id`, gated on a canonical UUID. **Anti-pattern:** do not move capture back into
`OnOutputLine` — it would parse the `● Session` marker text instead of the JSON and break.

## Stale Codex sessions

Codex is the second reference path for stale-session reliability after Claude. The same product invariant applies: a successful `codex exec resume <uuid>` is necessary, but not sufficient. The resumed turn must act on the latest user follow-up and reconcile against current job-folder evidence.

Codex has a stronger structured-protocol story than Claude: the cloned `openai-codex` reference contains an App Server protocol over JSON-RPC, and ADR-0013 points the future adapter in that direction. Until that migration exists, the current `codex exec --json` path must still prove three things:

1. `session_meta.payload.id` is captured and persisted into `sessionChain`.
2. `exec resume <uuid> --json -` (prompt over stdin) continues the intended conversation rather than starting fresh.
3. If a resume target is rejected or produces no useful work, the runner routes through Recovery and re-issues the user follow-up once with stronger framing.

Next stale-session probes for Codex should mirror Claude's: fresh run, short resume, backend-restart resume, deliberately missing session id, and accepted stale resume with an observable edit or protocol update.

## Model handling: live discovery + detection-driven default (ADR-0060)

[`CodexModelDiscovery`](../../../../backend/Features/Cli/Pty/CodexModelDiscovery.cs)
spawns the CLI in a PTY (`codex debug models`), parses the `visibility: list`
models in priority order, and caches the catalog on disk + in memory with a TTL
(`CodexModelsCacheMinutes`, default 60). `GetModelCatalogAsync` is the thin
wrapper. To refresh, the user clicks the side-sheet refresh button, which calls
`/api/cli/codex/models?forceRefresh=true`.

**The catalog follows the installed CLI, not a hardcoded list.** This is the
house rule (convention/derivation over settings). The flagship `gpt-5.6-*`
family is deliberately **not** a static `ModelMetadataRegistry` entry: it
appears only when the live CLI advertises it.

**Merged catalog: registry union live discovery (AGT-2707).** `Publish` runs
`WithKnownButUnavailableModels`, which appends every OpenAI registry entry the
live catalog does not list as `Available=false` with
`AvailabilityNote = "Not offered by the installed codex-cli <version>."` (the
version comes from `CliVersionTracker`; it is omitted when no probe has seen
one). The picker renders those entries as **disabled pills with the note**,
never hides them, so an onboarded model such as `gpt-6-astra` is visibly
"known but your CLI is too old" instead of silently missing. The merge is
recomputed on every read, so it follows both the current registry and the
currently installed CLI; the disk cache stores only what the CLI reported.
A model the CLI lists but the registry does not know stays selectable and
carries `"Discovered from CLI; missing registry metadata."`.
`ClaudeModelDiscovery.Reconcile` applies the same rule through the shared
`ModelMetadataRegistry.AppendUnavailableRegistryEntries`.

**`gpt-6-astra` is registry-onboarded but not tiered.** It has a registry entry
(label, vendor, 272k context, pricing left null) so the disabled-not-hidden
rule can fire, but it is not the product default and is not a routing tier -
see the "not yet tiered" note in
[model-routing-policy.md](../../domains/model-routing-policy.md).

**Detection-driven product default.** Every catalog path (`fresh`, `mem-cache`,
`disk-cache`, and the fallback below) runs through `Publish`, which calls
`PickDetectedDefault` and writes the result to
`ModelMetadataRegistry.SetDetectedCodexDefault`:

- `PickDetectedDefault` returns the CLI's own active model when it is already a
  `gpt-5.6-*`, else the highest-priority `gpt-5.6-*` the CLI lists, else `null`.
- `ModelMetadataRegistry.DefaultForCli("codex")` then returns the detected
  `gpt-5.6` when present, otherwise the account-valid `gpt-5.5` baseline
  (AGT-1941: a ChatGPT-account spawn rejects `gpt-5-codex` with a 400). A `null`
  detection correctly clears back to `gpt-5.5` (the baseline never sticks to a
  stale detection).
- This is the single lever every default site already routes through: task
  creation, `PUT /cli-type` / `PUT /model` (`SetJobCliType`/`SetJobModel`),
  owner/client-default materialization (`BackfillAgentDefaults`,
  `AgentDefaultsMaterialization`), and the invocation-time floor in
  `BuiltInCliBehaviors.DefaultCodexModel`.

**Reasoning ladders come from the CLI, but only for onboarded models
(AGT-2707).** `ParseDebugModelsJson` reads a model's
`supported_reasoning_levels[].effort` (in CLI order) and
`default_reasoning_level` **only when
`ModelMetadataRegistry.UsesLiveDiscoveredThinkingLadder(id)` is true** — today
that allowlist holds exactly `gpt-6-astra`. Every other codex model, the
`gpt-5.6-*` family included, always resolves through the static
`CliThinkingLevels.For` table regardless of what the same `debug models`
response reports for it. This scoping exists because the static table drifts
with every CLI release — codex-cli 0.153.4 lists `gpt-6-astra` as
`low, medium, high, xhigh, max, ultra` while the static `gpt-6` branch still
answers `minimal ... xhigh` — but a 2026-09-07 21:03 review blocked an earlier,
unscoped version of this feature: letting every model's CLI-reported
`default_reasoning_level` win changed `gpt-5.6-sol`'s product default from
`ultra` to the CLI's own `low`, an unrelated regression the astra onboarding
must not carry. `ModelMetadataRegistry.DetectedLadder` enforces the same
allowlist on the read side, so `ThinkingLevelsFor`, `DefaultThinkingLevelForCli`,
and `ResolveThinkingLevel` only ever consult a CLI-reported ladder for an
onboarded model; a rung such as `max` on `gpt-6-astra` is accepted, but the
5.6 family's ladder and default stay exactly what the static table says,
byte-for-byte. Regression coverage:
`CodexDetectedDefaultTests.Gpt56Ladder_And_Default_StayByteForByte_WhenCliReportsADifferentOne`.
Whether to extend the allowlist to the gpt-5.6 family (so the static table's
drift gets fixed there too) is the separate operator decision noted in
[model-routing-policy.md](../../domains/model-routing-policy.md); add ids to
`LiveDiscoveredLadderModelIds` only when that decision is made.

- `WithCurrentCodexCapabilities` fills in a ladder only when an entry has none.
  Overwriting a CLI-reported ladder with the static one is exactly the drift
  this replaced. The disk cache is versioned (`codex-model-catalog.v2.json`)
  because a pre-AGT-2707 file cannot be told apart from a CLI-reported one.
- An explicit or owner-supplied level still wins and is normalized to the
  selected model's ladder (`ResolveThinkingLevel`); an out-of-ladder request
  lands on the model's own default rather than escalating to the top rung.

**Cache / TTL / fallback.** When the CLI cannot be queried and no cache exists,
discovery returns a registry-backed `FallbackCatalog` (the static OpenAI models,
`gpt-5.5` default, **no** `gpt-5.6`) rather than emptying the model surface,
mirroring `ClaudeModelDiscovery.FallbackCatalog`. Because that catalog also runs
through `Publish`, the fallback keeps the default on the `gpt-5.5` baseline.

**Boot warm-up.** A best-effort, fire-and-forget warm-up in
[`Program.cs`](../../../../backend/Host/Program.cs) triggers one discovery on start
so new-task defaults resolve to the current model before the first UI fetch. It
is gated off under the Test host and via `CodexModels:WarmupOnBoot`, honors the
disk-cache TTL (a warm cache skips the PTY spawn), and emits
`codex-model-warmup-complete` / `codex-model-warmup-skipped` structured logs;
`Publish` logs `Codex detected default published`.

When a CLI version bump changes the output format, the regression shows up as an
empty parse (discovery falls back to cache, then `FallbackCatalog`) and the
dropdown shows only the static list. Tests in
[`CodexModelDiscoveryTests.cs`](../../../../backend.Tests/CodexModelDiscoveryTests.cs)
lock the parser shape (5.6 reported / not reported, priority ordering, fallback)
and [`CodexDetectedDefaultTests.cs`](../../../../backend.Tests/CodexDetectedDefaultTests.cs)
locks default resolution in both cases plus vendor isolation.

## Quirks (and what to do about them)

1. **Trust prompt has "1. Yes, continue" pre-selected and accepts a bare Enter.** Sending `1<Enter>` works but leaves a stray `1` in the input box that prefixes the next slash command. Use `<Enter>` alone when scripting Codex over a PTY (the quota probe does this).
2. **Hook-trust review is an operator decision.** Enabled plugin hooks that are new or no longer trusted can open a `Hooks` table with `hooks need review` and `Press t to trust all` immediately after startup. The quota probe presses guarded `<Esc>` to close this dialog and continue without changing trust. An operator can press `t` once in an interactive Codex session to trust the hooks, or leave them untrusted. Codex also provides `codex exec --dangerously-bypass-hook-trust`, but Studio does not use that bypass.
3. **`/status` PTY probe is fragile.** Trust + startup gates + welcome + `/status` is a chained multi-step probe; one extra prompt or layout shift breaks it. See comments in [`CodexQuotaProbe`](../../../../backend/Features/Cli/Quota/CodexQuotaProbe.cs). When updating, capture the new PTY transcript under `backend.Tests/Fixtures/quota/codex/` and lock with a fixture-based test.
4. **Codex reports % left, we report % used.** The probe inverts the value so the UI's `UsedPct` semantics stay consistent across CLIs. Don't double-invert.
5. **`--json` is required.** Without it, stdout is a colored panel that can't be parsed. The runner always passes it.

## Watchdog parity with Claude (ADR-0030)

The watchdog tunings shipped for Claude in ADR-0030 are CLI-agnostic and apply unchanged here:

- **`SessionInitializing` budget is 60 s suspicious / 120 s hung.** Codex's first frame (`{"type":"session_configured", …}`) sometimes lags 30-50 s behind spawn under API load; the old 30 / 60 budget killed those legitimately-slow inits.
- **`Unknown` frames count as activity.** A future Codex `--json` frame variant that the adapter does not yet classify still resets the silence clock; the unknown-sample is captured for diagnosis.
- **Reasoning items are a liveness ping (ASS-1671).** Codex at high reasoning effort (notably `xhigh`) thinks silently for minutes before its first turn frame, staying in `PromptConsumed` with no `OutputDelta`. `CodexEventAdapter` maps `item.started` / `item.completed` with `item.type=reasoning` to `CliRunEvent.Heartbeat` (an `IsActivitySignal`), so each reasoning frame resets the silence clock instead of being logged as a phantom `reasoning` tool call or mis-advancing the phase to `ToolExecuting`. The wide `PromptConsumed` budget (300 s / 1200 s) remains as the backstop that still kills a pre-turn run emitting *no* frames at all. The marker-line `CodexOutputRenderer` independently suppresses reasoning from the visible Activity Log (the watchdog side and the render side are separate).
- **Loud-failure routing on N same-job kills.** When the same Codex job fails three runs in a row, the runner moves it to `5-human-review` (instead of leaving it stuck in `3-progress` while auto-mode flips to `manual`).
- **`logs/tool-calls.jsonl`** is written for any CLI driver — Codex's `tool_use` events flow through `CodexEventAdapter.MapLineToRunEvents` to `CliRunEvent.ToolStarted` / `ToolCompleted` and land in the same per-job JSONL the operator playbook for Claude references.

Codex-specific differences worth flagging during a hang:

- Codex `--json` frames have two parallel surfaces: the typed-event `CodexEventAdapter` (what the watchdog reads) and the marker-line `CodexOutputRenderer` (what the Activity Log reads). They are independent pure mappers over the same frames; patching one does not touch the other. If you patch frame parsing, run the deterministic suite in [`backend.Tests/CliWatchdogIntegrationTests.cs`](../../../../backend.Tests/CliWatchdogIntegrationTests.cs), the typed-event tests in [`backend.Tests/CodexEventAdapterTests.cs`](../../../../backend.Tests/CodexEventAdapterTests.cs), and the marker-line tests in [`backend.Tests/CodexOutputRendererTests.cs`](../../../../backend.Tests/CodexOutputRendererTests.cs).
- Codex does not emit Claude's `rate_limit_event` shape; the rate-limit-aware budget multiplier (an ADR-0030 follow-up) will need its own probe before it can flip on for Codex runs.
- Codex has no equivalent of Claude's `~/.claude/projects/<cwd>/<uuid>.jsonl` side-channel session file. The heartbeat helper documented for Claude does not have a Codex analogue today; the same pipe-buffer hypothesis would need a different signal (e.g. polling `codex` IPC or a process-level CPU heartbeat).

## Silent-completion detector (Codex-only)

Codex sometimes stops emitting frames after a successful `item.completed`
(`type=command_execution`, `exit_code=0`) without producing a closing
`turn.completed` or a terminal sentinel. The process stays alive and
stdout-silent; the watchdog would eventually kill it as a hard failure even
though the on-disk work is real. The detector recognises that shape
explicitly so the run finalizes as a graceful `Completed` instead.

**Trigger contract** (pure function in
[`src/AgentTaskboard.Runner/Cli/CodexSilentCompletionDetector.cs`](../../../src/AgentTaskboard.Runner/Cli/CodexSilentCompletionDetector.cs)):

- CLI type is `codex`.
- Phase is one of `TurnInProgress`, `OutputDelta`, `ToolExecuting`.
- The last observed `item.completed` had a nested `item.type=command_execution`
  with `exit_code=0` (parsed by `CodexCliService.TryExtractCommandExecution`).
- Silence since that frame is >= `DefaultSilenceSeconds` (60 s).
- The per-run `SilentCompletionTripped` latch on `ProcInfo` is not set.

**Per-tick wiring**
([`ProjectRunner.TickSilentCompletion`](../../../backend/Services/Runner/ProjectRunner.cs)
runs BEFORE `TickWatchdog` so it always wins the race when the shape matches):

1. Writes the synthetic `[codex-silent-completion] <diagnosis>` system line
   to the run's output buffer + persisted log (the canonical text the
   `status.md` regenerator picks up).
2. Posts a typed chat note via `OrchestratorMessageKind.SilentCompletion`
   so the activity log shows why the run was finalized early.
3. Emits a `kind:observation severity:Warn topic:codex-silent-completion`
   bus event via
   [`AgentMessageBusBridge.EmitCodexSilentCompletionAsync`](../../../backend/Services/Bus/AgentMessageBusBridge.cs)
   carrying the last command + output tail.
4. Stamps the `outcome-silent-finish` tag on the job via
   `TaskMutationService.AddJobTag` (seeded with friendly label
   "Outcome: silent finish" in `TagRegistryService`).
5. Asks the CLI base class to `TripSilentCompletion(jobKey, diagnosis)`,
   which sets the latch and calls `Stop(jobKey, RunStopReason.SilentCompletion)`.

**Why this finalizes as Completed.** `RunStopReason.SilentCompletion` is a
typed "stop reason that maps to Completed", same shape as
`SentinelDetected`. `RunStatusClassifier.Classify` returns
`RunStatuses.Completed`, the standard `OnCliFinishedAsync` path runs the
analyzer, and `AgentOutcomeAnalyzer` recognises the
`[codex-silent-completion]` marker (gated like the
`[environment-blocker]` marker) and surfaces
`RunIssueKind.SilentCompletion` with `AgentOutcomeKind.Done` +
`MatchedSentinel=false`. `RunOutcomePolicy.Decide` routes that to
`OutcomeActionKind.NotifyUserAndAccept`, so the run moves to
`4-auto-review` and aspect calls run normally - the chat just sees a
typed note distinguishing "agent finished and signed off" from "agent
likely finished but never said so".

**Tag and bus contract**

| Surface | Stable id |
|---|---|
| Tag (persisted in `job.json`) | `outcome-silent-finish` |
| Bus topic | `codex-silent-completion` |
| Bus kind / severity | `observation` / `Warn` |
| Synthetic marker line prefix | `[codex-silent-completion] ` |
| Chat message kind | `OrchestratorMessageKind.SilentCompletion` |
| RunStopReason value | `SilentCompletion` (maps to `Completed`) |

**Calibration knobs.** Override the silence threshold in tests via
`CodexSilentCompletionDetector.Decide(inputs, silenceThresholdSeconds: ...)`.
There is no per-project config knob today; if you need to widen / tighten
the default, edit `DefaultSilenceSeconds`. Other CLIs (Claude, Gemini)
have different completion contracts and are deliberately excluded from
this detector.

**Tests.** The pure detector + capture + analyzer + policy paths are
locked by:

- [`backend.Tests/CodexSilentCompletionDetectorTests.cs`](../../../../backend.Tests/CodexSilentCompletionDetectorTests.cs) (25 cases including the canonical 60 s boundary).
- `AgentOutcomeAnalyzerTests.SilentCompletionMarker_Output_IsClassifiedAsSilentCompletion`.
- `RunStatusClassifierTests.SilentCompletion_AnyExitCode_IsCompleted` (5 cases).
- `RunOutcomePolicyTests.SilentCompletion_NotifiesUserAndAccepts_RoutesThroughAutoReview`.
- `CodexCliServiceTests.TryExtractCommandExecution_*` (canonical Codex 0.128 frame shape, truncation, malformed-frame safety).

## Quota probe

[`CodexQuotaProbe`](../../../../backend/Features/Cli/Quota/CodexQuotaProbe.cs) reads the standard and Spark quota windows that the current `/status` panel exposes. Implementation runs `codex` over a PTY, passes guarded startup gates, navigates to `/status`, and scrapes the panel.

The probe reports `% used` (1 - `% left`). Source string is `/status (PTY)`.

**Versioned status fixtures and startup gates (AGT-2679).** Codex 0.149.0
introduced an update-choice dialog before the ready prompt and moved reset text
onto a separate visual row. The quota driver only sends trust confirmation when
the trust prompt matched, selects "Skip until next version" when the update
dialog appears, and then submits `/status`. Parser fixtures live under
`backend.Tests/Fixtures/quota/codex/` with the CLI version in each filename.
Keep an older fixture when adding a new layout so compatibility remains an
explicit test contract. A missing standard 5-hour row is valid; Spark rows must
never be promoted into that missing standard bucket.

Codex 0.151.0 can also interpose hook-trust review after startup when enabled
plugin hooks have not been trusted. The probe closes that dialog with a guarded
`<Esc>` and emits an operator-facing warning; it never presses `t` or makes the
trust decision. If the final snapshot is still the hooks table rather than the
status panel, the probe reports a hook-review-specific error so stale quota data
is attributable.

**Stale-while-revalidate failure contract (AGT-2679).** `GET /api/cli/quota`
serves the cache immediately and schedules a coalesced, bounded background
probe. A failed probe does not replace the last good plan or windows. The
snapshot retains their original `fetchedAt` and adds `probeFailedAt`,
`cliVersion`, and a normalized `error`. The UI marks those values stale and
puts the probe error in the marker tooltip. Startup and periodic version checks
compare Claude and Codex against the disk-cached baseline and log
`CLI version changed` with the previous version, current version, and source.

**Spark-block split is version-agnostic (AGT-2064).** `/status` renders a
`<model>-Spark limit:` sub-block with its own near-empty 5h/Weekly lines. The
standard windows are read only from the region ABOVE that header, so the Spark
lines can never be mistaken for the main windows. The header regex matches
`Spark limit` alone — do **not** re-pin it to a model string
(`GPT-5.3-Codex-Spark`): when the Spark model bumped (5.3 → 5.6) that pinning
collapsed the split and reported an exhausted account as `5-hour: 4% |
Weekly: 1%`. `ParseStatusWindows_SparkOnlyWithBumpedHeader_DoesNotMasqueradeAsMainWindows`
locks this.

**Snapshots are guarded before they reach admission (AGT-2064).**
`QuotaService` runs every probe through `QuotaPlausibilityGate`: any Weekly
decrease before the previously announced reset, or another window that drops
more than 50 points with no reset to explain it, is re-probed immediately and
only replaces the old value when a second probe agrees. A candidate cannot
justify its own Weekly decrease merely by advertising a later reset boundary.
An unconfirmed drop keeps the prior (blocking) value flagged `Suspicious`, and
`CliQuotaCapsService.Evaluate` blocks any `Suspicious` snapshot regardless of
how green it reads. A launch that
dies with a usage-limit error invalidates the cached snapshot immediately
(`QuotaService.InvalidateForGroundTruthLimit`, wired from
`ProjectRunner.OnCliFinishedAsync` on `RunIssueKind.QuotaExhausted`) and
re-probes now rather than waiting out the TTL.

## Common tasks

### "Add a marker mapping for a new Codex frame / item type"

The `TransformReadLine` translation now exists in
[`CodexOutputRenderer`](../../../src/AgentTaskboard.Runner/Cli/Rendering/CodexOutputRenderer.cs).
To extend it for a new frame or `item.type`:

1. Capture the real `--json` frame from `.runtime/cli-output/codex-*.jsonl`.
2. Add a `case` to `CodexOutputRenderer.Render` (top-level frame) or `RenderItem`
   (item type), mapping to an existing marker in `cli-overview` § "Marker-line
   vocabulary". Do not invent a new marker shape — pick `read`/`search`/`command`/`edit`.
3. Add a snapshot test to `backend.Tests/CodexOutputRendererTests.cs` (frame in → marker out).
4. Session capture stays in `MapLineToRunEvents` on the raw JSON — never move it onto
   the rendered marker line.

### "Codex isn't resuming"

1. Verify the persisted `sessionName` in `job.json` is a UUID.
2. Verify the `exec resume <uuid> --json -` argument order (prompt arrives over stdin).
3. Verify the captured UUID matches what's on disk in `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`.

### "Live model discovery returned an empty list"

1. Check `CodexModelDiscoveryTests.cs` for the latest expected output format.
2. Run `codex debug models` by hand to capture the current output (this is the exact command `CodexModelDiscovery` spawns).
3. Update `ParseDebugModelsJson`; lock with a new fixture row.

### "Add a regression test for a new frame shape"

1. Capture the raw JSON from `~/.runtime/cli-output/codex-*.jsonl`.
2. Scrub and save the canonical provider capture under
   `testdata/cli-fixtures/streams/codex/<exact-version>/<name>.codex.fixture`.
3. Add Agent Host replay assertions for derived events and outcomes. Add a
   `CodexCliServiceTests` renderer snapshot only when marker rendering changes.
4. Update the shared
   [frame compatibility matrix](../frame-compatibility-matrix.md).

## Fixtures

`testdata/cli-fixtures/streams/codex/<exact-version>/` holds the canonical
`--json` captures and replay metadata. Keep one fixture per concern.
`backend.Tests/Fixtures/cli/codex/` is limited to renderer-specific snapshots
that cannot consume the shared fixture directly, plus the trimmed
`codex debug models` catalogs used by `CodexModelDiscoveryTests`
(`debug-models-v0.153.4.json` with `gpt-6-astra`, `debug-models-v0.151.0.json`
without it). Keep the reasoning-ladder fields (`supported_reasoning_levels`,
`default_reasoning_level`) in any new catalog fixture: they are what the parser
reads.
