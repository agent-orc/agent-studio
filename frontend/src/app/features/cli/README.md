# cli

CLI catalog and management surfaces. Covers Copilot / Claude / Codex / Gemini.

## Public API

Imports via `from './features/cli'`. See [`index.ts`](./index.ts).

**Components**:

- `CliAdminPanelComponent`: the **"CLI Management"** hub, a section inside the global Workspace Settings home (rail key `caps`). It answers "what's available and how is it wired": the CLI catalog + per-CLI models and fallback routes (as compact rows), per-window usage caps, and the read-only completion contracts. The status-bar "Usage" button and legacy CLI-admin events open this section.
- `CliConsoleComponent` — read-only console viewer for raw CLI output.

**Types** (lifted from `models/job.model.ts` per ADR-0034):

- `CliModelInfo`, `CliModelCatalog` — model catalogue from `/api/cli/{type}/models`.
- `CopilotModelInfo`, `CopilotModelCatalog` — backwards-compat aliases (the records were Copilot-named before the multi-CLI refactor).
- `CliSessionInfo` — one CLI session row.
- `CliUsageProjectGroup`, `CliUsageSection`, `CliUsageReport` — cross-CLI usage report from `/api/cli/usage`.

## Notable

- CLI types live alongside other CLI surface (admin / console / usage). Job-coupled CLI types (`CliExecution`, `CliSettings`, `CliOutputLine`, `ContinueMode`) stay in `models/job.model.ts` because they participate in the JobInfo graph.
- CLI administration and usage have one hub: the formerly loose right-edge `CliUsageSheetComponent` sidesheet was retired and its management surfaces were folded into `CliAdminPanelComponent` in Workspace Settings. See [`frontend/AGENTS.md`](../../../../AGENTS.md) under "Side-sheet layout contract" for the history note.
