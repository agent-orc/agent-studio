import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import {
  deriveCompletionLoop,
  type CompletionLoopState,
  type TaskTimelineEvent,
} from '../../../features/task-timeline';
import { TaskService } from '../../../services/task.service';
import { TaskBackgroundPoller } from './task-background-poller';
import { stripAnsi } from '../../../utils/ansi-text';

/**
 * Polls the per-task event ledger (`/api/tasks/{id}/timeline`, ADR-0049 /
 * ASS-566) while a job is open. The ledger is the union of lifecycle
 * events - prompt creation, agent runs, pipeline steps, and the
 * orchestrator's completion-loop verdicts (accept / reopen / escalate).
 *
 * 10 s cadence: the timeline changes at the granularity of a run
 * finishing or the orchestrator emitting a verdict, not per-frame, so a
 * slower poll than the 5 s live-CLI services is plenty. Matches the
 * orchestrator-log cadence.
 */
@Injectable()
export class TaskTimelinePollService extends TaskBackgroundPoller<TaskTimelineEvent[]> {
  private readonly jobService = inject(TaskService);

  protected readonly intervalMs = 10_000;

  /** Raw ledger rows in chronological (append) order. */
  readonly events = signal<TaskTimelineEvent[]>([]);
  readonly loadState = signal<'idle' | 'ready' | 'error'>('idle');


  /**
   * Derived "where is the completion loop right now" summary the
   * Overview attempt-cycle indicator binds to. Recomputed whenever the
   * ledger changes.
   */
  readonly completionLoop = computed<CompletionLoopState>(() =>
    deriveCompletionLoop(this.events()),
  );

  protected fetch(jobId: string, watchPath: string): Observable<TaskTimelineEvent[]> {
    return this.jobService.getTaskTimeline(jobId, watchPath);
  }

  protected applyResponse(res: TaskTimelineEvent[]): void {
    this.events.set(sanitizeTimelineEvents(res));
    this.loadState.set('ready');
  }

  protected override onError(): void {
    this.loadState.set('error');
  }

  protected clearValue(): void {
    this.events.set([]);
    this.loadState.set('idle');
  }
}

/** Plain-text timeline surfaces must never expose terminal control codes. */
export function sanitizeTimelineEvents(
  events: readonly TaskTimelineEvent[] | null | undefined,
): TaskTimelineEvent[] {
  return (events ?? []).map((event) => ({
    ...event,
    summary: stripAnsi(event.summary),
    details: event.details
      ? Object.fromEntries(Object.entries(event.details).map(([key, value]) => [key, stripAnsi(value)]))
      : event.details,
  }));
}
