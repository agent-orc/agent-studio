/**
 * Wire shape of `GET /api/usage/cockpit` (HUC-S1, docs/system/domains/tokens.md
 * "Workspace usage cockpit read contract"). Read-only; the browser never
 * recomputes prices or converts quota percentages into money.
 */
export type UsageSourceStatus = 'complete' | 'partial' | 'stale' | 'suspicious' | 'unavailable';

export interface UsageSourceState {
  status: UsageSourceStatus | string;
  observedAt: string | null;
  ttlSeconds: number | null;
  reason?: string | null;
}

export interface UsageCliWindow {
  /** Stable window id, e.g. `codex/weekly`. */
  id: string;
  /** Provider-defined label, e.g. `Current session (5h)`. */
  label: string;
  /** Quota used, not remaining. May exceed 100. */
  usedPct: number | null;
  resetAtUtc: string | null;
  resetLabel: string | null;
  suspiciousReason: string | null;
}

export interface UsageCli {
  cliId: string;
  primary: boolean;
  plan: string | null;
  source: string | null;
  windows: UsageCliWindow[];
  fetchedAt: string | null;
  ttlSeconds: number;
  suspicious: boolean;
  suspiciousReason: string | null;
  probeFailedAt: string | null;
  /** null when the limit source could not be read. */
  limited: boolean | null;
  limitedReason: string | null;
  availability: UsageSourceState;
}

export interface UsageCalendar {
  timeZone: string;
  weekStart: number | string;
  dayStartUtc: string;
  dayEndUtc: string;
  weekStartUtc: string;
  weekEndUtc: string;
}

export interface UsageProjectCost {
  projectId: string | null;
  name: string;
  todayUsd: number | null;
  weekUsd: number | null;
  coverage: UsageSourceState;
  latestReceiptAt?: string | null;
}

export interface UsageCostProjection {
  currency: 'USD' | string;
  calendar: UsageCalendar;
  /** null means unknown, never zero. */
  todayUsd: number | null;
  weekUsd: number | null;
  projects: UsageProjectCost[];
  coverage: UsageSourceState;
  pricingVersion: string;
  normalizationVersion: string;
  ledgerEndpointTemplate: string;
  latestReceiptAt?: string | null;
  dailyBudgetUsd?: number | null;
  weeklyBudgetUsd?: number | null;
}

export interface UsageSlotPool {
  name: 'remote' | 'review' | 'auto' | string;
  occupied: number | null;
  capacity: number | null;
  availability: UsageSourceState;
}

export interface UsageRun {
  id: string;
  projectId: string;
  taskId: string;
  taskKey: string | null;
  startedAt: string | null;
  provisionalCostUsd: number | null;
  includedInTotals: boolean;
  configuredCli: string;
  configuredModel: string;
  configuredReasoning: string;
  effectiveCli: string;
  effectiveModel: string;
  effectiveReasoning: string;
  fallbackReason: string | null;
}

export interface UsageCockpitResponse {
  snapshotVersion: number;
  workspaceId: string;
  timeZone: string;
  weekStart: number | string;
  generatedAt: string;
  calendar: UsageCalendar;
  clis: UsageCli[];
  cost: UsageCostProjection;
  runs: UsageRun[];
  slots: UsageSlotPool[];
  sources: Record<string, UsageSourceState>;
}
