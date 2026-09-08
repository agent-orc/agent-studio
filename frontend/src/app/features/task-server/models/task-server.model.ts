/**
 * Task-Server management model (AGT-1924).
 *
 * The "Task Server" settings page is the operator's one place to read the
 * durable task server the whole platform talks to: the connected local or
 * networked URL, the workspace store it owns (root, size,
 * counts), the durable evidence inventory, the registered Runner identities,
 * and the management functions (archive sweep, orphan scan, fixture
 * cleanup). See docs/research/remote-ready-kickoff-2026-07.md for the theme.
 *
 * Live projection of `GET /api/v1/management/status`. The recovery console and
 * Agent Studio consume this same authoritative contract.
 */

/** Which deployment phase the connected server is in. */
export type TaskServerPhase = 'local' | 'central';

/** Server liveness. Only `unreachable` is acute (R4). */
export type TaskServerHealth = 'healthy' | 'degraded' | 'maintenance' | 'unreachable';

/**
 * Client identity kind, mirrors the `/api/clients` `ClientSummary.kind` union so
 * the registry list reads the same vocabulary the backend already emits.
 */
export type TaskServerClientKind =
  | 'human'
  | 'agent-instance'
  | 'external-tool'
  | 'service'
  | 'retired';

/** The three management sweeps offered on the page. */
export type ManagementActionKind =
  | 'archive-sweep' | 'orphan-sweep' | 'fixture-sweep'
  | 'backup-create' | 'restore-verify' | 'backup-retention'
  | 'maintenance-enter' | 'maintenance-read-only' | 'maintenance-exit' | 'shutdown-prepare'
  | 'runner-enrollment-create' | 'runner-credential-rotate' | 'runner-credential-revoke'
  | 'runner-revoke' | 'runner-drain' | 'runner-retire';

/** How connected the UI is to the task server. */
export interface TaskServerConnection {
  id: string;
  /** The URL the SPA is talking to (live: the serving origin). */
  url: string;
  /** Local loopback or separately hosted networked server. Derived from {@link url}. */
  phase: TaskServerPhase;
  health: TaskServerHealth;
  /** Server build / version string, or null if not reported. */
  version: string | null;
  /** Human uptime label reported by the server (e.g. "2d 9h"). */
  uptimeLabel: string | null;
  protocolMinimum: string;
  protocolMaximum: string;
  ready: boolean;
  /** Current profile-specific access boundary. X-Client-Id is attribution only. */
  authMode: string;
}

/** The durable task store the server owns. */
export interface TaskServerStore {
  /** Absolute workspace root path. */
  root: string;
  /** Total on-disk size of the store, in bytes. */
  sizeBytes: number;
  projectCount: number;
  taskCount: number;
  archivedTaskCount: number;
  /** Registered client identity files under the store. */
  identityCount: number;
  eventCount: number;
  artifactCount: number;
}

/** Authoritative evidence inventory reported by the Task Server. */
export interface TaskServerEvidenceStatus {
  state: string;
  eventFiles: number;
  artifactFiles: number;
  /** ISO timestamp of the latest evidence write, or null when empty. */
  lastWriteAt: string | null;
}

/** One registered client identity in the server's registry. */
export interface TaskServerClient {
  id: string;
  displayName: string;
  emoji: string | null;
  kind: TaskServerClientKind;
  /** ISO timestamp of the last authenticated request, or null if never seen. */
  lastSeenAt: string | null;
  /** How many tasks this identity currently owns. */
  ownedTaskCount: number;
  managementState?: string;
}

/** Outcome of one management sweep, newest first in {@link TaskServerStatus.recentResults}. */
export interface ManagementActionResult {
  kind: ManagementActionKind;
  /** ISO timestamp the sweep ran. */
  ranAt: string;
  /** One-line human summary of what the sweep did. */
  summary: string;
  /** Number of items the sweep touched (0 = nothing to do). */
  affected: number;
  matched: number;
  dryRun: boolean;
  commandId: string;
  state: string;
  targetId?: string | null;
  credentialId?: string | null;
  /** One-time credential reveal. Kept only in this browser session. */
  secret?: string | null;
  /** One-time enrollment reveal. Kept only in this browser session. */
  enrollmentCode?: string | null;
}

/** The whole live Task-Server management status snapshot. */
export interface TaskServerStatus {
  connection: TaskServerConnection;
  store: TaskServerStore;
  evidence: TaskServerEvidenceStatus;
  clients: readonly TaskServerClient[];
  /** Recent management-sweep outcomes, newest first. Starts empty. */
  recentResults: readonly ManagementActionResult[];
  maintenance: {
    mode: string;
    drainRequested: boolean;
    shutdownPrepared: boolean;
    reason: string | null;
  };
  migrations: readonly { id: string; state: string; startedAt: string | null; detail: string | null }[];
  backups: {
    directory: string;
    retentionCount: number;
    lastFailure: string | null;
    items: readonly { id: string; sizeBytes: number; createdAt: string; verificationState: string }[];
  };
  security: {
    available: boolean;
    userCount: number;
    credentialRunnerCount: number;
    sessionUrl: string;
    usersUrl: string;
    runnerCredentialsUrl: string;
    integration: string;
  };
}

export interface ArchiveTargetStatus {
  activeTarget: 'local' | 's3';
  copyToSecondary: boolean;
  deleteLocalAfterVerification: boolean;
  localConfigured: boolean;
  s3Configured: boolean;
  s3Endpoint: string | null;
  s3Bucket: string | null;
  s3Prefix: string;
  deleteArchivedAfterYears: number | null;
}

export interface RetentionPlan {
  actionCount: number;
  totalBytes: number;
  affectedTasks: number;
  actions: readonly { kind: string; taskKey: string; bytes: number }[];
}

// ---------------------------------------------------------------------------
// Pure display helpers - co-located with the types so the components and their
// specs share one source of truth. Side-effect free.
// ---------------------------------------------------------------------------

/** Shared status tone vocabulary (drives dot colour + tint), same as hosts. */
export type StatusTone = 'ok' | 'warn' | 'error' | 'calm';

/**
 * Format a byte count as a human size with one decimal ("1.4 GB", "812 MB"),
 * or "-" when unknown. Uses binary (1024) steps to match on-disk reporting.
 */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || Number.isNaN(bytes) || bytes < 0) return '-';
  if (bytes < 1024) return `${Math.round(bytes)} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(1)} ${units[unit]}`;
}

/** Human label for the deployment phase. */
export function phaseLabel(phase: TaskServerPhase): string {
  return phase === 'local' ? 'Local' : 'Central';
}

/** Human label for server health. */
export function healthLabel(health: TaskServerHealth): string {
  switch (health) {
    case 'healthy': return 'Healthy';
    case 'degraded': return 'Degraded';
    case 'maintenance': return 'Maintenance';
    case 'unreachable': return 'Unreachable';
  }
}

/** Tone for server health. Only `unreachable` is acute error (R4). */
export function healthTone(health: TaskServerHealth): StatusTone {
  switch (health) {
    case 'healthy': return 'ok';
    case 'degraded': return 'warn';
    case 'maintenance': return 'warn';
    case 'unreachable': return 'error';
  }
}

/** Human label for the evidence working-tree state. */
export function evidenceStateLabel(state: string): string {
  if (state === 'available') return 'Available';
  if (state === 'empty') return 'Empty';
  if (state === 'failed') return 'Failed';
  return state || 'Unknown';
}

/**
 * Tone for the evidence state. A clean tree renders calm; a dirty tree is a
 * soft warn (pending work), never acute - the platform commits after each run,
 * so an uncommitted tree is notable but not an emergency (R4).
 */
export function evidenceStateTone(state: string): StatusTone {
  if (state === 'available') return 'ok';
  if (state === 'empty') return 'calm';
  return 'warn';
}

/** Human label for a client identity kind. */
export function clientKindLabel(kind: TaskServerClientKind): string {
  switch (kind) {
    case 'human': return 'Human';
    case 'agent-instance': return 'Agent';
    case 'external-tool': return 'Tool';
    case 'service': return 'Service';
    case 'retired': return 'Retired';
  }
}

/** Human label for a management action. */
export function managementActionLabel(kind: ManagementActionKind): string {
  switch (kind) {
    case 'archive-sweep': return 'Archive sweep';
    case 'orphan-sweep': return 'Orphan sweep';
    case 'fixture-sweep': return 'Fixture sweep';
    case 'backup-create': return 'Create backup';
    case 'restore-verify': return 'Verify restore';
    case 'backup-retention': return 'Apply retention';
    case 'maintenance-enter': return 'Enter maintenance';
    case 'maintenance-read-only': return 'Enter read-only';
    case 'maintenance-exit': return 'Exit maintenance';
    case 'shutdown-prepare': return 'Prepare shutdown';
    case 'runner-enrollment-create': return 'Create host enrollment';
    case 'runner-credential-rotate': return 'Rotate host credential';
    case 'runner-credential-revoke': return 'Revoke host credential';
    case 'runner-revoke': return 'Revoke host';
    case 'runner-drain': return 'Drain host';
    case 'runner-retire': return 'Retire host';
  }
}

/**
 * Relative age of an ISO timestamp ("just now", "2m ago", "3h ago", "2d ago"),
 * or "never" when absent. `nowMs` is injected so the helper is pure and
 * deterministically testable.
 */
export function formatRelativeTime(iso: string | null | undefined, nowMs: number): string {
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

/**
 * A URL points at the local machine when its host is loopback. Drives the
 * phase derivation so the connected URL and its phase badge stay consistent.
 */
export function isLocalUrl(url: string): boolean {
  try {
    const host = new URL(url).hostname.toLowerCase();
    return host === 'localhost' || host === '127.0.0.1' || host === '[::1]' || host === '::1';
  } catch {
    return /localhost|127\.0\.0\.1/i.test(url);
  }
}
