import type { RemoteHost } from './remote-host.model';
import { hostExecutorRole } from './remote-host.model';

const MAX_TELEMETRY_AGE_MS = 5 * 60_000;

export interface StatusBarPlaneSlots {
  active: number;
  /**
   * Sum of every contributing host's own configured slot ceiling
   * (role-local maximum, effective maximum, central host capacity, then the
   * active+available split). Null when no host in the plane reports one, so
   * the footer never fabricates a fleet-wide maximum (AGT-2645).
   */
  ceiling: number | null;
  /** At least one connected runner supplied a live occupied-slot value. */
  hasUtilization: boolean;
  hostCount: number;
  hosts: StatusBarPlaneHostSlots[];
}

export interface StatusBarPlaneHostSlots {
  id: string;
  name: string;
  active: number | null;
  ceiling: number | null;
}

export interface StatusBarSlotsByRole {
  coding: StatusBarPlaneSlots;
  review: StatusBarPlaneSlots;
}

function hostSlotCeiling(host: RemoteHost): number | null {
  // Role-local configuration is authoritative for separate coding and review
  // daemons on the same machine. Central host capacity is a compatibility
  // fallback for runners that do not advertise their own plane ceiling.
  if (host.roleMaxParallelism !== null && host.roleMaxParallelism !== undefined) {
    return host.roleMaxParallelism;
  }
  if (host.effectiveMaxParallelism !== null && host.effectiveMaxParallelism !== undefined) {
    return host.effectiveMaxParallelism;
  }
  if (host.runtimeCapacity) return host.runtimeCapacity.maxParallelism;
  if (host.activeTaskCount !== undefined && host.availableSlots !== undefined) {
    return host.activeTaskCount + host.availableSlots;
  }
  return null;
}

function freshActiveSlots(host: RemoteHost, nowMs: number): number | null {
  // The registry projection is updated by the latest claim/heartbeat request
  // and is newer than the separately fetched telemetry history in normal use.
  const heartbeatAt = host.lastHeartbeatAt ? Date.parse(host.lastHeartbeatAt) : Number.NaN;
  if (host.activeTaskCount !== undefined
      && Number.isFinite(heartbeatAt)
      && nowMs - heartbeatAt <= MAX_TELEMETRY_AGE_MS) {
    return Math.max(0, host.activeTaskCount);
  }

  const point = host.telemetry?.points.at(-1) ?? null;
  if (point) {
    const observedAt = Date.parse(point.timestamp);
    if (Number.isFinite(observedAt) && nowMs - observedAt <= MAX_TELEMETRY_AGE_MS) {
      return Math.max(0, point.activeSlots);
    }
  }
  return null;
}

/**
 * Split active execution slots and configured ceilings by executor plane
 * (coding vs review) so the footer can show "coding x/N - review y/M"
 * instead of one merged figure that hides review-plane load (AGT-2645).
 * Coding and review daemons register as separate RunnerIds even on a shared
 * physical host, so each `RemoteHost` belongs to exactly one plane
 * ({@link hostExecutorRole}) and this is a plain filter + reduce over the
 * same host list {@link summarizeStatusBarHostLoad} already consumes.
 */
export function summarizeStatusBarSlotsByRole(
  hosts: readonly RemoteHost[],
  nowMs = Date.now(),
): StatusBarSlotsByRole {
  const result: StatusBarSlotsByRole = {
    coding: { active: 0, ceiling: null, hasUtilization: false, hostCount: 0, hosts: [] },
    review: { active: 0, ceiling: null, hasUtilization: false, hostCount: 0, hosts: [] },
  };
  for (const host of hosts) {
    if (host.role !== 'remote' || host.status === 'offline' || host.status === 'retired') continue;
    const plane = result[hostExecutorRole(host)];
    const active = freshActiveSlots(host, nowMs);
    if (active !== null) {
      plane.active += active;
    }
    const ceiling = hostSlotCeiling(host);
    if (ceiling !== null) plane.ceiling = (plane.ceiling ?? 0) + ceiling;
    plane.hostCount++;
    plane.hosts.push({
      id: host.id,
      name: host.name,
      active,
      ceiling,
    });
  }
  for (const plane of [result.coding, result.review]) {
    // A fleet denominator or occupied count is useful only when every
    // connected host contributed. Never present a partial sum as the total.
    plane.hasUtilization = plane.hostCount > 0
      && plane.hosts.every(host => host.active !== null);
    if (plane.hosts.some(host => host.ceiling === null)) plane.ceiling = null;
  }
  return result;
}
