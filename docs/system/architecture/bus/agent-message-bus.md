# Agent Message Bus

Single source of truth for the contract that lets every agent, supervisor, orchestrator, and runtime component speak into one observable conversation layer. Read this before changing how agents log decisions, before adding a new participant kind, and before wiring a new UI panel that shows agent activity.

> **Language:** English. See [AGENTS.md](../../../../AGENTS.md#documentation-language).
>
> **Schema home:** all field-level rules live next to this doc under [`docs/app/schemas/`](../../schemas/README.md). The contract here is the prose; the schemas are the validator.

## 1. Purpose and non-goals

The Agent Message Bus is the product's communication and observability spine. It records who observed, asked, decided, advised, intervened, produced evidence, spent tokens, transitioned state, errored, or pulsed alive. Every cross-cutting structured signal that today gets buried in a CLI log, a SignalR push, or an ad-hoc `.jsonl` becomes a typed `AgentMessage` so the UI, the system-review monitor, and future analysis skills read from one shape.

Purpose:

- Make every meaningful agent signal inspectable from one timeline.
- Give the Project Screen a participant graph: who talked to whom, on which job, in which run.
- Give the system-review monitor (Layer 3) one input format instead of N format-specific parsers.
- Let supporting agents (security audit, UX/UI council, source-map skill) emit structured events without inventing a new file each time.
- Carry token attribution and artifact references so the security-evidence chain becomes a query, not a manual hunt.

Non-goals (do not add, even if asked offhandedly):

- **Not a workflow engine.** A message records that something happened. It does not move a job between state lanes, queue another task, or fan out coding work. Routine outcomes still flow through `RunOutcomePolicy`; emergency primitives still route through `TaskRunnerService.StopJob` / `SetMode`. The bus is what those layers write into; it is not their replacement.
- **Not a parallel orchestrator.** Producing an `intervention` message does not perform the intervention. The supervisor calls the runner, the runner is the single state-machine authority, and the supervisor also writes a bus message so the user can see why.
- **Not the slot or branch orchestrator.** The hard product boundary in [AGENTS.md](../../../../AGENTS.md#product-goal--non-goals) and [ROADMAP.md](../../../../ROADMAP.md#hard-boundaries) holds. ADR-0052 owns whether a project may run several coding tasks in isolated worktrees; the bus only records and visualises the resulting events.
- **Not a database.** Source of truth is many small JSON or JSONL documents on disk. An in-memory projection serves query and aggregation; it never owns the data.
- **Not a chat history store.** User and agent text already live in `logs/cli-output.log` and the orchestrator chat log. The bus carries decisions, observations, and references to those logs. It does not duplicate raw transcripts.

## 2. Participant model

A participant is one actor that may emit messages. Defined by [`agent-participant.schema.json`](../../schemas/agent-participant.schema.json). Stable id, declared once per workspace, referenced from every `AgentMessage.participantId` so the timeline does not have to re-derive identity per message.

Eight participant kinds:

| Kind | Examples | Scope | Lifecycle authority |
|------|----------|-------|---------------------|
| `User` | `user` | Workspace | Owns prompts and decisions; the bus never speaks on the user's behalf. |
| `Orchestrator` | `orchestrator`, `orchestrator:my-project` | Workspace and per-project | Owns queue movement and the deterministic post-run policy. The bus mirrors its decisions; it does not replace them. |
| `Supervisor` | `supervisor:my-project` | Per-project | Layer 2 health watcher. Writes advisories and (rarely) intervention records. |
| `CodingAgent` | `agent:claude`, `agent:codex` | Per-project, per-job | The CLI editing the repository for one admitted task slot. One per project by default; more only when ADR-0052 parallel slots are enabled and isolated. |
| `SupportingAgent` | `support:security-audit`, `support:ux-council` | Per-project or per-job | User-triggered meta worker; runs in its own CLI process when the work is non-trivial. Never edits source code on its own. |
| `SystemReview` | `system-review`, `master-of-disaster` | Workspace | Layer 3 stand-alone monitor; reads bus + disk, writes Markdown reports. |
| `Runtime` | `runtime:taskboard`, `runtime:supervisor-host` | Workspace | The application code itself when it emits lifecycle, error, or heartbeat messages. |
| `External` | `relay:companion`, `companion:phone` | Workspace | Outbound integrations (companion app relay, future webhook adapters). |

Participants are descriptive only. Adding a participant declaration does not grant any capability; capability comes from code. The runner does not consult the participant registry to decide who may move state.

Storage: one JSON document per participant under `logs/bus/participants/<id>.json`. The runtime registers built-in participants on first boot; supporting agents register themselves before emitting their first message.

## 3. Message lifecycle

```
emit -> append (atomic) -> in-memory projection updates -> SignalR fan-out -> UI render / system-review read
```

Every message is small (target under 4 KB), append-only, and immutable once written. There is no edit, no delete, no reorder. Mistakes are corrected by emitting a new message that references the wrong one via `replyToId` or `correlationId`.

### 3.1 Producers

- **Runtime** writes `lifecycle` messages on job create, run start, run stop, sentinel parsed, state lane moved, recovery restarted; `error` messages on unrecoverable backend failures; `heartbeat` messages on supervisor host tick.
- **Orchestrator** writes `decision` messages mirroring `OrchestratorChatLog` entries (Decision / Reissue / HeuristicFallback / GiveUp). The chat log remains the textual record the activity-log parser already reads; the bus message is the typed projection.
- **Supervisor** writes `advisory` messages mirroring `SupervisorAdvisory` and `intervention` messages mirroring `SupervisorIntervention`.
- **Coding agent** does not write to the bus directly. The runner observes its CLI output, parses sentinels and tool calls, and emits `lifecycle` and `decision` messages on its behalf. Free-form agent text stays in `cli-output.log` and is referenced via `artifact:log-slice` when a bus message needs to point at a specific span.
- **Supporting agent** writes `observation`, `artifact`, and `decision` messages keyed to its skill. A council member writes one `observation` per critique pass; the council coordinator writes one final `decision`.
- **User** writes `question` messages when the UI sends a prompt or follow-up. The runtime is the actual writer; `participantId` is `user`.

### 3.2 Consumers

- **Frontend project view**: subscribes to a per-project SignalR stream and renders the timeline + participant graph.
- **System-review monitor (Layer 3)**: tail-reads `logs/bus/<project>/<date>.jsonl`, joins on `runId`, and writes a Markdown health report. The skill prompt at [`scripts/supervisor/system-review.md`](../../../../scripts/supervisor/system-review.md) and the helper at [`scripts/supervisor/system-health-check.mjs`](../../../../scripts/supervisor/system-health-check.mjs) implement eight structured checks (long silent periods, repeated interventions, repeated failed/cancelled runs, token spikes, supporting jobs without accepted review, stuck loops, weak review evidence, backend crash markers); the helper accepts an exported JSONL fixture too, so the monitor is exercisable before every producer is wired into the live bus.
- **Companion app relay**: serves the most recent N messages per project to the phone PWA, filtered to `decision`, `intervention`, and `question` kinds for the small surface.
- **Backend in-memory store**: keeps the last K messages per project hot for `/api/projects/{id}/bus` queries; older messages stay on disk and are paged in lazily.

### 3.3 Ordering

Lexical order of `id` matches creation order when `id` is a ULID or UUID v7. Consumers sort by `id` for stable replay. `createdAt` is the wall-clock truth but may collide; `id` is the tie-breaker. The bus does not promise causal ordering across participants; use `replyToId` and `correlationId` for causality.

## 4. Storage shape

Source of truth: many small JSON or JSONL documents on disk. No SQL, no SQLite, no LiteDB, no EF. The repo's [Schema-First Communication and In-Memory Data Layer](../../../../ROADMAP.md#schema-first-communication-and-in-memory-data-layer) doctrine applies here in full.

Layout under each watched workspace:

```
<workspace>/
  logs/
    bus/
      participants/
        user.json
        orchestrator.json
        supervisor-my-project.json
        agent-claude.json
        ...
      _workspace/
        2026-05-05.jsonl
        2026-05-06.jsonl
      <project>/
        2026-05-05.jsonl
        2026-05-06.jsonl
```

Rules:

- One JSONL file per participant scope per UTC day. Daily rotation keeps individual files under a few megabytes even on busy projects.
- One message per line. Strict UTF-8. No trailing comma, no enclosing array. Lines are independently parseable.
- Messages with `project: null` go to `_workspace/`. Messages with a project go to `<project>/`. Cross-project correlation uses `correlationId`, not file movement.
- Append-only. The writer opens the file in append mode, writes a single `JSON.stringify(message) + "\n"`, fsyncs on Windows-default cadence, and closes. No partial writes; if the JSON serialiser throws, nothing is written.
- File names are the source of `project` and `date` filters; the in-memory projection indexes by `(project, date, participantId, kind)` only.
- Old days are not deleted by the runtime. A future workspace-hygiene job may roll them into monthly archives; that is not part of v1.
- Participant files are small JSON documents (not JSONL) so the registry can be loaded with one parse per participant on boot.

The bus runs alongside, not on top of, the existing `logs/meta/<project>/observations.jsonl` and `interventions.jsonl`. During the migration window (see Section 9) the supervisor writes both. After migration the supervisor writes the bus only and a one-shot reader translates the legacy files for system-review backfill.

## 5. Id and correlation strategy

Five identifier fields, each with a narrow purpose. Do not overload them.

| Field | Format | Purpose |
|-------|--------|---------|
| `id` | ULID or UUID v7 | Globally unique, lexically sortable. The canonical reference target. |
| `replyToId` | Another `id` | Direct answer relationship. Question -> answer, decision -> follow-up decision, advisory -> intervention. Render as a thread in the UI. |
| `correlationId` | Free-form, often the originating user message `id` | Group of messages that belong to one logical activity but cross participants and runs. Render as a participant graph cluster. |
| `runId` | Stable id from `/api/tasks/{id}/runs` | Bus message belongs to one CLI invocation. Lets the timeline filter "show me run 3" without scanning text. |
| `cliSessionId` | Provider session id | Bus message belongs to one provider chat session. Lets system-review correlate with on-disk session evidence. |

Recommended id choice: ULID. 26 chars, lexically sortable, no dashes, OS-clock based. UUID v7 is acceptable when a library already produces them. Plain UUID v4 is **not** acceptable because lexical order does not match time and replay becomes O(n log n) on every read.

Correlation rule of thumb: when in doubt, set `correlationId` to the `id` of the user message or lifecycle:JobCreated message that started the activity. The supervisor and supporting agents inherit it on every message they emit during that activity.

## 6. Reference fields: project, job, run, CLI session, artifact, token

Every cross-stream link is a typed field on the message envelope. The bus does not embed copies of foreign records.

- **`project`** matches `ProjectRunnerStatus.projectName` and the watch-paths name. Workspace-wide messages set it to `null`.
- **`jobId`** is the `JobInfo.Id` slug. Lifecycle messages that create the job carry `jobId` (the new id) plus a `payload.transitionReason` describing why.
- **`runId`** is the run id used by `/api/tasks/{id}/runs`. The runner emits it on `lifecycle:RunStarted`; downstream messages within the same run inherit it. When a run dies and is recovered, the recovered run gets a new `runId` and the recovery `decision` carries `replyToId` to the original run's last message.
- **`cliSessionId`** is the provider session UUID (Claude), session id (Codex), session name (Copilot, Gemini). Optional; only set when the runner knows it. Useful for stale-session diagnostics.
- **`artifacts`** is an array of [`agent-artifact-ref`](../../schemas/agent-artifact-ref.schema.json) pointers. Always references, never inlined bytes. Screenshots live under `<job>/results/`; log slices reference `logs/cli-output.log` with a `byteRange` or `lineRange`; supervisor records reference `logs/meta/<project>/observations.jsonl` with a `lineRange`. Artifact `kind` drives how the UI renders the link.
- **`tokens`** is a per-message attribution block: `{ input, output, cacheRead?, cacheWrite?, model?, thinkingLevel?, dollars? }`. `kind:token-usage` messages always carry it, with `model` plus `thinkingLevel` as the identity of what spent the tokens (`thinkingLevel` is null when the producer does not know the level); other kinds may carry it for traceability. Rolling windowed totals belong in [`token-aggregate.schema.json`](../../schemas/token-aggregate.schema.json), not on each message.

## 7. UI projection expectations

The Project Screen Observability surface (queued at `agent-taskboard/2-ready/project-observability-message-bus-panel/`) renders the bus through three views:

1. **Timeline.** Vertical list of messages, newest at top by default. One row per message. Columns: time, participant glyph + display name, kind chip, severity dot when not Info, summary, optional artifact-count badge. Click a row to open the raw JSON drawer. Heartbeats collapse into a single "alive: N" row per participant per minute. Use `participant-glyph` plus `kind-chip` so the user can scan without reading text.
2. **Participant graph.** Force-directed layout where each node is a participant and each edge is a `replyToId` or shared `correlationId` link, weighted by message count. Hovering a node filters the timeline to that participant. Hovering an edge highlights the messages that connect them.
3. **Aggregates.** Heatmaps and counters: messages per kind per hour, intervention rate per project per day, expensive `token-usage` events ranked by dollars or output tokens, error bursts, long silent periods (no message for > N minutes on a project that has an active run).

Filters available everywhere: participant, kind, severity, time window, jobId, runId, skill (via `tags`), CLI (via `participant.cli`).

Drill-down is mandatory: every aggregate row, timeline row, and graph node has a "view raw" affordance that opens the underlying JSON message and any referenced artifact. Findings without drill-down are findings the reviewer cannot trust.

The frontend already renders `Orchestrator` and `Supervisor` as distinct activity-log participants today (see [`OrchestratorChatLog`](../../../backend/Services/Runner/OrchestratorChatLog.cs)). The new panel sits beside the activity log, fed by the bus rather than by the parsed `cli-output.log`. Both surfaces stay during the migration window.

## 8. How this preserves the product boundary

Three explicit guards:

1. **Producing a message never moves a job.** The bus is observability and reference; lane transitions remain owned by the runner. The supervisor's `intervention` message records that an intervention was invoked; the actual call is `TaskRunnerService.StopJob` or `SetMode`, unchanged.
2. **One coding-agent participant per project at any time.** The participant model can describe many supporting agents per project, but at most one `CodingAgent` participant has a live `runId` per project. Adding a second one would require relaxing the hard boundary in AGENTS.md and ROADMAP, which is out of scope.
3. **No branch orchestration on the bus.** There is no `kind:branch-create`, no `payload.targetBranch`, no `participant.kind: BranchManager`. If a future feature needs to record git operations, it does so under existing `lifecycle` messages with `payload` fields, not by introducing branch concepts to the bus.

## 9. Migration path from existing logs

Existing structured streams on disk today:

- `logs/cli-output.log` per job (orchestrator chat lines + agent stdout, see [`OrchestratorChatLog`](../../../backend/Services/Runner/OrchestratorChatLog.cs)).
- `logs/meta/<project>/observations.jsonl` (supervisor advisories).
- `logs/meta/<project>/interventions.jsonl` (supervisor interventions).
- `logs/tokens/<project>.jsonl` (token aggregates, planned).
- `status.md` per job (regenerated, projection only).

Three-phase migration:

**Phase A - bridge writers.** Add bus emit calls beside each existing writer. The supervisor writes the existing JSONL plus a bus message. The orchestrator chat log writes both `cli-output.log` and a bus message. No reader changes; the bus accumulates a complete duplicate of structured signals while consumers stay on the legacy paths. This is the slice in `agent-taskboard/2-ready/bridge-existing-events-to-message-bus/`.

**Phase A canonical-source decision (V1, 2026-05-05).** The legacy raw streams remain canonical; the bus is a derived, append-only projection on top. Every bridged source still writes to the raw stream first; the bus emit fires after, best-effort. A bus failure never breaks the producer, and the bus never claims to be the only copy. Concretely:

| Signal | Canonical writer | Bus mirror |
|--------|------------------|------------|
| User prompts and continuations | `cli-output.log` `[user]` line in `TaskRunnerService.AppendUserPromptToCliLog` | `kind:question`, participant `user` |
| Orchestrator chat (Decision / Reissue / HeuristicFallback / GiveUp) | `cli-output.log` `[orchestrator]` line in `OrchestratorChatLog.Append` | `kind:decision`, participant `orchestrator:<project>`, severity per kind |
| Supervisor chat notes (cancel-run, force-fail, chat-note, escalate, cycle-resume-failed) | `cli-output.log` `[supervisor]` line in `OrchestratorChatLog.AppendSupervisor` | `kind:advisory` or `kind:intervention`, participant `supervisor:<project>` |
| Supervisor advisories (hard health, soft reasoning) | `logs/meta/<project>/observations.jsonl` via `HardHealthCheckHostedService.AppendObservationRecord` | `kind:advisory`, participant `supervisor:<project>`, artifact ref to the JSONL line |
| Supervisor interventions (CancelRun, PausePickup, ForceFail, Resume) | `logs/meta/<project>/interventions.jsonl` via `SupervisorInterventionService.AppendInterventionRecord` | `kind:intervention`, participant `supervisor:<project>`, artifact ref to the JSONL line |
| Run lifecycle (RunStarted, RunFinished, RunStopRequested) | `session-events.jsonl` + `[taskboard] Started/exited` markers in `cli-output.log` | `kind:lifecycle`, participant `runtime:taskboard`, shared `runId` derived from `(jobId, startedAtUtc)` |
| Job state lane moves | Folder rename + `job.json` `state` field | `kind:lifecycle`, topic `JobStateMoved`, participant `runtime:taskboard` (emitted only at the explicit `EmitJobLifecycleAsync` call sites; folder watching does not emit) |
| Token usage (orchestrator boot / decision turns) | `orchestrator.jsonl` `OrchestratorLogEntry.TokenUsage` | `kind:token-usage`, participant `orchestrator:<project>`, populated `tokens` block |

The bridge is a single service - `AgentMessageBusBridge` - that the producers call with typed helpers. None of the producers depend on the bridge succeeding; an empty workspace config or a write failure logs at debug and lets the canonical path proceed. Tests in `backend.Tests/AgentMessageBusBridgeTests.cs` lock the mapping for every bridged source plus an end-to-end check that `OrchestratorChatLog.Append` produces both a `cli-output.log` line and a bus message.

The CLI agent's free-form stdout is **not** parsed line-by-line into the bus. Per the constraint at the top of this section, individual agent lines stay in `cli-output.log` and are referenced via `artifact:log-slice` from the typed bus messages around them (the run lifecycle pair, the orchestrator decisions, the user prompts). When the project screen wants the full transcript, it follows the artifact.

**Phase B - move readers.** New surfaces (Project Screen Observability panel, system-review monitor) read the bus. Legacy surfaces (frontend activity log, supervisor protocol panel) keep reading their original sources. Both stay green.

**Phase C - drop duplicate writers.** Once readers are stable and a workspace-hygiene pass has converted historical legacy files into bus form, the supervisor writes only the bus. The orchestrator chat log keeps writing `cli-output.log` because the activity-log parser is the user-facing transcript and the bus does not duplicate raw transcripts. The bus message for an orchestrator decision references the corresponding `cli-output.log` slice via an `artifact:log-slice`.

A one-shot reader under `scripts/bus-backfill/` translates pre-migration `observations.jsonl`, `interventions.jsonl`, and orchestrator chat lines into bus messages so the system-review monitor can answer questions about runs that predate Phase A. The backfill is idempotent on `id` (deterministic id derivation from source file + line) and never deletes the originals.

No phase requires editing `RunOutcomePolicy`, the supervisor decision logic, or the runner state machine. The bus piggybacks on existing signals; it does not change them.

## 9a. HTTP query surface

The backend exposes the bus through `AgentMessageBusStore` and four read endpoints under `/api/bus`. The store keeps an in-memory projection per `(workspaceRoot, project)` so UI-polled endpoints never trigger a full disk scan; appends update the projection incrementally.

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/bus/{project}/summary` | Total messages, first/last timestamp, counts by `kind`, `participantId`, `severity`. |
| GET | `/api/bus/{project}/recent?limit=N` | Newest N messages (default 100, max 1000), oldest-first within the window. |
| GET | `/api/bus/{project}/messages?...` | Filtered query. Filter keys: `jobId`, `runId`, `participantId`, `kind`, `severity`, `cli`, `skill`, `tag`, `correlationId`, `since`, `until`, `limit`. Multiple filters are AND-combined. `cli` and `skill` resolve via the participant registry. |
| GET | `/api/bus/{project}/messages/{id}` | One raw message by id, or 404. |

Workspace root is resolved from the `TaskRepository` configuration value, matching the existing `/api/supervisor/{project}/recent-events` endpoint. Unknown projects return empty results, not 404, so the project screen renders during the first-message gap.

The store is registered as a singleton in `Program.cs`. Writers can call `AgentMessageBusStore.AppendAsync(workspace, message)` directly; the store validates against the schema's required fields and known enums (`AgentMessageValidator`), then atomically appends one JSON line under a per-file `SemaphoreSlim`. The disk path follows section 4: `{workspace}/logs/bus/{project|_workspace}/{yyyy-mm-dd}.jsonl`. Participant ids that contain `:` (e.g. `supervisor:my-project`) are mapped to `-` for the on-disk filename only; the id inside the JSON document is preserved verbatim.

## 9b. Supporting agents

Supporting agents (`SupportingAgent` participant kind) cover user-triggered meta work: roadmap alignment review, security audit, architecture review, source-map skill, UX/UI critique, design council, screenshot comparison, token analysis, QA status. They are explicit, action-driven, and never run automatically just because a project exists. Each call produces an [`AnalysisReport`](../../reports/analysis-reports.md) plus one bus mirror so the project timeline shows the supporting run alongside coding-agent activity.

Participant id convention: `support:<topic>`, e.g. `support:roadmap-alignment`, `support:security-audit`, `support:docs-drift`, `support:ux-council`. The id is created on first emit by `AgentMessageBusBridge.RegisterSupportingAgentAsync` and is idempotent across boots. The participant carries `kind: "SupportingAgent"`, an optional `cli` (claude / codex / copilot / gemini) when the producer was a CLI agent, and a `skill` slug equal to the topic by default.

### 9b.1 Message shape

| Field | Value |
|-------|-------|
| `participantId` | `support:<topic>` (override permitted via the bridge call when the same topic runs against multiple skills). |
| `role` | `evidence` - the message points at a durable Markdown + JSON pair on disk. |
| `kind` | `decision` when the report parsed (`Structured` or `Unstructured`); `observation` when the JSON sidecar failed (`MalformedJson`) so the timeline does not promise a typed verdict that does not exist. |
| `severity` | Bus envelope severity (`Info|Warn|High`) mapped from the analysis-report ladder; `Critical` collapses to `High` and the original is preserved on `payload.analysisSeverity`. |
| `topic` | Topic slug, e.g. `roadmap-alignment`. |
| `summary` | The report's one-line verdict, truncated to 280 chars. |
| `artifacts` | `markdown-report` pointer to `<workspace>/logs/analysis/<project>/<reportId>.md`; `json-document` pointer to the sibling `.json` when `parseStatus == Structured`. |
| `payload` | `{ reportId, topic, parseStatus, analysisSeverity, parseError, cli, skill }`. The `parseError` field is the raw fallback warning the UI surfaces verbatim when the JSON sidecar did not parse. |
| `tags` | `supporting-agent`, the topic slug, `parse-<status>` (e.g. `parse-malformedjson`), and optional `cli-<name>` / `skill-<name>` tags so filters can pivot on either dimension. |

### 9b.2 Action catalogue

Each project-level supporting action declares the same five-field contract. The first two rows are implemented bridges; the remaining rows are planned topics that follow the same shape.

| Topic | Trigger source | Inputs | Markdown report | JSON sidecar | Bus messages | Token category |
|-------|----------------|--------|-----------------|--------------|--------------|----------------|
| `roadmap-alignment` | `POST /api/analysis/{project}/actions/roadmap-alignment` (project Analysis Reports surface). | Project lane folders (`1-preparation` -> `5-human-review`), repo docs (`README`, `ROADMAP`, `AGENTS`, ADRs, design-principles, mockups), recent analysis reports. | `# Roadmap alignment review` with verdict, queue snapshot, evidence, follow-up suggestions. Skeleton in [`prompts/runtime/roadmap-alignment-review.md`](../../../../prompts/runtime/roadmap-alignment-review.md). | `{ verdict, severity, findings[], followUpTaskSuggestions[], recommendedPriorityOrder[] }` per the agent reply. | One `kind:decision` (or `observation` when malformed) from `support:roadmap-alignment`, with `markdown-report` + optional `json-document` artifacts. | Supporting Jobs Tokens. |
| `steering-docs-summary-and-drift` | `POST /api/analysis/{project}/actions/steering-docs-drift` (project Analysis Reports surface; future Steering Docs surface). | Canonical steering inventory (`AGENTS.md`, project AGENTS, frontend AGENTS, README, ROADMAP, task contract, skills lookup, ADRs, runtime prompts), recent jobs per inspected lane as drift evidence, prior analysis reports. | `# Steering docs summary and drift check` with inventory table, drift findings, proposed text changes. Skeleton in [`prompts/runtime/steering-docs-summary-and-drift.md`](../../../../prompts/runtime/steering-docs-summary-and-drift.md). | `{ verdict, severity, findings[], proposalRefs[], sources[] }` per the agent reply. | One `kind:decision` (or `observation` when malformed) from `support:steering-docs-summary-and-drift`, with `markdown-report` + optional `json-document` artifacts. Evidence-only run (no `agentResponse`) stays Manual and does not emit. | Supporting Jobs Tokens. |
| `security-audit` | Project Action button (planned). | Job folder diff, `cli-output.log`, dependency manifests, secrets-scan output. | `# Security audit` with risk verdict, finding list, evidence pointers. | `{ verdict, severity, findings[], cwes[] }`. | One `kind:decision` from `support:security-audit`; severe findings carry `severity: High` and a `parse-structured` tag. | Supporting Jobs Tokens. |
| `architecture-review` | Project Action button (planned). | Service catalogue, recent commits, ADR titles. | `# Architecture review` with drift score, layering findings. | `{ verdict, severity, findings[], adrSuggestions[] }`. | One `kind:decision` from `support:architecture-review`. | Supporting Jobs Tokens. |
| `ux-council` | Project Action button (planned). | Frontend mockups, Playwright screenshots in `<job>/results/`, product-runtime events. | `# UX/UI council` with critique per heuristic. | `{ verdict, severity, findings[], heuristics[] }`. | One `kind:decision` from `support:ux-council`. | Supporting Jobs Tokens. |
| `screenshot-compare` | Project Action button (planned). | Two screenshot artifacts (before/after) under `<job>/results/`. | `# Screenshot comparison` with diff verdict, regions of interest. | `{ verdict, severity, regions[], diffPath }`. | One `kind:decision` from `support:screenshot-compare`. | Supporting Jobs Tokens. |
| `source-map` | Skill button (planned). | Repo glob filters, recent commits. | `# Source map` with module summary, hot files. | `{ verdict, modules[], hotFiles[] }`. | One `kind:decision` from `support:source-map`. | Supporting Jobs Tokens. |
| `token-analysis` | Workspace Action (planned). | `logs/tokens/<project>.jsonl`, recent `kind:token-usage` bus messages. | `# Token-spend review` with rolling totals, expensive turns. | `{ verdict, severity, expensiveTurns[], windows[] }`. | One `kind:decision` from `support:token-analysis`; the inputs already carry `kind:token-usage` rollups. | Supporting Jobs Tokens. |

### 9b.3 Constraints

- **Action-driven, not automatic.** A supporting agent fires only when the user (or a deliberate, opt-in scheduler) invokes the action. Pickup loops do not enqueue supporting runs as a side effect.
- **No source edits.** Supporting agents are evidence + decision producers. They never edit code, mockups, or documentation. Steering doc updates that fall out of a supporting run go through normal review-task creation.
- **No lane moves.** A supporting message never moves a job between lanes. The runner remains the single state-machine authority.
- **Markdown is the durable artifact.** When the JSON sidecar fails to parse, the bus message keeps `kind:observation` plus a `parse-malformedjson` tag and surfaces the parser error on `payload.parseError`; the Markdown stays visible. The UI must show the raw fallback warning rather than hiding the report.
- **Token attribution stays split.** Supporting-jobs token usage is reported on bus messages whose participant id starts with `support:`. The token rollup view groups by participant prefix so Job Tokens (coding agent), Supporting Jobs Tokens (`support:*`), and Orchestrator Tokens (`orchestrator*`) never bleed into one another.

### 9b.4 Implementation pointers

- Bridge methods: `AgentMessageBusBridge.RegisterSupportingAgentAsync` and `AgentMessageBusBridge.EmitSupportingAgentReportAsync`.
- Wired paths in [`backend/Endpoints/AnalysisReportEndpoints.cs`](../../../backend/Endpoints/AnalysisReportEndpoints.cs):
  - `POST /api/analysis/{project}/actions/roadmap-alignment` - first wired path; topic `roadmap-alignment`.
  - `POST /api/analysis/{project}/actions/steering-docs-drift` - second wired path; topic `steering-docs-summary-and-drift` (matches the service's `SteeringDocsSummaryDriftService.Topic` constant).
  - Both endpoints emit the bus mirror only when `agentResponse` is supplied; the evidence-only path stays a Manual report so the timeline does not falsely advertise a supporting-agent run that never happened.
- Tests: [`backend.Tests/AgentMessageBusBridgeTests.cs`](../../../../backend.Tests/AgentMessageBusBridgeTests.cs) locks the supporting-agent message shape (participant id, kind, severity mapping, artifact list, parse-failure fallback). Topic-specific assertions for the two wired paths: `EmitSupportingAgentReportAsync_StructuredReport_ProducesDecisionWithBothArtifacts` (roadmap-alignment), `EmitSupportingAgentReportAsync_SteeringDocsDriftTopic_LandsAsCanonicalParticipantAndTags` (steering-docs-drift). The named-producer service tests live in `backend.Tests/RoadmapAlignmentReviewServiceTests.cs` and `backend.Tests/SteeringDocsSummaryDriftServiceTests.cs`.

## 9c. Auto-review diff discovery

Auto-review aspect runners must receive a full job-range diff summary, not a HEAD-only or latest-commit-only view. The summary is built from every commit attributed to the job across all runs (`HeadShaBefore..HeadShaAfter` ranges from the run timeline, deduped) plus the auto-commit recorded on `JobInfo.Commit`. This matches the `/api/tasks/{id}/commits` protocol-pane aggregation so the automated reviewer and the human reviewer inspect the same commit set.

Crash-recovery commits are often empty fixups on top of the real work. If an aspect prompt only receives that latest recovery commit, it can falsely report "0 files changed" and block a successful task. Truly empty aggregates must be stated explicitly as "No commits attributed to this task" rather than rendered as a zero-file commit.

## 10. Changing this contract

Before you touch any of the moving parts:

1. If you change the **schema** for `AgentMessage`, `AgentParticipant`, or `AgentArtifactRef`, update Sections 4-6 of this file in the same PR and bump `schemaVersion` only when the change forces readers to fork.
2. If you add a **new message kind**, add the enum value to `agent-message.schema.json`, document its expected `payload` and `severity` defaults in Section 3.1, and add a UI glyph in the projection so the timeline is not blind to it.
3. If you add a **new participant kind**, add the enum value to `agent-participant.schema.json`, declare which scopes it lives at in Section 2, and confirm Section 8's "preserves the boundary" guards still hold.
4. If you add a **new artifact kind**, add the enum value to `agent-artifact-ref.schema.json` and a UI render branch in the projection panel.
5. If you change the **on-disk layout**, update Section 4 and add a backfill step in Section 9. The legacy layout must keep working until system-review can read the new one.
6. If you wire a **new supporting-agent topic**, add a row to the action catalogue in Section 9b.2, register the participant via `AgentMessageBusBridge.RegisterSupportingAgentAsync`, emit the report via `AgentMessageBusBridge.EmitSupportingAgentReportAsync`, and confirm the topic carries the constraints in Section 9b.3 (action-driven, no source edits, no lane moves, raw fallback warning on parse failure).

The single-source-of-truth rule from [AGENTS.md](../../../../AGENTS.md): if the doc and the code disagree, the doc is wrong. Fix it.
