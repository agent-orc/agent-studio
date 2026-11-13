import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, OnDestroy, afterNextRender, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BatchGateService } from '../../../services/batch-gate.service';
import { ModalStackService } from '../../../services/modal-stack.service';
import { batchIsolationRows, batchIsolationState, batchPublishedSha, batchReplayLabel, batchRiskLabel, batchRunIsRunning, batchRunStatus, batchRunEvidenceUrl, batchWaitingCards, batchTimeLine } from '../../../models/batch-gate.model';
import { formatDurationMinutes } from '../../../models/duration-format';
import type { BatchGateBatch, BatchTestFailureRank, BatchTestFailureReport, BatchGateState } from '../../../models/batch-gate.model';
import { CreateTaskFormService } from '../../board';
import { TaskService } from '../../../services/task.service';
import { PendingButtonDirective } from '../../../components/async-feedback';
import { BatchIsolationComponent } from '../batch-isolation/batch-isolation';
import { TaskReferenceNavigationService } from '../../../services/task-reference-navigation.service';
import { StatusIndicatorComponent, type StatusTone } from '../../../components/status-indicator/status-indicator.component';
import { BatchQuarantineComponent } from '../batch-quarantine/batch-quarantine';
import { BatchPhaseMetricsComponent } from '../batch-phase-metrics/batch-phase-metrics';

@Component({
  selector: 'app-batch-panel',
  standalone: true,
  imports: [PendingButtonDirective, BatchIsolationComponent, BatchQuarantineComponent, StatusIndicatorComponent, BatchPhaseMetricsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './batch-panel.html',
  styleUrl: './batch-panel.scss',
})
export class BatchPanelComponent implements OnDestroy {
  readonly project = input.required<string>();
  readonly closed = input.required<() => void>();
  readonly gate = inject(BatchGateService);
  readonly state = computed(() => this.gate.state(this.project())());
  readonly waitingCards = computed(() => batchWaitingCards(this.state()));
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
  readonly ejectReason = signal('');
  readonly localEject = signal<{ taskKey: string; reason: string } | null>(null);
  readonly ejectRequests = computed(() => {
    const stored = this.state()?.ejectRequests ?? [];
    const local = this.localEject();
    if (!local) return stored;
    const current = stored.find(request => request.taskKey === local.taskKey);
    if (current?.status === 'released') return stored;
    return current ? stored : [{ project: this.project(), taskKey: local.taskKey,
      actor: 'You', reason: local.reason, requestedAtUtc: new Date().toISOString(),
      status: 'pending' as const }, ...stored];
  });
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly riskLabel = batchRiskLabel;
  readonly replayLabel = batchReplayLabel;
  readonly publishedSha = batchPublishedSha;
  readonly timeLine = batchTimeLine;
  phaseReasonLabel(reason: string | null | undefined): string | null {
    if (!reason) return null;
    const pause = 'paused-to-per-task-gate: ';
    return reason.startsWith(pause)
      ? `Moved to per-task gates after pause: ${reason.slice(pause.length)}` : reason;
  }
  readonly formatDurationMinutes = formatDurationMinutes;
  runDuration(batch: BatchGateBatch, run: BatchGateBatch['runs'][number]): string {
    if (batchRunIsRunning(batch, run)) return 'Running';
    const detached = this.state()?.detachedFlights?.find(flight => flight.batchId === batch.batchId);
    return this.duration(run.startedAtUtc, run.finishedAtUtc ?? detached?.detachedAtUtc ?? batch.phaseAtUtc);
  }
  flightDeadline(startedAtUtc: string, deadlineMinutes: number): string | null {
    const started = Date.parse(startedAtUtc);
    return Number.isFinite(started) && Number.isFinite(deadlineMinutes)
      ? new Date(started + deadlineMinutes * 60_000).toISOString() : null;
  }
  phaseTone(phase: string): StatusTone {
    switch (phase) {
      case 'running': return 'running';
      case 'red': return 'danger';
      case 'paused': return 'warning';
      case 'green': case 'published': return 'success';
      default: return 'neutral';
    }
  }
  runTone(status: string): StatusTone {
    switch (status.toLowerCase()) {
      case 'running': return 'running';
      case 'pass': case 'passed': return 'success';
      case 'fail': case 'failed': return 'danger';
      default: return 'neutral';
    }
  }
  readonly runStatus = batchRunStatus;
  readonly runIsRunning = batchRunIsRunning;
  readonly runEvidenceUrl = batchRunEvidenceUrl;
  private readonly references = inject(TaskReferenceNavigationService);
  private readonly body = viewChild<ElementRef<HTMLDivElement>>('body');
  private readonly closeButton = viewChild<ElementRef<HTMLButtonElement>>('closeButton');
  private readonly confirmButton = viewChild<ElementRef<HTMLButtonElement>>('confirmButton');
  private readonly ejectReasonInput = viewChild<ElementRef<HTMLTextAreaElement>>('ejectReasonInput');
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
  retryTime(value: string): string {
    return `${new Date(value).toLocaleTimeString('en-GB', { timeZone: 'UTC', hour: '2-digit', minute: '2-digit', second: '2-digit' })} UTC`;
  }
  duration(start?: string | null, end?: string | null): string {
    if (!start) return 'Waiting';
    if (!end) return 'Interrupted';
    const elapsed = Date.parse(end) - Date.parse(start);
    if (!Number.isFinite(elapsed)) return 'Unknown';
    return formatDurationMinutes(elapsed / 60_000);
  }
  memberTitle(key: string): string | null {
    const job = this.taskService.jobs().find(item => item.taskKey === key || item.id === key || item.key === key);
    return job?.title ?? null;
  }
  memberHref(key: string): string { return `#/tasks/${encodeURIComponent(key.split('::').at(-1) ?? key)}`; }
  openMember(event: MouseEvent, key: string): void {
    const opened = this.references.openTaskKey(key) || this.references.openReference(key);
    if (opened) { event.preventDefault(); this.close(); }
  }
  redNext(batch: BatchGateBatch): string {
    const children = this.batches().filter(child => child.parentBatchId === batch.batchId);
    if (children.some(child => ['formed', 'assembling', 'assembled', 'running', 'green', 'publishing'].includes(child.phase)))
      return 'Isolation has started. The next verdict will identify which members need a separate gate.';
    const slots = this.state()?.gateSlots;
    if (slots && slots.busy >= slots.total)
      return 'Isolation waits for a gate slot. It starts when a slot becomes free; members then return to their agents for a fix or continue through their own gate.';
    return 'Isolation is awaiting the next planner tick. Members return to their agents for a fix or continue through their own gate after isolation.';
  }
  isolationRows(batch: BatchGateBatch) { return batchIsolationRows(this.state(), batch); }
  isolationState(batch: BatchGateBatch) { return batchIsolationState(batch); }
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
    afterNextRender(() => (target === 'close' ? this.confirmButton() : this.ejectReasonInput())
      ?.nativeElement.focus(), { injector: this.injector });
  }
  close(): void { this.closed()(); }
  act(): void {
    const target = this.confirm();
    if (!target || this.busy() || (target !== 'close' && !this.ejectReason().trim())) return;
    this.busy.set(true);
    this.actionError.set(null);
    const reason = this.ejectReason().trim();
    const request = target === 'close' ? this.gate.closeNow(this.project()) : this.gate.eject(this.project(), target, reason);
    request.subscribe({
      next: () => {
        this.busy.set(false);
        this.confirm.set(null);
        if (target !== 'close') this.localEject.set({ taskKey: target, reason });
        this.ejectReason.set('');
        void this.gate.refresh(this.project());
      },
      error: err => { this.busy.set(false); this.actionError.set(err?.error?.error ?? 'The action could not be completed.'); },
    });
  }
}
