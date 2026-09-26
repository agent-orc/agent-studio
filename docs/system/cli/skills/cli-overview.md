---
name: cli-overview
description: Cross-cutting reference for the Claude, Codex, and Antigravity integrations. Use it with the per-CLI skills when changing backend/Features/Cli, the CAR host bridge, quota probes, the Activity Log parser, or any consumer of CLI output. Covers the single CAR execution layer, callback ordering, session capture, durable output, and host-owned lifecycle policy.
sentinel: TASKBOARD-CLI-SKILL-OVERVIEW-2026
---

<!-- SENTINEL: TASKBOARD-CLI-SKILL-OVERVIEW-2026 - pickup-tests assert any CLI driving the repo can echo this back. -->

# CLI integrations: cross-cutting reference

This project drives **three** coding-agent integrations from one backend: Claude Code, OpenAI Codex, and Antigravity through `agentapi`. Antigravity retains the persisted CLI type `gemini` for compatibility. All card runs use CodingAgentRunner and share Studio lifecycle and output contracts.

> **GitHub Copilot was removed.** Its driver (`CopilotCliService`) predated the shared base class and couldn't share the hardened spawn/stream path cleanly. References below to slug session ids survive only as the reason `IsCompatibleSessionName` still rejects them.

> **Authoritative contract:** [docs/system/cli/supported-clis.md](../supported-clis.md) is the formal "what every supported CLI must provide" document. It is updated in the same PR that touches a CLI integration. This skill complements it with operational/working knowledge.

## When to use which skill

| You are working on … | Read first |
|---|---|
| Claude behavior, stream-json framing, session capture, rate-limit pill | `cli-claude` |
| Codex behavior, JSON events, `thread.started`, and `codex exec` | `cli-codex` |
| Antigravity `agentapi` protocol, stored under CLI type `gemini` | `cli-gemini` until that skill is renamed |
| Engine rollout or the Studio-to-CAR bridge | This skill and [`BackendCarExecution.cs`](../../../../backend/Features/Cli/Execution/BackendCarExecution.cs) |
| Adding a new CLI | This skill + [docs/system/cli/supported-clis.md](../supported-clis.md) §4 checklist |
| Activity-log marker parser, conversation rendering | This skill (§ "Marker-line vocabulary") + the per-CLI `TransformReadLine` notes |
| Resume / continuation logic across CLIs | This skill (§ "Session model invariants") + `cli-claude` (UUID) |

## Architecture at a glance

```text
ProjectRunner
  |
  +-- GenericCliExecutionService
        |
        +-- CAR ICliDriver: descriptor, argv, process lifecycle, typed events
        +-- CarCallbackBridge: raw metadata first, matching typed events second

CAR -> Studio output mirror -> marker renderer -> SignalR -> Activity Log
    -> Studio session, quota, usage, ledger, sentinel and reaper policy
```

[`GenericCliExecutionService`](../../../../backend/Features/Cli/Execution/CliExecutionServiceBase.cs) is the shared Studio host adapter. It always creates a typed CAR request. Engine rollout settings and the raw-spawn rollback were removed in AGT-2373.

## Session model invariants

Every current integration uses a UUID session id. The removed Copilot driver used a slug; compatibility checks still reject that old shape so it cannot be passed to a UUID-based CLI.

| CLI | Format | How we capture it | Resume flag |
|---|---|---|---|
| Claude | UUID | Capture the rendered session marker from the CAR raw-output path, with a narrow early-frame UUID fallback. | CAR `ResumeSessionId`, rendered as `-r <uuid>` by the descriptor |
| Codex | UUID | Parse `thread_id` from raw `thread.started`; legacy `session_meta.payload.id` is also accepted. | CAR `ResumeSessionId`, rendered as `exec resume <uuid>` |
| Antigravity, stored as `gemini` | UUID conversation id | Render the `agentapi` conversation id to `● Session init <uuid>` and capture it from that marker. | `agentapi send-message <uuid> <prompt>` |

**Hard invariant:** `IsCompatibleSessionName` must reject incompatible identifier shapes, and cross-CLI routing must always drop the prior session id. A valid UUID does not encode which provider owns it, so shape validation alone cannot make a cross-CLI resume safe. The task-runner plan tests lock this behavior.

**Two capture strategies are valid.** Claude and Antigravity capture the rendered session marker. Codex captures the raw `thread.started` frame before rendering because that is also where its usage and command metadata are parsed.

Either strategy is valid. Do not capture the same identifier from both hooks. Keep marker regexes narrow, and use the raw hook when rendering drops required fields.

### Stale-session invariants

A stale session is not the same thing as a missing session. Missing sessions reject the resume target and route through Recovery. Stale sessions still resume, but the agent may have lost useful context, older reasoning, cached prompt state, or alignment with the job's current disk evidence.

The [April 23, 2026 Anthropic Claude Code postmortem](https://www.anthropic.com/engineering/april-23-postmortem) is the cautionary example: a product-layer optimization for sessions idle over one hour accidentally kept pruning older thinking on every later turn. The symptom looked like worse model quality, but the root cause was harness/session management.

For this project, the invariant is: **a successful resume is not proof of a successful continuation.** A continuation is healthy only if the agent acts on the latest user follow-up, reconciles with the job folder, and produces useful new evidence or a clear blocker. Pin that behavior in `RunPlanner`, `RunOutcomePolicy`, and session-event tests before touching per-CLI drivers.

Claude and Codex are the reference paths for stale-session work. Antigravity inherits the general recovery contract through its legacy adapter.

## Marker-line vocabulary

The frontend's [`activity-log.parser`](../../../../frontend/src/app/features/task-detail/components/activity-log.parser.ts) classifies output lines based on a leading-marker convention. Renderers must emit lines that match this vocabulary, or they fall into the `message` / `other` bucket.

| Marker | Kind | Example |
|---|---|---|
| `● Read <path>` | `read` | `● Read /repo/src/foo.ts` |
| `● Write <path>` | `edit` | `● Write /repo/src/foo.ts` |
| `● Edit <path>` | `edit` | `● Edit /repo/src/foo.ts` |
| `● Search <pattern>` | `search` | `● Search "TODO"` |
| `● Search glob <pattern>` | `search` | `● Search glob src/**/*.ts` |
| `● Search web <query>` | `search` | `● Search web claude api docs` |
| `● Run <command>` | `command` | `● Run npm test` |
| `● Fetch <url>` | `command` | `● Fetch https://example.com` |
| `● Todo update` | `todo` | (used by Claude's `TodoWrite` tool) |
| `● Task <description>` | `task` | (used by Claude's `Task` tool) |
| `● Session init <uuid> (<model>)` | `message` | Claude or Antigravity session frame; captured via a narrow regex. Keep the shape stable. |
| `● Session <uuid>` | `message` | Codex session frame (`thread.started`). Captured from the raw line, not this marker. |
| `● Result <subtype>` | `message` | Claude final-result frame. |
| `● Turn completed (tokens: N)` | `message` | Codex `turn.completed`; analogue of Claude's `● Result (success)`. |
| `● Turn failed: <reason>` | `message` | Codex `turn.failed`; emitted on `stderr`. |
| `● Rate limit ... [window=... status=... resetsAt=... overage=... usingOverage=...]` | `message` | Anthropic only. The bracketed key-value tail is parsed into a typed `ClaudeRateLimitSnapshot` for the UI pill. |

Continuation lines (`  | <details>` or anything starting with whitespace) attach to the preceding marker as a subtitle / extra body line.

**Don't invent a new marker shape.** If you need a new tool kind, decide which existing kind it falls into (`read` / `search` / `command` / `edit`) and emit one of the markers above. Adding a parser branch ties the parser to a specific CLI.

## Unified renderer layer

The marker lines above are produced by `ICliOutputRenderer`, the marker-line twin of the typed-event adapters. Both are pure mappers over one CLI frame. CAR emits `CliRunEvent` instances for Claude and Codex; Studio renderers emit `CliOutputLine` marker lines for the Activity Log.

```text
CAR callback order for one stdout frame:

typed events arrive -> CarCallbackBridge buffers them
raw line arrives     -> persist -> capture usage/session -> render markers
                    -> publish the matching typed event batch
```

The ordering bridge is required because the token and cost ledger handles `TurnCompleted` synchronously and immediately reads the usage captured from the raw frame. Publishing CAR's typed event first would produce a missing or stale ledger entry.

Files live in [`backend/Features/Cli/Execution/Rendering`](../../../../backend/Features/Cli/Execution/Rendering):

- `ICliOutputRenderer` returns marker lines and has no side effects.
- `CliMarkerFormat` contains shared stateless formatting primitives.
- `ClaudeOutputRenderer` and `CodexOutputRenderer` implement the CAR-backed renderers.

The CLI frame catalogues diverge enough that a shared base switch would be a leaky abstraction. Pure renderers are easy to fixture-test and keep rendering separate from process orchestration. Shared formatting belongs in `CliMarkerFormat`, not in inheritance.

### How a new CLI adapter plugs in

1. Implement `ICliOutputRenderer` for the new CLI under `Cli/Rendering/`, mapping each
   frame to an existing marker in the vocabulary table above. Reuse `CliMarkerFormat`
   for splitting/trimming; **don't** invent new marker shapes.
2. Wire it through the CLI's `CliBehavior.TransformReadLine` delegate.
3. Capture session or usage data from either the raw hook or the rendered-line hook, never both.
4. Add a `<Cli>OutputRendererTests.cs` fixture for each frame type and encoding edge case.

> Antigravity's legacy `agentapi` behavior still carries an inline renderer. Moving it to `ICliOutputRenderer` is independent of whether its process protocol can move to CAR.

## Output stream conventions

| Stream | Comes from | Frontend treatment |
|---|---|---|
| `stdout` | Process stdout (whatever the CLI writes) | Default. Markers + agent text. |
| `stderr` | Process stderr | Rendered as ERR / `error` kind. |
| `system` | Synthesized by the runner (`Started <cli>`, `<cli> exited`) | Rendered as SYS. |
| `user` | Synthesized when the user types a follow-up in the chat box (see `TaskRunnerService.AppendUserPromptToCliLog`) | Rendered as YOU; never folded into a tool group. |

`system` and `user` are synthesized by Studio. CLI renderers produce only `stdout` and `stderr`. Persisted lines are ANSI-cleaned; downstream consumers should still tolerate ANSI in transient input.

## Lifecycle invariants

1. **CAR is the only card-run execution layer.** Backend and Runner do not expose an engine selector or raw-spawn rollback.
2. **CAR owns launch mechanics.** Descriptor argv, permission flags, common thinking normalization, UTF-8 environment, process lifecycle, process-tree stop, typed events, and Claude npm-shim healing stay in the library.
3. **Claude prompts use CAR-A stdin.** CAR writes and closes the one-shot prompt stream before the turn. The prompt is not an argv value, and the deterministic gate covers at least 200 KiB.
4. **Raw metadata precedes matching typed events.** `CarCallbackBridge` buffers CAR events until Studio has persisted and parsed the corresponding raw line. This is mandatory for the synchronous usage ledger.
5. **Persist before notifying.** Studio writes the raw line to its per-stream JSONL mirror before invoking UI subscribers. Subscriber exceptions are isolated.
6. **Keep synthetic start and exit lines.** They make process state visible before the first provider frame and at terminal classification.
7. **Backend restart reaps; it does not reattach.** `ReattachOnStartup` validates persisted PID, process name, and start time, kills the leftover process tree, and clears the active-job record. The run is recovered by demotion or reissue. CAR cannot recover lost pipes after its host process exits.
8. **Clean context is task-stable in Studio.** Studio reuses one linked clean home across attempts, passes CAR `ContextMode=shared`, and injects `CLAUDE_CONFIG_DIR` or `CODEX_HOME`. CAR 0.7.0 must not create a second process-scoped home.
9. **Steer is a resumed run.** Product steer records the user input and starts the bounded continuation path. It does not depend on reattaching stdin to a process after backend restart.
10. **The output buffer cap is 5000 lines.** Longer history is read from the durable per-stream mirror.

## CAR 0.7.0 public API boundaries

The bespoke Studio `WindowsHandleScrubSpawner` and legacy card-run healer call
were removed. CAR owns npm healing for agent runs. Studio's `NpmShimHealer`
remains only for the bounded non-card `ClaudeOneShot` operator utility. Studio
uses the public `ICliProcessSpawner` seam for host bookkeeping and the Claude
rules-file overlay. Do not copy CAR's internal Windows helpers into the backend.

Four PROJ-011 cards track the seams still needed for a cleaner integration:

- `public-clean-context-lease`: let a host supply a clean-context lifetime longer than one process.
- `public-hardened-spawner-composition`: expose a supported way to decorate CAR's hardened default spawner, including its internal Windows handle scrubber.
- `public-cli-launch-overlay`: add host-owned argv or descriptor overlays without decorating the final spawn request.
- `public-pre-spawn-health`: expose the driver's pre-spawn health and repair operation to temporary non-driver host paths.

## Quota probes

Each CLI has a `QuotaProbeBase` subclass under `backend/Features/Cli/Quota/`. Claude and Codex probes run in scratch directories under `%TEMP%/agent-taskboard-quota/<cliType>`. Antigravity has no local numeric quota surface and reports that the IDE session owns quota information. The aggregate is served by `/api/cli/quota`.

**Common pitfalls when adding/changing a probe:**
- Never run a probe in the user's working directory. The CLI may pollute `~/.<cli>/sessions/` with junk runs and the disk-based session listing in `SessionRegistry` would surface them.
- Send `<Esc><Esc>` before tearing down a PTY-driven probe to close any open modal pickers.
- Probes refresh in the background; the user can force-refresh per-CLI via the side-sheet button.

## Common tasks

| Task | Where to start | Tests to add |
|---|---|---|
| Add a new tool marker for Claude or Codex | `backend/Features/Cli/Execution/Rendering/` | A fixture in the matching `*OutputRendererTests.cs` |
| Add a new model to the catalog | The live discovery service or `CliBehavior.GetModelCatalog` | Model-catalog or discovery tests |
| Make resume work across a new edge case | `RunPlanner.PlanRun`, then the CAR request or explicit legacy behavior | A new row in the task-runner plan matrix plus a CAR bridge contract test |
| Capture new telemetry | `CliBehavior.CaptureRawLine`; verify the CAR event-order bridge | A raw-frame fixture and ledger assertion |
| Add a brand-new CLI | [docs/system/cli/supported-clis.md](../supported-clis.md) §4 checklist | New `<Cli>CliServiceTests.cs` + new `@billable` E2E `frontend/e2e/<cli>-hello-world.spec.ts` |
| Fix a session-capture regression | Check whether that CLI owns capture in the raw or rendered hook and confirm the CAR bridge order. | Add a fixture-based regression test |

## Where state lives on disk

- **Studio runtime output mirror:** `<TaskRepository>/.runtime/cli-output/<cliType>-<jobKey>/<stream>.jsonl`
- **Temporary CAR log mirror:** `<TaskRepository>/.runtime/car-cli-output/...` (deleted when the CAR-backed run finishes)
- **Active jobs (for orphan reaper):** `<TaskRepository>/.runtime/active-jobs-<cliType>.json`
- **Task-stable clean home:** a marker-validated directory referenced by `CLAUDE_CONFIG_DIR` or `CODEX_HOME`, stored under the platform state root and retained across attempts and host restarts until bounded inactivity cleanup
- **Session indexes (each CLI's own store):**
  - Claude: `~/.claude/projects/<encoded-cwd>/<uuid>.jsonl`
  - Codex: `~/.codex/session_index.jsonl` + `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`
  - Antigravity, stored as `gemini`: legacy session discovery under `~/.gemini/tmp/...` where available
- **Quota scratch:** `%TEMP%/agent-taskboard-quota/<cliType>/...` (PTY scratch dirs)

`SessionRegistry` reads each store and serves `/api/cli/usage`. Adding a CLI means adding a `BuildXxxProjects()` method here.

## Test-fixture story

`testdata/cli-fixtures/streams/<cliType>/<cliVersion>/` is the shared, versioned
provider capture corpus. Agent Host tests replay it for events and terminal
outcomes; CAC uses the same provider bytes for rendering. Backend-only renderer
snapshots may still live under `backend.Tests/Fixtures/cli/<cliType>/`, but they
must not duplicate the canonical provider capture.

When you observe a new CLI behaviour (new frame shape, new error mode, new tool
name), capture and scrub the real stream under its exact CLI version and add a
regression test in the same PR. Update the
[frame compatibility matrix](../frame-compatibility-matrix.md) and its CAC
rendering counterpart against the same fixture identity.

## Things that are not in scope here

- Branch orchestration / multi-agent / per-task workspaces — see [AGENTS.md](../../../../AGENTS.md) "Out of scope". The CLI drivers run **one** process per project at a time.
- Login flows. The task processor never drives auth; the user logs the CLI in out-of-band, the runner just spawns it.
- Stream-format wars. We translate every CLI's output to the marker-line vocabulary; we do not push back upstream to align CLI output formats.
