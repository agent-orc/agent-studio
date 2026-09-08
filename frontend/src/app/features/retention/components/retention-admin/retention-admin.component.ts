import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { formatRetentionBytes, type RetentionPlan } from '../../models/retention.model';
import { RetentionService } from '../../services/retention.service';
import { RetentionBackupsComponent } from '../retention-backups/retention-backups.component';
import { RetentionReportTableComponent } from '../retention-report-table/retention-report-table.component';
import { RetentionRulesTableComponent } from '../retention-rules-table/retention-rules-table.component';
import { RetentionRunHistoryComponent } from '../retention-run-history/retention-run-history.component';

@Component({
  selector: 'app-retention-admin',
  standalone: true,
  imports: [
    RetentionRulesTableComponent,
    RetentionReportTableComponent,
    RetentionRunHistoryComponent,
    RetentionBackupsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './retention-admin.component.html',
  styleUrl: './retention-admin.component.scss',
})
export class RetentionAdminComponent {
  private readonly retention = inject(RetentionService);
  private readonly history = viewChild(RetentionRunHistoryComponent);

  readonly report = signal<RetentionPlan | null>(null);
  readonly reportMode = signal<'plan' | 'apply'>('plan');
  readonly elapsedMs = signal<number | null>(null);
  readonly busy = signal<'plan' | 'apply' | null>(null);
  readonly error = signal<string | null>(null);
  readonly warnings = signal<string[]>([]);

  readonly archiveBytes = computed(() => this.report()?.actions
    .filter(action => action.kind === 'ArchiveHeavy' || action.kind === 'ArchiveTask')
    .reduce((sum, action) => sum + action.bytes, 0) ?? 0);
  readonly deleteBytes = computed(() => this.report()?.actions
    .filter(action => action.kind === 'DeleteCold' || action.kind === 'DeleteRuntime')
    .reduce((sum, action) => sum + action.bytes, 0) ?? 0);

  async preview(): Promise<void> {
    if (this.busy()) return;
    this.busy.set('plan');
    this.error.set(null);
    this.warnings.set([]);
    const started = performance.now();
    try {
      this.report.set(await firstValueFrom(this.retention.plan()));
      this.reportMode.set('plan');
    } catch (error: unknown) {
      this.error.set(this.errorMessage(error, 'The retention preview could not be generated.'));
    } finally {
      this.elapsedMs.set(Math.round(performance.now() - started));
      this.busy.set(null);
    }
  }

  async apply(): Promise<void> {
    if (!this.report() || this.busy()) return;
    this.busy.set('apply');
    this.error.set(null);
    const started = performance.now();
    try {
      const result = await firstValueFrom(this.retention.apply());
      this.report.set(result.plan);
      this.reportMode.set('apply');
      this.warnings.set([...result.warnings, ...result.errors]);
      await this.history()?.reload();
    } catch (error: unknown) {
      this.error.set(this.errorMessage(error, 'The retention run could not be completed.'));
    } finally {
      this.elapsedMs.set(Math.round(performance.now() - started));
      this.busy.set(null);
    }
  }

  exportJson(): void {
    const report = this.report();
    if (!report) return;
    const blob = new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `retention-${this.reportMode()}-${new Date(report.plannedAt).toISOString()}.json`;
    link.click();
    URL.revokeObjectURL(url);
  }

  bytes(value: number): string { return formatRetentionBytes(value); }

  private errorMessage(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) return error.error?.message ?? error.error?.error ?? fallback;
    return error instanceof Error ? error.message : fallback;
  }
}
