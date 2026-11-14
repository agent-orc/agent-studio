import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom, switchMap, takeWhile, timer, last } from 'rxjs';
import type { BatchMoveJobResponse, TaskInfo } from '../../../models/task.model';
import { TaskState } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import { laneName } from '../../../models/lane-presentation';
import type {
  AcceptanceReadinessFilter,
  AcceptanceReadinessItem,
  AcceptanceReadinessReport,
  AcceptanceResult,
} from '../models/acceptance-readiness.model';
import { groupByReadiness, readinessKey, readyJobs, type ReadinessGroup } from '../models/acceptance-readiness-groups.util';

/** A round the owner is reviewing before it is sent. */
export interface AcceptanceRoundDraft {
  readonly job: TaskInfo;
  readonly item: AcceptanceReadinessItem;
}

const BATCH_POLL_MS = 250;

/**
 * AGT-3564 - acceptance readiness of the Human Review lane. Reads the
 * completion dry run, accepts ready cards through the normal batch move, and
 * sends a confirmed round through the normal continue route. It never moves
 * a card on its own and never bypasses a completion check.
 */
@Injectable({ providedIn: 'root' })
export class AcceptanceReadinessStore {
  private readonly http = inject(HttpClient);
  private readonly tasks = inject(TaskService);

  private readonly items = signal<ReadonlyMap<string, AcceptanceReadinessItem>>(new Map());
  readonly checkedAt = signal<string | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly filter = signal<AcceptanceReadinessFilter>('all');
  readonly accepting = signal(false);
  readonly results = signal<readonly AcceptanceResult[]>([]);
  readonly roundDraft = signal<AcceptanceRoundDraft | null>(null);
  readonly sendingRound = signal(false);
  readonly roundError = signal<string | null>(null);

  /** True once a check has answered; until then the lane keeps its board order. */
  readonly checked = computed(() => this.checkedAt() !== null);

  itemFor(job: Pick<TaskInfo, 'taskKey' | 'id'>): AcceptanceReadinessItem | null {
    return this.items().get(readinessKey(job)) ?? null;
  }

  /** Readiness groups of the lane's cards, or null before the first check. */
  groupsFor(jobs: readonly TaskInfo[]): ReadinessGroup[] | null {
    return this.checked() ? groupByReadiness(jobs, this.items(), this.filter()) : null;
  }

  readyAmong(jobs: readonly TaskInfo[]): TaskInfo[] {
    return readyJobs(jobs, this.items());
  }

  /** Runs the completion checks for the lane's cards without moving any of them. */
  async refresh(jobs: readonly TaskInfo[]): Promise<void> {
    const cards = jobs.filter(job => job.state === TaskState.HumanReview);
    if (cards.length === 0) {
      this.items.set(new Map());
      this.checkedAt.set(new Date().toISOString());
      return;
    }
    this.loading.set(true);
    this.error.set(null);
    try {
      const report = await firstValueFrom(this.http.post<AcceptanceReadinessReport>(
        '/api/tasks/acceptance-readiness',
        { items: cards.map(job => ({ jobId: job.id, watchPath: job.watchPath })) },
      ));
      this.items.set(new Map(report.items.map(item => [item.taskKey || item.jobId, item])));
      this.checkedAt.set(report.checkedAtUtc);
    } catch (err) {
      this.error.set(errorMessage(err, 'The acceptance checks could not run.'));
    } finally {
      this.loading.set(false);
    }
  }

  /**
   * Accepts the ready cards through the normal batch move. Every card still
   * passes the full completion check there and may be refused; the result
   * names each card's outcome. The lane is checked again afterwards.
   */
  async acceptReady(jobs: readonly TaskInfo[], lane: readonly TaskInfo[]): Promise<void> {
    const ready = this.readyAmong(jobs);
    if (ready.length === 0 || this.accepting()) return;
    this.accepting.set(true);
    this.results.set([]);
    try {
      const queued = await firstValueFrom(this.tasks.startBatchMove(ready.map(job => ({
        jobId: job.id, watchPath: job.watchPath, targetState: TaskState.Completed,
      }))));
      const done = await firstValueFrom(timer(0, BATCH_POLL_MS).pipe(
        switchMap(() => this.tasks.getBatchMove(queued.id)),
        takeWhile(job => job.status !== 'completed' && job.status !== 'failed', true),
        last(),
      ));
      this.results.set(acceptResults(ready, done));
      this.tasks.refresh(true);
    } catch (err) {
      this.results.set(ready.map(job => ({
        jobId: job.id, title: job.title, accepted: false,
        message: errorMessage(err, 'The batch accept could not start.'),
      })));
    } finally {
      this.accepting.set(false);
    }
    await this.refresh(lane.filter(job => !this.results().some(result => result.accepted && result.jobId === job.id)));
  }

  openRound(job: TaskInfo): void {
    const item = this.itemFor(job);
    if (!item?.round) return;
    this.roundError.set(null);
    this.roundDraft.set({ job, item });
  }

  closeRound(): void {
    this.roundDraft.set(null);
    this.roundError.set(null);
  }

  /** Sends the confirmed round through the normal continue route. */
  async sendRound(prompt: string, baseRef: string): Promise<boolean> {
    const draft = this.roundDraft();
    if (!draft || !prompt.trim() || this.sendingRound()) return false;
    this.sendingRound.set(true);
    this.roundError.set(null);
    try {
      await firstValueFrom(this.tasks.continueJob(
        draft.job.id, prompt.trim(), draft.job.watchPath,
        undefined, undefined, undefined, undefined, undefined, 'Close the gap that refused the acceptance of this card.', baseRef.trim() || undefined,
      ));
      this.roundDraft.set(null);
      this.tasks.refresh(true);
      return true;
    } catch (err) {
      this.roundError.set(errorMessage(err, 'The round could not be sent.'));
      return false;
    } finally {
      this.sendingRound.set(false);
    }
  }
}

function acceptResults(ready: readonly TaskInfo[], job: BatchMoveJobResponse): AcceptanceResult[] {
  return ready.map((card, index) => {
    const result = job.results.find(item => item.index === index) ?? job.results.find(item => item.jobId === card.id);
    const accepted = result?.status === 'moved';
    return {
      jobId: card.id,
      title: card.title,
      accepted,
      message: accepted ? `Moved to ${laneName(TaskState.Completed)}` : result?.message || (result ? `Not accepted (${result.status})` : job.message || 'No result was reported.'),
    };
  });
}

function errorMessage(err: unknown, fallback: string): string {
  const body = (err as { error?: { message?: string; error?: string } } | null)?.error;
  return body?.message || body?.error || (err as { message?: string } | null)?.message || fallback;
}
