# orchestrator

Per-project orchestrator: log feed, long-lived session, and the orchestrator chat side sheet (where the user talks to the orchestrator agent directly).

## Public API

Imports via `from './features/orchestrator'`. See [`index.ts`](./index.ts).

**Components**:

- `OrchestratorFeedComponent` — per-project log + token rollup + global card; renders inside an overlay opened from the project tab feed icon.
- `GlobalOrchestratorCardComponent` — shows the singleton orchestrator session above the per-project log.
- `OrchestratorSideSheetComponent`: right-hand chat host. Its header contains only the project picker and the context-count badge; Pin, Debug, Settings, and Refresh live in the expanded context menu.
- `OrchestratorContextHeaderComponent`: the "where am I right now" locator inside the expanded context menu. It shows project, task, lane/state, and live-run telemetry. The host resolves the run in scope (`App.orchSideSheetActiveRun`): the open task's run, or the running task in the active project when on the board.

**Types**:

- `OrchestratorLogEntry`, `OrchestratorTokenUsage`, `OrchestratorLogResponse` — log feed.
- `OrchestratorSession`, `OrchestratorSessionResponse` — long-lived session (manager-style conversation alongside agent runs).
- `OrchestratorChatTurn`, `OrchestratorChatAttachment`, `OrchestratorChatResponse` — chat surface.

## Notable

- The transcript is rendered by `<cac-conversation-view>` from `coding-agent-chat/conversation`. A pure host adapter maps orchestrator turns and inline events to `ConversationEvent[]`. `<cac-chat>` from `coding-agent-chat/composer` is mounted with no messages or events, so it contributes only the canonical compact composer: optional context chips, textarea, then one footer with the model selector and Send. The host does not render a picker row, toolbar, breadcrumb, or image-upload action; the library's context-add request opens the host picker as a popover.
- The context-thread switcher remains host-owned because it reads Studio's `/api/orchestrator/sessions` contract, which the library does not know. Collapsed, it is a count badge in the header. Expanded, it is a full-width menu with no outer side frame.
- `coding-agent-chat` is an exact registry dependency. Run `npm install` in `frontend/` after changing its pinned version so the guarded postinstall compatibility bridges validate the published package output.
- The sheet's open/close push contract (host `:host(.is-open) { width: min(640px, 96vw) }` + flex-row-reverse `.app-shell` parent + `<app-sidesheet>` inner-width 100 %) is described in [`frontend/AGENTS.md`](../../../../AGENTS.md) under "Side-sheet layout contract" and pinned by `e2e/orchestrator-side-sheet-position.spec.ts`. Don't introduce `position: fixed` on the host or a fixed px width on the inner `.sidesheet`.
