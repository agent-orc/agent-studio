# quota

CLI subscription / rate-limit visualisation. Each CLI exposes one or more "windows" (monthly premium for Copilot; 5h+weekly for Codex; rate-limit reset for Claude when over-quota).

## Public API

Imports via `from './features/quota'`. See [`index.ts`](./index.ts).

**Components**:

- `QuotaStripComponent` — compact strip surfacing each installed CLI's quota status; lives at the top of the CLI Usage sidesheet.
- `HeaderQuotaComponent` — donut-ring variant for the status-bar usage hover panel.
- `QuotaForecastPanelComponent`: CLI Management "Quota forecast" section (AGT-3001): per CLI the 48 h weekly curve, the 3-hour rate, the time of 100 %, the reset, a session gauge, and the armed fallback when 100 % comes first. Data from `/api/cli/quota/history`.
- `QuotaCurveComponent`: the SVG weekly curve used by the forecast panel, with a hover crosshair and a visually hidden table view.

**Types**: `QuotaWindow`, `QuotaSnapshot`, `QuotaReport`.

## Notable

- `usedPct` above 100 means the user has overshot the included allotment.
- The "↻" buttons force a synchronous re-probe; calls take several seconds because they spawn a fresh PTY.
- Strip vs donut is a deliberate split: the strip is for density when the user wants the full picture; the donut header is for at-a-glance "do I have headroom" in the status bar.
