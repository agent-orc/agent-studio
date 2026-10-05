# usage-cockpit

Header usage cockpit chips from the Dossier
[docs/header-usage-cockpit/index.html](../../../../../docs/header-usage-cockpit/index.html)
(AGT-2913). This folder holds slice HUC-S2 (the shared chip components) and
slice HUC-S3 (the usage detail popover and sheet). Header integration
(HUC-S4) and alarm states (HUC-S5) are separate slices.

## Public API

Imports via `from './features/usage-cockpit'`. See [`index.ts`](./index.ts).

- `UsageCliChipComponent` (`app-usage-cli-chip`): one CLI with its weekly and
  current-session windows as one native button, for example
  `Codex WK 15% 5H 32%`.
- `UsageCostChipComponent` (`app-usage-cost-chip`): today's USD token-ledger
  estimate in the workspace-local day, for example `Today $12.48 USD`.
- `UsageSlotChipComponent` (`app-usage-slot-chip`): remote, review and auto
  slot pools. Expanded usage view only; never in the header strip.
- `UsageDetailSurfaceComponent` (`app-usage-detail-surface`): one native
  `<dialog>`. Desktop opens it with `show()` as a nonmodal popover anchored
  under the visible trigger; phone (`max-width: 767px`) opens it with
  `showModal()` as a bottom sheet. The host passes the snapshot and a
  `UsageDetailFocus` and clears it on `closed`.
- `UsageDetailPanelComponent` (`app-usage-detail-panel`): the shared content.
  Every CLI (all quota windows, reset local and UTC, observed models), the
  cost section (today and week, project split with unattributed, live runs,
  slot pools) and ledger links. The focus only picks the section that gets
  focus; every section stays in the panel.
- `usage-chip.util.ts`: pure view models, formatting and state rules.
- `usage-detail.util.ts`: detail view models, reconciliation, model grouping
  and ledger hash links.
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
  alarms. Quota warning, provider limited and budget crossed are HUC-S5.
- Staleness is measured against the host's `now` input; without it each chip
  follows its own 30-second clock (`usage-clock.ts`), so a snapshot that ages
  past its TTL turns stale on screen without a new projection.
- Times use the workspace IANA zone with the UTC equivalent alongside.
- Chips emit `activate`; the host owns the dialog or pool region and
  passes `expanded` and `controls` back. A chip with `controls` carries
  `data-usage-trigger` (`cli:<id>` or `cost`). The surface finds its anchor
  and focus-return target through that attribute, so a breakpoint change that
  swaps the visible chip still returns focus to a visible trigger (a CLI
  without a phone chip falls back to the first visible trigger).

## Detail rules (HUC-S3)

- Project rows, including the unattributed bucket, reconcile with the total.
  When cent rounding breaks the sum, an explicit `Rounding` row closes it;
  unknown amounts read `N/A` and a note says the rows may not add up.
- Live runs are a breakdown of the totals: each row says `Included in today's
  total` or `Not yet in totals`; a missing receipt reads `Pending`.
- Models are grouped per CLI by each run's effective model and reasoning. A
  missing value reads `Unknown` and is never filled from the configured route
  or a global default. A differing configured route is shown as `Configured
  cli / model / reasoning` next to the run. Quota is never split by model.
- Ledger links open the existing token-usage section of workspace settings
  (`#/workspace/settings/tokens[/claude|/codex]`) with `ledger-workspace`,
  `ledger-range`, `ledger-from`, `ledger-to`, `ledger-zone` and optional
  `ledger-project` hash segments. The section shows that scope as one line.
  `Manage CLIs` opens `#/workspace/settings/caps`. The retired standalone CLI
  usage surface is not restored.
- Desktop: Escape and Close return focus to the trigger; Tab past the last
  control closes and continues after the trigger; Shift+Tab out closes.
  Phone: the browser makes the page inert, Tab wraps inside the sheet, and the
  Close button is 44 px.

## Evidence

The standalone `usage-chips-mockup` app (`src/mockups/usage-chips/`) mounts
the real components with fixtures. `e2e/mockups/usage-chips.spec.ts` checks
geometry, accessible names, focus and state labels in both themes and writes
screenshots. `?view=detail` mounts the HUC-S3 harness, a stand-in header
with the real chips and surface; `e2e/mockups/usage-detail.spec.ts` covers
it. Build first with `npm run build:mockup:usage`.
