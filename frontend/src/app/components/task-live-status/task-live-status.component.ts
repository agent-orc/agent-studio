import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import type { TaskInfo } from '../../models/task.model';
import { TaskState } from '../../models/task.model';
import { NowTickService } from '../../services/now-tick.service';
import { deriveActiveTaskRun, isTaskRunActive, resolveRunActivityKind } from '../../services/run-activity.util';

export type TaskLiveStatusVariant = 'card' | 'detail';
type LiveTone = 'active' | 'waiting' | 'idle' | 'stalled';

interface LiveStatusView {
  tone: LiveTone;
  headline: string;
  detail: string | null;
  next: string[];
  attempt: number;
}

const STALE_AFTER_MS = 10 * 60 * 1000;

@Component({
  selector: 'app-task-live-status',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-live-status.component.html',
  styleUrl: './task-live-status.component.scss',
})
export class TaskLiveStatusComponent {
  readonly task = input.required<TaskInfo>();
  readonly variant = input<TaskLiveStatusVariant>('card');
  readonly dense = input(false);
  private readonly now = inject(NowTickService).now;

  readonly view = computed<LiveStatusView | null>(() => {
    const task = this.task();
    const status = task.liveStatus;
    if (!status) return null;

    const next = (status.nextSteps ?? []).slice(0, 3).map(step => step.displayName);
    const active = status.activeStep;
    if (active) {
      const startedAt = timestamp(active.startedAt);
      const duration = startedAt === null ? null : elapsed(this.now() - startedAt);
      const startClock = startedAt === null
        ? null
        : new Date(startedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
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
        tone: 'waiting',
        headline: dependencyWait,
        detail: activityDetail(task, status.latestEventAt, this.now()),
        next,
        attempt: status.attempt,
      };
    }

    if (status.queue) {
      const queueName = status.queue.kind === 'review' ? 'review slot' : 'runner slot';
      return {
        tone: 'waiting',
        headline: `Waiting for ${queueName} · position ${status.queue.position}`,
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
          : `Retry scheduled ${new Date(retryAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`,
        detail: activity.lastError || activityDetail(task, status.latestEventAt, this.now()),
        next,
        attempt: status.attempt,
      };
    }

    const latestAt = latestActivityAt(task, status.latestEventAt);
    const idleMs = latestAt === null ? null : Math.max(0, this.now() - latestAt);
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
          : task.state === TaskState.Preparation ? 'Preparing' : 'Between steps',
      detail: idleMs === null ? 'No recorded activity time' : `Last activity ${elapsed(idleMs)} ago`,
      next,
      attempt: status.attempt,
    };
  });
}

/**
 * The backend waitsOn overlay is the same archive-inclusive dependency truth
 * used by claim admission. A dependency gate therefore outranks any stale or
 * concurrent runner queue projection when choosing the one CURRENT wait state.
 */
function dependencyWaitHeadline(task: TaskInfo): string | null {
  const waitsOn = task.waitsOn;
  if (!waitsOn?.blocked) return null;

  const open = waitsOn.items.filter(item => !item.fulfilled);
  const primary = open[0] ?? waitsOn.items[0];
  if (!primary) return waitsOn.cycleDetected ? 'Dependency gate blocked · cycle' : 'Dependency gate blocked';

  if (waitsOn.cycleDetected) return `Dependency gate blocked · cycle: ${primary.key}`;

  const extra = Math.max(0, open.length - 1);
  const reason = primary.waitingForRelease ? 'release' : 'completion';
  return `waits for ${reason}: ${primary.key}${extra > 0 ? ` +${extra}` : ''}`;
}

function timestamp(value: string | null | undefined): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isNaN(parsed) ? null : parsed;
}

function latestActivityAt(task: TaskInfo, projected: string | null | undefined): number | null {
  const values = [
    projected,
    task.executionLocation?.lastActivityAt,
    task.lastActivity,
  ].map(timestamp).filter((value): value is number => value !== null);
  return values.length === 0 ? null : Math.max(...values);
}

function activityDetail(task: TaskInfo, projected: string | null | undefined, now: number): string | null {
  const latest = latestActivityAt(task, projected);
  return latest === null ? null : `Last activity ${elapsed(Math.max(0, now - latest))} ago`;
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
