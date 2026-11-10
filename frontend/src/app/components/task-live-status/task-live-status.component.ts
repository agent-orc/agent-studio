import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import type { TaskInfo } from '../../models/task.model';
import { TaskState } from '../../models/task.model';
import { NowTickService } from '../../services/now-tick.service';
import { deriveActiveTaskRun, isTaskRunActive, resolveRunActivityKind } from '../../services/run-activity.util';
import { PickupHoldComponent } from '../pickup-hold/pickup-hold.component';
import { runnerWaitCode, runnerWaitHeadline } from '../../models/ready-wait-reason';
import { TaskService } from '../../services/task.service';
import { ErrorDialogService } from '../../services/error-dialog.service';
import { BatchGateService } from '../../services/batch-gate.service';

export type TaskLiveStatusVariant = 'card' | 'detail';
type LiveTone = 'active' | 'waiting' | 'idle' | 'stalled';

interface LiveStatusView {
  tone: LiveTone;
  headline: string;
  detail: string | null;
  next: string[];
  attempt: number;
  perTaskGate?: boolean;
  /** AGT-3579: runner reason code when the runner is not picking this card. */
  waitReason?: string;
}

const STALE_AFTER_MS = 10 * 60 * 1000;
const FUTURE_SKEW_TOLERANCE_MS = 60 * 1000;

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
  private readonly tasks = inject(TaskService);
  private readonly errors = inject(ErrorDialogService);
  private readonly batchGate = inject(BatchGateService);
  readonly switchingToAuto = signal(false);

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
    if (!status) return null;

    const next = (status.nextSteps ?? []).slice(0, 3).map(step => step.displayName);
    const perTask = this.batchGate.state(task.projectName)()?.perTaskGates?.find(gate =>
      gate.taskKey === task.taskKey || gate.taskKey === task.key || gate.taskKey === task.id);
    if (perTask && task.state === TaskState.AutoReview) {
      const started = timestamp(perTask.startedAtUtc);
      return {
        tone: perTask.running ? 'active' : 'waiting',
        headline: perTask.running
          ? `Per-task gate running on ${perTask.host || 'gate host'}${started === null ? '' : ` since ${clock(started)}`}`
          : `Per-task gate queued (${perTask.position} of ${perTask.total}) after ${perTask.reason}`,
        detail: perTask.running ? 'Red escalates; green integrates.' : 'One full suite per member. Red escalates; green integrates.',
        next: [],
        attempt: status.attempt,
        perTaskGate: true,
      };
    }
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
      const queueName = status.queue.kind === 'review' ? 'review slot' : 'runner slot';
      const queuedHeadline = status.queue.kind === 'runner' ? 'Queued for runner' : `Waiting for ${queueName}`;
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
}

/**
 * The backend waitsOn overlay is the same archive-inclusive dependency truth
 * used by claim admission. A dependency gate therefore outranks any stale or
 * concurrent runner queue projection when choosing the one CURRENT wait state.
 *
 * AGT-2818: an unsatisfiable gate gets its own sentence. "Waits for release" and
 * "this gate can never open" describe different situations and only one of them
 * ends by waiting; a card that conflates them reads as queued for a month.
 */
function dependencyWaitHeadline(task: TaskInfo): string | null {
  const waitsOn = task.waitsOn;
  if (!waitsOn?.blocked) return null;

  const open = waitsOn.items.filter(item => !item.fulfilled);
  const primary = open[0] ?? waitsOn.items[0];
  if (!primary) return waitsOn.cycleDetected ? 'Dependency gate blocked · cycle' : 'Dependency gate blocked';

  if (waitsOn.cycleDetected) return `Dependency gate blocked · cycle: ${primary.key}`;

  const blocked = open.find(item => item.unsatisfiable);
  if (blocked) return `this gate can never open: ${blocked.key}`;

  // Slim board cards carry only the first open items; openCount is the total.
  const extra = Math.max(0, (waitsOn.openCount ?? open.length) - 1);
  const reason = primary.waitingForRelease ? 'release' : 'completion';
  return `waits for ${reason}: ${primary.key}${extra > 0 ? ` +${extra}` : ''}`;
}

/** Short name for the non-dependency holds; the hold block states the detail. */
function holdHeadline(mechanism: string): string {
  switch (mechanism) {
    case 'dispatch-rejection': return 'Queued, but the runner refused it';
    case 'crash-backoff': return 'Crash cooldown before the next attempt';
    default: return 'Queued, but not pickable';
  }
}

/**
 * AGT-3589: an idle card says what it waits for. "Between steps" named no
 * wait at all and read as work in flight on a Post Processing card nothing
 * was reviewing.
 */
function idleHeadline(state: string): string {
  switch (state) {
    case TaskState.Preparation: return 'Preparing';
    case TaskState.Ready: return 'Waiting for a runner';
    case TaskState.AutoReview: return 'Waiting for review';
    default: return 'Waiting for the next step';
  }
}

function timestamp(value: string | null | undefined): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? null : parsed;
}

function clock(epochMs: number): string {
  return new Date(epochMs).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
}

function latestActivityAt(task: TaskInfo, projected: string | null | undefined): number | null {
  const values = [
    projected,
    task.executionLocation?.lastActivityAt,
    // Board cards carry the heartbeat, not the derived activity stamp.
    task.executionLocation?.lastHeartbeat,
    task.lastActivity,
  ].map(timestamp).filter((value): value is number => value !== null);
  return values.length === 0 ? null : Math.max(...values);
}

/**
 * AGT-3589: a stamp ahead of this browser's clock (server clock skew, a
 * pinned demo clock) used to clamp to "0s ago", which claims activity that
 * did not happen. Past the tolerance the real stamp is shown instead.
 */
function activityDetail(task: TaskInfo, projected: string | null | undefined, now: number): string | null {
  const latest = latestActivityAt(task, projected);
  if (latest === null) return null;
  if (latest - now > FUTURE_SKEW_TOLERANCE_MS) return `Last activity ${dateTime(latest)}`;
  return `Last activity ${elapsed(Math.max(0, now - latest))} ago`;
}

function dateTime(epochMs: number): string {
  return new Date(epochMs).toLocaleString('en-US', {
    day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit',
  });
}

function elapsed(milliseconds: number): string {
  const totalSeconds = Math.max(0, Math.floor(milliseconds / 1000));
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) return `${minutes}m${seconds.toString().padStart(2, '0')}s`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h${(minutes % 60).toString().padStart(2, '0')}m`;
}

function cliLabel(cli: string): string {
  const value = cli.trim().toLowerCase();
  return value === 'codex' ? 'Codex'
    : value === 'claude' ? 'Claude'
      : value === 'gemini' ? 'Gemini'
        : value === 'copilot' ? 'Copilot'
          : cli;
}

function stepPrefix(kind: string): string {
  return kind.toLowerCase() === 'aspect' ? 'Review aspect · ' : '';
}
