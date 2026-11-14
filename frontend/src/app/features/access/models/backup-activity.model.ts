import type { AuditIntegrity } from './operator-agent.model';

/**
 * Backup records of the control plane audit chain (AGT-3737,
 * `GET /api/auth/audit/backups`): create, upload, restore verification,
 * retention, and the offline decrypt and restore, each with its actor
 * (`schedule` for the scheduler) and outcome. Owner only.
 */
export type BackupAuditOutcome = 'succeeded' | 'failed' | 'denied';

export interface BackupAuditEntry {
  sequence: number;
  at: string;
  actor: string;
  session: string | null;
  delegatedBy: string | null;
  operation: string;
  target: string | null;
  outcome: BackupAuditOutcome | string;
  trigger: string | null;
  summary: string | null;
  hash: string;
}

export interface BackupActivity {
  entries: readonly BackupAuditEntry[];
  integrity: AuditIntegrity;
}

const OPERATION_LABELS: Readonly<Record<string, string>> = {
  'backup.create': 'Create',
  'backup.upload': 'Off-host copy',
  'backup.verify': 'Restore verification',
  'backup.retention': 'Retention',
  'backup.decrypt': 'Offline decrypt',
  'backup.restore': 'Offline restore',
};

export function backupOperationLabel(operation: string): string {
  return OPERATION_LABELS[operation] ?? operation;
}

/** Outcome on the shared indicator: a failure is danger, a refusal a warning, success stays calm. */
export function backupOutcomeTone(outcome: string): 'success' | 'warning' | 'danger' | 'neutral' {
  return outcome === 'succeeded' ? 'success' : outcome === 'failed' ? 'danger' : outcome === 'denied' ? 'warning' : 'neutral';
}

export function backupOutcomeLabel(outcome: string): string {
  return outcome === 'succeeded' ? 'Succeeded' : outcome === 'failed' ? 'Failed' : outcome === 'denied' ? 'Refused' : outcome;
}
