# usage-cockpit

Header usage cockpit chips from the Dossier
[docs/header-usage-cockpit/index.html](../../../../../docs/header-usage-cockpit/index.html)
(AGT-2913). This folder holds the shared chips (HUC-S2) and usage alarm
transitions (HUC-S5). The production host mounts the chips in the existing
Studio header and opens the existing usage destination. The fuller detail and
responsive integration remain in HUC-S3/S4.

## Public API

Imports via `from './features/usage-cockpit'`. See [`index.ts`](./index.ts).

- `UsageCliChipComponent` (`app-usage-cli-chip`): one CLI with its weekly and
  current-session windows as one native button, for example
  `Codex WK 15% 5H 32%`.
- `UsageCostChipComponent` (`app-usage-cost-chip`): today's USD token-ledger
  estimate in the workspace-local day, for example `Today $12.48 USD`.
- `UsageSlotChipComponent` (`app-usage-slot-chip`): remote, review and auto
  slot pools. Expanded usage view only; never in the header strip.
- `UsageAlarmStatusComponent` (`app-usage-alarm-status`): a polite status region
  to mount once beside the strip.
- `UsageAlarmStateService`: the host calls `ingest(snapshot)` on each cockpit
  snapshot, then passes `alarmsFor('cli:<id>')`, `alarmsFor('cost')`, and
  `hiddenAlarms(visibleCliIds)` into the chips. Pass hidden alarms only to the
  primary chip.
- `UsageCockpitHostComponent`: polls the authorized cockpit read endpoint while
  visible, isolates workspace changes, and supplies the latched alarms to
  production chips. Secondary CLIs move out of the header at narrow widths;
  their alarms appear as one nonnumeric mark on the primary chip.
- `usage-alarm.policy.ts`: pure alarm evaluation and transition reducer.
- `usage-chip.util.ts`: pure view models, formatting and state rules.
- `models/usage-cockpit.model.ts`: wire types of `GET /api/usage/cockpit`.

## Rules

- Percentages are quota used, keep at most one decimal and are never clamped
  (`142%` stays `142%`).
- An unreported window reads `N/A`; unknown cost reads `N/A`. Neither is ever
  shown as `0%` or `$0.00`. A session window never borrows the weekly value.
- Visible cost from $10,000 uses a complete compact amount (`$12.5K`); the
  accessible name and the tooltip carry the exact amount.
- `5H` appears only when the provider reports a five-hour window; otherwise
  the tag is `Session`.
- Base data states are normal, loading, unknown, stale, suspicious and
  (cost only) partial coverage. Each changes the accessible name; stale and
  suspicious values are muted and use a clock or question mark.
- Staleness is measured against the host's `now` input; without it each chip
  follows its own 30-second clock (`usage-clock.ts`), so a snapshot that ages
  past its TTL turns stale on screen without a new projection.
- Times use the workspace IANA zone with the UTC equivalent alongside.
- Chips emit `activate`; the host owns the dialog or pool region and
  passes `expanded` and `controls` back.

## Alarm rules (HUC-S5)

- Precedence is explicit provider limit, then confirmed quota or budget
  overrun, then suspicious, stale, loading/unavailable and normal. Simultaneous
  causes remain in the accessible name and detail.
- A fresh, complete weekly or session quota above 80% warns. Exactly 80% is
  headroom; values above 100% retain their reported value. A provider limit
  warns at any percentage and stays latched until trusted recovery.
- Cost warns only when a known daily or weekly USD total strictly exceeds an
  optional configured budget from the projection. Equality stays quiet and
  absent budgets create no limit. The current projection may omit budgets.
  Partial totals can confirm an overrun but cannot establish safety.
- Stale, suspicious, partial or unreadable samples do not clear an alarm.
  A quota window clears only when that same window has a trusted numeric
  reading; an omitted window cannot clear a latched warning.
  Crossing a reset instant alone cannot clear an alarm; a trusted recovery or
  confirmed next window resolves the old cycle quietly. New alarms and limit
  recovery are announced once per source/window/cycle;
  ordinary numeric refreshes and resolved history stay quiet.
- A full-surface semantic tint accompanies a warning mark or the word
  `Limited`. On a compact phone chip, only weekly quota is numeric; alarms
  on hidden CLIs add one nonnumeric mark and a spoken explanation to the
  primary chip. No alarm changes routing or model pins.

## Evidence

The standalone `usage-chips-mockup` app (`src/mockups/usage-chips/`) mounts
the real components with fixtures. `e2e/mockups/usage-chips.spec.ts` checks
geometry, accessible names, focus and state labels in both themes and writes
screenshots. Build first with `npm run build:mockup:usage`.
`?view=alarms` mounts the HUC-S5 gallery. `e2e/mockups/usage-alarms.spec.ts`
checks warning boundaries, limit precedence, data quality states, polite
announcements and a hidden-provider phone alarm in both themes. Its screenshots
and contrast JSON are written under `JOB_RESULTS_DIR/usage-alarms`.
