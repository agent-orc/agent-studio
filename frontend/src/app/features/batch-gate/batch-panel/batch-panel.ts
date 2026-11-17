import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, OnDestroy, afterNextRender, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { map, switchMap } from 'rxjs';
import { BatchGateService } from '../../../services/batch-gate.service';
import { ModalStackService } from '../../../services/modal-stack.service';
import { batchAlert, batchFlakySuspectLabel, batchIsolationRows, batchIsolationState, batchPublishedSha, batchReplayLabel, batchRiskLabel, batchRunIsRunning, batchRunStatus, batchRunEvidenceUrl, batchWaitingCards, batchTimeLine, batchDateTime, BATCH_TIME_ZONE_NOTE } from '../../../models/batch-gate.model';
import { formatDurationMinutes } from '../../../models/duration-format';
import type { BatchFlakySuspect, BatchGateBatch, BatchNextAction, BatchTestFailureRank, BatchTestFailureReport, BatchGateState } from '../../../models/batch-gate.model';
import { CreateTaskFormService } from '../../board';
import { TaskService } from '../../../services/task.service';
import { PendingButtonDirective } from '../../../components/async-feedback';
import type { BatchLaneIdleCause } from '../../../models/batch-gate.model';
import { BatchIsolationComponent } from '../batch-isolation/batch-isolation';
import { TaskReferenceNavigationService } from '../../../services/task-reference-navigation.service';
import { StatusIndicatorComponent, type StatusTone } from '../../../components/status-indicator/status-indicator.component';
import { BatchQuarantineComponent } from '../batch-quarantine/batch-quarantine';
import { BatchPhaseMetricsComponent } from '../batch-phase-metrics/batch-phase-metrics';
import { BatchLaneAdmissionComponent } from '../batch-lane-admission/batch-lane-admission';
import { BatchPerTaskGatesComponent } from '../batch-per-task-gates/batch-per-task-gates';
import { BatchInfrastructureRetriesComponent } from './batch-infrastructure-retries/batch-infrastructure-retries';

class FixCardError extends Error {}

@Component({
  selector: 'app-batch-panel',
  standalone: true,
  imports: [NgTemplateOutlet, BatchInfrastructureRetriesComponent, PendingButtonDirective, BatchIsolationComponent, BatchQuarantineComponent, StatusIndicatorComponent, BatchPhaseMetricsComponent, BatchLaneAdmissionComponent, BatchPerTaskGatesComponent],
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
  /**
   * F14: the batch the lane pill names as red or isolating, then every running
   * batch, open the panel; statistics and history follow.
   */
  readonly activeBatches = computed(() => {
    const alert = batchAlert(this.state())?.batchId;
    return this.batches().filter(batch => batch.batchId === alert || batch.phase === 'running')
      .sort((a, b) => Number(b.batchId === alert) - Number(a.batchId === alert));
  });
  readonly recentBatches = computed(() => {
    const active = new Set(this.activeBatches().map(batch => batch.batchId));
    return this.batches().filter(batch => !active.has(batch.batchId));
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
  readonly timeZoneNote = BATCH_TIME_ZONE_NOTE;
  phaseReasonLabel(reason: string | null | undefined): string | null {
    if (!reason) return null;
    const pause = 'paused-to-per-task-gate: ';
    return reason.startsWith(pause)
      ? `Moved to per-task gates after pause: ${reason.slice(pause.length)}` : reason;
  }
  readonly formatDurationMinutes = formatDurationMinutes;
  laneIdleCause(cause: BatchLaneIdleCause): string {
    switch (cause) {
      case 'per-task-holder': return 'Per-task holder';
      case 'restart-recovery': return 'Restart recovery';
      default: return 'Planner';
    }
  }
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

  /** AGT-3872: every panel time is UTC; the header names the zone once. */
  readonly date = batchDateTime;
  retryTime(value: string): string {
    return new Date(value).toLocaleTimeString('en-GB', { timeZone: 'UTC', hour: '2-digit', minute: '2-digit', second: '2-digit' });
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
  /** F13: who acts on the batch's single next step. */
  actorLabel(next: BatchNextAction): string {
    return next.actor === 'owner' ? 'you' : next.actor === 'none' ? 'nobody' : 'planner';
  }
  isolationRows(batch: BatchGateBatch) { return batchIsolationRows(this.state(), batch); }
  isolationState(batch: BatchGateBatch) { return batchIsolationState(batch); }
  flakySuspectLabel(suspect: BatchFlakySuspect): string { return batchFlakySuspectLabel(suspect); }
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
      ...(test.flakySuspectRuns ? [`Red runs no isolation child or same-code re-gate reproduced: ${test.flakySuspectRuns}.`] : []),
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
    const title = `${test.flakySuspect ? 'Fix flaky test' : 'Fix failing test'}: ${test.testName}`;
    // AGT-3872: create the bug card in the backlog and open it in the card
    // detail, instead of the legacy full-field editor.
    this.taskService.getWatchPaths().pipe(
      map(paths => paths.find(path => path.name === project)?.path ?? null),
      switchMap(watchPath => {
        if (!watchPath) throw new FixCardError('This project is no longer available for a fix card.');
        return this.createForm.createBacklogCard({ watchPath, title, prompt, taskType: 'bug' });
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({ next: created => {
      this.openingFix.set(null);
      this.close();
      this.references.openCreatedTask(created.watchPath, created.jobId);
    }, error: (error: unknown) => {
      this.openingFix.set(null);
      this.actionError.set(error instanceof FixCardError ? error.message : 'Could not create the fix card. Try again.');
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
