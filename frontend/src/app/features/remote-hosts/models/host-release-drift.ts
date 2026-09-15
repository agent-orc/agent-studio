/**
 * Execution-host release drift (AGT-2826).
 *
 * A runner host used to show only an opaque release label, so an operator could
 * not tell that agent-runner-01 still ran an agent-host build from three weeks
 * earlier while Stable and the Task Server ran v0.3.0. The server owns the
 * comparison - the same verdict drives the operator-feed alarm - and exposes it
 * at `GET /api/v1/management/host-releases`. This module holds the wire shape
 * and the pure label helpers the table renders; it never re-decides the verdict.
 */

/** Deployment identity a runner daemon reports in registration and heartbeat. */
export interface HostReleaseIdentity {
  releaseId: string;
  version: string;
  commit?: string | null;
  builtAt?: string | null;
}

/** The release the server runs; every host row is measured against it. */
export interface StableReleaseIdentity {
  version: string;
  commit?: string | null;
  builtAt?: string | null;
}

/**
 *  current - runs the Stable release, or something newer
 *  behind  - provably older than Stable (acute once past the grace window)
 *  unknown - nothing comparable was reported; never rendered as an accusation
 */
export type HostReleaseDriftState = 'current' | 'behind' | 'unknown';

/** One runner role compared against Stable. Mirrors `HostReleaseDriftEntry`. */
export interface HostReleaseDrift {
  runnerId: string;
  name: string;
  hostId: string;
  role: string;
  release: HostReleaseIdentity | null;
  state: HostReleaseDriftState;
  /** Release-age gap: how much older this build is than the Stable build. */
  behindByHours: number | null;
  /** Duration the alarm is judged on; equals the gap when both builds are stamped. */
  behindForHours: number | null;
  alarmDue: boolean;
  reason: string;
  lastSeenAt: string;
  heartbeatStale: boolean;
}

/** Wire shape of `GET /api/v1/management/host-releases`. */
export interface HostReleaseDriftSnapshot {
  observedAt: string;
  stable: StableReleaseIdentity;
  behindCount: number;
  hosts: readonly HostReleaseDrift[];
}

/**
 * Compact age for the drift marker: "5h behind", "19d behind". Sub-hour gaps
 * round up to one hour so the marker never reads "0h behind".
 */
export function releaseAgeLabel(hours: number | null | undefined): string | null {
  if (hours === null || hours === undefined || !Number.isFinite(hours) || hours <= 0) return null;
  if (hours < 24) return `${Math.max(1, Math.round(hours))}h behind`;
  return `${Math.round(hours / 24)}d behind`;
}

/** Short marker next to the release label, or null when there is nothing to mark. */
export function releaseDriftLabel(drift: HostReleaseDrift | null | undefined): string | null {
  if (!drift || drift.state !== 'behind') return null;
  return releaseAgeLabel(drift.behindByHours) ?? 'behind Stable';
}

/**
 * Only a drift past the grace window is acute (R4). A host that is merely a few
 * hours behind is a rolling update in progress and renders calm.
 */
export function releaseDriftTone(drift: HostReleaseDrift | null | undefined): 'ok' | 'warn' | 'calm' {
  if (!drift || drift.state === 'unknown') return 'calm';
  if (drift.state === 'current') return 'ok';
  return drift.alarmDue ? 'warn' : 'calm';
}

export function releaseDriftTooltip(
  drift: HostReleaseDrift | null | undefined,
  stable: StableReleaseIdentity | null | undefined,
): string | null {
  if (!drift) return null;
  const reference = stable ? ` Stable runs ${stable.version}.` : '';
  return `${drift.reason}${reference}`;
}

/** "Stable 0.3.0" for the page header, so every row has a visible reference. */
export function stableReleaseLabel(stable: StableReleaseIdentity | null | undefined): string | null {
  return stable?.version?.trim() ? `Stable ${stable.version.trim()}` : null;
}

/** The machine-level verdict is the worst of its roles: one late role is drift. */
export function worstDrift(
  drifts: readonly (HostReleaseDrift | null | undefined)[],
): HostReleaseDrift | null {
  const known = drifts.filter((drift): drift is HostReleaseDrift => !!drift);
  if (!known.length) return null;
  return known.reduce((worst, drift) => rank(drift) > rank(worst) ? drift : worst);
}

function rank(drift: HostReleaseDrift): number {
  if (drift.state !== 'behind') return drift.state === 'unknown' ? 1 : 0;
  return drift.alarmDue ? 3 : 2;
}
