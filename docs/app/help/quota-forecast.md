# Quota forecast

Workspace Settings > CLI Management > **Quota forecast** answers "when does the
weekly window reach 100 %, and what happens then?" without sampling
`/api/cli/quota` by hand (AGT-3001).

## What the section shows

For every CLI with recorded readings:

- **Session gauge** on the card header: the latest used percent of the 5-hour
  session window and its reset time.
- **Weekly curve** of the last 48 hours, fixed 0 to 100 % scale, with a "now"
  line, the reset line when it falls inside the view, and a dashed projection at
  the current rate. The projection stops at 100 % or at the reset, whichever
  comes first. Hovering shows the nearest reading.
- **Used**, **Rate (3 h)**, **100% at**, and **Reset** for each weekly window,
  plus one sentence that says whether 100 % comes before the reset.
- **Fallback notice**, only when 100 % is forecast before the reset: the
  effective fallback route from `GET /api/cli/quota/model-routes` and whether it
  is armed.

| Fallback badge | Meaning |
|---|---|
| Armed | A fallback model is configured (operator override or equivalence catalogue) and a cross-CLI fallback is below its own cap. New runs switch when the window reaches its cap (default 95 %). |
| Engaged | The CLI already runs on its fallback (cap crossed or operator preference). |
| Not armed | No fallback route exists, or the fallback CLI is itself at its cap. New work will wait for the reset. |

## How the numbers are computed

The backend computes everything; the UI only renders it.

1. **Series.** Every trusted quota probe appends one reading per window
   (probe time, used percent, reset time) to
   `<TaskRepository>/.runtime/quota-history/<cli>.jsonl`. Failed probes,
   suspicious snapshots (AGT-2064), and repeated readings of the same probe are
   not recorded. Readings are kept for 14 days.
2. **Current cycle.** Readings before the latest reset are ignored. A reset is
   detected when a reading's own reset time has passed by the next reading, or
   when used percent drops by more than one point.
3. **Rate.** Change from the earliest reading of the last 3 hours to the latest
   reading, divided by the hours between them. Fewer than two readings or less
   than 20 minutes of span reads "Not enough readings". A flat or falling series
   is idle and has no forecast.
4. **Forecast.** Linear extrapolation from the latest reading to 100 %,
   compared with the latest known reset. An unknown reset counts as "before the
   reset" so the warning is not hidden.

Example from the night that motivated the view: 77 % at 01:27Z and 83 % at
04:27Z give 2 %/h, so 100 % lands at 12:57Z, well before a Friday reset.

This is the recent pace. Admission control keeps using
`QuotaWindowProjection`, which averages from the window start; the two can
differ during a burst.

## API

`GET /api/cli/quota/history?cli=claude&hours=48`

- `cli` is required and must be a known CLI type; `hours` defaults to 48 and
  accepts 1 to 336 (the retention).
- Reads the store only. It never starts a probe.
- Response: `cliType`, `hours`, `from`, `to`, `retentionDays`,
  `rateLookbackHours`, and `windows[]` with `label`, `kind`
  (`session`, `weekly`, `other`), `points[]` (`at`, `usedPct`, `resetAt`), and
  `forecast` (`status`, `currentPct`, `currentAt`, `ratePctPerHour`, `rateFrom`,
  `forecastFullAt`, `resetAt`, `reachesFullBeforeReset`).
- `forecast.status` is one of `no-data`, `insufficient-data`, `reached`, `idle`,
  `full-before-reset`, `resets-first`.

The forecast always reads the whole retained series, so a short `hours` range
does not starve the rate window.

## Code

- Store: `backend/Features/Cli/Quota/QuotaHistoryStore.cs`, fed by
  `QuotaService` after each trusted probe.
- Policy: `backend/Features/Cli/Quota/QuotaForecastPolicy.cs` (pure; matrix in
  `backend.Tests/QuotaForecastPolicyTests.cs`).
- UI: `frontend/src/app/features/quota/components/quota-forecast-panel/` and
  `quota-curve/`; the armed decision is `quotaFallbackStatus` in
  `quota-forecast.util.ts`.
