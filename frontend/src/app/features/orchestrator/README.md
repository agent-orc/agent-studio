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

- The transcript is rendered by `<cac-conversation-view>` from `coding-agent-chat/conversation`. A pure host adapter maps orchestrator turns and inline events to `ConversationEvent[]`. `<cac-chat>` from `coding-agent-chat/composer` is mounted with no messages or events, so it contributes only the canonical composer.
- **Composer contract (coding-agent-chat 0.4.1, CAC-20).** The composer renders at most three rows: the context chip row when chips exist, the textarea, and the footer (model selector left, Send right). The host binds no `toolbarStart`, `routingLabel`, `contextLabel`, or `composerContext`, so the library draws neither a toolbar row nor a breadcrumb — the sheet header and the automatic chip already say where the operator is. `allowAttachments` is `false`: this composer has no image upload, and the chat POST carries no `attachments`.
- **There is no host-owned picker row.** Context state (the automatic current-tab block, every attached source, and each one's token estimate) is projected into the library's `contextAttachments` input by `buildComposerContextAttachments` in `composer-location-context.ts`. `OrchestratorContextPickerComponent` is popover-only: the library's `+` raises `contextAttachmentAddRequested`, the host opens the popover, and `contextAttachmentRemoved` removes that attachment. Removing the automatic chip (id `context:automatic`) means "leave the current tab out of the next message"; task scope keeps it mandatory and answers that removal with a no-op, which the chip tooltip states.
- The "GPT-only · Inherited Codex default" routing chip went away with the toolbar row. The GPT-only policy is still visible as the per-CLI `disabledReason` inside the model picker, and `selectionSource` still travels on every send; the explicit-vs-inherited provenance is no longer rendered, because `<cac-chat>` exposes no seam for the selector's trigger tooltip.
- The context-thread switcher remains host-owned because it reads Studio's `/api/orchestrator/sessions` contract, which the library does not know. Collapsed, it is a count badge in the header. Expanded, it is a full-width menu with no outer side frame.
- The package is pinned to an exact published registry version (`coding-agent-chat: 0.4.1`), never a `file:` dependency. Two postinstall bridges in `frontend/scripts/` patch the published output for fixes the release does not carry yet; both guard on the exact version, so an upgrade fails loudly until the bridge is retired or retargeted.
- The sheet's open/close push contract (host `:host(.is-open) { width: min(640px, 96vw) }` + flex-row-reverse `.app-shell` parent + `<app-sidesheet>` inner-width 100 %) is described in [`frontend/AGENTS.md`](../../../../AGENTS.md) under "Side-sheet layout contract" and pinned by `e2e/orchestrator-side-sheet-position.spec.ts`. Don't introduce `position: fixed` on the host or a fixed px width on the inner `.sidesheet`.
