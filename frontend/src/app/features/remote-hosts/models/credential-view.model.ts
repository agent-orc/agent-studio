import type { RemoteHost } from './remote-host.model';

/**
 * I8 Execution Hosts credential view (docs/credentials-and-logins/index.html#surface,
 * D4 option A). The Task Server projects registry metadata, fleet health and
 * generation-keyed reminders for the caller's role; this model only presents it.
 * No field carries a secret value.
 */
export type CredentialStatus =
  | 'healthy'
  | 'credential_invalid'
  | 'provider_incident'
  | 'quota_exhausted'
  | 'network_failure'
  | 'diagnosis_pending'
  | 'not_verified';

export type CredentialTone = 'ok' | 'info' | 'warn' | 'error' | 'muted';

export interface CredentialDue {
  knowledge: 'known' | 'none' | 'unknown';
  subject: 'expiry' | 'rotation' | null;
  dueAt: string | null;
  thresholdDays: 14 | 7 | 1 | null;
  overdue: boolean;
  expiryUnknown: boolean;
  reviewDueAt: string | null;
  unknownReason: string | null;
}

export interface CredentialReminder {
  generation: string;
  subject: 'expiry' | 'rotation';
  dueAt: string;
  thresholdDays: 14 | 7 | 1;
  openedAt: string;
  updatedAt: string;
}

export interface CredentialViewAction {
  kind: 'renew' | 'runbook' | 'requires-administrator';
  label: string;
  primary: boolean;
  method?: string | null;
  href?: string | null;
}

export interface CredentialViewRow {
  installationId: string;
  hostId: string;
  credentialId: string;
  kind: string;
  provider: string;
  renewalMethod: string;
  adapter: string;
  effectiveSource: 'active' | 'shadowed' | 'absent' | 'unknown';
  localRef: string | null;
  scopes: readonly string[];
  services: readonly string[];
  owner: string;
  generation: string;
  supersedes: string | null;
  lastVerifiedAt: string | null;
  lastRealSuccessAt: string | null;
  observedAt: string;
  status: CredentialStatus;
  statusDetail: string | null;
  nextRetryAt: string | null;
  serviceAvailability: string | null;
  evidenceQuality: 'ok' | 'clock-skew' | 'stale';
  due: CredentialDue;
  reminder: CredentialReminder | null;
  healthItem: { kind: 'renewal' | 'diagnosis'; openedAt: string; runbookHref: string | null } | null;
  runbookHref: string | null;
  evidenceRefs: readonly string[];
  actions: readonly CredentialViewAction[];
}

export interface CredentialView {
  role: 'owner' | 'operator' | 'viewer';
  generatedAt: string;
  rows: readonly CredentialViewRow[];
}

const KIND_LABELS: Readonly<Record<string, string>> = {
  claude_oauth_token: 'Claude setup token',
  claude_native_login: 'Claude native login',
  codex_chatgpt_login: 'Codex ChatGPT login',
  github_https_token: 'GitHub HTTPS token',
  github_provisioning_oauth: 'GitHub provisioning OAuth',
  github_deploy_key: 'GitHub deploy key',
  task_server_principal: 'Task Server principal',
  wireguard_peer: 'WireGuard peer',
  provider_api_key: 'Provider API key',
  administration_ssh_key: 'Administration SSH key',
};

const ADAPTER_LABELS: Readonly<Record<string, string>> = {
  'environment-file': 'Environment file',
  'native-cli-store': 'Native CLI store',
  'credential-helper': 'Credential helper',
  'ssh-private-key': 'SSH key',
  'service-secret': 'Service secret',
  'network-key': 'Network key',
  'external-vault': 'External vault',
  'docker-secret': 'Docker secret',
};

const SOURCE_LABELS: Readonly<Record<CredentialViewRow['effectiveSource'], string>> = {
  active: 'active',
  shadowed: 'shadowed',
  absent: 'missing',
  unknown: 'source unknown',
};

const SERVICE_LABELS: Readonly<Record<string, string>> = {
  coding: 'Coding',
  review: 'Review',
  engine: 'Engine',
  studio: 'Studio edge',
};

const REPOSITORY_DOCS = 'https://github.com/agent-orc/agent-studio/blob/main/';
const SAFE_DOC_PATH = /^docs\/[A-Za-z0-9/_.-]+\.(?:md|html)(?:#[A-Za-z0-9_-]+)?$/;
const DAY_MS = 86_400_000;
const HOUR_MS = 3_600_000;

export function credentialKindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? kind;
}

export function credentialSourceLabel(row: CredentialViewRow): string {
  return `${ADAPTER_LABELS[row.adapter] ?? row.adapter} · ${SOURCE_LABELS[row.effectiveSource] ?? row.effectiveSource}`;
}

export function credentialScopeLabel(row: CredentialViewRow): string {
  const services = row.services.map(service => SERVICE_LABELS[service] ?? service);
  if (services.length) return services.join(' and ');
  return row.scopes.join(', ') || 'Scope not recorded';
}

export function credentialStatusPresentation(row: CredentialViewRow): { label: string; tone: CredentialTone } {
  switch (row.status) {
    case 'healthy': return { label: 'Verified', tone: 'ok' };
    case 'credential_invalid':
      return {
        label: row.statusDetail === 'effective-source-missing' ? 'Credential missing · renewal needed' : 'Login invalid · renewal needed',
        tone: 'error',
      };
    case 'provider_incident':
      return { label: `Provider incident · retry scheduled${clock(row.nextRetryAt, ' ')}`, tone: 'info' };
    case 'quota_exhausted': return { label: `Quota exhausted${clock(row.nextRetryAt, ' · resets ')}`, tone: 'warn' };
    case 'network_failure': return { label: 'Network failure · reconnecting', tone: 'warn' };
    case 'diagnosis_pending': return { label: 'Diagnosis pending', tone: 'info' };
    default: return { label: 'Not verified', tone: 'muted' };
  }
}

export function credentialServiceLabel(availability: string | null): string {
  switch (availability) {
    case 'available': return 'Service available';
    case 'limited': return 'Service limited';
    case 'unavailable': return 'Service unavailable';
    default: return 'Service not reported';
  }
}

/** Unknown stays unknown: no date is invented and absence never reads as healthy. */
export function credentialDueLabel(due: CredentialDue, nowMs: number): string {
  if (due.knowledge === 'none') return 'No expiry';
  if (due.knowledge === 'unknown' || !due.dueAt) {
    return due.reviewDueAt ? `Expiry unknown · next check ${shortDate(due.reviewDueAt)}` : 'Expiry unknown';
  }
  const remaining = Date.parse(due.dueAt) - nowMs;
  const rotation = due.subject === 'rotation';
  let base: string;
  if (due.overdue || remaining <= 0) {
    base = rotation ? 'Rotation overdue' : 'Expired';
  } else {
    const days = Math.ceil(remaining / DAY_MS);
    const span = remaining < DAY_MS
      ? `${Math.max(1, Math.ceil(remaining / HOUR_MS))} h`
      : `${days} ${days === 1 ? 'day' : 'days'}`;
    base = `${rotation ? 'Rotation due' : 'Expires'} in ${span}`;
  }
  return due.expiryUnknown ? `${base} · expiry unknown` : base;
}

export function credentialDueTone(due: CredentialDue): CredentialTone {
  if (due.knowledge === 'unknown') return 'muted';
  switch (due.thresholdDays) {
    case 1: return 'error';
    case 7: return 'warn';
    case 14: return 'info';
    default: return 'muted';
  }
}

export function credentialEvidenceQualityLabel(quality: CredentialViewRow['evidenceQuality']): string | null {
  if (quality === 'clock-skew') return 'Clock skew: a host timestamp is ahead of the Task Server clock';
  if (quality === 'stale') return 'Metadata not observed in the last day';
  return null;
}

export function credentialLastVerifiedLabel(row: CredentialViewRow): string {
  return row.lastVerifiedAt ? shortDate(row.lastVerifiedAt, true) : 'Never verified';
}

/** Only repository documentation paths become links; anything else stays text. */
export function credentialDocHref(path: string | null | undefined): string | null {
  return path && !path.includes('..') && SAFE_DOC_PATH.test(path) ? REPOSITORY_DOCS + path : null;
}

/** Registry host IDs are Task Server host IDs; match every alias the host card knows. */
export function credentialRowsForHost(view: CredentialView | null, host: RemoteHost): CredentialViewRow[] {
  if (!view) return [];
  const aliases = new Set([host.id, host.clientId, host.capacityHostId ?? '', host.name]
    .filter(Boolean).map(alias => alias.toLowerCase()));
  return view.rows.filter(row => aliases.has(row.hostId.toLowerCase()));
}

/** One count per credential: an open reminder, an open renewal/diagnosis item or a confirmed failure. */
export function credentialAttentionCount(rows: readonly CredentialViewRow[]): number {
  return rows.filter(row => row.reminder || row.healthItem || row.status === 'credential_invalid').length;
}

export function credentialSummary(
  rows: readonly CredentialViewRow[],
  state: 'idle' | 'loading' | 'ready' | 'unavailable',
): string {
  if (state === 'unavailable') return 'Credential registry unavailable';
  if (!rows.length) return state === 'ready' ? 'No credentials registered' : 'Loading credentials';
  const attention = credentialAttentionCount(rows);
  const total = `${rows.length} ${rows.length === 1 ? 'credential' : 'credentials'}`;
  return attention ? `${total} · ${attention} need attention` : total;
}

function clock(value: string | null, prefix: string): string {
  if (!value) return '';
  const time = new Date(value).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit', hour12: false });
  return `${prefix}${time}`;
}

function shortDate(value: string, withTime = false): string {
  return new Date(value).toLocaleString('en-US', withTime
    ? { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false }
    : { month: 'short', day: 'numeric', year: 'numeric' });
}
