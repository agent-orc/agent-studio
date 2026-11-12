import { Injectable, OnDestroy, computed, signal, inject } from '@angular/core';
import { CliExecution, CliOutputLine } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import { TaskSectionAccessService } from '../../../services/task-section-access.service';

interface TaskRef {
  id: string;
  watchPath: string;
}

/**
 * Owns the live-CLI side of the job-detail view: the rolling output
 * buffer, the elapsed-time ticker, and the 2 s output-poll loop. The
 * component supplies the current job (via setJob) and the methods
 * below; the service drives all timers + signals from there.
 *
 * Provided locally on TaskDetailComponent so each detail instance has
 * its own state and timers.
 */
/**
 * Cycle 4 bounded buffer: the polled output is capped at the last
 * MAX_RENDERED_LINES entries before we stash it into the signal. The full
 * log stays on disk (cli-output.log); the browser holds only what the
 * activity-log can reasonably render. A long-running job that emits 50k
 * lines used to balloon the JS heap with every poll because we set the
 * full buffer back into a signal that change detection traversed on every
 * tick. With the cap, the heap stays bounded and Angular's render cost
 * stops growing with run duration. The boundary is generous (2000 entries
 * is roughly 200 KB of plain text - still scrollable in the activity log)
 * but tight enough that an hours-long run cannot OOM the tab.
 */
const MAX_RENDERED_LINES = 2000;

function capLines(lines: CliOutputLine[]): CliOutputLine[] {
  if (lines.length <= MAX_RENDERED_LINES) return lines;
  // Drop the oldest entries; the activity log shows newest at the bottom,
  // so the cap takes the tail. The user can still scroll the log up to
  // the trim boundary; the bounded durable tail lives on disk in
  // cli-output.log, with one preceding segment in cli-output.log.1.
  return lines.slice(-MAX_RENDERED_LINES);
}

@Injectable()
export class CliOutputPollService implements OnDestroy {
  private jobService = inject(TaskService);
  private readonly sectionAccess = inject(TaskSectionAccessService);

  /** A Product session is refused the CLI output; it never polls it. */
  canReadOutput(): boolean {
    return this.sectionAccess.canRead('output');
  }

  // The polled buffer is what GET /api/tasks/{id}/output returns — it's the
  // authoritative server state. We keep it separate from the optimistic
  // echo buffer so a poll round-trip never wipes the user's just-typed
  // follow-up off the screen.
  private readonly polledOutput = signal<CliOutputLine[]>([]);
  private readonly pendingUserLines = signal<CliOutputLine[]>([]);

  readonly output = computed<CliOutputLine[]>(() => {
    const polled = this.polledOutput();
    const pending = this.pendingUserLines();
    if (pending.length === 0) return polled;
    // Drop pending lines whose text already appears as a [user]-stream entry
    // in the polled output — that means the backend has persisted our optimistic
    // line and we'd otherwise show a duplicate.
    const persisted = new Set(polled.filter((l) => l.stream === 'user').map((l) => l.text));
    const stillPending = pending.filter((l) => !persisted.has(l.text));
    if (stillPending.length === 0) return polled;
    return [...polled, ...stillPending];
  });

  readonly isRunning = signal(false);
  readonly startedAt = signal<Date | null>(null);
  readonly elapsedTime = signal('');

  private elapsedTimer: ReturnType<typeof setInterval> | null = null;
  private pollGeneration = 0;
  private pollTimeout: ReturnType<typeof setTimeout> | null = null;
  private currentJob: TaskRef | null = null;

  setJob(job: TaskRef | null): void {
    this.currentJob = job;
  }

  /** Reset all per-job state — call when switching to a different job. */
  resetForJobSwitch(): void {
    this.polledOutput.set([]);
    this.pendingUserLines.set([]);
    this.isRunning.set(false);
    this.startedAt.set(null);
    this.elapsedTime.set('0s');
    this.pollGeneration += 1;
    this.clearPollTimer();
    this.clearElapsedTimer();
  }

  /** Mirror an incoming CliExecution snapshot into running/started state. */
  applyExecution(execution: CliExecution | null): void {
    if (!execution) return;
    if (execution.status === 'running') {
      this.isRunning.set(true);
      this.startedAt.set(new Date(execution.startedAt));
      if (!this.elapsedTimer) this.startElapsedTimer();
      return;
    }
    this.isRunning.set(false);
    this.clearElapsedTimer();
  }

  /** Begin a fresh run with a known startedAt — clears the buffer first. */
  beginRun(startedAt: Date): void {
    this.isRunning.set(true);
    this.startedAt.set(startedAt);
    this.polledOutput.set([]);
    this.pendingUserLines.set([]);
    this.clearPollTimer();
    this.startElapsedTimer();
    this.startPolling();
  }

  /** Resume an existing session — keeps the buffer intact so the log accumulates. */
  beginContinuation(startedAt: Date): void {
    this.isRunning.set(true);
    this.startedAt.set(startedAt);
    this.clearPollTimer();
    this.startElapsedTimer();
    this.startPolling();
  }

  /**
   * Optimistically append a user-typed follow-up to the activity log so it
   * shows up the instant the user clicks Send — without waiting 1-2 s for the
   * next poll. The line lives in a separate "pending" buffer that the polled
   * output never wipes; once the backend persists the same prompt to
   * cli-output.log (TaskRunnerService.AppendUserPromptToCliLog) the polled
   * output contains a matching [user]-stream line and the dedupe in
   * <c>output()</c> drops the pending copy. Net effect: the user sees their
   * message instantly and exactly once.
   */
  appendOptimisticUserMessage(text: string): void {
    const trimmed = (text ?? '').trim();
    if (!trimmed) return;
    const now = new Date().toISOString();
    const oneLine = trimmed.replace(/[\r\n]+/g, ' ');
    this.pendingUserLines.update((lines) => [
      ...lines,
      { timestamp: now, stream: 'user', text: oneLine },
    ]);
  }

  /** Halt timers and polling without resetting buffers — used by Stop. */
  stop(): void {
    this.isRunning.set(false);
    this.clearPollTimer();
    this.clearElapsedTimer();
  }

  /**
   * Start polling the output endpoint if not already running. Public
   * because the detail-effect calls it whenever the loaded job is
   * already in `running` state.
   */
  startPolling(): void {
    if (this.pollTimeout || !this.canReadOutput()) return;
    const generation = this.pollGeneration;
    const poll = () => {
      const job = this.currentJob;
      if (!this.isRunning() || generation !== this.pollGeneration || !job) return;
      // Cycle 3: skip the fetch (but keep the timer) when the tab is
      // backgrounded - re-arm so we resume cleanly when the user comes
      // back, without burning a request every 2 s for a buffer nobody
      // is reading right now.
      if (typeof document !== 'undefined' && document.hidden) {
        this.pollTimeout = setTimeout(poll, 2000);
        return;
      }
      this.jobService.getJobOutput(job.id, job.watchPath).subscribe({
        next: (output) => {
          if (generation !== this.pollGeneration) return;
          this.polledOutput.set(capLines(output));
          this.pollTimeout = setTimeout(poll, 2000);
        },
        error: () => {
          this.pollTimeout = setTimeout(poll, 5000);
        },
      });
    };
    this.pollTimeout = setTimeout(poll, 1000);
  }

  /** Whether the output buffer is currently being polled. */
  isPolling(): boolean {
    return this.pollTimeout !== null;
  }

  /** Hydrate the buffer from a prior run's logs (called by detail-effect). */
  hydrateOutput(output: CliOutputLine[], execStartedAt?: string | null): void {
    if (output.length === 0) return;
    this.polledOutput.set(capLines(output));
    if (!this.startedAt() && execStartedAt) {
      this.startedAt.set(new Date(execStartedAt));
    }
    if (!this.elapsedTimer && this.isRunning()) {
      this.startElapsedTimer();
    }
  }

  private startElapsedTimer(): void {
    this.clearElapsedTimer();
    this.updateElapsed();
    this.elapsedTimer = setInterval(() => this.updateElapsed(), 1000);
  }

  private updateElapsed(): void {
    const start = this.startedAt();
    if (!start) {
      this.elapsedTime.set('0s');
      return;
    }
    const secs = Math.floor((Date.now() - start.getTime()) / 1000);
    if (secs < 60) this.elapsedTime.set(`${secs}s`);
    else if (secs < 3600) this.elapsedTime.set(`${Math.floor(secs / 60)}m ${secs % 60}s`);
    else this.elapsedTime.set(`${Math.floor(secs / 3600)}h ${Math.floor((secs % 3600) / 60)}m`);
  }

  private clearPollTimer(): void {
    if (this.pollTimeout) {
      clearTimeout(this.pollTimeout);
      this.pollTimeout = null;
    }
  }

  private clearElapsedTimer(): void {
    if (this.elapsedTimer) {
      clearInterval(this.elapsedTimer);
      this.elapsedTimer = null;
    }
  }

  ngOnDestroy(): void {
    this.isRunning.set(false);
    this.clearPollTimer();
    this.clearElapsedTimer();
  }
}
