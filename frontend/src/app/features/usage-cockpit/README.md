# usage-cockpit

Header usage cockpit chips from the Dossier
[docs/header-usage-cockpit/index.html](../../../../../docs/header-usage-cockpit/index.html)
(AGT-2913). This folder holds slice HUC-S2 (the shared chip components) and
slice HUC-S5 (usage alarm transitions). The detail popover and sheet (HUC-S3)
and header integration (HUC-S4) are separate slices.

## Public API

Imports via `from './features/usage-cockpit'`. See [`index.ts`](./index.ts).

- `UsageCliChipComponent` (`app-usage-cli-chip`): one CLI with its weekly and
  current-session windows as one native button, for example
  `Codex WK 15% 5H 32%`.
- `UsageCostChipComponent` (`app-usage-cost-chip`): today's USD token-ledger
  estimate in the workspace-local day, for example `Today $12.48 USD`.
- `UsageSlotChipComponent` (`app-usage-slot-chip`): remote, review and auto
  slot pools. Expanded usage view only; never in the header strip.
- `UsageAlarmStatusComponent` (`app-usage-alarm-status`): the polite status
  region that speaks alarm transitions. Mount it once beside the strip.
- `UsageAlarmStateService`: latched alarms. The host calls `ingest(snapshot)`
  for every cockpit snapshot and passes `alarmsFor('cli:<id>')` /
  `alarmsFor('cost')` and, on the primary chip, `hiddenAlarms(visibleCliIds)`
  into the chips.
- `usage-alarm.policy.ts`: pure alarm evaluation and the transition reducer.
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
- States in this slice: normal, loading, unknown, stale, suspicious and
  (cost only) partial coverage. Each state changes the accessible name and
  shows a clock or question mark; values are muted, never recoloured as
  alarms.
- Times use the workspace IANA zone with the UTC equivalent alongside.
- Chips emit `activate`; the host owns the dialog or pool region and
  passes `expanded` and `controls` back.

## Alarm rules (HUC-S5)

- Precedence: provider limited, then budget or confirmed quota alarm, then
  suspicious, stale, loading/unavailable, normal. Simultaneous causes stay in
  the accessible name and the detail.
- Quota warns when a fresh, confirmed weekly or session window is strictly
  above 80% (`80` is headroom, `80.1` warns, values above 100 warn unclamped).
- `limited === true` is a critical alarm at any percentage. `limited === null`
  (limit source unreadable) makes no claim.
- Cost warns when the known daily or weekly total strictly exceeds
  `dailyBudgetUsd` / `weeklyBudgetUsd` from the projection. No budget, no
  alarm; this code never invents one. Today the projection reports no budget
  because no workspace USD budget owner exists yet. Partial totals can
  confirm an overrun but never read as within budget.
- Only a trusted sample clears an alarm: a complete, confirmed quota sample
  within its TTL (and `limited === false` for a limit), or complete and fresh
  ledger coverage. Stale, suspicious, partial and unreadable data keep the
  latched alarm. Quota and budget alarms expire quietly at their reset or
  local period end.
- Each alarm is announced once per source, window and reset cycle; a limit
  announces its trusted recovery once. Numeric ticks are never announced.
- Treatment: whole-surface tint from `--studio-warn` / `--studio-error` plus
  a word (`Limited`) or the warning mark; never colour alone, no accent bar.
  The compact (phone) chip shows the weekly window only and uses the mark.
  Alarms of CLIs whose chip is hidden add one nonnumeric mark to the primary
  chip and an `Also needs attention:` sentence to its accessible name.
- Visibility only: nothing here changes routing, correctness floors or model
  pins.

## Evidence

The standalone `usage-chips-mockup` app (`src/mockups/usage-chips/`) mounts
the real components with fixtures. `e2e/mockups/usage-chips.spec.ts` checks
geometry, accessible names, focus and state labels in both themes and writes
screenshots. `?view=alarms` mounts the HUC-S5 alarm gallery;
`e2e/mockups/usage-alarms.spec.ts` checks the state matrix, tint and contrast,
the phone hidden-provider alarm and once-per-cycle announcements. Build first
with `npm run build:mockup:usage`.
