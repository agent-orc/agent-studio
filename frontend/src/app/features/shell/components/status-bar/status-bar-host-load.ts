import type { HostTelemetryPoint, RemoteHost } from '../../../remote-hosts/core-api';
import { hostExecutorRole } from '../../../remote-hosts/core-api';

export type StatusBarLoadTone = 'unknown' | 'calm' | 'working' | 'hot' | 'mismatch';
export type StatusBarLoadCorrelation = 'unknown' | 'consistent' | 'load-without-runs' | 'runs-without-load';

export interface StatusBarHostLoad {
  load1: number;
  cpuCores: number;
  activeSlots: number;
  ratio: number;
  tone: StatusBarLoadTone;
  correlation: StatusBarLoadCorrelation;
}

const MAX_TELEMETRY_AGE_MS = 5 * 60_000;

/** A host qualifies for the live status-bar signal while it has stats and is neither offline nor retired. */
function isLiveHost(host: RemoteHost): boolean {
  return host.stats !== null && host.status !== 'offline' && host.status !== 'retired';
}

/** The host's freshest telemetry point, or null when it is missing or older than {@link MAX_TELEMETRY_AGE_MS}. */
function freshPoint(host: RemoteHost, nowMs: number): HostTelemetryPoint | null {
  const point = host.telemetry?.points.at(-1) ?? null;
  if (point === null || point.load1 === null || point.cpuCores <= 0) return null;
  const observedAt = Date.parse(point.timestamp);
  if (!Number.isFinite(observedAt) || nowMs - observedAt > MAX_TELEMETRY_AGE_MS) return null;
  return point;
}

/**
 * Fold fresh execution-host telemetry into the tiny status-bar signal.
 * `stats` and the point timestamp both guard freshness: the service clears
 * stats for stale points when hydrating, while the timestamp keeps a
 * long-lived status bar from presenting an old sample as current.
 */
export function summarizeStatusBarHostLoad(
  hosts: readonly RemoteHost[],
  runningCount: number,
  nowMs = Date.now(),
): StatusBarHostLoad | null {
  const points = hosts
    .filter(isLiveHost)
    .map(host => freshPoint(host, nowMs))
    .filter((point): point is HostTelemetryPoint => point !== null);

  if (points.length === 0) return null;

  const load1 = points.reduce((sum, point) => sum + (point.load1 ?? 0), 0);
  const cpuCores = points.reduce((sum, point) => sum + point.cpuCores, 0);
  const activeSlots = points.reduce((sum, point) => sum + point.activeSlots, 0);
  const ratio = load1 / cpuCores;
  // `activeSlots` covers both coding and review workers reported by all daemons.
  // Only flag `load-without-runs` when no plane has any active work at all;
  // review workers showing up in `activeSlots` but absent from the coding
  // board lane must not trigger a false consistency hint.
  const correlation: StatusBarLoadCorrelation =
    runningCount === 0 && activeSlots === 0 && ratio >= 0.5
      ? 'load-without-runs'
      : runningCount >= 2 && ratio <= 0.1
        ? 'runs-without-load'
        : 'consistent';
  const tone: StatusBarLoadTone = correlation !== 'consistent'
    ? 'mismatch'
    : ratio >= 1
      ? 'hot'
      : ratio >= 0.5
        ? 'working'
        : 'calm';

  return { load1, cpuCores, activeSlots, ratio, tone, correlation };
}

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
