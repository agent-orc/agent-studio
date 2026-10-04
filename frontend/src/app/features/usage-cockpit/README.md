# usage-cockpit

Header usage cockpit chips from the Dossier
[docs/header-usage-cockpit/index.html](../../../../../docs/header-usage-cockpit/index.html)
(AGT-2913). This folder holds slice HUC-S2 (the shared chip components) and
HUC-S4 (the responsive header integration). The detail popover and sheet
(HUC-S3) and alarm states (HUC-S5) are separate slices.

## Public API

Imports via `from './features/usage-cockpit'`. See [`index.ts`](./index.ts).

- `UsageCliChipComponent` (`app-usage-cli-chip`): one CLI with its weekly and
  current-session windows as one native button, for example
  `Codex WK 15% 5H 32%`.
- `UsageCostChipComponent` (`app-usage-cost-chip`): today's USD token-ledger
  estimate in the workspace-local day, for example `Today $12.48 USD`.
- `UsageSlotChipComponent` (`app-usage-slot-chip`): remote, review and auto
  slot pools. Expanded usage view only; never in the header strip.
- `UsageCockpitHeaderComponent` (`app-usage-cockpit-header`): the Studio
  header. A 44 px navigation row (projected by the host) above a 40 px usage
  row; 48 + 48 px on coarse pointers; one 48 px row on phone with the Studio
  identity, primary weekly percentage, today's cost and a text-only More menu.
- `UsageCockpitService`: one shared `GET /api/usage/cockpit` poll every 30
  seconds while visible, scoped to the active workspace.
- `usage-header-layout.ts`: pure tier, primary-order and content-fit policy.
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
  alarms. Quota warning, provider limited and budget crossed are HUC-S5.
- Staleness is measured against the host's `now` input; without it each chip
  follows its own 30-second clock (`usage-clock.ts`), so a snapshot that ages
  past its TTL turns stale on screen without a new projection.
- Times use the workspace IANA zone with the UTC equivalent alongside.
- Chips emit `activate`; the host owns the dialog or pool region and
  passes `expanded` and `controls` back.

## Header layout (HUC-S4)

- The tier follows the header's own width (768 / 1200 / 1600 CSS px), so a
  narrow container and 200% zoom use the same rules as the viewport.
- Primary CLI: the operator's current selection (the chip last opened), then
  the saved default (`defaultCliType`), then projection order.
- Collapse order: drop the breadcrumb; move optional navigation into More;
  move whole secondary CLI chips into Details (`+N`), last first; use the
  phone composition (weekly only, no visible `USD`); abbreviate the provider
  and drop the wordmark; drop the visible WK / Today labels. Values are never
  cut. Hidden chips are removed, so they leave the focus order; focus on a
  chip that collapses moves to Details (or More on phone).
- Hosts mark optional inline controls with `data-nav-inline-from="tablet"` or
  `"desktop"` and pass the same destinations in `navItems`.
- Slot counters, models and weekly cost never appear in the strip.
- The content fit remeasures every chip variant after a projection or CLI
  selection changes, even if the header container keeps the same width.
- Until HUC-S3 lands, the studio shell opens the existing usage hub from
  every chip and from Details.

## Evidence

The standalone `usage-chips-mockup` app (`src/mockups/usage-chips/`) mounts
the real components with fixtures. `e2e/mockups/usage-chips.spec.ts` checks
geometry, accessible names, focus and state labels in both themes and writes
screenshots. Build first with `npm run build:mockup:usage`.

The `usage-header-mockup` app (`src/mockups/usage-header/`) mounts the real
header with a stand-in navigation row. `e2e/mockups/usage-header.spec.ts`
checks row heights, coarse targets, whole-value fit at 320 px, long values and
200% zoom, the More menu, focus order and focus restoration. Build first with
`npm run build:mockup:usage-header`.
