/**
 * Wire types of operator agents (SM-1): Owner-created agent identities that
 * act through short-lived, Owner-delegated sessions, and the audit trail
 * those sessions leave (`/api/auth/iam/operator-agents`, `/api/auth/audit`).
 */

export type OperatorSessionState = 'active' | 'expired' | 'revoked';

export interface OperatorSession {
  id: string;
  operatorAgentId: string;
  name: string | null;
  /** `operator-agent:<agentId>/session:<sessionId>` as written into the audit trail. */
  actor: string;
  delegatedBy: string;
  createdAt: string;
  createdBy: string;
  expiresAt: string;
  renewedAt: string | null;
  lastUsedAt: string | null;
  revokedAt: string | null;
  state: OperatorSessionState;
}

export interface OperatorAgent {
  id: string;
  name: string;
  description: string | null;
  createdAt: string;
  createdBy: string | null;
  disabledAt: string | null;
  sessions: readonly OperatorSession[];
}

/** The bearer token of a new session; the server keeps only its hash. */
export interface OneTimeOperatorSession {
  operatorAgentId: string;
  sessionId: string;
  token: string;
  actor: string;
  delegatedBy: string;
  expiresAt: string;
}

export interface AuditEntry {
  sequence: number;
  at: string;
  stream: string;
  actor: string;
  session: string | null;
  delegatedBy: string | null;
  operation: string;
  target: string | null;
  parameterDigest: string | null;
  outcome: string;
  hash: string;
  previousHash: string;
}

export interface AuditIntegrity {
  state: 'verified' | 'failed' | 'not-run';
  checkedAt: string | null;
  verifiedThrough: number;
  message: string | null;
  alert: boolean;
}

export interface AuditForwarding {
  sinkKind: string;
  configured: boolean;
  headSequence: number;
  forwardedSequence: number;
  pending: number;
  lastForwardedAt: string | null;
  oldestPendingAt: string | null;
  lastError: string | null;
  alert: boolean;
}

export interface OperatorActivity {
  operatorAgents: readonly OperatorAgent[];
  entries: readonly AuditEntry[];
  integrity: AuditIntegrity;
  forwarding: AuditForwarding;
}

/** Service accounts with this origin back an operator agent and are listed only in the operator panel. */
export const OPERATOR_AGENT_ORIGIN = 'operator-agent';

export const OPERATOR_SESSION_HOURS: readonly number[] = [1, 2, 3, 4, 5, 6, 7, 8];
export const DEFAULT_OPERATOR_SESSION_HOURS = 8;

export function isOperatorAgentAccount(account: { origin?: string | null }): boolean {
  return account.origin === OPERATOR_AGENT_ORIGIN;
}

export function sessionStateLabel(state: OperatorSessionState): string {
  return state === 'active' ? 'Active' : state === 'expired' ? 'Expired' : 'Revoked';
}

/** The first 12 characters of a parameter digest; the full value goes into a title attribute. */
export function shortDigest(digest: string | null | undefined): string {
  return digest ? digest.slice(0, 12) : '';
}

/** Newest session first, active sessions ahead of settled ones. */
export function orderedSessions(sessions: readonly OperatorSession[]): OperatorSession[] {
  return [...sessions].sort((left, right) =>
    Number(right.state === 'active') - Number(left.state === 'active')
    || right.createdAt.localeCompare(left.createdAt));
}

/** An audit timestamp with seconds; locale and zone are fixed so every reader sees the same value. */
export function formatAuditTime(value: string | null | undefined): string {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return `${date.toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'medium', timeZone: 'UTC' })} UTC`;
}

/**
 * The operator kill switch (SM-2, `/api/auth/iam/operator-kill-switch`): every
 * use, the revocation of the operator's Operations Server principals, and the
 * drills that exercise the switch against a test principal.
 */
export type OperatorKillKind = 'all' | 'session' | 'drill';
export type OperatorKillTrigger = 'studio' | 'out-of-band' | 'scheduled-drill' | 'manual-drill';
export type OperatorKillPropagationState = 'not-configured' | 'not-required' | 'pending' | 'revoked';

export interface OperatorKillPropagation {
  state: OperatorKillPropagationState;
  attempts: number;
  lastAttemptAt?: string | null;
  lastError?: string | null;
  revokedPrincipalIds?: readonly string[] | null;
  cancelledOperations: number;
  completedAt?: string | null;
}

export interface OperatorKillRecord {
  id: string;
  kind: OperatorKillKind;
  trigger: OperatorKillTrigger;
  actor: string;
  reason?: string | null;
  at: string;
  operatorAgentIds: readonly string[];
  sessionIds: readonly string[];
  allOperators: boolean;
  operationsServer: OperatorKillPropagation;
  notifiedAt?: string | null;
}

export interface OperatorKillDrill {
  id: string;
  trigger: OperatorKillTrigger;
  startedAt: string;
  durationMs: number;
  studioRefusedMs: number | null;
  operationsServerState: OperatorKillPropagationState;
  operationsServerMs: number | null;
  otherSessionsKept: boolean;
  passed: boolean;
  failure: string | null;
}

export interface OperatorKillSwitchStatus {
  kills: readonly OperatorKillRecord[];
  drills: readonly OperatorKillDrill[];
  nextDrillAt: string | null;
  drillIntervalHours: number;
  operationsServerConfigured: boolean;
  outOfBandRequestPending: boolean;
}

export function killTriggerLabel(trigger: OperatorKillTrigger): string {
  switch (trigger) {
    case 'studio': return 'Studio';
    case 'out-of-band': return 'Host command';
    case 'scheduled-drill': return 'Scheduled drill';
    case 'manual-drill': return 'Manual drill';
  }
}

export function killKindLabel(kind: OperatorKillKind): string {
  return kind === 'all' ? 'All operator access' : kind === 'session' ? 'One session' : 'Drill';
}

export function propagationLabel(state: OperatorKillPropagationState): string {
  switch (state) {
    case 'revoked': return 'Operations Server principals revoked';
    case 'pending': return 'Operations Server revocation pending';
    case 'not-configured': return 'No Operations Server configured';
    case 'not-required': return 'Operations Server unchanged';
  }
}

/** Milliseconds below one second, else seconds with one decimal. */
export function formatDuration(milliseconds: number | null | undefined): string {
  if (milliseconds === null || milliseconds === undefined || Number.isNaN(milliseconds)) return '';
  return milliseconds < 1000 ? `${Math.round(milliseconds)} ms` : `${(milliseconds / 1000).toFixed(1)} s`;
}
