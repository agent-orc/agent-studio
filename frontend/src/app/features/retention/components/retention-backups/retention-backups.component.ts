import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { TaskServerService } from '../../../task-server';
import {
  formatRetentionBytes,
  type FullBackupRetention,
  type FullBackupSummary,
  type RetentionPolicy,
} from '../../models/retention.model';
import { RetentionService } from '../../services/retention.service';

@Component({
  selector: 'app-retention-backups',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './retention-backups.component.html',
  styleUrl: './retention-backups.component.scss',
})
export class RetentionBackupsComponent implements OnInit {
  private readonly retention = inject(RetentionService);
  private readonly taskServer = inject(TaskServerService);
  private readonly confirm = inject(ConfirmDialogService);

  readonly backups = signal<FullBackupSummary[]>([]);
  readonly policy = signal<RetentionPolicy | null>(null);
  readonly thinning = signal<FullBackupRetention>({ daily: 7, weekly: 4, monthly: 12 });
  readonly verified = signal<ReadonlySet<string>>(new Set());
  readonly busy = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly maintenanceMode = computed(() => this.taskServer.status()?.maintenance.mode === 'maintenance');

  ngOnInit(): void {
    this.taskServer.ensureLoaded();
    void this.reload();
  }

  async reload(conflict = false): Promise<void> {
    this.error.set(null);
    try {
      const [sets, policy] = await Promise.all([
        firstValueFrom(this.retention.listFullBackups()),
        firstValueFrom(this.retention.getWorkspacePolicy()),
      ]);
      this.backups.set([...sets.backups].sort((a, b) => b.createdAt.localeCompare(a.createdAt)));
      this.policy.set(policy);
      this.thinning.set(policy.fullBackups ?? { daily: 7, weekly: 4, monthly: 12 });
      if (conflict) this.message.set('The backup rule changed elsewhere. The newer version is loaded.');
    } catch (error: unknown) {
      this.error.set(this.errorMessage(error, 'Backup sets could not be loaded.'));
    }
  }

  setThinning(field: keyof FullBackupRetention, value: string): void {
    this.thinning.update(current => ({ ...current, [field]: Math.round(Number(value)) }));
  }

  thinningError(): string | null {
    const value = this.thinning();
    if (value.daily < 1 || value.daily > 366) return 'Keep 1 to 366 daily sets.';
    if (value.weekly < 1 || value.weekly > 260) return 'Keep 1 to 260 weekly sets.';
    if (value.monthly < 1 || value.monthly > 120) return 'Keep 1 to 120 monthly sets.';
    return null;
  }

  async saveThinning(): Promise<void> {
    const policy = this.policy();
    if (!policy || this.thinningError() || this.busy()) return;
    this.busy.set('thinning');
    this.error.set(null);
    try {
      const updated = await firstValueFrom(this.retention.updateWorkspacePolicy({
        rules: policy.rules,
        expectedVersion: policy.version,
        fullBackups: this.thinning(),
      }));
      this.policy.set(updated);
      this.message.set('Backup set retention saved.');
    } catch (error: unknown) {
      if (error instanceof HttpErrorResponse && error.status === 409) await this.reload(true);
      else this.error.set(this.errorMessage(error, 'Backup set retention could not be saved.'));
    } finally {
      this.busy.set(null);
    }
  }

  async create(): Promise<void> {
    if (this.busy()) return;
    this.busy.set('create');
    this.error.set(null);
    try {
      const created = await firstValueFrom(this.retention.createFullBackup());
      this.backups.update(current => [created, ...current]);
      this.message.set(`Backup ${created.id} created.`);
    } catch (error: unknown) {
      this.error.set(this.errorMessage(error, 'The full backup could not be created.'));
    } finally {
      this.busy.set(null);
    }
  }

  async verify(backup: FullBackupSummary): Promise<void> {
    if (this.busy()) return;
    this.busy.set(`verify:${backup.id}`);
    this.error.set(null);
    try {
      const result = await firstValueFrom(this.retention.verifyFullBackup(backup.id));
      if (result.verified) this.verified.update(current => new Set([...current, backup.id]));
      this.backups.update(current => current.map(item => item.id === backup.id ? result.summary : item));
      this.message.set(result.verified ? `Backup ${backup.id} verified.` : `Backup ${backup.id} failed verification.`);
    } catch (error: unknown) {
      this.error.set(this.errorMessage(error, 'The full backup could not be verified.'));
    } finally {
      this.busy.set(null);
    }
  }

  async restore(backup: FullBackupSummary): Promise<void> {
    if (!this.maintenanceMode() || this.busy()) return;
    const confirmed = await this.confirm.confirm({
      title: 'Restore full backup?',
      message: 'This replaces the current Task Server store after verification and rollback preparation.',
      detail: backup.id,
      confirmLabel: 'Restore backup',
      kind: 'danger',
    });
    if (!confirmed) return;
    this.busy.set(`restore:${backup.id}`);
    this.error.set(null);
    try {
      const result = await firstValueFrom(this.retention.restoreFullBackup(backup.id));
      this.message.set(result.message);
    } catch (error: unknown) {
      this.error.set(this.errorMessage(error, 'The full backup could not be restored.'));
    } finally {
      this.busy.set(null);
    }
  }

  date(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? '–' : date.toLocaleString();
  }

  bytes(value: number): string { return formatRetentionBytes(value); }

  verification(backup: FullBackupSummary): string {
    if (this.verified().has(backup.id)) return 'Verified';
    return backup.warnings.length ? 'Needs attention' : 'Not checked';
  }

  shortHash(hash: string): string { return hash ? hash.slice(0, 12) : '–'; }

  private errorMessage(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) return error.error?.message ?? error.error?.error ?? fallback;
    return error instanceof Error ? error.message : fallback;
  }
}
