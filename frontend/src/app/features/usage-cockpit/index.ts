/** Header usage cockpit public API (HUC-S2, HUC-S4). Cycle 9h / ADR-0034. */
export { UsageCliChipComponent } from './components/usage-cli-chip/usage-cli-chip';
export { UsageCostChipComponent } from './components/usage-cost-chip/usage-cost-chip';
export { UsageSlotChipComponent } from './components/usage-slot-chip/usage-slot-chip';
export { UsageCockpitHeaderComponent } from './components/usage-cockpit-header/usage-cockpit-header';
export type { CockpitNavItem, UsageDetailRequest } from './components/usage-cockpit-header/usage-cockpit-header';
export { UsageCockpitService, USAGE_COCKPIT_REFRESH_MS } from './services/usage-cockpit.service';
export {
  cliAbbreviation,
  headerTierForWidth,
  orderClis,
  planUsageHeader,
} from './usage-header-layout';
export type { HeaderTier, UsageChipFit, UsageHeaderMeasures, UsageHeaderPlan } from './usage-header-layout';
export {
  buildCliChipView,
  buildCostChipView,
  buildSlotChipView,
  cliDisplayName,
  findWindow,
  formatLocalWithUtc,
  formatUsdCompact,
  formatUsdExact,
  formatUsedPct,
  NOT_AVAILABLE,
} from './usage-chip.util';
export type {
  UsageChipState,
  UsageCliChipView,
  UsageCostChipView,
  UsageSlotChipView,
} from './usage-chip.util';
export type {
  UsageCalendar,
  UsageCli,
  UsageCliWindow,
  UsageCockpitResponse,
  UsageCostProjection,
  UsageProjectCost,
  UsageRun,
  UsageSlotPool,
  UsageSourceState,
} from './models/usage-cockpit.model';
