import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { DatePipe, formatDate } from '@angular/common';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { DisclosureMarkerComponent } from '@studio/ui/components/disclosure-marker/disclosure-marker.component';
import {
  backupKind, backupPolicy, backupStatus, formatBytes, operationKind, operationState,
  type DatabaseBackup, type DatabaseOperation, type DatabaseOperationRequest, type ManagedDatabase,
} from '../managed-databases-panel/managed-database.model';
import { PrincipalNamePipe } from '../../../../pipes/principal-name.pipe';

/** Backups shown before the disclosure reveals the rest. */
export const VISIBLE_BACKUPS = 5;

interface RestoreTarget { value: string; label: string }

function backupTime(value: string): string {
  return `${formatDate(value, 'd MMM y, HH:mm', 'en-US', 'UTC')} UTC`;
}

/**
 * Backups block of one managed database (AGT-3878): last backup and policy,
 * the backup list with test restore and restore, and recent operations with
 * the Owner approval of production restores. It owns only form state; the
 * panel sends every request, guards it by project and reloads afterwards.
 */
@Component({
  selector: 'app-managed-database-backups',
  standalone: true,
  imports: [PrincipalNamePipe, DatePipe, StatusIndicatorComponent, DisclosureMarkerComponent],
  templateUrl: './managed-database-backups.html',
  styleUrl: './managed-database-backups.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ManagedDatabaseBackupsComponent {
  readonly database = input.required<ManagedDatabase>();
  /** Other running managed databases of the same environment. */
  readonly targets = input<string[]>([]);
  readonly busy = input(false);
  readonly operate = output<DatabaseOperationRequest>();

  readonly showAll = signal(false);
  readonly restoring = signal<string | null>(null);
  readonly target = signal('');
  readonly typed = signal('');

  readonly running = computed(() => this.database().state === 'running');
  readonly status = computed(() => backupStatus(this.database().backupStatus));
  readonly policy = computed(() => backupPolicy(this.database().backupPolicy));
  readonly backups = computed(() => this.database().backups ?? []);
  readonly visibleBackups = computed(() => this.showAll() ? this.backups() : this.backups().slice(0, VISIBLE_BACKUPS));
  readonly operations = computed(() => this.database().operations ?? []);
  readonly production = computed(() => this.database().environmentKind === 'production');
  readonly targetOptions = computed<RestoreTarget[]>(() => [
    ...(this.running() ? [{ value: this.database().name, label: `This database (${this.database().name})` }] : []),
    ...this.targets().map(name => ({ value: name, label: `Another database: ${name}` })),
  ]);
  readonly canRestore = computed(() => this.targetOptions().some(option => option.value === this.target())
    && this.typed() === this.target());

  readonly bytes = formatBytes;
  readonly kind = backupKind;
  readonly operationKind = operationKind;
  readonly operationState = operationState;
  readonly visibleLimit = VISIBLE_BACKUPS;

  describe(operation: DatabaseOperation): string {
    const backup = this.backups().find(item => item.id === operation.backupId);
    const source = backup ? `the backup from ${backupTime(backup.takenAt)}` : operation.backupId ? `backup ${operation.backupId}` : 'a backup';
    if (operation.kind === 'backup') return `of ${operation.sourceDatabase}`;
    if (operation.kind === 'verify') return `of ${source} into a scratch database`;
    return `of ${source} into ${operation.targetDatabase}`;
  }

  toggleAll(): void { this.showAll.update(value => !value); }

  backUpNow(): void {
    const name = this.database().name;
    this.operate.emit({
      path: 'operations', body: { kind: 'backup' }, fallback: 'The backup could not be started.',
      notice: () => `Backup of ${name} queued. The run host takes it on its next poll.`,
    });
  }

  verify(backup: DatabaseBackup): void {
    this.operate.emit({
      path: 'operations', body: { kind: 'verify', backupId: backup.id }, fallback: 'The test restore could not be started.',
      notice: () => `Test restore of the backup from ${backupTime(backup.takenAt)} queued.`,
    });
  }

  startRestore(backup: DatabaseBackup): void {
    this.restoring.set(backup.id);
    this.target.set(this.targetOptions()[0]?.value ?? '');
    this.typed.set('');
  }

  cancelRestore(): void { this.restoring.set(null); this.typed.set(''); }

  chooseTarget(value: string): void { this.target.set(value); this.typed.set(''); }

  type(event: Event): void { this.typed.set((event.target as HTMLInputElement).value); }

  restore(backup: DatabaseBackup): void {
    if (!this.canRestore()) return;
    const target = this.target();
    this.operate.emit({
      path: 'operations', body: { kind: 'restore', backupId: backup.id, targetDatabase: target, confirm: this.typed() },
      fallback: 'The restore could not be requested.',
      notice: operation => operation?.state === 'awaiting-approval'
        ? `Restore into ${target} waits for an Owner approval.`
        : `Restore into ${target} queued. Its current data is backed up first.`,
      done: () => this.cancelRestore(),
    });
  }

  decide(operation: DatabaseOperation, decision: 'approve' | 'reject'): void {
    this.operate.emit({
      path: `operations/${encodeURIComponent(operation.id)}/${decision}`, body: {},
      fallback: decision === 'approve' ? 'The restore could not be approved.' : 'The restore could not be rejected.',
      notice: () => `Restore into ${operation.targetDatabase} ${decision === 'approve' ? 'approved' : 'rejected'}.`,
    });
  }
}
