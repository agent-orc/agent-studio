import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import type { SessionEventsResponse } from '../../../features/session-events';
import { TaskService } from '../../../services/task.service';
import { TaskSectionAccessService } from '../../../services/task-section-access.service';
import type { TaskInfo } from '../../../models/task.model';
import { TaskBackgroundPoller } from './task-background-poller';

/**
 * Polls the per-job session-event log every 10 s. Drives the
 * "session continued / lost" chip in the protocol pane header so the
 * user can tell at a glance whether a follow-up actually rode the
 * existing CLI session or had to reconstruct from files.
 *
 * Slower cadence than ClaudeSessionPollService (5 s) on purpose —
 * session events only flip on start/continue/recovery, not per turn.
 */
@Injectable()
export class SessionEventsPollService extends TaskBackgroundPoller<SessionEventsResponse | null> {
  private readonly jobService = inject(TaskService);
  private readonly sectionAccess = inject(TaskSectionAccessService);

  protected readonly intervalMs = 10_000;

  readonly response = signal<SessionEventsResponse | null>(null);

  /** The most recent event in the log, or null when there is none. */
  readonly latest = computed(() => {
    const r = this.response();
    if (!r || r.events.length === 0) return null;
    return r.events[r.events.length - 1];
  });

  /**
   * Number of segments in the chain — i.e. one more than the number of
   * `(recovery)` markers. A value of 1 means "uninterrupted lineage";
   * higher values mean the user has had to reconstruct at least once.
   */
  readonly chainSegmentCount = computed(() => {
    const r = this.response();
    if (!r || r.sessionChain.length === 0) return 0;
    return r.sessionChain.filter((s) => s === '(recovery)').length + 1;
  });

  /** Number of real (non-recovery-marker) session ids on record. */
  readonly chainLength = computed(() => {
    const r = this.response();
    if (!r) return 0;
    return r.sessionChain.filter((s) => s && s !== '(recovery)').length;
  });

  /** A Product session is refused the session-event log; never request it. */
  protected override shouldPoll(info: TaskInfo): boolean {
    void info;
    return this.sectionAccess.canRead('session-events');
  }

  protected fetch(jobId: string, watchPath: string): Observable<SessionEventsResponse | null> {
    return this.jobService.getSessionEvents(jobId, watchPath);
  }

  protected applyResponse(res: SessionEventsResponse | null): void {
    this.response.set(res ?? null);
  }

  protected clearValue(): void {
    this.response.set(null);
  }
}
