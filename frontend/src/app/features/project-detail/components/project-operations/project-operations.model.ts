import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';

export interface OperationsMarker {
  deploymentId: string; releaseId: string; version: string; atUtc: string;
  trigger: string; state: string; cardKeys: string[];
}
export interface OperationsMetrics {
  responseP50Ms: number | null; responseP95Ms: number | null; errorRate: number | null;
  restarts: number | null; requests: number | null; sessions: number | null; activeUsers: number | null;
  cpuCores: number | null; cpuReservedCores: number | null;
  memoryBytes: number | null; memoryReservedBytes: number | null;
  diskBytes: number | null; diskReservedBytes: number | null; networkBytes: number | null;
  topEndpoints: { path: string; requests: number }[];
  dailyUsage: { day: string; requests: number; sessions: number; activeUsers: number }[];
  samples: { at: string; responseP95Ms: number | null; errorRate: number | null;
    requests: number | null; cpuCores: number | null; memoryBytes: number | null; diskBytes: number | null }[];
}
export interface EnvironmentOperations {
  environment: { id: string; name: string; kind: string };
  telemetryAvailable: boolean; metrics: OperationsMetrics | null; markers: OperationsMarker[];
  observed?: { state: string; restarts: number; crashOutput?: string[] | null } | null;
  runtimeIncident?: { id: string; kind: string; endedAt: string | null; restarts: number } | null;
}

export function hostState(row: EnvironmentOperations): string {
  return row.runtimeIncident && !row.runtimeIncident.endedAt ? row.runtimeIncident.kind : row.observed?.state ?? 'unknown';
}
export function hostStateTone(row: EnvironmentOperations): StatusTone {
  switch (hostState(row)) {
    case 'running': return 'success';
    case 'starting': return 'running';
    case 'crash-loop': case 'crash-rollback': case 'failed': case 'unhealthy': return 'danger';
    default: return 'neutral';
  }
}
export function hostStateLabel(row: EnvironmentOperations): string {
  const state = hostState(row);
  return state === 'crash-loop' ? 'Crash loop' : state === 'crash-rollback' ? 'Crash rollback'
    : state.charAt(0).toUpperCase() + state.slice(1);
}
export interface DoraSummary {
  successfulProductionDeploys: number; deploysPerWeek: number | null;
  medianCardToProductionHours: number | null; cardsMeasured: number;
  changeFailureRate: number | null; medianRecoveryHours: number | null; recoveriesMeasured: number;
}
export interface ProjectOperations {
  project: string; since: string; until: string; environments: EnvironmentOperations[]; dora: DoraSummary;
}
export interface OperationsLogLine { at: string; level: string; message: string; releaseId: string | null; traceId?: string | null }
export interface OperationsLogResult { telemetryAvailable: boolean; lines: OperationsLogLine[]; nextCursor?: string | null }
export interface OperationsTrace { traceId: string; startedAt: string; durationMs: number; failed: boolean; name: string; spanCount: number }
export interface OperationsSpan { spanId: string; parentSpanId: string | null; name: string; kind: string;
  startedAt: string; durationMs: number; failed: boolean; attributes: Record<string, string> }

export function metric(value: number | null | undefined, unit = ''): string {
  return value === null || value === undefined ? 'No data' : `${Number(value.toFixed(1)).toLocaleString('en-US')}${unit}`;
}
/** Used against reserved; without a reservation the used value still shows. */
export function ratio(used: number | null, reserved: number | null, unit: string): string {
  if (used === null) return 'No data';
  return reserved === null ? metric(used, unit) : `${metric(used, unit)} / ${metric(reserved, unit)}`;
}
