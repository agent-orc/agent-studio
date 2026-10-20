/** Wire shapes of `/api/environment/health` and `/api/projects/{project}/environments/health` (IP-14). */
/** `invalid`: the declaration is broken or a target resolves to an address the control plane refuses. */
export type HealthStatus = 'up' | 'degraded' | 'down' | 'invalid' | 'unknown';

export interface HealthSample {
  at: string;
  ok: boolean;
  latencyMs: number | null;
  status: number | null;
  error: string | null;
  certificateNotAfter: string | null;
}

export interface HealthLatency {
  p50Ms: number | null;
  p95Ms: number | null;
  p99Ms: number | null;
}

export interface HealthIncident {
  id: string;
  environmentId: string;
  checkId: string;
  startedAt: string;
  firstError: string;
  failedProbes: number;
  endedAt: string | null;
  cardId: string | null;
}

export interface EnvironmentHealthCheck {
  id: string;
  kind: 'http' | 'tcp' | 'tls';
  target: string;
  derived: boolean;
  expectedStatus: number;
  hasBodyMarker: boolean;
  intervalSeconds: number;
  timeoutSeconds: number;
  failureThreshold: number;
  recoveryThreshold: number;
  status: HealthStatus;
  lastSample: HealthSample | null;
  consecutiveFailures: number;
  uptime24h: number | null;
  uptime7d: number | null;
  uptime30d: number | null;
  latency24h: HealthLatency;
  certificateNotAfter: string | null;
}

export interface EnvironmentHealth {
  project: string;
  environmentId: string;
  name: string;
  kind: string;
  address: string;
  status: HealthStatus;
  uptime24h: number | null;
  uptime7d: number | null;
  uptime30d: number | null;
  latency24h: HealthLatency;
  currentIncident: HealthIncident | null;
  lastIncident: HealthIncident | null;
  checks: EnvironmentHealthCheck[];
  /** Why the environment cannot be watched, for example a broken declaration file. */
  error: string | null;
}

export interface EnvironmentHealthResponse {
  capturedAt: string;
  probeOrigin: string;
  environments: EnvironmentHealth[];
}

export const HEALTH_STATUS_LABEL: Record<HealthStatus, string> = {
  up: 'Up',
  degraded: 'Degraded',
  down: 'Down',
  invalid: 'Misconfigured',
  unknown: 'No data',
};

/** Uptime share as a percentage with two decimals, so 99.95 % and 100 % stay distinct. */
export function uptimeLabel(value: number | null): string {
  if (value === null) return 'n/a';
  // Floor so 99.996 % never reads as 100 %; the epsilon absorbs binary rounding of exact values.
  return `${(Math.floor(value * 10_000 + 1e-6) / 100).toFixed(2)} %`;
}

export function latencyLabel(value: number | null): string {
  return value === null ? 'n/a' : `${value} ms`;
}

export function certificateDaysLeft(notAfter: string | null, now: number): number | null {
  if (!notAfter) return null;
  return Math.floor((Date.parse(notAfter) - now) / 86_400_000);
}
