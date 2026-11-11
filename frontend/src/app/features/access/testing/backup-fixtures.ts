import { signal } from '@angular/core';
import { vi } from 'vitest';
import type { BackupActivity, BackupAuditEntry } from '../models/backup-activity.model';

export function backupEntry(overrides: Partial<BackupAuditEntry> = {}): BackupAuditEntry {
  return {
    sequence: 12,
    at: '2026-11-09T06:00:00Z',
    actor: 'schedule',
    session: null,
    delegatedBy: null,
    operation: 'backup.create',
    target: 'studio-backup-20261109T060000Z-a1b2c3',
    outcome: 'succeeded',
    trigger: 'schedule',
    summary: 'Created encrypted backup studio-backup-20261109T060000Z-a1b2c3 (412 files, 1048576 bytes).',
    hash: 'h12',
    ...overrides,
  };
}

export function backupActivity(entries: BackupAuditEntry[] = [
  backupEntry({ sequence: 14, at: '2026-11-09T08:12:00Z', actor: 'usr_operator', operation: 'backup.retention', target: null, outcome: 'denied',
    trigger: 'manual:usr_operator', summary: 'Retention down to 1 archive refused: below the configured 12 needs the Owner role.' }),
  backupEntry({ sequence: 13, at: '2026-11-09T06:00:05Z', operation: 'backup.upload', outcome: 'failed',
    summary: 'Copy of studio-backup-20261109T060000Z-a1b2c3 to the s3 target failed: connection refused' }),
  backupEntry(),
]): BackupActivity {
  return {
    entries,
    integrity: { state: 'verified', checkedAt: '2026-11-09T07:00:00Z', verifiedThrough: 13, message: null, alert: false },
  };
}

/** A signal-backed stand-in for BackupActivityService. */
export function fakeBackupActivityService(activity: BackupActivity | null = backupActivity()) {
  return {
    activity: signal<BackupActivity | null>(activity),
    state: signal<'idle' | 'loading' | 'ready' | 'denied' | 'error'>(activity ? 'ready' : 'denied'),
    error: signal<string | null>(null),
    load: vi.fn(() => Promise.resolve()),
  };
}
