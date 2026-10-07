/** Header usage cockpit public API (HUC-S2 chips, HUC-S3 detail, HUC-S5 alarms). Cycle 9h / ADR-0034. */
export { UsageCliChipComponent } from './components/usage-cli-chip/usage-cli-chip';
export { UsageCostChipComponent } from './components/usage-cost-chip/usage-cost-chip';
export { UsageCockpitHostComponent } from './components/usage-cockpit-host/usage-cockpit-host';
export { UsageSlotChipComponent } from './components/usage-slot-chip/usage-slot-chip';
export { UsageAlarmStatusComponent } from './components/usage-alarm-status/usage-alarm-status';
export { UsageAlarmStateService } from './state/usage-alarm-state.service';
export {
  alarmsForSource,
  EMPTY_USAGE_ALARM_STATE,
  hiddenProviderAlarms,
  isTrustedQuotaSample,
  observeCli,
  observeCost,
  observeSnapshot,
  QUOTA_WARNING_ABOVE_PCT,
  reduceUsageAlarms,
} from './usage-alarm.policy';
export type {
  UsageAlarm,
  UsageAlarmKind,
  UsageAlarmSeverity,
  UsageAlarmState,
  UsageAlarmStep,
  UsageSourceObservation,
} from './usage-alarm.policy';
export { UsageDetailPanelComponent } from './components/usage-detail-panel/usage-detail-panel';
export {
  UsageDetailSurfaceComponent,
  USAGE_PHONE_QUERY,
  USAGE_TRIGGER_ATTR,
  usageTriggerKey,
} from './components/usage-detail-surface/usage-detail-surface';
export {
  buildUsageDetailView,
  CLI_MANAGEMENT_HREF,
  ledgerHref,
  describeLedgerScope,
  ledgerScopeFromHash,
  UNKNOWN,
} from './usage-detail.util';
export type { UsageDetailFocus, UsageDetailView, UsageLedgerScope } from './usage-detail.util';
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
  UsageChipAlarm,
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
