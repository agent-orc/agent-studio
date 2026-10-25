# token-analysis

Card-centric token analysis (AGT-3267): a table of cards with tokens split by
usage kind (coding run, fix round, review aspect, review grade, deep review,
gate, orchestrator decision, chat, enrichment), model, reasoning level, CLI,
host, rounds, outcome and theoretical cost; charts for usage kinds per card,
tokens over time by model and level with the provider quota windows on the
same day axis, tokens versus rounds by outcome, per-level and per-model
distribution, and the token mix; and a per-card drill-down timeline.

## Public API

Imports via `from './features/token-analysis'`. See [`index.ts`](./index.ts).

- `TokenAnalysisViewComponent`: the view. Workspace scope on the Token usage
  settings page `#/workspace/settings/tokens/analysis`; project scope with
  `[fixedProject]` in the project Token usage rail.
- `openCardTokenAnalysis(project, jobId)`: opens the drill-down of one card
  (board token popover, card detail).

## Notable

- Data: `GET /api/workspace/tokens/analysis?days=&project=` and
  `GET /api/projects/{p}/token-usage/analysis/cards/{jobId}`. The backend folds
  the merged ledger and the pipeline execution logs so every token is counted
  once (`backend/Features/Tokens/Analysis/`).
- Each card is a usage cube of slices. Filters keep or drop slices and every
  total is re-summed from the visible slices (R3). See `token-analysis.util.ts`.
- Filter state is the `ta=` hash segment; the route segment is untouched.
- The quota chart shares the x axis with the time chart, never the y axis.
