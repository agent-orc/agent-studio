import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, OnDestroy, afterNextRender, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BatchGateService } from '../../../services/batch-gate.service';
import { ModalStackService } from '../../../services/modal-stack.service';
import { StatusIndicatorComponent } from '../../../components/status-indicator/status-indicator.component';
import { batchAdvisorVerdictTone, batchDecisionTone, batchOutcomeTone } from './batch-status-tones';
import { batchIsolationRows, batchPublishedSha, batchReplayLabel, batchRiskLabel } from '../../../models/batch-gate.model';
import type { BatchGateBatch, BatchTestFailureRank, BatchTestFailureReport, BatchGateState } from '../../../models/batch-gate.model';
import { CreateTaskFormService } from '../../board';
import { TaskService } from '../../../services/task.service';
import { PendingButtonDirective } from '../../../components/async-feedback';
import { BatchIsolationComponent } from '../batch-isolation/batch-isolation';

@Component({
  selector: 'app-batch-panel',
  standalone: true,
  imports: [StatusIndicatorComponent, PendingButtonDirective, BatchIsolationComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-panel.html',
  styleUrl: './batch-panel.scss',
})
export class BatchPanelComponent implements OnDestroy {
  readonly project = input.required<string>();
  readonly closed = input.required<() => void>();
  readonly gate = inject(BatchGateService);
  readonly state = computed(() => this.gate.state(this.project())());
  readonly history = signal<BatchGateState | null>(null);
  readonly batches = computed(() => {
    const current = this.state()?.batches ?? [];
    const older = this.history()?.batches ?? [];
    const currentIds = new Set(current.map(batch => batch.batchId));
    return [...current, ...older.filter(batch => !currentIds.has(batch.batchId))];
  });
  readonly failures = signal<BatchTestFailureReport | null>(null);
  readonly failureError = signal(false);
  readonly openingFix = signal<string | null>(null);
  readonly error = computed(() => this.gate.error(this.project())());
  readonly confirm = signal<string | null>(null);
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly riskLabel = batchRiskLabel;
  readonly replayLabel = batchReplayLabel;
  readonly publishedSha = batchPublishedSha;
  readonly decisionTone = batchDecisionTone;
  readonly advisorTone = batchAdvisorVerdictTone;
  readonly outcomeTone = batchOutcomeTone;
  readonly phases = ['formed', 'assembling', 'assembled', 'running', 'green', 'publishing', 'published', 'red', 'paused', 'abandoned'];
  private readonly body = viewChild<ElementRef<HTMLDivElement>>('body');
  private readonly closeButton = viewChild<ElementRef<HTMLButtonElement>>('closeButton');
  private readonly confirmButton = viewChild<ElementRef<HTMLButtonElement>>('confirmButton');
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);
  private readonly taskService = inject(TaskService);
  private readonly createForm = inject(CreateTaskFormService);
  private readonly previousFocus = document.activeElement as HTMLElement | null;

  constructor() {
    const stack = inject(ModalStackService);
    stack.pushUntilDestroyed('batch-panel', () => this.close(), this.destroyRef, {
      dialog: () => (this.host.nativeElement as HTMLElement).querySelector<HTMLElement>('[role="dialog"]'),
      backgroundRoot: () => this.host.nativeElement,
    });
    afterNextRender(() => {
      document.body.appendChild(this.host.nativeElement);
      this.closeButton()?.nativeElement.focus();
      stack.refreshModalScope();
    }, { injector: this.injector });
    effect(onCleanup => {
      const project = this.project();
      this.history.set(null);
      this.failures.set(null);
      this.failureError.set(false);
      if (project) onCleanup(this.gate.watch(project));
    });
    effect(onCleanup => {
      const project = this.project();
      if (!project) return;
      const subscription = this.gate.failedTests(project).subscribe({
        next: value => this.failures.set(value),
        error: () => this.failureError.set(true),
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }
  ngOnDestroy(): void {
    this.host.nativeElement.remove();
    queueMicrotask(() => this.previousFocus?.focus());
  }

  date(value?: string | null): string {
    const stamp = value ? Date.parse(value) : NaN;
    if (!Number.isFinite(stamp)) return 'Unknown';
    return new Date(stamp).toLocaleString('en-US', { dateStyle: 'medium', timeStyle: 'short' });
  }
  duration(start?: string | null, end?: string | null): string {
    if (!start) return 'Waiting';
    if (!end) return 'Running';
    const elapsed = Date.parse(end) - Date.parse(start);
    if (!Number.isFinite(elapsed)) return 'Unknown';
    const minutes = Math.max(0, Math.round(elapsed / 60_000));
    return `${minutes} min`;
  }
  phaseLabel(phase: string): string { return phase; }
  isolationRows(batch: BatchGateBatch) { return batchIsolationRows(this.state(), batch); }
  /** The members a failed run proved red, joined for display, or null. */
  provenBy(failure: { batchId: string; runId: string }): string | null {
    const culprits = (this.state()?.isolationFindings ?? []).filter(finding => finding.status === 'proven-red'
      && finding.provingBatchId === failure.batchId && finding.provingRunId === failure.runId)
      .map(finding => finding.taskKey);
    return culprits.length ? [...new Set(culprits)].join(', ') : null;
  }
  onBodyKeydown(event: KeyboardEvent): void {
    const owner = this.body()?.nativeElement;
    if (!owner || (event.target instanceof HTMLElement && event.target.closest(
      'input, textarea, select, [contenteditable="true"], [role="listbox"], [role="combobox"], [role="tree"]'))) return;
    const step = 40;
    const page = owner.clientHeight * .9;
    const positions: Record<string, number> = { ArrowUp: owner.scrollTop - step,
      ArrowDown: owner.scrollTop + step, PageUp: owner.scrollTop - page,
      PageDown: owner.scrollTop + page, Home: 0, End: owner.scrollHeight };
    const next = positions[event.key];
    if (next === undefined) return;
    owner.scrollTop = next;
    event.preventDefault();
    event.stopPropagation();
  }
  openBatch(batchId: string): void {
    const show = () => afterNextRender(() => document.getElementById(`batch-${batchId}`)?.scrollIntoView(),
      { injector: this.injector });
    if (this.batches().some(batch => batch.batchId === batchId)) {
      document.getElementById(`batch-${batchId}`)?.scrollIntoView();
      return;
    }
    this.gate.history(this.project(), 500).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: value => { this.history.set(value); show(); },
      error: () => this.actionError.set('Could not open this batch. Try again.'),
    });
  }
  createFixCard(test: BatchTestFailureRank): void {
    if (this.openingFix()) return;
    this.actionError.set(null);
    this.openingFix.set(test.testName);
    const project = this.project();
    const proven = test.failures.map(failure => ({ failure, culprits: this.provenBy(failure) }))
      .filter(item => item.culprits);
    const prompt = [
      `${test.flakySuspect ? 'Fix the recurring test failure' : 'Fix the failing test'}: ${test.testName}`,
      '',
      ...proven.map(({ failure, culprits }) =>
        `Batch isolation proved ${culprits} red in batch ${failure.batchId}, run ${failure.runId}.`),
      `Observed in ${test.batches} batches and ${test.runs} runs over 7 days, with ${test.distinctMemberSets} distinct member sets.`,
      `First failure: ${test.firstFailureUtc}. Last failure: ${test.lastFailureUtc}.`,
      `Flaky rerun passed: ${test.flakyRerunPassed ? 'yes' : 'no'}.`,
      `Batches: ${test.batchIds.join(', ')}.`,
      '',
      'Failure evidence:',
      ...test.failures.slice(0, 30).flatMap(failure => [
        `- ${failure.batchId}, run ${failure.runId}, ${failure.atUtc}, members ${failure.members.join(', ') || 'unknown'}, rerun passed: ${failure.flakyRerunPassed ? 'yes' : 'no'}`,
        failure.errorExcerpt || '(no error excerpt recorded)',
      ]),
      ...(test.failures.length > 30 ? [`${test.failures.length - 30} older failure records omitted from this draft; all batch IDs are listed above.`] : []),
      '',
      'Investigate the shared test or fixture, fix the root cause, and add a regression check.',
    ].join('\n');
    this.taskService.getWatchPaths().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: paths => {
      this.openingFix.set(null);
      if (!paths.some(path => path.name === project)) {
        this.actionError.set('This project is no longer available for a fix card.');
        return;
      }
      this.createForm.openUxuiFollowUp({ projectName: project, prefill: prompt,
        title: `${test.flakySuspect ? 'Fix flaky test' : 'Fix failing test'}: ${test.testName}` }, paths);
      this.createForm.onTaskTypeChange('bug');
      this.close();
    }, error: () => {
      this.openingFix.set(null);
      this.actionError.set('Could not load the project for the fix card.');
    } });
  }
  showConfirm(target: string): void {
    this.confirm.set(target);
    this.actionError.set(null);
    afterNextRender(() => this.confirmButton()?.nativeElement.focus(), { injector: this.injector });
  }
  close(): void { this.closed()(); }
  act(): void {
    const target = this.confirm();
    if (!target || this.busy()) return;
    this.busy.set(true);
    this.actionError.set(null);
    const request = target === 'close' ? this.gate.closeNow(this.project()) : this.gate.eject(this.project(), target);
    request.subscribe({
      next: () => { this.busy.set(false); this.confirm.set(null); void this.gate.refresh(this.project()); },
      error: err => { this.busy.set(false); this.actionError.set(err?.error?.error ?? 'The action could not be completed.'); },
    });
  }
}
