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
}
export interface DoraSummary {
  successfulProductionDeploys: number; deploysPerWeek: number | null;
  medianCardToProductionHours: number | null; cardsMeasured: number;
  changeFailureRate: number | null; medianRecoveryHours: number | null; recoveriesMeasured: number;
}
export interface ProjectOperations {
  project: string; since: string; until: string; environments: EnvironmentOperations[]; dora: DoraSummary;
}
export interface OperationsLogLine { at: string; level: string; message: string; releaseId: string | null }
export interface OperationsLogResult { telemetryAvailable: boolean; lines: OperationsLogLine[] }

export function metric(value: number | null | undefined, unit = ''): string {
  return value === null || value === undefined ? 'No data' : `${Number(value.toFixed(1)).toLocaleString('en-US')}${unit}`;
}
export function ratio(used: number | null, reserved: number | null, unit: string): string {
  return used === null || reserved === null ? 'No data' : `${metric(used, unit)} / ${metric(reserved, unit)}`;
}
