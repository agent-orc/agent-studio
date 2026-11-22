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

/**
 * Where the round was opened. Each host renders only its own drafts, so the
 * board lane, the card detail and the task toolbar never stack two dialogs.
 */
export type AcceptanceRoundOrigin = 'board' | 'detail' | 'toolbar';

/** A round the owner is reviewing before it is sent. */
export interface AcceptanceRoundDraft {
  readonly job: TaskInfo;
  readonly item: AcceptanceReadinessItem;
  readonly origin: AcceptanceRoundOrigin;
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
  private readonly checking = new Set<string>();
  /**
   * F2: a client sequence for ordering a recorded Accept refusal against
   * later checks and owner steps. `answeredAt` holds, per card, the sequence
   * of the request its current answer came from; `addressedAt` the sequence
   * of the last round or content review the owner requested for it.
   */
  private sequenceNo = 0;
  private readonly answeredAt = signal<ReadonlyMap<string, number>>(new Map());
  private readonly addressedAt = signal<ReadonlyMap<string, number>>(new Map());
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

  /** The open round draft of `origin`, or null when another host opened it. */
  draftFor(origin: AcceptanceRoundOrigin): AcceptanceRoundDraft | null {
    const draft = this.roundDraft();
    return draft?.origin === origin ? draft : null;
  }

  readyAmong(jobs: readonly TaskInfo[]): TaskInfo[] {
    return readyJobs(jobs, this.items());
  }

  /** The current sequence; a refusal recorded now is older than every later check or step. */
  sequence(): number {
    return this.sequenceNo;
  }

  /**
   * F2: true when something newer than `since` replaces a refusal of `job`:
   * the owner sent a round or requested a content review, or a check asked
   * after it answers `ready`.
   */
  supersedes(job: Pick<TaskInfo, 'taskKey' | 'id'>, since: number): boolean {
    const key = readinessKey(job);
    if ((this.addressedAt().get(key) ?? -1) > since) return true;
    return (this.answeredAt().get(key) ?? -1) > since && this.items().get(key)?.readiness === 'ready';
  }

  /** F2: the owner requested the step that closes the gap of `job` (a round or a content review). */
  markAddressed(job: Pick<TaskInfo, 'taskKey' | 'id'>): void {
    const at = ++this.sequenceNo;
    this.addressedAt.update(current => new Map(current).set(readinessKey(job), at));
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
    const asked = ++this.sequenceNo;
    try {
      const report = await firstValueFrom(this.http.post<AcceptanceReadinessReport>(
        '/api/tasks/acceptance-readiness',
        { items: cards.map(job => ({ jobId: job.id, watchPath: job.watchPath })) },
      ));
      this.items.set(new Map(report.items.map(item => [item.taskKey || item.jobId, item])));
      this.answeredAt.update(current => {
        const next = new Map(current);
        for (const item of report.items) next.set(item.taskKey || item.jobId, asked);
        return next;
      });
      this.checkedAt.set(report.checkedAtUtc);
    } catch (err) {
      this.error.set(errorMessage(err, 'The acceptance checks could not run.'));
    } finally {
      this.loading.set(false);
    }
  }

  /** Card keys and activity stamps {@link ensure} already asked for. */
  private readonly asked = new Map<string, string>();

  /**
   * F12: the open card's readiness when no lane check covered it yet, so its
   * toolbar and acceptance grounds rank their gaps by the same answer as the
   * board note. Asked once per card activity, also after a failure; a lane
   * check answers as well.
   */
  async ensure(job: TaskInfo): Promise<void> {
    if (job.state !== TaskState.HumanReview) return;
    const key = readinessKey(job);
    const stamp = job.lastActivity ?? '';
    if (this.items().has(key) || this.asked.get(key) === stamp) return;
    this.asked.set(key, stamp);
    await this.checkCard(job);
  }

  /**
   * AGT-3867: checks one Human Review card, for its detail and toolbar, and
   * merges the answer without touching the lane's grouping state. A failed
   * check keeps the last answer: the detail then offers no speculative step.
   * AGT-3830: a card opened without the board (a phone, a deep link, Needs
   * you) reads the same readiness the lane shows. Concurrent checks of a
   * card coalesce.
   */
  async checkCard(job: Pick<TaskInfo, 'id' | 'taskKey' | 'watchPath' | 'state'>): Promise<void> {
    const key = readinessKey(job);
    if (job.state !== TaskState.HumanReview || this.checking.has(key)) return;
    this.checking.add(key);
    const asked = ++this.sequenceNo;
    try {
      const report = await firstValueFrom(this.http.post<AcceptanceReadinessReport>(
        '/api/tasks/acceptance-readiness',
        { items: [{ jobId: job.id, watchPath: job.watchPath }] },
      ));
      const item = Array.isArray(report?.items)
        ? report.items.find(entry => entry.taskKey === job.taskKey || entry.jobId === job.id)
        : undefined;
      this.items.update(current => {
        const next = new Map(current);
        if (item) next.set(key, item);
        else next.delete(key);
        return next;
      });
      if (item) this.answeredAt.update(current => new Map(current).set(key, asked));
    } catch {
      // The board check and the server refusal still name the reason.
    } finally {
      this.checking.delete(key);
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

  openRound(job: TaskInfo, origin: AcceptanceRoundOrigin = 'board'): void {
    const item = this.itemFor(job);
    if (!item?.round) return;
    this.roundError.set(null);
    this.roundDraft.set({ job, item, origin });
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
      this.markAddressed(draft.job);
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
