import { ChangeDetectionStrategy, Component, OnInit, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DevToolsService, E2EJob, DeleteE2EReport } from '../../../../services/dev-tools.service';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';

import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
type Phase = 'loading' | 'list' | 'deleting' | 'report' | 'error';

/**
 * Lists every job whose id or title matches "E2E" (case-insensitive)
 * across every watched project, with all rows selected by default so
 * the common "wipe leftover playwright fixtures" flow is one click.
 *
 * Backed by:
 *   GET  /api/devtools/e2e-jobs
 *   POST /api/devtools/e2e-jobs/delete
 */
@Component({
  selector: 'app-e2e-cleanup-dialog',
  standalone: true,
  imports: [FormsModule, DialogComponent, StatusIndicatorComponent],
  templateUrl: './e2e-cleanup-dialog.component.html',
  styleUrl: './e2e-cleanup-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class E2ECleanupDialogComponent implements OnInit {
  private devTools = inject(DevToolsService);

  readonly closed = output<void>();
  readonly didDelete = output<void>();

  readonly phase = signal<Phase>('loading');
  readonly jobs = signal<E2EJob[]>([]);
  readonly selected = signal<Set<string>>(new Set());
  readonly errorText = signal<string>('');
  readonly report = signal<DeleteE2EReport | null>(null);
  readonly pendingCount = signal(0);

  private readonly confirmDialog = inject(ConfirmDialogService);

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.phase.set('loading');
    this.devTools.listE2EJobs().subscribe({
      next: (jobs) => {
        this.jobs.set(jobs);
        // Pre-select everything: the common case is "kill all of them".
        this.selected.set(new Set(jobs.map((j) => j.taskKey)));
        this.phase.set('list');
      },
      error: (err) => {
        this.errorText.set(err?.error?.error || err?.message || 'Failed to load E2E jobs');
        this.phase.set('error');
      },
    });
  }

  selectedCount(): number {
    return this.selected().size;
  }
  allSelected(): boolean {
    return this.jobs().length > 0 && this.selected().size === this.jobs().length;
  }
  someSelected(): boolean {
    return this.selected().size > 0;
  }

  toggle(key: string, checked: boolean): void {
    const next = new Set(this.selected());
    if (checked) next.add(key);
    else next.delete(key);
    this.selected.set(next);
  }

  toggleAll(checked: boolean): void {
    if (checked) this.selected.set(new Set(this.jobs().map((j) => j.taskKey)));
    else this.selected.set(new Set());
  }

  async confirmDelete(): Promise<void> {
    const keys = [...this.selected()];
    if (keys.length === 0) return;
    const ok = await this.confirmDialog.confirm({
      title: 'Delete E2E jobs?',
      message: `Delete ${keys.length} E2E job(s) across all projects?`,
      confirmLabel: 'Delete',
      cancelLabel: 'Keep',
      kind: 'danger',
    });
    if (!ok) return;
    this.pendingCount.set(keys.length);
    this.phase.set('deleting');
    this.devTools.deleteE2EJobs(keys).subscribe({
      next: (rep) => {
        this.report.set(rep);
        this.phase.set('report');
        if (rep.deletedCount > 0) this.didDelete.emit();
      },
      error: (err) => {
        this.errorText.set(err?.error?.error || err?.message || 'Delete failed');
        this.phase.set('error');
      },
    });
  }

  onBackdropClick(): void {
    if (this.phase() === 'deleting') return;
    this.closed.emit();
  }
}
