import type { CliType } from '../../../models/task.model';
import type { RunnerProjectPreflight } from '../../../models/task.model';

/**
 * Remote-hosts registry model (AGT-1921).
 *
 * The "Execution Hosts" settings page shows every execution location - the
 * operator's local machine and each remote runner host - in one list, so the
 * whole fleet reads as a single picture (see
 * docs/research/remote-ready-kickoff-2026-07.md). Host definitions are seeded
 * from configuration and their liveness is hydrated from the Task Server client
 * registry, whose LastSeen timestamp is refreshed by real runner requests.
 */

/** Where a host sits relative to the operator. */
export type HostRole = 'local' | 'remote';
export type RunnerServiceRole = 'local' | 'coding' | 'review' | 'runner';

/**
 * Heartbeat-derived liveness. Ordered loosely from healthiest to gone:
 *   online   - heartbeat fresh, ready to pick up work
 *   idle     - reachable but nothing assigned
 *   degraded - heartbeat late or a probe reported trouble (acute)
 *   offline  - no heartbeat within the expected window (acute)
 *   draining - operator asked it to finish current work and stop taking more
 *   retired  - permanently removed from the pool (settled history, renders calm)
 */
export type HostHeartbeatStatus =
  | 'online'
  | 'idle'
  | 'degraded'
  | 'offline'
  | 'draining'
  | 'retired';

/** Operator actions offered per host row. */
export type HostActionKind =
  | 'reprobe'
  | 'reconnect'
  | 'drain'
  | 'retire'
  | 'revive'
  | 'delete'
  | 'capacity'
  | 'project-policy';
export type HostRampStrategy = 'conservative' | 'balanced' | 'aggressive';

/**
 * The one capacity record of an execution host: a hard ceiling on concurrent
 * runs, the CPU load the host aims to stay under, and how fast concurrency may
 * grow. Every project the host claims for shares it - per-project
 * `maxParallelism` is deprecated (AGT-2302 / AGT-2376).
 *
 * Two servers can own this record. The standalone Task Server versions it and
 * is written through `PUT /api/v1/hosts/{hostId}/runtime-capacity`; the
 * monolith keeps it on the host's client identity and is written through
 * `PUT /api/clients/{clientId}/runner-capacity`. A `version` of 0 marks the
 * unversioned client-identity record and selects the second route.
 */
export interface RuntimeCapacitySettings {
  hostId: string;
  maxParallelism: number;
  targetLoadPercent: number;
  rampStrategy: HostRampStrategy;
  version: number;
  updatedAt: string;
}

/** Task Server claim admission for projects on one execution host. */
export interface HostProjectPolicy {
  hostId: string;
  allowAllProjects: boolean;
  allowedProjectIds: readonly string[];
  version: number;
  updatedAt: string;
}

/** How many of a host's slots one project currently occupies. */
export interface HostProjectSlots {
  projectName: string;
  activeSlots: number;
}

/**
 * Per-CLI quota window lifted from the runner's quota probe. One row per CLI
 * window the host reported (Claude 5h, Codex weekly, ...). Mirrors the shape of
 * {@link QuotaWindow} but flattened to a single window so the host row can list
 * "Claude · 5h · 63%".
 */
export interface HostCliQuota {
  cliType: CliType;
  plan: string | null;
  windowLabel: string;
  usedPct: number | null;
  resetLabel: string | null;
}

/**
 * Host system properties (Robert addendum 2026-07-09): RAM, CPU, disk. The
 * remote runner reports these in its heartbeat; the backend exposes the local
 * machine's own values so the local entry carries the same shape. Memory is in
 * MB, disk in GB, load and usage are 0-100 percentages.
 */
export interface HostSystemStats {
  ramTotalMb: number;
  ramFreeMb: number;
  cpuCores: number;
  cpuModel: string;
  cpuLoadPct: number;
  diskTotalGb: number;
  diskFreeGb: number;
}

export interface HostTelemetryPoint {
  timestamp: string;
  cpuPercent: number | null;
  load1: number | null;
  load5: number | null;
  load15: number | null;
  memoryUsedBytes: number | null;
  memoryTotalBytes: number | null;
  swapInBytesPerSecond: number | null;
  swapOutBytesPerSecond: number | null;
  cpuStealPercent: number | null;
  ioWaitPercent: number | null;
  cpuCores: number;
  activeSlots: number;
}

export interface HostTelemetryFinding {
  kind: 'vm-throttled' | 'oversubscribed' | 'memory-pressure';
  label: string;
  since: string;
  until: string;
  /** Number of completed phases represented by this window-level finding. */
  occurrences?: number;
  /** Omitted by older servers, whose findings represented active phases only. */
  isActive?: boolean;
}

export interface HostTelemetrySeries {
  clientId: string;
  window: string;
  points: readonly HostTelemetryPoint[];
  findings: readonly HostTelemetryFinding[];
}

/** Latest route observation piggybacked on the host telemetry advertisement. */
export interface TaskServerConnectionTelemetry {
  status: 'unknown' | 'reachable' | 'unreachable';
  observedAt: string | null;
  failureStartedAt: string | null;
  consecutiveFailures: number;
  escalatedAt: string | null;
  lastError: string | null;
  lastRecoveredAt: string | null;
}

export type HostLiveDataState = 'loading' | 'ready' | 'error';

export type CapabilityHealthState = 'healthy' | 'suspect' | 'draining' | 'half-open';

export interface CapabilityRecoveryEvent {
  occurredAt: string;
  fromState: CapabilityHealthState | 'ready' | 'unavailable' | 'unknown';
  toState: CapabilityHealthState | 'ready' | 'unavailable' | 'unknown';
  reason: string;
  claimId?: string | null;
}

export interface RemoteHostCapabilityHealth {
  key: string;
  category: string;
  advertisedStatus: string;
  healthState: CapabilityHealthState;
  reason?: string | null;
  advertisedAt: string;
  freshUntil: string;
  isFresh: boolean;
  firstFailureAt?: string | null;
  lastFailureAt?: string | null;
  cooldownUntil?: string | null;
  canaryClaimId?: string | null;
  consecutiveFailures: number;
  version?: string | null;
  identity?: string | null;
  detail?: string | null;
  /** Typed provider condition. A ready transient state retains last-good admission. */
  signal?: 'ok' | 'transient-auth-error' | 'rate-limited' | 'signed-out' | 'credentials-expiring' | 'binary-missing' | null;
  /** Optional provider-reported credential expiry. Older runners omit it. */
  expiresAt?: string | null;
  limitedUntil?: string | null;
  credentialModifiedAt?: string | null;
  affectedClaims: readonly string[];
  recoveryHistory: readonly CapabilityRecoveryEvent[];
}

export interface TaskServerTelemetrySnapshot {
  observedAt: string;
  cpuPercent: number | null;
  memoryUsedBytes: number | null;
  memoryTotalBytes: number | null;
  cpuCores: number;
  /** Live occupied slots reported by this runner process. */
  activeSlots?: number;
  diskFreeBytes?: number | null;
  diskTotalBytes?: number | null;
  taskServerConnectionStatus?: 'unknown' | 'reachable' | 'unreachable';
  taskServerConnectionObservedAt?: string | null;
  taskServerConnectionFailureStartedAt?: string | null;
  taskServerConnectionConsecutiveFailures?: number;
  taskServerConnectionEscalatedAt?: string | null;
  taskServerConnectionLastError?: string | null;
  taskServerConnectionLastRecoveredAt?: string | null;
}

/** Wire shape returned by GET /api/v1/management/remote-hosts. */
export interface TaskServerRunnerCapabilitySnapshot {
  runnerId: string;
  name: string;
  hostId: string;
  instanceId: string;
  runnerVersion: string;
  protocolVersion: number;
  status: string;
  registeredAt: string;
  lastSeenAt: string;
  hostAdmission: RemoteHostAdmission;
  capabilities: RemoteHostCapabilityHealth[];
  telemetry?: TaskServerTelemetrySnapshot | null;
  runtimeCapacity?: NonNullable<RemoteHost['runtimeCapacity']>;
  effectiveMaxParallelism?: number | null;
  runtimeCapacityAppliedAt?: string | null;
  runtimeCapacityAppliedVersion?: number | null;
  projectPolicy?: NonNullable<RemoteHost['projectPolicy']> | null;
  /** Role-local RUNNER_MAX_PARALLELISM declared by this runner process. */
  roleMaxParallelism?: number | null;
}

export interface TunnelKeeperHealth {
  supported: boolean;
  taskName: string;
  state: 'healthy' | 'unhealthy' | 'unsupported';
  enabled: boolean;
  running: boolean;
  sshRunning: boolean;
  cause: 'task-disabled' | 'not-running' | 'ssh-not-running' | 'probe-failing' | null;
  observedAt: string | null;
  logTail: readonly string[];
  detail: string | null;
}

export interface RemoteRunnerLinkHealth {
  runnerId: string;
  name: string;
  linkState: 'connected' | 'stale' | 'down';
  lastSnapshotAt: string | null;
  stateSince: string;
  snapshotAgeSeconds: number | null;
  readyCardsTargetHost: boolean;
  keeper: TunnelKeeperHealth | null;
}

export interface RemoteRunnerReconnectResponse {
  runnerId: string;
  succeeded: boolean;
  enabled: boolean;
  started: boolean;
  detail: string;
  linkState: RemoteRunnerLinkHealth['linkState'];
  nextSnapshotAgeSeconds: number | null;
  keeper: TunnelKeeperHealth;
}

export interface RemoteHostAdmission {
  hostId: string;
  admissionState: 'open' | 'automatic-draining' | 'operator-draining';
  automaticDrainReason?: string | null;
  automaticDrainAt?: string | null;
  operatorDrainReason?: string | null;
  operatorDrainAt?: string | null;
}

/** A single execution location in the registry. */
export interface RemoteHost {
  id: string;
  /** Display name / hostname. */
  name: string;
  role: HostRole;
  /** Executor role advertised by this runner identity. */
  serviceRole?: RunnerServiceRole;
  /** SSH target for remote hosts; null for the local machine. */
  address: string | null;
  /** Task-server client identity used as X-Client-Id by this host. */
  clientId: string;
  /** Exact release identity advertised by the runner registration. */
  releaseId?: string | null;
  /** Process instance and wire version used for deployment diagnosis. */
  runnerInstanceId?: string | null;
  runnerProtocolVersion?: number | null;
  status: HostHeartbeatStatus;
  os: string;
  /** ISO timestamp of the last heartbeat, or null if never seen. */
  lastHeartbeatAt: string | null;
  /** Capability-snapshot freshness, separate from generic client activity. */
  runnerLink?: RemoteRunnerLinkHealth | null;
  /** Human uptime label reported by the host (e.g. "2d 9h"). */
  uptimeLabel: string | null;
  /** General capability chips: OS, runtimes, features. */
  capabilities: readonly string[];
  /** Per-CLI quota windows from the runner probes. */
  cliQuotas: readonly HostCliQuota[];
  /** Live system stats, or null when the host reports none (e.g. retired). */
  stats: HostSystemStats | null;
  telemetry?: HostTelemetrySeries | null;
  /** Process-local route observation; capability freshness remains the remote alarm. */
  taskServerConnection?: TaskServerConnectionTelemetry | null;
  /** Freshness of the client/daemon projection requested for this mount. */
  liveDataState?: HostLiveDataState;
  /** Acute registry failure projected from a synthetic /api/clients row. */
  identityFileError?: string | null;
  identityRestoreHint?: string | null;
  /** Telemetry has a separate request so runtime truth never waits on history. */
  telemetryLoading?: boolean;
  /** Latest daemon startup proof of contents and workflow write access. */
  gitPushStatus?: 'ready' | 'ready-no-workflow-scope' | 'read-only' | null;
  gitPushDetail?: string | null;
  gitPushCheckedAt?: string | null;
  /** Last server-accepted delivery proof for every project offered to this host. */
  projectPreflights?: readonly RunnerProjectPreflight[];
  daemonState?: 'running' | 'read-only' | 'stopped';
  lastClaimAt?: string | null;
  activeTaskCount?: number;
  availableSlots?: number;
  retireRequestedAt?: string | null;
  capabilityHealth?: readonly RemoteHostCapabilityHealth[];
  hostAdmission?: RemoteHostAdmission | null;
  /** Task Server host key and its centrally managed runtime slot policy. */
  capacityHostId?: string | null;
  runtimeCapacity?: RuntimeCapacitySettings | null;
  /** Latest capacity value reported as adopted by this daemon process. */
  effectiveMaxParallelism?: number | null;
  /** Role-local ceiling advertised from RUNNER_MAX_PARALLELISM. */
  roleMaxParallelism?: number | null;
  runtimeCapacityAppliedAt?: string | null;
  /** Exact Task Server policy version confirmed by this daemon. */
  runtimeCapacityAppliedVersion?: number | null;
  /** Projects the Task Server may offer to this host. Missing means compatibility allow-all. */
  projectPolicy?: HostProjectPolicy | null;
  /** Which projects currently occupy this host's shared slot ceiling. */
  projectSlots?: readonly HostProjectSlots[];
  /** Transient: an action currently in flight for this host. */
  busyAction?: HostActionKind | null;
}

// ---------------------------------------------------------------------------
// Pure display helpers - co-located with the types so the component and its
// spec share one source of truth. Side-effect free.
// ---------------------------------------------------------------------------

/** Format a MB value as GB with one decimal ("41.0 GB"), or "-" if unknown. */
export function formatMemory(mb: number | null | undefined): string {
  if (mb === null || mb === undefined || Number.isNaN(mb)) return '-';
  return `${(mb / 1024).toFixed(1)} GB`;
}

/** Format a GB value ("180 GB"), or "-" if unknown. */
export function formatDisk(gb: number | null | undefined): string {
  if (gb === null || gb === undefined || Number.isNaN(gb)) return '-';
  return `${Math.round(gb)} GB`;
}

/** Clamp a percentage into the 0-100 range for a bar width. */
export function clampPct(pct: number | null | undefined): number {
  if (pct === null || pct === undefined || Number.isNaN(pct)) return 0;
  return Math.max(0, Math.min(100, pct));
}

/** Utilisation tone for a meter bar. Acute red only past 90% (R4). */
export type MeterTone = 'ok' | 'warn' | 'high';
export function meterTone(pct: number | null | undefined): MeterTone {
  const v = clampPct(pct);
  if (v >= 90) return 'high';
  if (v >= 70) return 'warn';
  return 'ok';
}

/** Human label for a status value. */
export function hostStatusLabel(status: HostHeartbeatStatus): string {
  switch (status) {
    case 'online': return 'Online';
    case 'idle': return 'Idle';
    case 'degraded': return 'Degraded';
    case 'offline': return 'Offline';
    case 'draining': return 'Draining';
    case 'retired': return 'Retired';
  }
}

/**
 * Status tone drives the dot colour, the badge tint, and whether the whole
 * card takes an acute background wash. Only `degraded` / `offline` are acute
 * (R4): they get a loud warn / error tone. Healthy, idle, draining, and retired
 * all render calm so settled/quiet states do not shout.
 */
export type HostStatusTone = 'ok' | 'idle' | 'warn' | 'error' | 'calm';
export function hostStatusTone(status: HostHeartbeatStatus): HostStatusTone {
  switch (status) {
    case 'online': return 'ok';
    case 'idle': return 'idle';
    case 'degraded': return 'warn';
    case 'offline': return 'error';
    case 'draining': return 'idle';
    case 'retired': return 'calm';
  }
}

/** Human label for a host role. */
export function hostRoleLabel(role: HostRole): string {
  return role === 'local' ? 'Local' : 'Remote';
}

export function runnerServiceRoleLabel(role: RunnerServiceRole | null | undefined): string {
  switch (role) {
    case 'local': return 'Local';
    case 'coding': return 'Coding';
    case 'review': return 'Review';
    case 'runner':
    default: return 'Runner';
  }
}

/** Which execution plane a runner belongs to: the coding daemon or the review (auto-review post-processing) daemon. */
export type HostExecutorRole = 'coding' | 'review';

/**
 * A runner advertises a distinct "executor" capability at registration
 * (`executor:coding` / `executor:review`, AGT-2645) because the coding and
 * review daemons register as separate RunnerIds even on the same physical
 * host. That capability already flows to this registry unfiltered, so the
 * plane a host belongs to is read from it rather than carried as a new wire
 * field. The local host never registers a V1 capability set (the monolith
 * runs local execution in-process, coding only), so it has no executor entry
 * and is treated as coding by construction.
 */
export function hostExecutorRole(host: RemoteHost): HostExecutorRole {
  const executor = host.capabilityHealth?.find(entry => entry.category === 'executor');
  return executor?.key === 'executor:review' ? 'review' : 'coding';
}

/** Used RAM as a rounded 0-100 percentage, or null when stats are unknown. */
export function ramUsedPct(stats: HostSystemStats | null | undefined): number | null {
  if (!stats || !stats.ramTotalMb) return null;
  return Math.round(clampPct(((stats.ramTotalMb - stats.ramFreeMb) / stats.ramTotalMb) * 100));
}

/** Used disk as a rounded 0-100 percentage, or null when stats are unknown. */
export function diskUsedPct(stats: HostSystemStats | null | undefined): number | null {
  if (!stats || !stats.diskTotalGb) return null;
  return Math.round(clampPct(((stats.diskTotalGb - stats.diskFreeGb) / stats.diskTotalGb) * 100));
}

/**
 * Relative age of a heartbeat ("just now", "2m ago", "3h ago", "2d ago"), or
 * "never" when the host has no heartbeat. `nowMs` is injected so the helper is
 * pure and deterministically testable.
 */
export function relativeHeartbeat(iso: string | null | undefined, nowMs: number): string {
  if (!iso) return 'never';
  const then = Date.parse(iso);
  if (Number.isNaN(then)) return 'never';
  const deltaSec = Math.max(0, Math.round((nowMs - then) / 1000));
  if (deltaSec < 45) return 'just now';
  const min = Math.round(deltaSec / 60);
  if (min < 60) return `${min}m ago`;
  const hrs = Math.round(min / 60);
  if (hrs < 24) return `${hrs}h ago`;
  const days = Math.round(hrs / 24);
  return `${days}d ago`;
}

/** Metrics older than the liveness window are history, never live values. */
export function hostIsStale(iso: string | null | undefined, nowMs: number, thresholdMs = 5 * 60_000): boolean {
  if (!iso) return true;
  const seen = Date.parse(iso);
  return Number.isNaN(seen) || nowMs - seen > thresholdMs;
}

export type TaskServerRouteStatus = 'reachable' | 'degraded' | 'unreachable' | 'unknown';

/**
 * The connectivity capability is the authoritative remote signal while a
 * route is down: no host can send fresh telemetry through a broken route.
 */
export function taskServerRouteStatus(host: RemoteHost): TaskServerRouteStatus {
  if (host.status === 'retired') return 'unknown';
  const capability = host.capabilityHealth?.find(item => item.key === 'task-server:connectivity');
  if (!capability) return host.taskServerConnection?.status ?? 'unknown';
  if (!capability.isFresh || capability.advertisedStatus !== 'ready'
      || capability.healthState === 'draining') return 'unreachable';
  if (capability.healthState === 'suspect' || capability.healthState === 'half-open') return 'degraded';
  if (host.taskServerConnection?.status === 'unreachable') return 'unreachable';
  return 'reachable';
}

export function taskServerRouteDetail(host: RemoteHost): string {
  const capability = host.capabilityHealth?.find(item => item.key === 'task-server:connectivity');
  if (capability && !capability.isFresh) {
    return `No connectivity advertisement has arrived since ${capability.advertisedAt}. Check the tunnel or Task Server route.`;
  }
  return host.taskServerConnection?.lastError
    || capability?.reason
    || capability?.detail
    || 'No route observation has been reported yet.';
}
