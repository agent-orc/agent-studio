# tokens

Cross-job token aggregation + the surfaces that visualise it (per-job summary block, workspace timeline, status-bar usage hover).

## Public API

Imports via `from './features/tokens'`. See [`index.ts`](./index.ts).

**Components**:

- `TokenSummaryBlockComponent` — per-project rollup block (totals + per-model breakdown + cost projections); embedded in the orchestrator-feed and the legacy project-detail.
- `WorkspaceTokenTimelineComponent` — the timeline, now embedded as the "Token usage" section of the Workspace-settings home (status-bar "Settings" entry + `#/workspace/tokens` deep-link).
- `UsageHoverPanelComponent` — hosts the status-bar quota strip; clicking a CLI card opens that CLI's own `CliUsageModalComponent` (one modal per CLI, no grouped view).
- `CliUsageModalComponent` — per-CLI usage detail modal (all quota windows + that CLI's top models); its "Manage usage caps" footer opens the CLI-Management panel.

**Types**:

- `JobTokenCall`, `JobTokenSummary` — per-call + per-job aggregates.
- `TokenSummary`, `TokenSummaryByModel`, `TokenSummaryByProject`, `TokenSummaryAggregate` — cross-job rollups.
- `TokenTimeline`, `TokenTimelineProject`, `TokenTimelineCell` — workspace timeline shape.
- `AdHocUsageAggregate`, `AdHocUsageBySource`, `AdHocUsageByDay`, `AdHocUsageByModel` — ad-hoc CLI usage outside jobs (developer's `claude` / `codex` shells).

**Formatting**:

- `formatTokenCount`, `formatTokenCurrencyUsd`, `formatTokenCostTotal`: locale-aware token and USD labels shared with project Token Usage views.

## Notable

- Numbers come straight from the JSONL logs (per-turn token usage) — they're authoritative, not estimates.
- The workspace timeline overlay open/close lives in `features/shell/state/workspace-overlays.service.ts`.
