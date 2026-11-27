import type { StatusTone } from '@studio/ui/components/status-indicator/status-indicator.component';

export type BackupStatus = 'ok' | 'pending' | 'overdue' | 'failed' | 'none';
export type BackupKind = 'scheduled' | 'manual' | 'pre-restore';
export type OperationKind = 'backup' | 'verify' | 'restore';
export type OperationState = 'awaiting-approval' | 'queued' | 'succeeded' | 'failed' | 'rejected';

export interface DatabaseBackup {
  id: string; takenAt: string; bytes: number; kind: BackupKind; verifiedAt: string | null; verifiedTables: number | null;
}

export interface DatabaseOperation {
  id: string; kind: OperationKind; backupId: string | null; sourceDatabase: string; targetDatabase: string;
  state: OperationState; requestedBy: string; requestedAt: string; approvedBy: string | null; approvedAt: string | null;
  finishedAt: string | null; message: string | null;
}

/** One managed database as `GET .../environments/{id}/databases` returns it (AGT-3877, AGT-3878). No value field exists. */
export interface ManagedDatabase {
  environmentId: string; name: string; engine: string; version: string; runningVersion: string | null; size: string;
  resources: { cpus: number; memoryMb: number; pidsLimit: number }; variable: string; credentialReference: string;
  state: string; declared: boolean; volumeBytes: number | null; observedAt: string | null; message: string | null;
  deletionRequestedBy: string | null; deletionRequestedAt: string | null; deletedAt: string | null;
  ready: boolean | null; connections: number | null; maxConnections: number | null;
  backupPolicy: { intervalHours: number; retentionDays: number; minimumKept: number };
  volumeAlertBytes: number; lastBackupAt: string | null; backupStatus: BackupStatus;
  lastBackupError: string | null; lastBackupAttemptAt: string | null;
  backups: DatabaseBackup[]; operations: DatabaseOperation[]; environmentKind: string;
}

/** A mutation the backups block asks the panel to send. `path` is relative to `.../databases/{name}/`. */
export interface DatabaseOperationRequest {
  path: string;
  body: object;
  notice: (operation: DatabaseOperation | null) => string;
  fallback: string;
  done?: () => void;
}

export interface Labelled { label: string; tone: StatusTone }

const STATES: Record<string, Labelled> = {
  'running': { label: 'Running', tone: 'success' },
  'provisioning': { label: 'Provisioning', tone: 'running' },
  'pending-deploy': { label: 'Waiting for a deploy', tone: 'info' },
  'retained': { label: 'Retained, not declared', tone: 'neutral' },
  'deleting': { label: 'Deletion requested', tone: 'running' },
  'deleted': { label: 'Data deleted', tone: 'neutral' },
  'upgrade-refused': { label: 'Upgrade refused', tone: 'warning' },
  'unhealthy': { label: 'Unhealthy', tone: 'danger' },
  'failed': { label: 'Failed', tone: 'danger' },
};
export function databaseState(state: string): Labelled {
  return STATES[state] ?? { label: state, tone: 'neutral' };
}

const BACKUP_STATUS: Record<BackupStatus, Labelled> = {
  ok: { label: 'Backups current', tone: 'success' },
  pending: { label: 'First backup pending', tone: 'info' },
  overdue: { label: 'Backup overdue', tone: 'warning' },
  failed: { label: 'Last backup failed', tone: 'danger' },
  none: { label: 'No backups expected', tone: 'neutral' },
};
export function backupStatus(status: string): Labelled {
  return BACKUP_STATUS[status as BackupStatus] ?? BACKUP_STATUS.none;
}

export function readiness(ready: boolean | null): Labelled {
  if (ready === true) return { label: 'Ready', tone: 'success' };
  if (ready === false) return { label: 'Not ready', tone: 'danger' };
  return { label: 'Unknown', tone: 'neutral' };
}

const OPERATION_STATES: Record<OperationState, Labelled> = {
  'awaiting-approval': { label: 'Awaiting approval', tone: 'warning' },
  'queued': { label: 'Queued', tone: 'running' },
  'succeeded': { label: 'Succeeded', tone: 'success' },
  'failed': { label: 'Failed', tone: 'danger' },
  'rejected': { label: 'Rejected', tone: 'neutral' },
};
export function operationState(state: string): Labelled {
  return OPERATION_STATES[state as OperationState] ?? { label: state, tone: 'neutral' };
}

const OPERATION_KINDS: Record<OperationKind, string> = { backup: 'Backup', verify: 'Test restore', restore: 'Restore' };
export function operationKind(kind: string): string { return OPERATION_KINDS[kind as OperationKind] ?? kind; }

const BACKUP_KINDS: Record<BackupKind, string> = { scheduled: 'Scheduled', manual: 'Manual', 'pre-restore': 'Before restore' };
export function backupKind(kind: string): string { return BACKUP_KINDS[kind as BackupKind] ?? kind; }

export function backupPolicy(policy: ManagedDatabase['backupPolicy']): string {
  return `Every ${policy.intervalHours} h, kept ${policy.retentionDays} days, at least ${policy.minimumKept} kept`;
}

export function connections(database: Pick<ManagedDatabase, 'connections' | 'maxConnections'>): string {
  if (database.connections === null) return 'Unknown';
  return database.maxConnections === null ? `${database.connections}` : `${database.connections} of ${database.maxConnections}`;
}

export function formatBytes(bytes: number | null): string {
  if (bytes === null) return 'Not measured yet';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${unit === 0 ? value : value.toFixed(1)} ${units[unit]}`;
}

export function volumeOverAlert(database: Pick<ManagedDatabase, 'volumeBytes' | 'volumeAlertBytes'>): boolean {
  return database.volumeBytes !== null && database.volumeAlertBytes > 0 && database.volumeBytes >= database.volumeAlertBytes;
}
