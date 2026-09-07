---
name: cli-claude
description: Deep operational reference for the Anthropic Claude Code integration. Use when touching the Studio Claude behavior, the CAR host bridge, stream-json rendering, session capture, the rate-limit pill, ClaudeQuotaProbe, or any consumer of Claude output. Covers invocation, frames, session UUIDs, model normalization, npm-shim incidents, and common tasks. Pair with cli-overview for the cross-CLI CAR contract.
sentinel: TASKBOARD-CLI-SKILL-CLAUDE-2026
---

<!-- SENTINEL: TASKBOARD-CLI-SKILL-CLAUDE-2026 — pickup-tests assert any CLI driving the repo can echo this back. -->

# Claude Code CLI (`claude`)

Anthropic's official Claude Code CLI. Distributed as the npm package `@anthropic-ai/claude-code`. The most feature-rich of our four CLIs and the one we lean on most.

> **Source:** [`BuiltInCliBehaviors.cs`](../../../../backend/Features/Cli/Execution/BuiltInCliBehaviors.cs) for Studio behavior and [`BackendCarExecution.cs`](../../../../backend/Features/Cli/Execution/BackendCarExecution.cs) for the CAR bridge.
> **Tests:** [`backend.Tests/ClaudeQuotaProbeTests.cs`](../../../../backend.Tests/ClaudeQuotaProbeTests.cs) (probe). Driver-level `TransformReadLine` tests live alongside fixtures under `backend.Tests/Fixtures/cli/claude/` (see § "Fixtures").
> **Contract:** [docs/system/cli/supported-clis.md §3.1](../supported-clis.md).

## Commit / push boundary

| Question | Answer |
|---|---|
| Does this CLI commit on its own? | **No.** The platform owns the commit boundary. |
| Does this CLI push on its own? | **No.** Push is the runner's job (today: a tracked gap). |
| What if it does? | Regression. Raise an issue and cite [docs/operations/git/commit-push-doctrine.md](../../../operations/git/commit-push-doctrine.md). |

Claude Code is the most agentic of the four and *will* run `git status` / `git diff` to inspect the working tree under `--dangerously-skip-permissions`. That is fine. What it must never do: `git commit`, `git push`, `git amend`, `git checkout`, `git reset --hard`, or any branch-mutating command. The runner records the commit on the `3-progress -> 4-review` transition; see [docs/operations/git/commit-push-doctrine.md](../../../operations/git/commit-push-doctrine.md) and [ADR-0019](../../architecture/decisions/adr-archive.md#adr-0019---platform-owns-the-commit-boundary-2026-05-04).

## Identity card

| | Value |
|---|---|
| Binary | `claude` (npm-installed). On Windows the shim resolves to `node_modules/@anthropic-ai/claude-code/bin/claude.exe` via `PATHEXT`. |
| Config key | `ClaudeCli:Path` (override) |
| Version probe | `claude --version` |
| Output mode used | `stream-json` (NDJSON, one frame per line) |
| Session ids | UUID (`8-4-4-4-12`) — strict |
| Resume flag | `-r <uuid>` |
| Rate limit telemetry | `rate_limit_event` frames per turn (Claude-only) |
| Session storage | `~/.claude/projects/<encoded-cwd>/<uuid>.jsonl` |
| Quota probe | `/usage` PTY probe — session bucket + weekly bucket |

## Invocation reference

### Fresh run

```sh
printf '%s' "<prompt>" | claude -p \
  --output-format stream-json --verbose --dangerously-skip-permissions \
  [--model <id>] \
  [--append-system-prompt-file <path>]
```

`-p` is the headless / "print" mode; output goes to stdout, the process exits when the model is done. The CAR-backed default writes the one-shot prompt to stdin and closes it immediately. The legacy rollback passes the prompt as the final argv value. `--verbose` is *required* alongside `stream-json` because Claude's CLI rejects the combo without it. `--dangerously-skip-permissions` auto-approves tool calls, which is required for unattended runs.

`--append-system-prompt-file <path>` injects [`agent-rules/core.md`](../../../../agent-rules/core.md) as a system-prompt overlay. It's a *file* flag (not the inline string flag) so the multi-line markdown stays out of the command-line argument and lets the Anthropic CLI cache the system-prompt portion across runs. We resolve the path via `ResolveAgentRulesPath` (looks at config + walks up from `AppContext.BaseDirectory`); files larger than 8 KB are skipped with a warning to avoid blowing the system-prompt cache.

### Resume

```sh
printf '%s' "<prompt>" | claude -r <session-uuid> -p \
  --output-format stream-json --verbose --dangerously-skip-permissions \
  [--model <id>] \
  [--append-system-prompt-file <path>]
```

`-r` accepts only the canonical UUID written by Claude's CLI itself. There is no equivalent of `--name` in current versions — sessions are identified by the UUID emitted in the first `system` `stream-json` frame.

The runner never pre-generates a session id for Claude. On a fresh run we pass no `-r`, then capture the UUID from the first frame and write it back to `job.json` so the next continuation can resume.

### Anti-patterns

- **Don't** pass `-r <slug>`. Claude's `-r` doesn't error — it hangs waiting for a session that doesn't exist. `IsCompatibleSessionName` rejects everything that isn't the canonical UUID precisely because of this.
- **Don't** combine `-p` with no `--output-format`. The default text format buffers the full reply until the model finishes, so the Activity Log stays empty for 30+ s on long replies. `stream-json` flushes per frame.
- **Don't** drop `--verbose`. Without it, Claude's CLI silently exits with status 1 when `-p` is combined with `stream-json`.

## Stream-json frame catalogue

Each line of stdout is a JSON object. The frame switch lives in the pure
[`ClaudeOutputRenderer`](../../../../backend/Features/Cli/Execution/Rendering/ClaudeOutputRenderer.cs)
(`ICliOutputRenderer`); the Claude behavior's `TransformReadLine` hook is a thin delegate
to it (see `cli-overview` § "Unified renderer layer"). The shapes below are
verified against the live CLI; when adding a new branch, capture a fixture under
`backend.Tests/Fixtures/cli/claude/` and lock it with a test.

| `type` | Purpose | Renders to | Action |
|---|---|---|---|
| `system` | Init / health frame. Carries `session_id` and `subtype` (`init` etc.). | `● Session <subtype> <session_id>` | Read `session_id` back via `SessionMarkerRegex` in `OnOutputLine` to capture the UUID. |
| `assistant` | Assistant turn. `message.content` is an array of parts. | One marker line or text line per part. | `text` parts split on newlines, emitted as plain text. `tool_use` parts go through `FormatToolUse` and become marker lines. `thinking` parts are silently dropped (extended-thinking is not user-actionable; could be exposed behind a debug flag later). |
| `user` | Tool result. `message.content[].type == "tool_result"` carries the tool output. | Indented continuation `  <first-line-trimmed-to-200-chars>` under the preceding marker. | `is_error: true` marks the line as `stderr`. |
| `result` | Final-result frame at end of run. Carries `subtype` (e.g. `success`), optional `result` text, `is_error`. | `● Result (<subtype>)` or the raw `result` text. | Errors flip stream to `stderr`. |
| `rate_limit_event` | Per-turn rate-limit telemetry. Carries `rate_limit_info: { rateLimitType, status, resetsAt, overageStatus, isUsingOverage }`. | Two-part marker: `● Rate limit · <window> · <status> · reset in <human>  [window=… status=… resetsAt=… overage=… usingOverage=…]` | The bracketed kv tail is parsed back into a `ClaudeRateLimitSnapshot` for the live header pill. **Keep the kv format stable**, the regex `RateLimitMarkerRegex` reads it back. |
| (other) | Unknown frame type. | `● <type>` (catch-all). | Never leak raw JSON into the activity log — that breaks the marker classifier downstream. New frame types should get an explicit case once we know what they carry. |

### Tool-name → marker mapping

Implemented in `ClaudeOutputRenderer.FormatToolUse`. The mapping is stable; new tools should land here, not in a per-tool branch in the parser.

| Claude tool | Marker line | Activity-log kind |
|---|---|---|
| `Read` | `● Read <file_path>` | `read` |
| `Write` | `● Write <file_path>` | `edit` |
| `Edit` | `● Edit <file_path>` | `edit` |
| `Glob` | `● Search glob <pattern>` | `search` |
| `Grep` | `● Search <pattern>` | `search` |
| `Bash` | `● Run <command (one-line, ≤ 200 chars)>` | `command` |
| `TodoWrite` | `● Todo update` | `todo` |
| `Task` | `● Task <description>` | `task` |
| `WebFetch` | `● Fetch <url>` | `command` |
| `WebSearch` | `● Search web <query>` | `search` |
| `NotebookEdit` | `● Edit notebook <notebook_path>` | `edit` |
| (anything else) | `● <name>` | `other` |

## Session-UUID capture

Capture is **two-step** because `OnOutputLine` runs on the *transformed* line:

1. `ClaudeOutputRenderer` reads the raw `system` frame, pulls `subtype` + `session_id`, and emits `● Session <subtype> <uuid>`.
2. `OnOutputLine` runs the `SessionMarkerRegex` against that marker line, sets `info.CapturedSessionId`, and assigns `info.SessionName` to the same UUID.

The runner picks the captured UUID up in `ProjectRunner.OnCliFinishedAsync` and persists it via `_sessions.AppendSessionToChain`. Without that, every "Continue" would start a fresh session because `info.SessionName` would never advance.

**Anti-pattern:** capturing inside the renderer / `TransformReadLine`. The renderer must stay a pure function over a single line; capture is a side effect on `ProcInfo` and belongs in `OnOutputLine` (Claude/Gemini) or `MapLineToRunEvents` (Codex, which captures from the raw frame). Both hooks live on the driver, never the renderer.

## Session loss is an expected state, not an error

A session UUID we previously captured can disappear between runs. Claude's CLI keeps session JSONL under `~/.claude/projects/<project-slug>/<uuid>.jsonl`; the user can prune the folder, switch machines, hit a CLI upgrade that rotates the slug format, or simply exhaust whatever retention the CLI applies. When that happens, `claude -r <uuid> -p ...` prints

> `No conversation found with session ID: <uuid>`

on **stdout** (not stderr), then exits with a non-zero code and emits a `result` frame with `subtype: "error_during_execution"`. There is no separate "session expired" exit code — the user-visible string is the contract.

The product treats this as part of normal operation, not a failure mode (ADR-0002 / ADR-0006). The hand-off is:

1. The run completes without capturing a new UUID. `OnCliFinishedAsync` notices the gap.
2. If the just-finished plan was a `--resume` attempt, the orchestrator clears `info.SessionName`, calls `MarkSessionChainRecovery`, and writes a `[capture-fail]` decision message into the chat log naming the rejected resume target. This is what makes the orchestrator's promise ("next follow-up will rebuild from disk") true.
3. The next user follow-up enters `RunPlanner.PlanRun` with no resumable session, so the planner returns a Recovery plan (`EventKind: "recovery"`, `RunnerRecoveryContinuation` template). The agent rebuilds context from `prompt.md`, `status.md`, and the cli-output log.
4. `RunOutcomePolicy` re-issues the user's follow-up once if the recovery run no-ops, with a sharper "history is gone, the user request is the only context" framing.

What this means for code that touches the resume path:

- **Don't** treat "No conversation found" as a hard error worth surfacing to the user as a failure — the orchestrator already announces it once via `[capture-fail]`, and the next turn auto-rebuilds. Two error pills for one expected event is just noise.
- **Don't** keep a dead resume target in `info.SessionName`. The first follow-up after a session loss will then re-issue `--resume <dead-uuid>` and fail identically. `ProjectRunner.OnCliFinishedAsync` clears it on capture-fail; preserve that.
- **Do** keep the `[capture-fail]` chat log message — it's the single visible signal in the conversation that the chain broke. Without it the user sees a generic "errored" run and no explanation.

The same shape applies to Codex and Gemini in this codebase (their session ids can also vanish), so the hand-off lives in `ProjectRunner` rather than per-CLI service code.

## Stale sessions are a harness-quality risk

Claude can accept a resume target and still behave badly if the session's useful context has degraded. Treat this as a first-class failure class, distinct from "No conversation found".

Reference incident: Anthropic's [April 23, 2026 postmortem](https://www.anthropic.com/engineering/april-23-postmortem) ("An update on recent Claude Code quality reports") traced a real Claude Code quality regression to three harness changes, not to model degradation. The stale-session lesson is the March 26 change: sessions idle for over one hour were supposed to clear older thinking once to reduce resume latency, but a bug kept clearing older thinking on every later turn. The result was forgetfulness, repetition, odd tool choices, and higher usage. Anthropic also noted that internal evals, unit tests, end-to-end tests, automated verification, and dogfooding missed it because it sat at the intersection of context management, the API, extended thinking, and stale sessions.

Operational consequences for Agent Software Studio:

- A healthy Claude continuation is not "process exited zero" and not "resume id accepted". It must act on the latest user follow-up and reconcile against `prompt.md`, `prompt-N.md`, `status.md`, and `logs/cli-output.log`.
- Stale-session probes need idle-age variation. Test fresh resume, short-idle resume, backend-restart resume, and an intentionally rejected resume target. The rejected target proves Recovery; the accepted stale target proves useful continuation.
- Prompt-template or system-prompt changes that touch recovery, continuation, verbosity, or model-specific behavior need live probes. Anthropic's postmortem shows that prompt changes can trade off against intelligence even when normal tests pass.
- If Claude resumes but no-ops, repeats old context, or ignores the latest follow-up, debug the runner/recovery evidence path before blaming the model.

### Operator playbook: stale Claude continuation

When a Claude job resumes after being idle for more than an hour or a day and then behaves forgetful, repetitive, or strangely shallow:

1. Read `logs/session-events.jsonl` and confirm whether the run was `continue` or `recovery`, which session id was used, and whether a new id was captured.
2. Read `logs/cli-output.log` and confirm the latest user follow-up appears as the primary turn, not as a footer behind stale framing.
3. Check `job.json.sessionChain`. A `(recovery)` marker means old ids before that marker are tombstoned and must not be reused.
4. Compare the agent's first meaningful action after resume with the user's latest follow-up. If it ignores the follow-up, add or update a `RunOutcomePolicy` regression before changing driver flags.
5. For live validation, run a Claude stale-session probe that creates a tiny session, waits past the chosen idle threshold, resumes with a concrete edit request, and asserts an observable file or protocol change.

## Rate-limit pill

Anthropic streams a `rate_limit_event` frame **per turn**. We render it to a single marker line with two halves:

- A human prefix: `● Rate limit · five-hour · allowed · reset in 109 min`
- A machine kv tail: `[window=five_hour status=allowed resetsAt=1777393800 overage=allowed usingOverage=false]`

`OnOutputLine` parses the tail back into `ClaudeRateLimitSnapshot` (window, status, resetsAt, overageStatus, isUsingOverage, capturedAt). The frontend's protocol-pane header pill reads `info.LastRateLimit` via `GET /api/tasks/{id}/claude/session-info`.

If you change the marker format, you break the pill. Update both halves together and add a test that round-trips a captured frame.

The rate-limit parser accepts both the original camelCase keys
(`rateLimitType`, `resetsAt`, `overageStatus`, `isUsingOverage`) and snake_case
aliases. Optional fields with an unknown type degrade to null/zero. Unknown
fields are ignored. The stable marker tail remains camelCase-compatible so
older downstream consumers continue to work.

## Model handling

Claude has no machine-readable model-list command. Studio opens the interactive
`/model` picker in a PTY, parses the installed CLI's entries, and merges them
with `ModelMetadataRegistry`. Models reported by the picker are selectable.
Known registry models missing from the picker remain visible but disabled with
an availability note. This keeps an existing model pin legible without claiming
that the installed CLI can run it.

Claude Code 2.1.26x renders numbered entries with a short name followed by the
display name, a middle dot, and a description. It then renders the effort
selector after the model list. PTY snapshots can collapse that entire surface
into one line and remove inter-word spaces, for example
`3.Fable✔Fable5.1·Mostcapable...4.SonnetSonnet5·Efficient...●Higheffort`.
The discovery parser therefore recognizes numbered entry boundaries and the
display-name/version token without relying on whitespace, stops before the
effort selector, and treats the check mark as the current default. The older
one-model-per-line parser remains as a compatibility fallback.

The registry contains the Claude 5 family currently exposed by the picker:
Opus 5 (`claude-opus-5`, 1 million context), Fable 5.1
(`claude-fable-5-1`, 200,000 context), Sonnet 5, and Haiku 4.5. The dated Haiku
id `claude-haiku-4-5-20251001` resolves through the short registry alias.
Opus 5 and Fable 5.1 expose `low`, `medium`, `high`, `xhigh`, and `max` in
Studio, with `high` as the default. Fable is a Studio compatibility entry until
CodingAgentRunner's `CliThinkingLevels` includes that family. The related
library work is tracked by `model-catalog-availability-discovery-gpt-6-astra`
in the Coding Agent Runner project.

`NormalizeModelId` coerces dotted forms (`claude-opus-4.7`) into the dashed form (`claude-opus-4-7`) the CLI requires. Unknown ids pass through unchanged so non-standard ids still flow.

To add a new known model, update `ModelMetadataRegistry`, its aliases and
thinking metadata, plus the picker parser fixture when the display shape is
new. Do not make registry presence imply installed-CLI availability.

## Quirks (and what to do about them)

1. **Claude prompt stdin is one-shot.** CAR writes the complete prompt, flushes it, and closes the stream before the turn begins. Keeping the pipe open can stall initialization; closing it early is load-bearing.
2. **Windows npm shim quirk.** The npm-installed binary is `node_modules/@anthropic-ai/claude-code/bin/claude.exe`. An interrupted `npm i -g @anthropic-ai/claude-code` can leave a `claude.exe.old.<timestamp>` and *no* `claude.exe`, breaking `claude --version`. The fix is to reinstall: `npm i -g @anthropic-ai/claude-code`. This is documented in `supported-clis.md` and shows up as `Available=false` in the side-sheet.
3. **`thinking` blocks are dropped.** Extended-thinking content (`type: "thinking"` parts of an assistant message) is filtered out of the visible buffer. They're noisy and not user-actionable; if a debug flag becomes useful, add a config-gated branch in `TransformReadLine`.
4. **Long Bash commands get trimmed to 200 chars.** `TrimSingleLine` collapses newlines and truncates with `…`. Multi-line shell scripts won't render in full; this is intentional, the full command is in the persisted `tool_use` payload via `Read` of the JSONL.
5. **`stream-json` requires `--verbose`.** Without it, the CLI exits silently. The runner always passes both.

## Known incidents (and the fixes)

- **Sessions never resumed (Issue tracked in `TaskRunnerPlanTests`):** the captured UUID never made it back to `job.json` because the `system` frame's `session_id` was being read in `TransformReadLine` and discarded. Fix: capture in `OnOutputLine` against the transformed marker line; persist via `_sessions.AppendSessionToChain` in `OnCliFinishedAsync`.
- **Restart of finished tasks: "I'll wait for your request":** when a job in `4-review` was re-started with the same session, the runner re-issued the `RunnerFreshStart` bootstrap as a new user turn. Claude saw a duplicate of turn 1, decided the task was already done, and replied with the generic English fallback. Fix: new `runner-resume-restart.md` template + planner branch for `ManualStart + resume + initialState ∈ {Review, Completed}`. Tests in `TaskRunnerPlanTests.Start_FromReviewOrCompletedWithSession_UsesRestartPrompt`.
- **Activity log blank for 30+ s at start:** Claude's `-p` mode emits no output until the model produces its first text in the default text format. Switching to `stream-json` made every frame stream live; the synthetic `[taskboard] Started claude CLI ...` line additionally fills the gap between spawn and first frame.
- **"Agent goes silent after `system/init` frame, watchdog kills at 124 s" (ADR-0011):** the legacy runner spawned `claude.CMD` instead of the underlying `claude.exe`. Windows wraps a `.CMD` invocation in `cmd.exe /c "..."`, which correlated with intermittent silence. The legacy behavior's [`ResolveCmdShimToExe`](../../../../backend/Features/Cli/Execution/BuiltInCliBehaviors.cs) retains the path probe for rollback. CAR owns npm-shim repair for the default CAR-backed path. The retained Studio healer is limited to the explicit rollback and `ClaudeOneShot`, must never run for a CAR-backed agent, and is removed with those paths in T4. The integration matrix at [`CliSpawnIntegrationTests.cs`](../../../../backend.Tests/CliSpawnIntegrationTests.cs) pins the launch contract.
- **Claude Code quality regression after stale resumes (external, Anthropic April 23 2026):** an upstream Claude Code harness bug pruned older thinking repeatedly after a session crossed a one-hour idle threshold. Lesson for this project: do not equate accepted resume ids with healthy continuations; stale-session behavior needs its own probes and recovery tests.

### Operator playbook: claude run hangs after init

When you observe a job in `3-progress` with only `● Session init <uuid>` in the activity log and the watchdog warning at 30 s / 60 s / killing at 120 s, walk the checklist below before reaching for code changes.

1. **Confirm direct invocation works.** From a shell, run:
   ```sh
   echo "say hi" | "%APPDATA%\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe" \
     -p --model claude-haiku-4-5 --output-format stream-json --verbose --dangerously-skip-permissions
   ```
   If this is silent, the issue is upstream of the runner (CLI install, auth, network). If it streams, the runner is the suspect.
2. **Check the spawned process tree.** `Get-CimInstance Win32_Process -Filter "name='cmd.exe' or name='claude.exe'"` - if the runner spawned `cmd.exe` as the parent of `claude.exe`, ADR-0011's `ResolveCmdShimToExe` is bypassed (probably a config override pointing at `claude.CMD`). Fix: clear `ClaudeCli:Path` in `appsettings.Local.json` or set it to the `.exe` directly.
3. **Count concurrent `claude.exe` processes.** `Get-CimInstance Win32_Process -Filter "name='claude.exe'" | Select-Object ProcessId,ParentProcessId`. The user's own Claude Code session can spawn 6+ background workers; if the agent-taskboard-spawned one shares an `~/.claude/projects/<cwd>` directory with another live session, watch for lock contention. Run the spawn from a `Path.GetTempPath()` cwd to isolate.
4. **Run the integration matrix.** `RUN_CLI_INTEGRATION=1 dotnet test --filter CliSpawnIntegrationTests`. Each probe maps to a diagnostic question (see ADR-0011's reasoning style). A failure narrows the search; all-green means the hang is environmental, not structural.
5. **Inspect the persisted log tail.** `<job-folder>/logs/cli-output.log` carries the newest bounded stream-json + watchdog timeline, and `cli-output.log.1` carries the immediately preceding segment after rotation. Look for the gap between `Session init` and the next non-system line; that gap's duration tells you whether claude was producing tokens that never reached the runner (Node-side buffering, addressed by ADR-0011's `.exe` rule) versus genuinely waiting on the API (rate-limit, network).
6. **Check the per-job tool-call log** (added in ADR-0030). `<job-folder>/logs/tool-calls.jsonl` lists every `ToolStarted` / `ToolCompleted` the adapter observed, with `tool` name and the most relevant argument (`file_path` / `command` / `pattern`). If the tail of this file is a `started` with no matching `completed`, the agent was mid-tool when the watchdog killed it; combine with `cli-output.log` to decide whether the tool was legitimately long (Bash build) or whether claude's stdout pipe blocked.
7. **Compare with the side-channel session file.** Claude maintains its own per-session log at `~/.claude/projects/<encoded-cwd>/<session-uuid>.jsonl` (the path rule is implemented by [`ClaudeSessionHeartbeat.cs`](../../../../backend/Features/Cli/Execution/ClaudeSessionHeartbeat.cs)). If that file kept growing during a watchdog-killed silence, the agent was alive and the stdout path was stalled. If the file also stopped at the same instant, Claude itself was waiting on the Anthropic API.
8. **Look at the rate-limit context.** `cli-output.log` includes Anthropic's `rate_limit_event` frames — under `allowed_warning` status the API can take 30-60 s longer to respond. Pattern analysis in May 2026 (see ADR-0030) found that warning state correlates with most hangs; the widened `SessionInitializing` budget (60 / 120 s) accommodates this without code changes per-incident.
9. **If three same-job kills in a row already routed to `5-human-review`:** the Loud-Failure routing (ADR-0030) moved the job out of `3-progress` automatically and paused auto-mode. Read the chat-note before re-running. Common causes: a prompt that triggers extended thinking the watchdog interprets as silence, a tool that hangs (e.g. an interactive `git rebase -i`), or a session id that resumes into a corrupted state.

## API-test-level harness (the contract)

Live-backend testing is flaky - the real ASP.NET host accumulates state, concurrent claude processes (e.g. the engineer's own Claude Code in the same cwd) contend on the per-cwd session DB, and timing differences mask root causes. The reliable harness is the **API-test level**: opt into `RUN_CLI_INTEGRATION=1` and run the integration matrix. It boots the actual host via `WebApplicationFactory<Program>` and drives the real CLI through DI, so passing here is the contract for "this change is safe to deploy".

Three integration test classes pin three contract layers:

| Test class | Pins |
|---|---|
| [`CliSpawnIntegrationTests`](../../../../backend.Tests/CliSpawnIntegrationTests.cs) | Spawn-path correctness for Claude / Codex / Gemini direct invocation, `.CMD` shim shape, sequential kill+restart |
| [`CliKestrelHostingRepoTests`](../../../../backend.Tests/CliKestrelHostingRepoTests.cs) | CAR spawn produces frames past `Session init` under the real Kestrel-hosted DI graph, including the Windows-hosted CAR composition probe. |
| [`CliResumeContractTests`](../../../../backend.Tests/CliResumeContractTests.cs) | Resume / continuation contract: fresh-then-resume produces init frames on both runs, dead-session-UUID fails cleanly within 30 s instead of hanging |

The two `WebApplicationFactory<Program>`-using classes are tagged `[Collection("LiveCli")]` (defined in [`LiveCliCollection.cs`](../../../../backend.Tests/LiveCliCollection.cs)) which serializes them - xUnit's default parallelism would otherwise let two tests spawn the same CLI in the same per-cwd `~/.claude/projects/...` session DB simultaneously and re-create the lock contention the ADR-0011 / ADR-0014 investigation kept tripping over. Default-suite tests stay parallel.

Standard sweep:

```sh
# fast unit / integration (no live CLI):
dotnet test backend.Tests/OrchestratorApi.Tests.csproj
# expected: 342 passed, 12 skipped (the live ones), 0 failed

# full live-CLI sweep:
RUN_CLI_INTEGRATION=1 dotnet test backend.Tests/OrchestratorApi.Tests.csproj \
  --filter "FullyQualifiedName~CliSpawnIntegrationTests|FullyQualifiedName~CliKestrelHostingRepoTests|FullyQualifiedName~CliResumeContractTests"
# expected: 12 passed, 0 failed, ~90 s
```

Live-backend retriggers (`api.sh start` → `POST /jobs/{id}/start`) are diagnostic only and not part of the deploy contract — if the API-test sweep is green and the live backend hangs anyway, the gap is environmental (concurrent claude processes, accumulated runtime state) and should be addressed via the operator playbook above, not by code changes.

## Quota probe

[`ClaudeQuotaProbe`](../../../../backend/Features/Cli/Quota/ClaudeQuotaProbe.cs) drives the `/usage` slash command via PTY in a scratch dir. It returns the quota windows reported by the CLI. The probe runs in `%TEMP%/agent-taskboard-quota/claude/` so it doesn't pollute the user's `~/.claude/projects/` listing.

If `/usage` output format changes, update
[`ClaudeQuotaProbe.ParseUsageWindows`](../../../../backend/Features/Cli/Quota/ClaudeQuotaProbe.cs);
the test fixtures live under `backend.Tests/Fixtures/quota/claude/`.

Claude Code 2.1.202 introduced a tabbed
`Settings / Status / Config / Usage / Stats` screen. On API-billed accounts the
Usage tab can contain only session cost, duration, code-change, and token
statistics. It has no subscription utilization percentage. This is a
recognized format, not a failed probe: the parser returns one `Quota` window
with `UsedPct = null`, and every Studio quota surface renders `Unknown`.
Legacy `Current session` and `Current week` formats remain supported. The real
2.1.202 PTY fixture is
`backend.Tests/Fixtures/quota/claude/claude-usage-v2.1.202-api-billing.txt`.

## Common tasks

### "Add a new tool marker"

1. Identify the Claude tool name (`Read`, `Edit`, `Bash`, …).
2. Add a switch arm in `ClaudeOutputRenderer.FormatToolUse` that builds the marker line in the existing vocabulary (§ Marker-line vocabulary in `cli-overview`). Don't invent a new prefix.
3. Add a snapshot test in `backend.Tests/ClaudeCliServiceTests.cs` (which drives `svc.TransformReadLine`, now delegating to the renderer) that constructs a synthetic `assistant` frame and asserts the emitted marker.

### "Capture new telemetry from a frame"

1. Locate the frame type in the catalogue above; add a switch arm in `ClaudeOutputRenderer` if new.
2. Render to a marker line with a stable bracketed kv tail (`[key=value …]`) — same pattern as `rate_limit_event`.
3. Add a regex in `OnOutputLine` to read the kv tail back; assign to a typed snapshot on `ProcInfo`.
4. Expose via the existing `/api/tasks/{id}/claude/session-info` endpoint (do not introduce a new endpoint per snapshot).

### "Add a new model"

1. Append to `GetModelCatalogAsync`'s hardcoded list.
2. Test in the existing `*.spec.ts` round-trip if the user is likely to enter a dotted form.

### "Trace a 'my session didn't resume' bug"

1. Read `logs/cli-output.log` for the run that should have produced the UUID. The `● Session init <uuid>` marker line is the source.
2. Confirm `info.CapturedSessionId` was set: the next line in the runner log is `Captured Claude session id <uuid>`.
3. Confirm `OnCliFinishedAsync` ran `_sessions.AppendSessionToChain` with that id. If not, the run was cancelled or crashed before exit-monitor.
4. Confirm `RunPlanner.PlanRun` reads the persisted UUID via `info.SessionName` on the next start.

### "Add a regression test for a new behaviour"

1. Capture a real raw frame (or several) from `logs/cli-output.log` — the unparsed JSON line(s).
2. Scrub and save the canonical provider capture under
   `testdata/cli-fixtures/streams/claude/<exact-version>/<name>.claude.fixture`.
3. Add Agent Host replay assertions for derived events and outcomes. Add a
   backend renderer snapshot only when marker rendering changes.
4. Update the shared
   [frame compatibility matrix](../frame-compatibility-matrix.md).

## Fixtures

`testdata/cli-fixtures/streams/claude/<exact-version>/` holds the canonical
stream-json captures and replay metadata. `backend.Tests/Fixtures/cli/claude/`
is limited to renderer-specific snapshots that cannot consume the shared
fixture directly.
