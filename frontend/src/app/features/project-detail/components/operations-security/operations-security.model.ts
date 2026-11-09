import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Wire types of GET /api/security/posture (AGT-3575). Metadata only; the API never sends a secret value. */
export type SecuritySeverity = 'critical' | 'high' | 'medium' | 'low' | 'info';
export type SecurityArea = 'exposure' | 'credentials' | 'second-factor' | 'versions' | 'audit' | 'cards';

export interface SecurityFinding {
  id: string; rank: number; score: number; severity: SecuritySeverity; area: SecurityArea;
  title: string; detail: string; actionSoon: boolean; dueAt: string | null; subject: string | null;
}

export interface SecuritySummary {
  total: number; critical: number; high: number; medium: number; low: number; info: number; actionSoon: number;
}

export interface SecurityExternalFact { id: string; label: string; state: string; detail: string; }

export interface SecurityExposure {
  profile: string; httpsRequired: boolean; cloudflareEdge: boolean; trustedProxyCount: number;
  loginAddressHealthy: boolean; anonymousRoutes: string[]; external: SecurityExternalFact[];
}

export interface SecurityToken {
  id: string; holder: string; kind: 'api-token' | 'service-account'; role: string; projects: string[];
  createdAt: string; expiresAt: string | null; lastUsedAt: string | null; state: string;
}

export interface SecurityOperatorAgent {
  id: string; name: string; disabled: boolean; liveSessions: number; nextSessionExpiry: string | null;
}

export interface SecurityInventoryItem {
  id: string; category: string; kind: string; label: string; hostId: string;
  expiresAt: string | null; lastUseAt: string | null; state: string; detail: string;
}

export interface SecurityCredentials {
  tokens: SecurityToken[]; operatorAgents: SecurityOperatorAgent[]; inventory: SecurityInventoryItem[];
  inventorySource: string; inventoryProblem: string | null;
}

export interface SecurityUser {
  id: string; username: string; displayName: string; role: string; disabled: boolean;
  totp: boolean; passkeys: number; recoveryCodes: number; required: boolean; state: string;
}

export interface SecuritySecondFactor { activeUsers: number; covered: number; users: SecurityUser[]; }

export interface SecurityHost {
  runnerId: string; name: string; hostId: string; status: string; runnerVersion: string;
  releaseVersion: string | null; releaseState: string; operatingSystem: string | null; lastSeenAt: string;
  clis: { name: string; version: string; targetVersion: string | null; belowTarget: boolean }[];
  containers: { name: string; imageDigest: string | null; state: string }[];
}

export interface SecurityVersions {
  server: { version: string; tag: string; commit: string; builtAt: string | null; known: boolean };
  hostSource: string; hostProblem: string | null; hosts: SecurityHost[];
}

export interface SecurityAuditEvent {
  at: string; kind: string; stream: string; actor: string; operation: string; outcome: string; count: number;
}

export interface SecurityAudit {
  integrityState: string; integrityCheckedAt: string | null; sinkConfigured: boolean; pendingForward: number;
  sinkAlert: boolean; openAlerts: string[]; lastKillAt: string | null; lastDrillAt: string | null;
  lastDrillPassed: boolean | null; activeBreakGlassWindows: number; windowDays: number; anomalies: SecurityAuditEvent[];
}

export interface SecurityCard { key: string; title: string; project: string; state: string; createdAt: string; }

export interface SecurityPosture {
  generatedAt: string; summary: SecuritySummary; findings: SecurityFinding[]; exposure: SecurityExposure;
  credentials: SecurityCredentials; secondFactor: SecuritySecondFactor; versions: SecurityVersions;
  audit: SecurityAudit; cards: SecurityCard[];
}

export interface StatusPresentation { tone: StatusTone; label: string; }

export const SEVERITY_ORDER: readonly SecuritySeverity[] = ['critical', 'high', 'medium', 'low', 'info'];

const SEVERITY: Readonly<Record<SecuritySeverity, StatusPresentation>> = {
  critical: { tone: 'danger', label: 'Critical' },
  high: { tone: 'danger', label: 'High' },
  medium: { tone: 'warning', label: 'Medium' },
  low: { tone: 'info', label: 'Low' },
  info: { tone: 'neutral', label: 'Info' },
};

export function severityPresentation(severity: string): StatusPresentation {
  return SEVERITY[severity as SecuritySeverity] ?? { tone: 'neutral', label: severity || 'Unknown' };
}

const AREA_LABELS: Readonly<Record<SecurityArea, string>> = {
  exposure: 'Exposure', credentials: 'Credentials', 'second-factor': 'Second factor',
  versions: 'Versions', audit: 'Audit', cards: 'Security cards',
};

export function areaLabel(area: string): string {
  return AREA_LABELS[area as SecurityArea] ?? area;
}

/** Credential, user, host, and audit states; a state the API adds later falls back to neutral. */
const STATE: Readonly<Record<string, StatusPresentation>> = {
  active: { tone: 'success', label: 'Active' },
  ok: { tone: 'success', label: 'Working' },
  covered: { tone: 'success', label: 'Second factor' },
  current: { tone: 'success', label: 'Up to date' },
  verified: { tone: 'success', label: 'Verified' },
  configured: { tone: 'success', label: 'Configured' },
  expiring: { tone: 'warning', label: 'Expiring' },
  'no-expiry': { tone: 'warning', label: 'Never expires' },
  'no-recovery-codes': { tone: 'warning', label: 'No recovery codes' },
  behind: { tone: 'warning', label: 'Behind' },
  'not-run': { tone: 'neutral', label: 'Not verified yet' },
  missing: { tone: 'danger', label: 'Missing' },
  unexpected: { tone: 'danger', label: 'Unexpected access' },
  failed: { tone: 'danger', label: 'Failed' },
  expired: { tone: 'neutral', label: 'Expired' },
  revoked: { tone: 'neutral', label: 'Revoked' },
  disabled: { tone: 'neutral', label: 'Disabled' },
  unused: { tone: 'neutral', label: 'Unused' },
  unknown: { tone: 'neutral', label: 'Unknown' },
  'not-reported': { tone: 'neutral', label: 'Not visible to the API' },
};

export function statePresentation(state: string): StatusPresentation {
  return STATE[state] ?? { tone: 'neutral', label: state || 'Unknown' };
}

/** A user without a second factor reads "No second factor", not the generic "Missing". */
export function userStatePresentation(state: string): StatusPresentation {
  return state === 'missing' ? { tone: 'danger', label: 'No second factor' } : statePresentation(state);
}

const ANOMALY_LABELS: Readonly<Record<string, string>> = {
  'refused-request': 'Refused request',
  'refused-secret-write': 'Refused secret write',
  'kill-switch': 'Kill switch',
  'break-glass': 'Break-glass',
};

export function anomalyLabel(kind: string): string {
  return ANOMALY_LABELS[kind] ?? kind;
}

/** Whole days from `now` until `iso`, rounded up; negative once past. */
export function daysUntil(iso: string | null, now: number): number | null {
  if (!iso) return null;
  const at = Date.parse(iso);
  return Number.isNaN(at) ? null : Math.ceil((at - now) / 86_400_000);
}
