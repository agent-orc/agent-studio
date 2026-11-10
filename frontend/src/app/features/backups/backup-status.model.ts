import type { StatusTone } from '../../components/status-indicator/status-indicator.component';

/** `GET /api/v1/management/backups` (AGT-3574). */
export interface BackupOverview {
  state: 'ok' | 'warning' | 'failing' | 'not-configured' | 'disabled';
  configured: boolean;
  running: boolean;
  keyId: string | null;
  secretKeyId: string | null;
  intervalHours: number;
  verifyIntervalHours: number;
  staleAfterHours: number;
  retentionCount: number;
  nextBackupAt: string | null;
  lastGood: BackupOverviewItem | null;
  lastGoodAgeHours: number | null;
  stale: boolean;
  lastRun: BackupRunRecord | null;
  lastVerification: BackupVerificationOverview | null;
  offHost: BackupOffHostOverview;
  warnings: readonly BackupWarning[];
  backups: readonly BackupOverviewItem[];
  generatedAt: string;
}

export interface BackupOverviewItem {
  id: string;
  createdAt: string;
  sizeBytes: number;
  sha256: string;
  fileCount: number;
  verification: 'verified' | 'failed' | 'unverified';
  offHost: 'uploaded' | 'pending' | 'failed' | 'not-configured';
  unsettledFiles: number;
}

export interface BackupRunRecord {
  at: string;
  trigger: string;
  outcome: 'succeeded' | 'failed' | 'verified';
  backupId: string | null;
  error: string | null;
  durationMilliseconds: number;
}

export interface BackupVerificationOverview {
  at: string;
  backupId: string | null;
  outcome: 'verified' | 'failed';
  detail: string | null;
}

export interface BackupOffHostOverview {
  kind: string;
  target: string | null;
  state: 'not-configured' | 'invalid' | 'idle' | 'current' | 'pending' | 'failed';
  lastUploadedId: string | null;
  lastUploadedAt: string | null;
  lastError: string | null;
}

export interface BackupWarning {
  code: string;
  severity: 'warning' | 'danger';
  message: string;
}

export function isBackupOverview(value: unknown): value is BackupOverview {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const candidate = value as Partial<BackupOverview>;
  return typeof candidate.state === 'string' && Array.isArray(candidate.warnings) && Array.isArray(candidate.backups)
    && typeof candidate.offHost === 'object' && candidate.offHost !== null;
}

export function overviewTone(state: BackupOverview['state']): StatusTone {
  switch (state) {
    case 'ok': return 'success';
    case 'warning': return 'warning';
    case 'failing': return 'danger';
    default: return 'neutral';
  }
}

export function overviewLabel(overview: BackupOverview): string {
  if (overview.running) return 'Backup running';
  switch (overview.state) {
    case 'ok': return 'Backups current';
    case 'warning': return 'Needs attention';
    case 'failing': return 'Backups failing';
    case 'disabled': return 'Backups switched off';
    default: return 'Backups off';
  }
}

export function offHostLabel(offHost: BackupOffHostOverview): string {
  switch (offHost.state) {
    case 'current': return 'Copied';
    case 'pending': return 'Copy pending';
    case 'failed': return 'Copy failed';
    case 'invalid': return 'Target misconfigured';
    case 'idle': return 'Waiting for a backup';
    default: return 'No target';
  }
}

export function offHostTone(offHost: BackupOffHostOverview): StatusTone {
  switch (offHost.state) {
    case 'current': return 'success';
    case 'pending': return 'running';
    case 'failed':
    case 'invalid': return 'danger';
    case 'not-configured': return 'warning';
    default: return 'neutral';
  }
}

export function verificationTone(outcome: string | null | undefined): StatusTone {
  if (outcome === 'verified') return 'success';
  if (outcome === 'failed') return 'danger';
  return 'neutral';
}

export function verificationLabel(outcome: BackupOverviewItem['verification']): string {
  if (outcome === 'verified') return 'Verified';
  if (outcome === 'failed') return 'Failed';
  return 'Not verified';
}

export function itemOffHostLabel(state: BackupOverviewItem['offHost']): string {
  switch (state) {
    case 'uploaded': return 'Copied';
    case 'pending': return 'Pending';
    case 'failed': return 'Failed';
    default: return 'No target';
  }
}

/** Age in hours as "45 min", "5.5 h" or "3.2 days". */
export function formatAge(hours: number | null): string {
  if (hours === null) return 'None';
  if (hours < 1) return `${Math.max(0, Math.round(hours * 60))} min`;
  if (hours < 48) return `${(Math.round(hours * 10) / 10).toString()} h`;
  return `${(Math.round((hours / 24) * 10) / 10).toString()} days`;
}

export function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${unit === 0 ? value.toString() : value.toFixed(1)} ${units[unit]}`;
}
