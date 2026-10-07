import type { UsageCli, UsageCockpitResponse, UsageCostProjection } from '../../../app/features/usage-cockpit';
import * as F from '../../usage-chips/app/usage-chips.fixtures';

/**
 * Synthetic HUC-S4 header fixtures pinned to the Dossier reference time,
 * 25 September 2026 16:58 Europe/Berlin. Not live telemetry.
 */
export const FIXTURE_NOW = F.FIXTURE_NOW;

function snapshot(clis: UsageCli[], cost: UsageCostProjection): UsageCockpitResponse {
  return {
    snapshotVersion: 1,
    workspaceId: 'demo',
    timeZone: F.FIXTURE_ZONE,
    weekStart: 1,
    generatedAt: '2026-09-25T14:58:00Z',
    calendar: cost.calendar,
    clis,
    cost,
    runs: [],
    slots: F.SLOTS,
    sources: {},
  };
}

const gemini = (overrides: Partial<UsageCli> = {}): UsageCli => ({
  ...F.CLAUDE,
  cliId: 'gemini',
  primary: false,
  windows: [
    { id: 'gemini/weekly', label: 'Weekly', usedPct: 41.5, resetAtUtc: '2026-09-29T07:00:00Z', resetLabel: null, suspiciousReason: null },
    { id: 'gemini/session', label: 'Current session', usedPct: 3, resetAtUtc: null, resetLabel: null, suspiciousReason: null },
  ],
  ...overrides,
});

/** `?scenario=` values of the harness. */
export const SCENARIOS: Record<string, UsageCockpitResponse | null> = {
  default: snapshot([F.CODEX, F.CLAUDE], F.COST),
  three: snapshot([F.CODEX, F.CLAUDE, gemini()], F.COST),
  long: snapshot([F.LONG_PROVIDER, F.CODEX_OVER, F.CLAUDE_FRACTIONAL, gemini()], F.COST_HUGE),
  large: snapshot([F.CODEX_OVER, F.CLAUDE], F.COST_LARGE),
  loading: null,
};
