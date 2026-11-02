import type { HostTelemetryPoint, RemoteHost } from '../../../remote-hosts';

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

// The plane-slot fold moved to the remote-hosts feature so other surfaces
// (the project flow-health page) read the same occupancy without a shell import.
export { summarizeStatusBarSlotsByRole } from '../../../remote-hosts';
export type { StatusBarPlaneHostSlots, StatusBarPlaneSlots, StatusBarSlotsByRole } from '../../../remote-hosts';
