# Persistent Orchestrator Chat

This document describes the product and architecture contract for the always-available chat window with the orchestrator. The Task Server context foundation was accepted on 10 August 2026.

The feature is not "another task chat". It is the user's standing relationship with the system that controls the board: what happened, why decisions were made, what the application knows about each project, and what should happen next.

For the redesign handoff, roadmap slices, and UI contract, see [Orchestrator Chat Redesign Handoff](./orchestrator-chat-redesign-handoff.md).

## Research Notes

Current coding-agent systems keep continuity through a few repeating patterns:

- **Saved local conversations.** Claude Code saves conversations locally and supports `claude --continue`, `claude --resume`, and in-session `/resume` so a task can span multiple sittings. See [Claude Code common workflows](https://code.claude.com/docs/en/common-workflows#resume-previous-conversations).
- **Explicit resume, continue, and fork APIs.** Claude's Agent SDK exposes continue, resume, and fork semantics as first-class session options. See [Claude Code Agent SDK sessions](https://code.claude.com/docs/en/agent-sdk/sessions).
- **Background-agent dashboards.** Cursor Background Agents expose a sidebar where users can view agents, send follow-ups, inspect status, and take over. Cursor also documents that background agents require data retention for a few days. See [Cursor Background Agents](https://docs.cursor.com/en/background-agents).
- **Chat history and past-chat references.** Cursor stores regular agent chat history locally in SQLite and lets users open previous chats or reference past chats as context. Background-agent chats live separately in a remote store. See [Cursor chat history](https://docs.cursor.com/agent/chat/history).
- **Programmatic session resumption.** Codex has an interactive resume path and a headless `codex exec resume` path. Open issues around headless forking show the same design pressure: orchestration surfaces need resume and fork without driving a TUI. See [openai/codex issue 11750](https://github.com/openai/codex/issues/11750).

The pattern is clear: the user's feeling that a session is "still open" does not require a process to stay alive forever. It requires a stable session identity, durable transcript, resumable execution, visible status, and a compact memory object that can be reloaded after days.

## Product Shape

The app exposes **Orchestrator Chat** as an always-available project-level
surface. Chat remains a resizable push-layout side sheet that can be pinned or
hidden; it is not a new full-screen route and does not replace the Board,
Project Hub, status-bar toggle, or other existing entries. Its open/closed
posture and width survive navigation and reload. Ordinary navigation never
opens it.

The per-user **Open project Chat on project entry** preference defaults to
Open, but applies only to an explicit Board or Project Overview entry when the
shell has no active editor tab. It is not evaluated for tab switches, task
routes, Dossiers, Wiki pages, or other ordinary navigation.
Its saved opt-out disables that one standard-entry exception while every
explicit Chat control remains available. Passive restore never moves keyboard
focus into the composer.

The chat has two scopes:

- **Global orchestrator.** Answers cross-project questions: what is happening across the board, which projects need attention, whether the queue is healthy, and what should be reviewed first.
- **Project orchestrator.** Answers project-local questions: what this application does, what jobs ran recently, what decisions were made, what the roadmap says, which tasks are blocked, and what follow-up should be created.
- **Task context.** Answers read-only questions about one task in a separate task transcript. It does not replace or alter the task detail Activity tab.

The user should be able to tell which scope is active. A target selector is better than pretending one model sees everything at once.

## Architecture Answer

The user-facing chat should use the same canonical orchestrator session that owns the scope:

- A board-level chat talks to the **global orchestrator**.
- A project-level chat talks to that project's **canonical project orchestrator**.
- Task agents remain separate. They execute jobs and report evidence; they do not become the user's standing orchestrator chat.

This means the app does not need two competing project orchestrators. It needs two levels:

1. One global orchestrator for cross-project awareness.
2. One project orchestrator per watched project for project-local memory, decisions, and chat.

That model matches the existing ADR-0007 and ADR-0009 direction. The chat is not a second brain next to the orchestrator. It is the visible conversation surface for the orchestrator that already owns the project or board scope.

## Managed Context and Session Contracts

Managed conversation context and CLI session mechanics are separate contracts.
The selected context store is the authority for project and task context
identity, transcripts, receipts, lifecycle visibility, and short summaries.
The monolith profile selects its in-process, registry-backed store when no
standalone Task Server URL is configured. A valid absolute HTTP or HTTPS
`TaskServer:BaseUrl` selects the remote Task Server store. Machine-local session
records may support execution continuity, but they are not Chat History and
cannot become a competing transcript store.

Every project is materialized as a permanently visible managed context. Opening
Orchestrator Chat for a task materializes a new managed task context. It remains
visible until the task reaches `7-archive`; archive sets `hiddenAt` and excludes
it from the current list without deleting the context or any turns. If the task
leaves archive, the retained context becomes visible again.

The backend exposes a context-keyed session registry for the canonical
orchestrator scopes. The accepted context keys are:

- `global` for the board-level orchestrator.
- `project:<PROJ-ID>` for one watched project's canonical orchestrator.
- `workbench:<PROJ-ID>/<DOSSIER-KEY>` for a Dossier-scoped orchestrator
  context, keyed by the Dossier's catalogue key (e.g. `AGT-W43`; AGT-2725).
  Unlike every other kind, its implicit context is the Dossier's own
  `workbench.json` descriptor and entrypoint excerpt only - no board or task
  digest carries in, and no project transcript is read or written for it. See
  [the AGT-2514 decision dossier's recorded operator
  override](../operations/kontext-orchestrator-chats/index.html#task-chat)
  for why this deviates from "Wiki, Workbench, board, file, and diff remain
  context inputs, not new transcript owners."
- `task:<PROJ-ID>/<KEY>` for a task-scoped orchestrator context.

The execution-session registry is lazy: reading a valid context key creates the
machine-local runtime record when it does not already exist. Records are stored below
`<TaskRepository>/.metadata/orchestrator-sessions/<encoded>/`, where
`session.json` holds the current session metadata and `history.jsonl` is the
append-only sidecar for future session events. The encoded path segment is the
URL-safe form produced by `OrchestratorContextKey`.

The HTTP surface combines the selected context projection with asynchronous
runtime dispatch:

- `GET /api/orchestrator/sessions` lists current project and
  task contexts with their short summaries, merged with ephemeral runtime
  status and queue position. Archived task contexts are omitted.
- `GET /api/orchestrator/sessions/{contextKey}` returns or creates one valid
  context-key record. Invalid context keys return `400`.
- `POST /api/orchestrator/sessions/{contextKey}/turns` appends a user prompt
  to the canonical session context and dispatches it through the existing
  orchestrator CLI runner. If `session.json` has a `sessionId`, the runner uses
  the resume path; otherwise it starts a fresh one-shot and stores the captured
  session id.
- `POST /api/orchestrator/sessions/{contextKey}/park` parks queued turns for
  that context and cancels the active turn if one is running.

Turn dispatch is capped by `Orchestrator:SessionTurns:ActiveLimit`, default
`4`. Requests above the cap return `status: "queued"` with a one-based
`queuePosition`; requests admitted immediately return `status: "active"`.
All state changes append compact entries to `history.jsonl`.

The legacy singleton global orchestrator session is migrated into the `global`
context key on first registry access so existing installs keep their board-level
session identity.

## Per-Context Chat Transcript

The side sheet's chat follows the operator's navigation (MC-2, Concept §4): the
board is a `project:<PROJ>` context, an open task page is a `task:<PROJ>/<KEY>`
context, and a pin freezes whichever is current. Each context has its own
transcript so a pinned task and the board no longer share one history.

- `GET /api/runner/{contextKey}/orchestrator-chat` returns the transcript for a
  navigation context. `{contextKey}` is the same canonical key as the session
  registry — `project:<PROJ>` (one path segment) or `task:<PROJ>/<KEY>` (two).
  The response is `{ contextKey, project, turns }`.
- `POST /api/runner/{contextKey}/orchestrator-chat` sends a user message and
  persists both turns to that context's transcript, returning
  `{ contextKey, project, reply }`.

Storage is context-keyed in the selected topology. A remote Task Server uses
its SQLite store. The monolith profile uses its existing context-keyed JSONL
files directly and derives the project list from registry-backed watch paths,
without a self-HTTP roundtrip. Both modes retain append-only turns, compact
source receipts, token usage, lifecycle visibility, and a short latest-intent
summary. Resolved source bodies are never copied into receipts. The
literal-prefixed Studio routes are strictly more specific than the
`{projectName}` compatibility route, so routing still prefers them without
ambiguity.

When a remote Task Server is configured, Studio reads existing
`<watchPath>/.orchestrator/orchestrator-chat.jsonl` project history as an
idempotent migration source and imports it into the Task Server. The source
file is retained. In local mode, that context-keyed file remains the active
store.

Side-sheet execution is GPT-only and stateless. The context key selects both
the authoritative thread and the ORCH-1 read digest for the turn, so a task thread
gets its focused task facts and a project thread cannot receive facts from
another project.

Every send carries an immutable typed envelope with conversation scope, active
surface, explicit stable references, the context budget, and capture time. The
backend validates the envelope against the route before persisting the user
turn. Scope mismatch, cross-project references, path traversal, and out-of-root
symlink resolution stop before model invocation.

Prompt assembly is deterministic: scoped preamble, context ledger, automatic
evidence, explicit attachments, bounded conversation continuity, then the new
user message last. Automatic evidence has a 4,000-token soft cap and 6,000-token
hard cap; explicit references can expand the total to 8,000 tokens. The latest
four to eight semantic turns provide continuity on every stateless call.

Each reply persists a receipt linked to its user turn id. The receipt records
stable source id, kind, revision or hash, freshness, included characters and
estimated tokens, status, omission reason, and applied budget. It stores no
resolved source body.

The project composer shows the active tab as automatic context and keeps
explicit additions as removable reference chips. **Add context** opens a
project-bound picker with the current tab first, followed by Tasks, Wiki and
Dossiers, Files, and Commits. Search results never carry repository content
in the browser. They add only stable task keys, page refs, repository-relative
paths, or project-qualified commit SHAs to the next envelope. Send snapshots
those refs once, so later navigation cannot change the in-flight turn.

The latest reply renders a compact **Used context** disclosure. Its inspector
reads the persisted context-store receipt and names included, excerpted,
unresolved, unavailable, blocked, and budget-excluded sources with revision,
freshness, character, token, and reason metadata. The header opens **Chat
history**, which consumes the current-context list and its short
summaries. A watched project contributes exactly one permanent project row;
archived task rows remain retained by the selected store but are omitted here.

The side sheet consumes these routes directly: it derives the context key from
navigation (`contextKey` on `OrchestratorSideSheetComponent`, frozen while
pinned) and reads/sends through `getOrchestratorChatByContext` /
`sendOrchestratorChatByContext`. The reload effect tracks the context key, so
moving between the board and a task in the same project swaps the visible
transcript even though the project is unchanged. When no context key is
derivable it falls back to the per-project route, keeping the board identical.

Navigation changes the next-message context, not an in-flight turn. Send
snapshots the context key and typed envelope before attachment work begins. A
reply that completes after the user navigates remains attached to that original
managed context and cannot overwrite the newly visible transcript. The new
context's composer remains available while the earlier context continues in
the background.

The side sheet owns one non-windowed vertical scroll container for its live
transcript. Orchestrator rows mix short turns, multi-line Markdown, badges, and
status rows, so replacing off-screen rows with one fixed-height estimate makes
the scroll height oscillate whenever a turn is appended. Stable event ids keep
existing DOM rows mounted, while the shared conversation stick-to-bottom
directive follows the host scroll container only when the operator is already
at the latest turn. Long, searchable history remains the responsibility of the
separate virtualised project-chat history surface.

Polling also preserves semantic signal identity. The chat-turn signal compares
the complete persisted turn contract before publishing a fresh response graph,
and the Markdown task-reference catalogue compares its ordered label/key pairs.
An unchanged chat or board heartbeat therefore does not invalidate every
Markdown body. This is load-bearing for task microcards: they are host-hydrated
inside rendered Markdown, so an unnecessary `safeHtml` refresh would discard
and recreate only those enriched nodes while ordinary text appeared stable.

## Execution Location and Checkout Context

Side-sheet chat execution follows the same project assignment that controls
card pickup. When `remoteExecutionEnabled` and `executionRunner` select a remote
Runner, project and task chat turns are queued for that Runner. The host
prepares a dedicated `project-chat` worktree from the same per-project git cache
used by card runs, fetches the configured integration branch, and starts Codex
with that checkout as its working directory. A project without a remote
assignment continues to use the local project root.

Studio never reaches into a Runner over SSH. The Runner pulls an opaque chat
work item through claim, renew, and fenced completion endpoints. This
in-process broker is a compatibility seam toward the durable Task Server work
permit model described by ADR-0063 and the distributed target architecture.
Repository materialization and CLI execution remain Runner responsibilities.

For local execution, the working directory resolves in this order: the watch
entry's `RootPath`, the project's registry `RepositoryPath`, then the operating
system temporary directory. The final fallback emits a warning containing the
project and the missing-path reason. Local Codex chat one-shots pass
`--skip-git-repo-check` because the chat sandbox is read-only and a missing Git
checkout must not block a Q&A turn.

Every side-sheet chat read and send response includes an `executionContext`
with:

- `executionKind`: `local` or `remote`;
- `hostName`: `local` or the hostname reported by the executing Runner;
- `repoPath`: the exact local or Runner-host checkout;
- `branch` and `headSha`: the branch context and checked-out revision;
- `state` and `capturedAt`: whether a remote inspection is still resolving and
  when the values were observed.

Opening a remote chat schedules a non-mutating checkout inspection, so the
header may briefly show the assigned Runner and `resolving`. Completion of an
inspection or chat turn replaces that projection with the exact host, path,
branch, and HEAD used on the Runner. The header presents the values as a compact
two-line mini indicator and retains the full revision in its tooltip. Remote
Codex chat runs remain read-only. The context key still selects transcript and
ORCH-1 read scope; execution placement is a project-level assignment.

## Application Read Context (ORCH-1)

Both chat dispatch paths use one deterministic context builder:

- the side-sheet path under `/api/runner/{contextKey}/orchestrator-chat`;
- the canonical session-turn path under
  `/api/orchestrator/sessions/{contextKey}/turns`.

The builder folds lane counts, recent lane transitions, progress tasks and
lifecycle phases, cached CLI quota windows, PUB-1 publish targets, backend and
filesystem-watcher health, and recent decision-journal verdicts into a bounded
text digest. `global` reads all registered projects. `project:<project>` and
`task:<project>/<task>` are project-isolated, and task scope adds a focused task
row. Raw quota samples, full decision prompts/responses, and unbounded logs are
never copied into the prompt.

The same digest is inspectable without spending a model turn:

- `GET /api/orchestrator/context/global`
- `GET /api/orchestrator/context/project:{projectId}`
- `GET /api/orchestrator/context/task:{projectId}/{taskKey}`

Each response carries `contextKey`, `capturedAt`, the compact `digest`, and
per-source freshness/degradation metadata. The matching `POST .../refresh`
routes express explicit operator intent: they re-probe quota before rebuilding.
Normal reads and chat turns stay cheap by using the existing quota cache. The
side-sheet Refresh action calls this explicit path and shows the real capture
time instead of a synthetic memory-age label.

The digest and visible chat prompt also carry component ownership routes from
Project Hub metadata. Navigation context answers where feedback was observed;
the separate routing block answers which project owns the implementation and
which consumers, packages, releases, environments, and deployment steps must
be completed. The prompt contains stable project ids, repository/package
identifiers, mapping evidence, confidence, and mapping version only. It does
not include filesystem paths or secrets. A low-confidence or conflicting route
instructs the orchestrator to ask before proposing or creating a task.

## Chat Switcher Rail

The side sheet includes an optional, collapsed-by-default chat switcher. Its
chip reports the number of active or queued contexts; expanding it groups the
registry into Global, Projects, Dossiers, and Tasks (AGT-2725 added the
Dossiers group between Projects and Tasks). Rows expose the central short
summary, runtime state, local unread state, and cumulative token usage. Clicking a row name changes the chat
context without moving the workspace. The separate arrow navigates to that
context's all-project board, project board, Dossier tab, or task tab.

Active and queued contexts are acute current state. They use a visible status
dot and label in both the side-sheet list and the central Chat History view.
The status-bar Chat entry also shows a pulsing active count while any reply
remains outstanding, including when the side sheet is closed.

The rail reads `GET /api/orchestrator/sessions`. The context row, summary, and
durable usage come from the selected context store; its runtime badge is a snapshot of the
in-memory turn dispatcher (`active`, `queued`, or `parked`). Unread state is intentionally local to the
browser because the registry does not own per-user read receipts. Global is
listed as a first-class registry context, but until a global transcript endpoint
exists its selected state renders an explicit empty transcript instead of
silently borrowing a project's conversation.

The side sheet is a host of the canonical `coding-agent-chat` composer. Studio
derives a location context from the active `StudioTabStateService` tab
(`buildComposerLocationContext`): the large scope is the project, while the
local scope names the active Board, Task, Dossier, Project Hub, Wiki, URL
preview, or other tab surface. The side sheet renders that value inside CAC's
standard composer footer — currently via the `[chat-foot-start]` projection
slot, until the library exposes a first-class `composerContext` input — and
CAC never re-derives navigation state. Changing tabs updates the value in
place, so the composer stays mounted and preserves its draft. Studio does not
project task-creation buttons or a parallel footer workflow.

## Memory Model

The orchestrator's memory should be layered, inspectable, and rebuildable:

| Layer | Purpose | Storage |
|-------|---------|---------|
| Live session id | Optional CLI execution continuity. It is not transcript authority. | Existing machine-local orchestrator-session JSON files. |
| Managed context | Authoritative project/task identity, transcript, source receipts, lifecycle visibility, short summary, and token usage. | In-process context-keyed JSONL for the monolith profile; Task Server SQLite and versioned API for remote mode. |
| Event log | Durable record of decisions, follow-ups, overrides, and app actions outside managed chat turns. | Existing `orchestrator.jsonl`. |
| Working memory | Compact "what I currently know" briefing: project purpose, current roadmap, active risks, recent task results, open decisions, next tasks. | New per-project memory snapshot, for example `.orchestrator/orchestrator-memory.md` or JSON. |
| Source context | Human-owned truth: README, ROADMAP, AGENTS, architecture decisions, skills, project docs. | Existing repository files. |
| Task evidence | Job results, status summaries, logs, screenshots, commits, and review decisions. | Existing job folders and run artifacts. |

The memory snapshot is not a secret system prompt. The user should be able to open it, diff it, refresh it, and ask why something is in it.

## Memory Update Policy

Memory should be maintained by a deterministic pipeline first, then optionally refined by the model:

1. Collect source facts: README, ROADMAP, AGENTS, ADRs, project settings, active jobs, recent completed jobs, orchestrator events, review outcomes.
2. Normalize them into a compact memory document with stable sections: project purpose, current state, recent decisions, open risks, next tasks, known user preferences.
3. Keep source links or file references for every important claim.
4. Ask the LLM to compress or reconcile only where judgment is useful, for example grouping related follow-ups or identifying drift.
5. Write the resulting memory snapshot as an artifact the user can inspect.

This keeps memory from becoming a hidden, self-referential chat summary. The
source of truth stays in repository and task evidence; the selected context
store is the durable authority for chat turns and their source ledger.

## Keeping It Alive

The chat should feel alive because the system continuously records what happens, not because it blindly pings the model.

Recommended behavior:

- On app start, resume the saved global and project orchestrator sessions where available.
- When a job changes state, a run finishes, a circuit breaker fires, or the user makes an override, append a structured event to the orchestrator log.
- Periodically, or after meaningful events, update the compact memory snapshot from deterministic sources: job results, roadmap, project settings, recent decisions.
- Only call the LLM when the user asks a question, an auto-mode decision requires it, or a memory refresh needs model judgment.
- If a session id is stale, re-boot from the memory snapshot and event log, then show the reset plainly in the chat.

A heartbeat may be useful for UI status, but it should be a local freshness check. A "keep alive" LLM call that only burns quota is not a product value.

## Context Transparency

The chat window should include a **Context** view. It answers:

- Which orchestrator am I talking to, global or project?
- Which CLI, model, and session id is backing it?
- When was it booted or last resumed?
- What source files were loaded into the initial briefing?
- What memory snapshot is active?
- Which recent jobs and decisions were summarized into memory?
- What is the last known context or token usage where the CLI exposes it?

This makes the orchestrator feel present without becoming mystical. The user can see what it knows and where that knowledge came from.

## Session Events Versus Conversation

CLI session mechanics are evidence, not the primary chat object. A continuation, recovery, steering handoff, or auto-orchestrator intervention should be rendered as a compact timeline event unless it contains semantic content the user needs to read as a message.

Examples of event-bubble content:

- session continued,
- session recovered,
- user steered the agent,
- orchestrator reissued a follow-up,
- memory refreshed,
- auto-loop circuit breaker fired.

The raw event remains expandable and auditable, but the main surface stays a continuous Global or Project conversation. This keeps the user's attention on project intent, decisions, and next steps instead of a registry of transport/session details.

## Control Surface

The orchestrator chat may eventually control the app, but only through typed app actions, not free-form DOM manipulation.

Examples:

- Create a task draft from a chat answer.
- Move a task only through the same state-transition API as the board.
- Start, stop, or continue a task with the same gates the UI uses.
- Refresh project memory.
- Summarize recent job results into roadmap proposals.
- Open a project, job detail, protocol pane, or git view.

Actions that change task state or spend CLI quota should be visible as planned actions before execution unless they are already covered by auto-mode rules.

The chat can still feel powerful without bypassing the application. The orchestrator proposes actions in structured form; the app validates and executes those actions through the same endpoints and policies the UI uses.

## Forking Semantics

The default chat should talk to the canonical orchestrator session for its scope. That gives the user continuity and keeps the session identity stable.

Forks are still useful, but they should be explicit:

- **Speculative fork.** Explore a possible plan without polluting the canonical session.
- **Research fork.** Investigate a large question and return a compact finding.
- **Recovery fork.** Reconstruct context if the canonical session becomes stale.

The product should avoid two peer project orchestrators that both think they own the same project. The architecture is one global orchestrator, one canonical project orchestrator per watched project, and optional short-lived forks that report back.

Fork results should be folded back as evidence, not as a new owner. A fork can produce a research note, a task draft, or a memory update proposal. The canonical orchestrator decides whether to absorb it.

## First Implementation Slice

The smallest valuable slice is:

1. Add a pinned or collapsible Orchestrator Chat panel.
2. Let the user choose Global or current Project scope.
3. Send messages to the existing global or project orchestrator session and append replies to the orchestrator log.
4. Render the same chat after reload from the log.
5. Add a Context view showing session id, model, boot source, last activity, and memory snapshot status.
6. Add a manual "Refresh memory" action that builds or updates the project memory snapshot from deterministic sources first.

That slice gives the user a durable conversation and visible context before the chat starts controlling the rest of the app.

## Open Product Questions

- Should memory refresh be automatic after every completed job, or only after review acceptance? Review acceptance is safer because it means the user agrees the result is real.
- Which actions need confirmation? A good first rule: read-only and draft-creation actions can run immediately; task state changes, CLI starts, stops, and continues need visible confirmation unless auto-mode already owns the decision.
- How much of the raw transcript should be loaded into chat? The default should be summaries plus source links; full logs stay one click away.
