import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { DisclosureMarkerComponent } from '../../../../components/disclosure-marker/disclosure-marker.component';
import { InfoButtonComponent } from '../../../../components/info-button/info-button.component';
import { ProjectGitService } from '../../../../services/project-git.service';
import type {
  BranchSweepCandidate,
  BranchSweepClassTotals,
  BranchSweepMode,
  BranchSweepReport,
} from '../../../git';
import { compactGitRef } from '../../../git/models/git-ref-label';
import {
  BRANCH_SWEEP_BATCH_SIZE,
  EMPTY_BATCH_PROGRESS,
  batchPercent,
  toBatches,
  toExecutionItems,
  type BranchSweepBatchProgress,
} from './branch-sweep-batches';

type LoadState = 'idle' | 'loading' | 'loaded' | 'error';

/** Rows rendered at once; a repository can carry thousands of refs. */
const VISIBLE_ROW_LIMIT = 200;

/**
 * Stale-branch sweep panel for the Project Hub Git-Management surface
 * (AGT-2794). It shows the latest sweep of this project's repository - totals
 * per ref class, the tip-age histogram, and every candidate with the reason the
 * shared retention policy gave - and offers three operator actions:
 *
 *  1. **Re-classify** loads a fresh read-only plan. Nothing is deleted.
 *  2. **Delete selected** posts the ticked subset. Only refs the policy marked
 *     eligible can be ticked, and the backend re-derives eligibility and
 *     re-checks each tip before deleting.
 *  3. **Reclaim all allowed** walks every eligible ref in batches of
 *     {@link BRANCH_SWEEP_BATCH_SIZE}, one request (one push) per batch, with
 *     live progress and a stop button that takes effect between batches.
 *
 * The mode switch is the durable per-project setting: `report-only` (default)
 * never deletes on the schedule, `reclaim` lets the hourly run delete what the
 * policy allows.
 */
@Component({
  selector: 'app-project-branch-sweep',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DisclosureMarkerComponent, InfoButtonComponent],
  templateUrl: './project-branch-sweep.component.html',
  styleUrl: './project-branch-sweep.component.scss',
})
export class ProjectBranchSweepComponent {
  private readonly projectGit = inject(ProjectGitService);

  readonly projectName = input.required<string>();

  readonly open = signal(false);
  readonly report = signal<BranchSweepReport | null>(null);
  readonly reportState = signal<LoadState>('idle');
  readonly reportError = signal<string | null>(null);
  readonly stale = signal(false);
  readonly mode = signal<BranchSweepMode>('report-only');
  readonly busy = signal(false);
  readonly showAll = signal(false);

  private readonly selected = signal<ReadonlySet<string>>(new Set<string>());
  readonly confirming = signal(false);
  readonly progress = signal<BranchSweepBatchProgress>(EMPTY_BATCH_PROGRESS);
  private stopRequested = false;

  readonly totals = computed<BranchSweepClassTotals[]>(() => this.report()?.totals ?? []);
  readonly histogram = computed(() => this.report()?.ageHistogram ?? []);
  readonly maxBucket = computed(() =>
    Math.max(1, ...this.histogram().map(bucket => bucket.refs)));

  readonly eligible = computed<BranchSweepCandidate[]>(() =>
    (this.report()?.candidates ?? []).filter(candidate => candidate.eligible));
  readonly rows = computed<BranchSweepCandidate[]>(() => {
    const candidates = this.showAll() ? this.report()?.candidates ?? [] : this.eligible();
    return candidates.slice(0, VISIBLE_ROW_LIMIT);
  });
  readonly rowSource = computed(() =>
    (this.showAll() ? this.report()?.candidates ?? [] : this.eligible()).length);
  readonly hiddenRows = computed(() => Math.max(0, this.rowSource() - this.rows().length));

  readonly selectedCount = computed(() => this.selected().size);
  readonly batchSize = BRANCH_SWEEP_BATCH_SIZE;
  readonly percent = computed(() => batchPercent(this.progress()));
  readonly compactRef = compactGitRef;

  constructor() {
    effect(() => {
      this.projectName();
      this.open.set(false);
      this.reset();
    });
  }

  toggle(): void {
    const next = !this.open();
    this.open.set(next);
    if (next && this.reportState() === 'idle') void this.loadLatest();
  }

  isSelected(candidate: BranchSweepCandidate): boolean {
    return this.selected().has(candidate.ref);
  }

  toggleCandidate(candidate: BranchSweepCandidate): void {
    if (!candidate.eligible) return;
    const next = new Set(this.selected());
    if (!next.delete(candidate.ref)) next.add(candidate.ref);
    this.selected.set(next);
    this.confirming.set(false);
  }

  selectAllEligible(): void {
    this.selected.set(new Set(this.eligible().map(candidate => candidate.ref)));
    this.confirming.set(false);
  }

  clearSelection(): void {
    this.selected.set(new Set<string>());
    this.confirming.set(false);
  }

  requestDelete(): void {
    if (this.selectedCount() > 0) this.confirming.set(true);
  }

  cancelDelete(): void {
    this.confirming.set(false);
  }

  stop(): void {
    this.stopRequested = true;
  }

  /** Re-classifies without writing a report or deleting anything. */
  async refresh(): Promise<void> {
    await this.load(() => firstValueFrom(this.projectGit.getBranchSweepPlan(this.projectName())));
  }

  /** Runs and persists a report-only sweep, so the artifact exists on disk. */
  async runReportOnly(): Promise<void> {
    await this.load(() =>
      firstValueFrom(this.projectGit.runBranchSweep(this.projectName(), 'report-only')));
  }

  async setMode(mode: BranchSweepMode): Promise<void> {
    if (this.busy() || mode === this.mode()) return;
    this.busy.set(true);
    try {
      const settings = await firstValueFrom(
        this.projectGit.setBranchSweepMode(this.projectName(), mode));
      this.mode.set(settings.mode);
      this.reportError.set(null);
    } catch (error) {
      this.reportError.set(this.describe(error, 'Could not change the sweep mode.'));
    } finally {
      this.busy.set(false);
    }
  }

  /** Deletes the ticked subset. */
  confirmDelete(): void {
    const chosen = this.eligible().filter(candidate => this.selected().has(candidate.ref));
    this.confirming.set(false);
    void this.deleteInBatches(chosen);
  }

  /** Deletes every ref the policy allows, for the one-off backlog pass. */
  reclaimAllAllowed(): void {
    this.confirming.set(false);
    void this.deleteInBatches(this.eligible());
  }

  trackTotals(_index: number, totals: BranchSweepClassTotals): string {
    return totals.class;
  }

  trackCandidate(_index: number, candidate: BranchSweepCandidate): string {
    return candidate.ref;
  }

  private async deleteInBatches(candidates: readonly BranchSweepCandidate[]): Promise<void> {
    if (this.busy() || candidates.length === 0) return;
    const batches = toBatches(toExecutionItems(candidates));
    this.stopRequested = false;
    this.busy.set(true);
    this.reportError.set(null);
    this.progress.set({
      ...EMPTY_BATCH_PROGRESS,
      total: candidates.length,
      batches: batches.length,
    });

    try {
      for (const [index, batch] of batches.entries()) {
        if (this.stopRequested) {
          this.progress.update(current => ({ ...current, stopped: true }));
          break;
        }
        this.progress.update(current => ({ ...current, currentBatch: index + 1 }));
        const result = await firstValueFrom(
          this.projectGit.executeBranchSweep(this.projectName(), batch));
        this.progress.update(current => ({
          ...current,
          processed: current.processed + batch.length,
          deleted: current.deleted + result.deletedCount,
          kept: current.kept + result.keptCount,
        }));
      }
    } catch (error) {
      this.reportError.set(this.describe(error, 'Deleting the selected refs failed.'));
    } finally {
      this.busy.set(false);
      this.clearSelection();
      // The plan the operator acted on is spent; re-classify so the panel
      // reflects what is actually left on origin.
      await this.refresh();
    }
  }

  private async loadLatest(): Promise<void> {
    this.reportState.set('loading');
    try {
      const settings = await firstValueFrom(
        this.projectGit.getBranchSweepSettings(this.projectName()));
      this.mode.set(settings.mode);
    } catch {
      // Mode is informational here; a failed read must not hide the report.
    }
    await this.load(
      () => firstValueFrom(this.projectGit.getBranchSweepLatest(this.projectName())),
      { persisted: true });
  }

  private async load(
    request: () => Promise<BranchSweepReport | null>,
    options?: { persisted?: boolean },
  ): Promise<void> {
    this.reportState.set('loading');
    this.reportError.set(null);
    try {
      const report = await request();
      this.report.set(report);
      this.stale.set(!!options?.persisted && !!report);
      this.reportState.set('loaded');
      this.clearSelection();
      if (report?.error) this.reportError.set(report.error);
    } catch (error) {
      this.reportError.set(this.describe(error, 'Could not load the branch sweep.'));
      this.reportState.set('error');
    }
  }

  private reset(): void {
    this.report.set(null);
    this.reportState.set('idle');
    this.reportError.set(null);
    this.stale.set(false);
    this.showAll.set(false);
    this.busy.set(false);
    this.progress.set(EMPTY_BATCH_PROGRESS);
    this.clearSelection();
  }

  private describe(error: unknown, fallback: string): string {
    const record = error as { error?: unknown; message?: string } | null;
    const body = record?.error;
    if (body && typeof body === 'object' && 'error' in body) {
      const message = (body as { error?: unknown }).error;
      if (typeof message === 'string' && message.trim()) return message;
    }
    if (typeof body === 'string' && body.trim()) return body;
    return record?.message || fallback;
  }
}
