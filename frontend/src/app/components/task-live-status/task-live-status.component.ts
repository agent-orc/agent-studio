import { ChangeDetectionStrategy, Component, Injector, computed, effect, inject, input, signal } from '@angular/core';
import type { TaskInfo } from '../../models/task.model';
import { TaskState } from '../../models/task.model';
import { NowTickService } from '../../services/now-tick.service';
import { deriveActiveTaskRun, isTaskRunActive, resolveRunActivityKind } from '../../services/run-activity.util';
import { PickupHoldComponent } from '../pickup-hold/pickup-hold.component';
import { runnerWaitCode, runnerWaitHeadline } from '../../models/ready-wait-reason';
import { TaskReferenceNavigationService } from '../../services/task-reference-navigation.service';
import { TaskService } from '../../services/task.service';
import { ErrorDialogService } from '../../services/error-dialog.service';
import { BatchGateService } from '../../services/batch-gate.service';
import {
  activityDetail, cliLabel, clock, dependencyWaitHeadline, elapsed, holdHeadline, idleHeadline, latestActivityAt, stepPrefix, timestamp,
} from './task-live-status.util';

export type TaskLiveStatusVariant = 'card' | 'detail';
type LiveTone = 'active' | 'waiting' | 'idle' | 'stalled';

interface LiveStatusView {
  tone: LiveTone;
  headline: string;
  detail: string | null;
  next: string[];
  perTaskGate?: boolean;
  /** Null when the card has no live status yet, so no attempt is claimed. */
  attempt: number | null;
  /** AGT-3579: runner reason code when the runner is not picking this card. */
  waitReason?: string;
  /** AGT-3617: the cause card this card is held behind, linked from the headline. */
  hold?: CauseHold;
}

interface CauseHold {
  causeKey: string;
  summary: string;
}

const STALE_AFTER_MS = 10 * 60 * 1000;

@Component({
  selector: 'app-task-live-status',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PickupHoldComponent],
  templateUrl: './task-live-status.component.html',
  styleUrl: './task-live-status.component.scss',
})
export class TaskLiveStatusComponent {
  readonly task = input.required<TaskInfo>();
  readonly variant = input<TaskLiveStatusVariant>('card');
  readonly dense = input(false);
  private readonly now = inject(NowTickService).now;
  private readonly injector = inject(Injector);
  private readonly tasks = inject(TaskService);
  private readonly errors = inject(ErrorDialogService);
  private readonly batchGate = inject(BatchGateService);
  readonly switchingToAuto = signal(false);
  readonly hasRecordedAttempt = computed(() => {
    const task = this.task();
    return !!(task.liveStatus?.activeStep || task.execution || task.runActivity || task.runner);
  });

  constructor() {
    effect(onCleanup => {
      const task = this.task();
      if (task.state === TaskState.AutoReview) onCleanup(this.batchGate.watch(task.projectName));
    });
  }

  switchToAuto(event: MouseEvent): void {
    event.stopPropagation();
    if (this.switchingToAuto()) return;
    const project = this.task().projectName;
    this.switchingToAuto.set(true);
    this.tasks.setRunnerMode(project, 'auto-continuous').subscribe({
      next: () => {
        this.switchingToAuto.set(false);
        this.tasks.refresh(true);
      },
      error: error => {
        this.switchingToAuto.set(false);
        this.errors.show(error, {
          title: 'Failed to enable auto-pickup',
          fallbackMessage: 'Failed to enable auto-pickup',
          source: `Project ${project}`,
        });
      },
    });
  }

  readonly view = computed<LiveStatusView | null>(() => {
    const task = this.task();
    const status = task.liveStatus;
    const causeHeld = causeHold(task);
    const perTask = this.batchGate.state(task.projectName)()?.perTaskGates?.find(gate =>
      gate.taskKey === task.taskKey || gate.taskKey === task.key || gate.taskKey === task.id);
    if (perTask && task.state === TaskState.AutoReview) {
      const started = timestamp(perTask.startedAtUtc);
      const handoff = perTask.batchId
        ? `batch ${perTask.batchId} paused: ${perTask.reason.replace(/^batch [^ ]+ paused:\s*/, '')}`
        : perTask.reason;
      return {
        tone: perTask.running ? 'active' : 'waiting',
        headline: perTask.running
          ? `Per-task gate running on ${perTask.host || 'gate host'}${started === null ? '' : ` since ${clock(started)}`}`
          : `Per-task gate queued (${perTask.position} of ${perTask.total}) after ${handoff}`,
        detail: perTask.running ? 'Red escalates; green integrates.' : 'One full suite per member. Red escalates; green integrates.',
        next: [],
        attempt: status?.attempt ?? null,
        perTaskGate: true,
      };
    }
    if (!status) return causeHeld ? holdView(task, causeHeld, null, [], this.now()) : null;

    const next = (status.nextSteps ?? []).slice(0, 3).map(step => step.displayName);
    const active = status.activeStep;
    if (active) {
      const startedAt = timestamp(active.startedAt);
      const duration = startedAt === null ? null : elapsed(this.now() - startedAt);
      const startClock = startedAt === null
        ? null
        : new Date(startedAt).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
      const host = task.executionLocation?.hostDisplayName
        || task.runner?.runnerName
        || task.runner?.hostname
        || null;
      const runtime = [
        startClock ? `started ${startClock}` : null,
        duration ? `running ${duration}` : null,
        host ? `on ${host}` : null,
        active.model || null,
        active.cliType ? `via ${cliLabel(active.cliType)}` : null,
      ].filter((part): part is string => !!part);
      return {
        tone: 'active',
        headline: `${stepPrefix(active.kind)}${active.displayName}`,
        detail: runtime.length > 0 ? runtime.join(' · ') : null,
        next,
        attempt: status.attempt,
      };
    }

    const activeRun = deriveActiveTaskRun(task);
    if (activeRun?.kind === 'remote') {
      const startedAt = timestamp(activeRun.startedAt);
      const duration = startedAt === null ? null : elapsed(this.now() - startedAt);
      const host = activeRun.hostDisplayName || activeRun.runnerId || 'remote host';
      return {
        tone: 'active',
        headline: `Running remote on ${host}`,
        detail: duration ? `Active for ${duration}` : null,
        next,
        attempt: status.attempt,
      };
    }

    // AGT-3617: a card held behind an open cause breaker is skipped by the
    // review claim, so the review queue position it still carries says
    // nothing. The hold, and the card that owns it, is the CURRENT state.
    if (causeHeld) return holdView(task, causeHeld, status.attempt, next, this.now());

    const dependencyWait = dependencyWaitHeadline(task);
    if (dependencyWait) {
      return {
        tone: task.pickupHold?.unsatisfiable ? 'stalled' : 'waiting',
        headline: dependencyWait,
        detail: activityDetail(task, status.latestEventAt, this.now()),
        next,
        attempt: status.attempt,
      };
    }

    // AGT-2818: every other pickup hold (a refused dispatch,
    // a crash cooldown, a pickup-policy refusal) used to fall through to the
    // idle branch and render as "Between steps" while the card was in fact
    // being skipped on every tick. The hold block below carries the specifics.
    const hold = task.pickupHold;
    if (hold) {
      return {
        tone: hold.unsatisfiable ? 'stalled' : 'waiting',
        headline: holdHeadline(hold.mechanism),
        detail: activityDetail(task, status.latestEventAt, this.now()),
        next,
        attempt: status.attempt,
      };
    }

    // AGT-3094: a review executor that holds the lease is working on the card,
    // and a review nothing will pick up again needs an operator. Neither is a
    // wait for a slot.
    if (status.queue?.state === 'reviewing') {
      const since = timestamp(status.queue.since);
      const executor = status.queue.executorId || 'review executor';
      return {
        tone: 'active',
        headline: since === null
          ? `Reviewing on ${executor}`
          : `Reviewing on ${executor} since ${clock(since)}`,
        detail: activityDetail(task, status.latestEventAt, this.now()),
        next,
        attempt: status.attempt,
      };
    }
    if (status.queue?.state === 'stalled') {
      // The headline carries the call to action; the reason names the outcome.
      return {
        tone: 'stalled',
        headline: 'Review stalled · operator decision needed',
        detail: status.queue.reason || activityDetail(task, status.latestEventAt, this.now()),
        next,
        attempt: status.attempt,
      };
    }
    // AGT-3579: the runner recorded why it is not picking this card (mode,
    // slots, quota defer, load throttle, provider limit). The sentence is the
    // runner's own; the headline names the reason the lane header counts.
    const runnerReason = runnerWaitCode(status.queue);
    if (status.queue && runnerReason) {
      const remoteManual = runnerReason === 'manual-mode'
        && task.state === TaskState.Ready
        && !!task.executionLocation?.configuredRunnerId;
      const runnerName = task.executionLocation?.hostDisplayName
        || task.executionLocation?.configuredRunnerId
        || 'the assigned runner';
      return {
        tone: 'waiting',
        headline: remoteManual
          ? `Pickup is Manual: switch to Auto to let ${runnerName} claim it`
          : runnerWaitHeadline(runnerReason, status.queue.position),
        detail: remoteManual ? null : status.queue.reason || activityDetail(task, status.latestEventAt, this.now()),
        next,
        attempt: status.attempt,
        waitReason: runnerReason,
      };
    }

    if (status.queue) {
      const queuedHeadline = status.queue.kind === 'runner' ? 'Queued for runner'
        : status.queue.state === 'integration' ? 'Waiting for delivery integration' : 'Waiting for review slot';
      const headline = status.queue.position != null
        ? `${queuedHeadline} · position ${status.queue.position}`
        : status.queue.reason
          ? `${queuedHeadline} · ${status.queue.reason}`
          : queuedHeadline;
      return {
        tone: 'waiting',
        headline,
        detail: activityDetail(task, status.latestEventAt, this.now()),
        next,
        attempt: status.attempt,
      };
    }

    // AGT-2703: the backoff verdict is the card's own, derived from the
    // deadline in the payload against this component's clock signal, so the
    // strip leaves the backoff state on its own tick instead of on a poll.
    const activity = task.runActivity;
    const activityKind = resolveRunActivityKind(activity, this.now());
    if (activity && activityKind === 'failed-backoff') {
      const retryAt = timestamp(activity.backoffUntil);
      return {
        tone: 'waiting',
        headline: retryAt === null
          ? 'Retry backoff · waiting for runner'
          : `Retry scheduled ${new Date(retryAt).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' })}`,
        detail: activity.lastError || activityDetail(task, status.latestEventAt, this.now()),
        next,
        attempt: status.attempt,
      };
    }

    const latestAt = latestActivityAt(task, status.latestEventAt);
    const idleMs = latestAt === null ? null : this.now() - latestAt;
    const activeLane = task.state === TaskState.Progress
      || task.state === TaskState.AutoReview;
    const stalled = activeLane && idleMs !== null && idleMs >= STALE_AFTER_MS;
    // AGT-2378: `runActivity` is classified from the LOCAL slot registry plus the
    // local CLI execution record. A remote run owns the task through a fenced
    // lease and attempt records, not a local process, so it lands on
    // `no-active-run` / `failed-idle` while it is demonstrably running — and this
    // strip then claims "No active run" right next to a live "Run aktiv" pill.
    // Any positive ownership evidence therefore outranks the negative
    // classification. The activity-based "possible hang" hint is deliberately
    // left alone: it is about silence, not about ownership.
    const runActive = isTaskRunActive(task);
    const noActiveRun = activeLane && !runActive
      && (activityKind === 'failed-idle' || activityKind === 'no-active-run');

    return {
      tone: stalled || noActiveRun ? 'stalled' : runActive ? 'active' : 'idle',
      headline: stalled
        ? `No activity for ${elapsed(idleMs!)} · possible hang`
        : noActiveRun
          ? 'No active run'
          : idleHeadline(task.state),
      detail: activityDetail(task, status.latestEventAt, this.now()) ?? 'No recorded activity time',
      next,
      attempt: status.attempt,
    };
  });

  /** Opens the cause card; on a board card the click must not open this card too. */
  openCause(event: Event, causeKey: string): void {
    event.preventDefault();
    event.stopPropagation();
    this.injector.get(TaskReferenceNavigationService).openReferenceOrNotify(causeKey);
  }
}

/** The cause-breaker hold of a Post Processing card, from its durable wait marker. */
function causeHold(task: TaskInfo): CauseHold | null {
  const wait = task.causeWait;
  if (task.state !== TaskState.AutoReview || !wait?.causeKey) return null;
  return {
    causeKey: wait.causeKey,
    summary: wait.summary?.trim() || 'review keeps failing for the same cause',
  };
}

function holdView(task: TaskInfo, hold: CauseHold, attempt: number | null, next: string[], now: number): LiveStatusView {
  const since = timestamp(task.causeWait?.since);
  return {
    tone: 'waiting',
    headline: `Held behind ${hold.causeKey}: ${hold.summary}`,
    detail: `Review skips this card until ${hold.causeKey} is resolved or a re-check passes`
      + (since === null ? '' : ` · held for ${elapsed(Math.max(0, now - since))}`),
    next,
    attempt,
    hold,
  };
}

