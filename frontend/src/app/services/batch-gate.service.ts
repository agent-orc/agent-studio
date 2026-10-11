import { DestroyRef, Injectable, inject, signal, type Signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom, map, type Observable } from 'rxjs';
import type { BatchGateConfiguration, BatchGateSettings, BatchGateState } from '../models/batch-gate.model';

interface ProjectFeed {
  state: ReturnType<typeof signal<BatchGateState | null>>;
  error: ReturnType<typeof signal<string | null>>;
  unavailable: ReturnType<typeof signal<boolean>>;
  consumers: number;
  timer?: ReturnType<typeof setInterval>;
  inFlight: boolean;
}

@Injectable({ providedIn: 'root' })
export class BatchGateService {
  private readonly http = inject(HttpClient);
  private readonly feeds = new Map<string, ProjectFeed>();
  private readonly visibilityChanged = () => {
    for (const [project, feed] of this.feeds) this.schedule(project, feed);
  };

  constructor() {
    document.addEventListener('visibilitychange', this.visibilityChanged);
    inject(DestroyRef).onDestroy(() => document.removeEventListener('visibilitychange', this.visibilityChanged));
  }

  private url(project: string): string {
    return `/api/projects/${encodeURIComponent(project)}/batch-gate`;
  }

  private feed(project: string): ProjectFeed {
    let feed = this.feeds.get(project);
    if (!feed) {
      feed = { state: signal<BatchGateState | null>(null), error: signal<string | null>(null), unavailable: signal(false), consumers: 0, inFlight: false };
      this.feeds.set(project, feed);
    }
    return feed;
  }

  state(project: string): Signal<BatchGateState | null> { return this.feed(project).state.asReadonly(); }
  error(project: string): Signal<string | null> { return this.feed(project).error.asReadonly(); }
  unavailable(project: string): Signal<boolean> { return this.feed(project).unavailable.asReadonly(); }

  /** One request and timer per visible project, regardless of consumer count. */
  watch(project: string): () => void {
    const feed = this.feed(project);
    feed.consumers++;
    this.schedule(project, feed);
    return () => {
      feed.consumers = Math.max(0, feed.consumers - 1);
      this.schedule(project, feed);
    };
  }

  private schedule(project: string, feed: ProjectFeed, refreshNow = true): void {
    if (feed.consumers === 0 || document.hidden) {
      if (feed.timer) clearInterval(feed.timer);
      feed.timer = undefined;
      return;
    }
    if (feed.timer) return;
    if (feed.unavailable()) {
      feed.timer = setTimeout(() => {
        feed.timer = undefined;
        void this.refresh(project);
      }, 300_000);
      return;
    }
    if (refreshNow) void this.refresh(project);
    feed.timer = setInterval(() => void this.refresh(project), 15_000);
  }

  async refresh(project: string): Promise<void> {
    const feed = this.feed(project);
    if (feed.inFlight) return;
    feed.inFlight = true;
    try {
      const payload: unknown = await firstValueFrom(this.http.get<unknown>(`${this.url(project)}/state?history=10`));
      if (!isBatchGateState(payload)) {
        this.markUnavailable(feed);
        return;
      }
      if (feed.unavailable() && feed.timer) {
        clearInterval(feed.timer);
        feed.timer = undefined;
      }
      feed.state.set(payload);
      feed.unavailable.set(false);
      feed.error.set(null);
    } catch (error) {
      if (error instanceof HttpErrorResponse && [404, 405].includes(error.status)) {
        this.markUnavailable(feed);
      } else {
        feed.error.set('Batch verification is temporarily unavailable.');
      }
    } finally {
      feed.inFlight = false;
      this.schedule(project, feed, false);
    }
  }

  private markUnavailable(feed: ProjectFeed): void {
    if (feed.timer) clearInterval(feed.timer);
    feed.timer = undefined;
    feed.state.set(null);
    feed.unavailable.set(true);
    feed.error.set(null);
  }

  getSettings(project: string): Observable<BatchGateConfiguration> {
    return this.http.get<unknown>(this.url(project)).pipe(map(configuration));
  }
  putSettings(project: string, settings: BatchGateSettings): Observable<BatchGateConfiguration> {
    return this.http.put<unknown>(this.url(project), settings).pipe(map(configuration));
  }
  closeNow(project: string): Observable<unknown> {
    return this.http.post(this.url(project) + '/close-now', {});
  }
  eject(project: string, taskKey: string): Observable<unknown> {
    return this.http.post(`${this.url(project)}/members/${encodeURIComponent(taskKey)}/eject`, {});
  }
}

const record = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);
const optionalString = (value: unknown): boolean => value == null || typeof value === 'string';
const strings = (value: unknown): value is string[] => Array.isArray(value) && value.every(item => typeof item === 'string');

function configuration(value: unknown): BatchGateConfiguration {
  if (!record(value) || !record(value['settings']) || !record(value['limits'])) throw new Error('Invalid batch settings response');
  const settings = value['settings'];
  const advisor = settings['advisor'];
  if (typeof settings['enabled'] !== 'boolean' ||
      !['documentation', 'code'].includes(String(settings['scope'])) ||
      !['threshold', 'look-ahead'].includes(String(settings['planner'])) ||
      !['minimumSize', 'maximumSize', 'deadlineMinutes', 'lookAheadMinutes', 'gateTimeoutMinutes']
        .every(field => typeof settings[field] === 'number') ||
      !strings(settings['urgentTags']) ||
      !['conservative', 'balanced', 'aggressive'].includes(String(settings['riskPolicy'])) ||
      !['adjacent', 'separate'].includes(String(settings['overlap'])) ||
      !['work-package', 'build-only'].includes(String(settings['reviewTestLevel'])) ||
      !record(advisor) || !['off', 'advise', 'enforce'].includes(String(advisor['mode'])) ||
      typeof advisor['cliType'] !== 'string' || !optionalString(advisor['model']) ||
      typeof advisor['timeoutSeconds'] !== 'number' ||
      typeof value['limits']['hardMaximumSize'] !== 'number') throw new Error('Invalid batch settings response');
  return value as unknown as BatchGateConfiguration;
}

/** Reject legacy catch-all responses before any template reads nested state. */
export function isBatchGateState(value: unknown): value is BatchGateState {
  if (!record(value) || !record(value['plan']) || !Array.isArray(value['pending']) ||
      !Array.isArray(value['batches']) || !record(value['members'])) return false;
  const plan = value['plan'];
  if (!['wait', 'close', 'per-task', 'idle', 'disabled'].includes(String(plan['decision'])) ||
      typeof plan['reason'] !== 'string' || typeof plan['targetSize'] !== 'number' ||
      !optionalString(plan['closeAtUtc']) || !strings(plan['proposedMembers']) || !Array.isArray(plan['upcoming'])) return false;
  if (!plan['upcoming'].every(item => record(item) && typeof item['taskKey'] === 'string' &&
      typeof item['stage'] === 'string' && typeof item['risk'] === 'string' &&
      optionalString(item['etaUtc']) && strings(item['overlapsWith']))) return false;
  if (plan['advisor'] != null && (!record(plan['advisor']) ||
      !strings(plan['advisor']['reasons']) || typeof plan['advisor']['mode'] !== 'string' ||
      typeof plan['advisor']['verdict'] !== 'string' || typeof plan['advisor']['model'] !== 'string' ||
      typeof plan['advisor']['atUtc'] !== 'string')) return false;
  if (!value['pending'].every(item => record(item) && typeof item['taskKey'] === 'string' &&
      typeof item['reviewAttemptId'] === 'string' && typeof item['resultSha'] === 'string' &&
      typeof item['waitMinutes'] === 'number' && typeof item['risk'] === 'string' &&
      optionalString(item['exclusion']))) return false;
  if (!value['batches'].every(batch => record(batch) && typeof batch['batchId'] === 'string' &&
      typeof batch['phase'] === 'string' && typeof batch['baseSha'] === 'string' &&
      optionalString(batch['parentBatchId']) && optionalString(batch['phaseReason']) &&
      optionalString(batch['candidateSha']) && Array.isArray(batch['members']) && Array.isArray(batch['runs']) &&
      batch['members'].every((member: unknown) => record(member) && typeof member['taskKey'] === 'string' &&
        typeof member['replay'] === 'string' && typeof member['released'] === 'boolean') &&
      batch['runs'].every((run: unknown) => record(run) && typeof run['batchRunId'] === 'string' &&
        typeof run['host'] === 'string' && optionalString(run['outcome']) &&
        optionalString(run['classification']) && optionalString(run['startedAtUtc']) &&
        optionalString(run['finishedAtUtc'])) &&
      (batch['publication'] == null || (record(batch['publication']) &&
        optionalString(batch['publication']['candidateSha']) &&
        optionalString(batch['publication']['publishedSha']) &&
        optionalString(batch['publication']['atUtc']))))) return false;
  return Object.values(value['members']).every(member => record(member) &&
    ['pending', 'in-batch', 'published', 'fallback'].includes(String(member['status'])) &&
    optionalString(member['batchId']) && optionalString(member['phase']) &&
    (member['position'] === undefined || typeof member['position'] === 'number') &&
    (member['size'] === undefined || typeof member['size'] === 'number'));
}
