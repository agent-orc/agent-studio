import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import type { HostZonePolicy } from '../../../remote-hosts/models/zone-policy';

export interface EnvironmentFlowSnapshot {
  capturedAt: string;
  today: {
    created: number; delivered: number; reviewed: number; integrated: number;
    deployed: number | null; completed: number; readyToIntegrationMedianMinutes: number | null;
  };
  lanes: readonly { lane: string; entered: number; left: number; medianMinutes: number | null }[];
  queues: readonly { queue: string; waiting: number | null; oldestWaitMinutes: number | null;
    throughputPerHour: number | null }[];
  waitingReasons: readonly { reason: string; count: number }[];
}

export interface AttemptVisibility {
  attemptId: string; project: string; subject: string; hostId: string; role: string;
  executor: string; leaseId: string; fence: number; leaseState: string;
  startedAt: string; leaseExpiresAt: string; imageDigest: string | null;
  grantHalfCores: number | null; grantSource: string; cacheOutcome: string;
  cleanupStatus: string; occupancy: 'occupied' | 'released' | 'orphan'; provenance: string;
}

export interface AttemptVisibilitySnapshot {
  generatedAt: string; attempts: readonly AttemptVisibility[];
}

export function minutes(value: number | null | undefined): string {
  if (value == null) return 'Unavailable';
  return value < 60 ? `${Math.round(value)} min` : `${(value / 60).toFixed(1)} h`;
}

/** Attempt occupancy on the shared status indicator; unknown values stay neutral. */
export const ATTEMPT_OCCUPANCY_DISPLAY: Readonly<Record<AttemptVisibility['occupancy'], { tone: StatusTone; label: string }>> = {
  occupied: { tone: 'running', label: 'Occupied' },
  released: { tone: 'neutral', label: 'Released' },
  orphan: { tone: 'warning', label: 'Orphan' },
};

export function attemptOccupancyDisplay(occupancy: string): { tone: StatusTone; label: string } {
  return Object.hasOwn(ATTEMPT_OCCUPANCY_DISPLAY, occupancy)
    ? ATTEMPT_OCCUPANCY_DISPLAY[occupancy as AttemptVisibility['occupancy']]
    : { tone: 'neutral', label: occupancy };
}

/** A failed workspace cleanup needs an operator look; every other cleanup status is a settled fact. */
export function attemptCleanupTone(status: string): StatusTone {
  return status === 'failed' ? 'warning' : 'neutral';
}

const PROVIDER_STATUS_TONE: Readonly<Record<string, StatusTone>> = {
  valid: 'success',
  healthy: 'success',
  ok: 'success',
  invalid: 'danger',
  credential_invalid: 'danger',
  'signed-out': 'danger',
  'binary-missing': 'danger',
  'credentials-expiring': 'warning',
  'rate-limited': 'warning',
  'transient-auth-error': 'warning',
  quota_exhausted: 'warning',
  provider_incident: 'warning',
  network_failure: 'warning',
};

/** Raw provider auth status (credentialHealth, healthOutcome, signal or healthState) on the shared indicator. */
export function providerStatusTone(status: string | null | undefined): StatusTone {
  return status && Object.hasOwn(PROVIDER_STATUS_TONE, status) ? PROVIDER_STATUS_TONE[status] : 'neutral';
}

/** Zone policy verification on the shared indicator. */
export const ZONE_POLICY_TONE: Readonly<Record<HostZonePolicy['state'], StatusTone>> = {
  verified: 'success',
  stale: 'warning',
  failed: 'danger',
  'not-reported': 'neutral',
};

export function zonePolicyTone(state: string): StatusTone {
  return ZONE_POLICY_TONE[state as HostZonePolicy['state']] ?? 'neutral';
}
