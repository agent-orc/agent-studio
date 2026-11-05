import { Directive, OnDestroy } from '@angular/core';
import { Observable, Subscription } from 'rxjs';
import { TaskState, type TaskInfo } from '../../../models/task.model';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';

/**
 * Cycle 9k base class for "poll one endpoint at a fixed cadence for the
 * currently-open job" services. Lifts the shared timer + job-key
 * change-detection + visibility-aware ticking out of the four similar
 * poll services that used to copy this loop. Subclasses only specify
 * what to fetch, what to do with the response, how to clear, and the
 * cadence.
 *
 * NOT a fit for `CliOutputPollService` — that one has two buffers
 * (polled + optimistic), buffer caps, dedup against echoed user lines,
 * elapsed-time ticker, and starts/stops on the runner's execution
 * status, not just on job change. It deliberately stays standalone.
 *
 * Marked `@Directive()` (not `@Injectable()`) so Angular's DI accepts
 * the abstract class as a base for `@Injectable()` subclasses without
 * synthesising a constructor for the abstract one.
 */
@Directive()
export abstract class TaskBackgroundPoller<TResponse> implements OnDestroy {
  protected abstract readonly intervalMs: number;

  /** Subclass calls the relevant TaskService method. */
  protected abstract fetch(jobId: string, watchPath: string): Observable<TResponse>;

  /** Subclass updates whatever signals it owns from the fresh response. */
  protected abstract applyResponse(res: TResponse): void;

  /** Subclass clears its signals back to the "no job selected" state. */
  protected abstract clearValue(): void;

  /** Surface a failed endpoint in pollers whose visible tab needs a retry. */
  protected onError(): void { /* Existing pollers keep their last value on failure. */ }

  /**
   * Override to filter which jobs trigger polling. Default: any
   * non-null job. Used by `ClaudeSessionPollService` to skip
   * non-claude jobs (so the loop doesn't burn requests for jobs that
   * have nothing to report).
   */
  protected shouldPoll(info: TaskInfo): boolean {
    void info;
    return true;
  }

  /**
   * Inactive task data is immutable until a push event delivers a new task
   * snapshot, so one fetch on selection is sufficient. Keep recurring HTTP
   * polling only while a task is in an orchestrator-owned processing state or
   * explicitly reports a running execution.
   */
  protected shouldRepeat(info: TaskInfo): boolean {
    return info.execution?.status === 'running'
      || info.state === TaskState.Preparation
      || info.state === TaskState.OrchestratorPrep
      || info.state === TaskState.Progress
      || info.state === TaskState.AutoReview;
  }

  private timer: VisibleIntervalHandle | null = null;
  private currentJob: { id: string; watchPath: string } | null = null;
  private currentKey = '';
  private preparedTaskKey: string | null = null;
  private activeRequest: Subscription | null = null;
  private requestInFlight = false;
  private requestGeneration = 0;

  /** Clear a previous task without starting a hidden tab's first request. */
  prepareTask(key: string | null): void {
    if (key === this.preparedTaskKey) return;
    this.preparedTaskKey = key;
    this.stop();
    this.clearValue();
  }

  /**
   * Sync polling state to a job. Pass `null` (or a job that fails
   * `shouldPoll`) to stop and clear. Re-arms the timer when the
   * effective key changes; a no-op when the same job is passed again.
   */
  syncTo(info: TaskInfo | null | undefined): void {
    const willPoll = info != null && this.shouldPoll(info);
    const willRepeat = willPoll && this.shouldRepeat(info!);
    const key = willPoll ? `${info!.watchPath}::${info!.id}::${willRepeat ? 'live' : 'snapshot'}` : '';
    if (key === this.currentKey) return;
    this.currentKey = key;
    this.stopTransport();
    if (!willPoll) {
      this.currentJob = null;
      this.clearValue();
      return;
    }
    this.currentJob = { id: info!.id, watchPath: info!.watchPath };
    this.refresh();
    if (willRepeat) {
      this.timer = setVisibleInterval(() => this.refresh(), this.intervalMs);
    }
  }

  /**
   * Force a fetch outside the timer (e.g. immediately after a user
   * action that should reflect in the data on the next render). Safe
   * to call when no job is set; clears the value.
   */
  refresh(): void {
    const job = this.currentJob;
    if (!job) {
      this.clearValue();
      return;
    }
    if (this.requestInFlight) return;

    const generation = ++this.requestGeneration;
    this.requestInFlight = true;
    try {
      const request = this.fetch(job.id, job.watchPath).subscribe({
        next: (res) => {
          if (generation === this.requestGeneration) this.applyResponse(res);
        },
        error: () => {
          if (generation === this.requestGeneration) this.onError();
          this.finishRequest(generation);
        },
        complete: () => this.finishRequest(generation),
      });
      if (!request.closed && generation === this.requestGeneration) {
        this.activeRequest = request;
      }
    } catch {
      this.finishRequest(generation);
    }
  }

  protected isCurrentJob(jobId: string): boolean {
    return this.currentJob?.id === jobId;
  }

  stop(): void {
    this.currentKey = '';
    this.currentJob = null;
    this.stopTransport();
  }

  private finishRequest(generation: number): void {
    if (generation !== this.requestGeneration) return;
    this.requestInFlight = false;
    this.activeRequest = null;
  }

  private stopTransport(): void {
    if (this.timer) {
      clearVisibleInterval(this.timer);
      this.timer = null;
    }
    this.requestGeneration++;
    this.activeRequest?.unsubscribe();
    this.activeRequest = null;
    this.requestInFlight = false;
  }

  ngOnDestroy(): void {
    this.stop();
  }
}
