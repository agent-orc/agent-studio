# CLI Frame Compatibility Matrix

This document records the Agent Host contract for provider stream frames. It
separates runner semantics from the parallel Coding Agent Chat (CAC) rendering
contract while keeping both consumers on one capture corpus.

Current verified baseline on 2026-08-11:

| Component | Version |
|---|---|
| CodingAgentRunner (CAR) adapter | 0.7.0, exact package pin |
| Claude Code CLI | 2.1.202 |
| Codex CLI | 0.144.1 |

The version identifies the stream that was captured, not a claim that all
earlier or later versions have the same protocol. A new CLI version needs a new
capture folder and matrix row before it can replace a baseline.

## Runner processing inventory

The remote Agent Host processes a line in this order:

1. `CarWorkerExecution` or the explicit legacy fallback receives the complete
   stdout line.
2. `CliProtocolNoveltyTracker` identifies top-level and nested frame types
   before durable-log line truncation or renderer adaptation.
3. CAR normalizes known frames into typed `CliRunEvent` values. The host writes
   that trace for diagnostics; raw stdout remains the outcome evidence source.
4. `ProviderOutputEvidenceExtractor` extracts the last provider terminal,
   final assistant text, and session id from NDJSON.
5. `ExecutionOutcomeAdapter` combines that evidence with exit, transport,
   lease, session, cancellation, timeout, and durable-delivery facts.
6. `LogShipper` publishes raw lines and the scrubbed protocol-novelty marker.
   Protocol v1 classifies the marker as `runner.protocol.unknown-frame`; the
   legacy API stores the equivalent `cli-frame-unknown` diagnostic. Both are
   available to journal and timeline projections.

### Version-sensitive assumptions

| Boundary | Current assumption | Drift risk and guard |
|---|---|---|
| Agent Host CLI selection | Only `claude` and `codex` use the remote CAR worker. | A new remote CLI must add an explicit vocabulary and capture baseline. Unknown configured names fail validation. |
| CAR callbacks | CAR 0.7.0 emits its typed callback before the matching raw-output callback. | Callback order is pinned by bridge tests. Host novelty detection uses the raw line so adapter classification cannot hide drift. |
| Claude frames | Known top-level types are `system`, `assistant`, `user`, `result`, `rate_limit_event`, and `tool_progress`. `tool_progress` is a known heartbeat frame; the renderer drops it (no Activity Log row) and novelty tracking never counts it. | Any other structured type produces a novelty event even if the run later completes. |
| Codex frames | Known top-level types are `thread.started`, `turn.started`, `turn.completed`, `turn.failed`, `session_meta`, `rate_limits`, `item.started`, `item.updated`, and `item.completed`. Known nested item types are `agent_message`, `reasoning`, `command_execution`, `command_call`, `local_shell_call`, `file_change`, `web_search`, `update_plan`, `todo`, and `todo_list`. | A new top-level type or nested `item.type` produces a novelty event. `todo_list` carries `{ text, completed }`; Studio derives the first incomplete item as active only for started/updated frames. |
| Provider terminal extraction | Completion types are `result`, `turn.completed`, and `response.completed`; failure types include `error`, `turn.failed`, and `response.failed`. Codex final text comes from `item.completed/agent_message`. | Exact type matching is replayed for every captured CLI version. An unknown frame does not become terminal evidence by accident. |
| Sentinel extraction | The last terminal sentinel in final assistant output wins. Raw stdout is used only when no structured final assistant text exists. | Tool arguments that merely contain a sentinel cannot override the actual final response. Fixture P1 through P5 pin this boundary. |
| Outcome classification | Explicit blocker, quota/auth/config failures, provider completion, crash facts, and delivery state are combined by `execution-outcome/v1`. | Direct matrix tests and the replay corpus pin both the outcome and recovery action. |
| Process-output tail | Outcome stdout retains a 2 MiB tail; stderr retains a 256 KiB tail. | Elision is explicit. Terminal fixtures and buffer tests prove useful tail evidence survives. |
| Durable log line | `LogShipper` caps a line at 64 KiB and appends `[runner: event payload truncated]`. | Novelty detection runs first. Its event keeps the frame identity and full-line SHA-256 even when the raw durable line is shortened. |
| Durable delivery | A semantically successful run with `LocalOnly` or `Published` output is not verified delivery. | The result remains `SuccessfulCompletion`, but recovery is `RetryHandoff` until acknowledgement. |

## Captured CLI deviations

| CLI and version | Observed stream vocabulary | Terminal and quota behavior | Replay coverage |
|---|---|---|---|
| Claude Code 2.1.202 | `system/init`, `assistant`, `user`, `result`, and informational `rate_limit_event`; the rate-limit payload occurs with camel-case and snake-case fields. | `result` supplies completion or provider failure. Informational rate-limit frames do not override a later successful terminal. | P1 Done, P2 NoOp, P3 Blocked, P4 NeedsInput, P5 provider completion without sentinel, P9 crash, P22 rate-limit casing, P23 unknown frame. Plaintext P1 and P5 preserve the former launch form. |
| Codex 0.144.1 | `thread.started`, `turn.*`, `item.started`, `item.updated`, `item.completed`, and legacy `session_meta`; assistant output is `item.completed/agent_message`; the live plan is an `item.*/todo_list` item with boolean completion entries. | `turn.completed` is terminal completion. The recorded quota case is a terminal `turn.failed`, so it derives `QuotaExceeded` and `WaitForCapabilityRecovery`. Todo snapshots are non-terminal observability and do not affect the run outcome. | P1 Done, P2 NoOp, P3 Blocked, P4 NeedsInput, P5 provider completion without sentinel, P9 crash, P22 quota, P23 unknown nested item, P24 living todo list. |
| Antigravity, persisted as `gemini` | Studio-only legacy `agentapi` protocol. | Not executed by the remote Agent Host. | No runner semantic baseline. A versioned capture is required before remote support. |
| Copilot | Integration removed. | No active outcome contract. | Historical only; do not add new fixtures. |

## Semantic outcome matrix

The corpus asserts derived semantics, not only that parsing succeeds.

| Evidence | Outcome | Recovery |
|---|---|---|
| Exit 0 plus final `TASK_DONE` or `TASK_NOOP` | `SuccessfulCompletion`, high confidence | `TerminateHonestly` only after acknowledged delivery; otherwise `RetryHandoff` |
| Final `TASK_BLOCKED` or `TASK_NEEDS_INPUT` | `ExplicitAgentBlocker`, high confidence | `AskForHumanInput` |
| Provider failure or stderr containing a ground-truth quota signature | `QuotaExceeded`, high confidence | `WaitForCapabilityRecovery` |
| Exit 0, provider completion, final assistant output, no sentinel | `SuccessfulCompletion`, medium confidence | Delivery-dependent, including `RetryHandoff` for unverified output |
| Exit 0 and plaintext without a sentinel or authoritative provider completion | `ProtocolInconclusive` | `AskForHumanInput` |
| Unknown frame followed by otherwise valid terminal evidence | Existing semantic outcome is preserved | Existing recovery is preserved; the novelty event is additional evidence |

## Protocol novelty contract

Unknown structured frames must never be silently swallowed. The runner emits
one typed telemetry record for every occurrence with:

- CLI and CAR adapter version
- scrubbed top-level type, or `item.started/<item-type>` /
  `item.completed/<item-type>` for Codex
- per-type occurrence and total unknown-frame counters for that run
- SHA-256 of the complete provider line

The durable event does not include the raw provider payload. Missing types,
non-object JSON, and malformed JSON that starts like a structured frame have
explicit synthetic identities. Unstructured stderr and ordinary plaintext are
not protocol novelty.

## Shared capture contract with CAC

The versioned source of truth is
[`testdata/cli-fixtures/`](../../../testdata/cli-fixtures/README.md). Its path is
`streams/<cli>/<cliVersion>/<scenario>.<form>.fixture`; metadata records the
schema version, exact CLI version, stream form, exit code, delivery state,
capture source, capture date, and scrub status.

This is one vocabulary at two layers:

| Consumer | Responsibility |
|---|---|
| Runner in Agent Studio | Provider semantics, terminal evidence, typed outcomes, recovery, delivery verification, and novelty telemetry |
| [CAC](../architecture/project-map.md#cac-coding-agent-chat) | Rendering the same captured frames into chat and tool-call presentation |

For P24, CAC and Studio must render the captured snapshots as one checklist
whose rows update by stable item identity. Provider JSON stays available in
Trace, not in the readable stream. A CLI without a native plan frame returns
`hasPlan: false`; the Activity stream and Orchestrator task context continue
without an empty placeholder.

The parallel CAC fixture-rendering matrix should index these paths rather than
copy or rewrite the captures. Rendering-specific expected output may live in
CAC, but provider bytes and version metadata remain shared.

## Adding a capture or CLI version

1. Capture the real stdout/stderr stream and exact CLI version.
2. Scrub credentials, account ids, home paths, private repository URLs, and
   unrelated prompt content without changing frame names, casing, nullability,
   ordering, or stream assignment.
3. Add it under a new or existing exact version folder and complete the fixture
   metadata. Synthetic drift probes must use `synthetic-drift-probe`.
4. Add or update replay expectations for events, outcome, recovery, and
   delivery state. Include Done, Blocked/NeedsInput, quota, provider-only
   completion, unverified delivery, and an unknown-frame probe where the CLI
   exposes those paths.
5. Update this matrix and the CAC rendering matrix against the same fixture
   identity.
