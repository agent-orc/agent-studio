import { DestroyRef, Injectable, inject } from '@angular/core';
import { HttpErrorResponse, type HttpResponse } from '@angular/common/http';
import { Observable, of, ReplaySubject, Subject, type Subscription } from 'rxjs';
import type { TaskInfo } from '../../../models/task.model';
import {
  taskCoreKey,
  type TaskCore,
  type TaskCoreResult,
} from '../../../models/task-core.model';
import { TaskService, type TaskStoreEvent } from '../../../services/task.service';

/** One task core the lookahead should warm. */
export interface TaskCoreTarget {
  project: string;
  id: string;
  /** Board `taskKey` (`watchPath::id`) of the task; scopes store events to it. */
  taskKey: string;
}

interface CoreEntry {
  core: TaskCore;
  etag: string | null;
  bytes: number;
  /** Must be revalidated (conditionally) before it is served as current. */
  stale: boolean;
}

interface CoreFlight {
  project: string;
  id: string;
  /**
   * Board `taskKey` of the requested task. A store event evicts a flight only
   * on an exact `taskKey` match, so the same slug in another project is safe.
   */
  taskKey: string;
  /** Board facts at request start, used to reject a reply overtaken by a grouped refresh. */
  boardSignature: string | null;
  subject: ReplaySubject<TaskCoreResult>;
  subscription: Subscription | null;
  /** Lookahead flights are cancellable until a foreground read joins them. */
  lookahead: boolean;
  /** Live foreground readers; the last one leaving aborts the request. */
  readers: number;
  /** Set when the task changed while this request was in flight. */
  superseded: boolean;
  /** Set when the task was deleted or its project left view mid-flight. */
  evicted: boolean;
}

/**
 * In-memory store of bounded task cores (`/api/tasks/{id}/core`, AGT-2953).
 * The selection paints a core from here, then loads enrichment resources
 * (`/details/*`, AGT-2955) for the selected task only; enrichment is never
 * prefetched. The core store (AGT-2956) is documented in
 * `docs/system/domains/frontend.md` (Task core cache):
 *
 * - Keyed by registry project handle plus task id.
 * - Bounded by entry count and estimated bytes; least recently used first.
 * - Concurrent reads of one key share one request; lookahead requests are
 *   cancelled once they leave the lookahead window, and a foreground request
 *   is aborted once its last reader unsubscribed (a superseded selection).
 * - Invalidation is per task and per resource. A board-store event marks only
 *   that task's core stale; the next read revalidates with `If-None-Match`.
 *   Git state and sidecar generations never touch it.
 *   Deletes of an exactly identified task, 404 and 403 evict; a delete that
 *   names only the id revalidates instead.
 */
@Injectable({ providedIn: 'root' })
export class TaskDetailPrefetchService {
  private readonly jobService = inject(TaskService);

  /** A visited core is at most 16 KiB; 48 of them stay under the byte bound. */
  static readonly MAX_CORE_ENTRIES = 48;
  static readonly MAX_CORE_BYTES = 512 * 1024;
  /** The lookahead warms at most the next two pager cores. */
  static readonly CORE_LOOKAHEAD = 2;

  /** Insertion order is recency order: a read re-inserts its entry. */
  private readonly cores = new Map<string, CoreEntry>();
  private coreBytes = 0;
  private readonly coreFlights = new Map<string, CoreFlight>();
  private readonly coreInvalidatedSubject = new Subject<string | null>();
  /**
   * Emits the cache key of a core that just became stale or was evicted, or
   * `null` when every entry must be revalidated (reconnect, bulk change). The
   * selection revalidates its visible core on a matching key.
   */
  readonly coreInvalidated = this.coreInvalidatedSubject.asObservable();

  constructor() {
    const subscription = this.jobService.taskEvents.subscribe((event) => this.onTaskEvent(event));
    inject(DestroyRef).onDestroy(() => subscription.unsubscribe());
  }

  /**
   * Mark every cached core of one task stale after a mutation of it, so the
   * next read revalidates instead of serving the pre-mutation core.
   */
  invalidate(id: string): void {
    for (const key of this.coreKeysFor(id, null)) this.markCoreStale(key, false);
  }

  /** The cached core, current or stale, without a request. Refreshes recency. */
  peekCore(project: string, id: string): TaskCore | null {
    const key = taskCoreKey(project, id);
    const entry = this.cores.get(key);
    if (!entry) return null;
    this.touchCore(key, entry);
    return entry.core;
  }

  /** True when a cached core may be served without revalidation. */
  isCoreCurrent(project: string, id: string): boolean {
    const entry = this.cores.get(taskCoreKey(project, id));
    return !!entry && !entry.stale;
  }

  /**
   * Foreground core read. A current entry answers synchronously; an in-flight
   * request (including a lookahead) is joined instead of duplicated; a stale
   * entry is revalidated with its ETag so an unchanged task costs a 304.
   */
  getCore(project: string, id: string, taskKey: string): Observable<TaskCoreResult> {
    const key = taskCoreKey(project, id);
    const entry = this.cores.get(key);
    if (entry && !entry.stale) {
      this.touchCore(key, entry);
      return of({ state: entry.core.state === 'stale' ? 'stale' : 'ready', core: entry.core });
    }
    const flight = this.coreFlights.get(key) ?? this.startCoreRequest(key, project, id, taskKey, false);
    flight.lookahead = false;
    return this.joinFlight(key, flight);
  }

  /**
   * Revalidate a core regardless of its freshness (a resource answered for
   * another generation). An unchanged core still costs only a 304.
   */
  revalidateCore(project: string, id: string, taskKey: string): Observable<TaskCoreResult> {
    this.markCoreStale(taskCoreKey(project, id), false);
    return this.getCore(project, id, taskKey);
  }

  /** Count a foreground reader; the last one to leave aborts a pending request. */
  private joinFlight(key: string, flight: CoreFlight): Observable<TaskCoreResult> {
    return new Observable<TaskCoreResult>((subscriber) => {
      flight.readers++;
      const inner = flight.subject.subscribe(subscriber);
      return () => {
        inner.unsubscribe();
        if (--flight.readers > 0 || flight.lookahead || this.coreFlights.get(key) !== flight) return;
        this.coreFlights.delete(key);
        flight.subscription?.unsubscribe();
        flight.subject.complete();
      };
    });
  }

  /**
   * Replace the lookahead window. Lookahead flights outside `targets` are
   * aborted; targets that are current or already in flight cost nothing.
   * Pass an empty list to cancel all lookahead.
   */
  prefetchCores(targets: readonly TaskCoreTarget[]): void {
    const window = targets.slice(0, TaskDetailPrefetchService.CORE_LOOKAHEAD);
    const wanted = new Set(window.map((t) => taskCoreKey(t.project, t.id)));
    for (const [key, flight] of [...this.coreFlights]) {
      if (!flight.lookahead || wanted.has(key)) continue;
      this.coreFlights.delete(key);
      flight.subscription?.unsubscribe();
      flight.subject.complete();
    }
    for (const target of window) {
      const key = taskCoreKey(target.project, target.id);
      const entry = this.cores.get(key);
      if ((entry && !entry.stale) || this.coreFlights.has(key)) continue;
      this.startCoreRequest(key, target.project, target.id, target.taskKey, true);
    }
  }

  /**
   * Evict every core whose project the viewer can no longer see. Called with
   * the current registry projection after a project-list change.
   */
  retainCoreProjects(allowed: (projectId: string, projectName: string) => boolean): void {
    for (const [key, entry] of [...this.cores]) {
      if (!allowed(entry.core.projectId, entry.core.projectName)) this.evictCore(key);
    }
    for (const [key, flight] of this.coreFlights) {
      if (!allowed(flight.project, this.cores.get(key)?.core.projectName ?? flight.project)) {
        this.evictCore(key);
      }
    }
  }

  /** Diagnostics and tests: entry count and estimated bytes held. */
  coreCacheSize(): { entries: number; bytes: number; inFlight: number } {
    return { entries: this.cores.size, bytes: this.coreBytes, inFlight: this.coreFlights.size };
  }

  private startCoreRequest(
    key: string, project: string, id: string, taskKey: string, lookahead: boolean,
  ): CoreFlight {
    const boardRow = this.jobService.jobs().find((job) => job.taskKey === taskKey);
    const flight: CoreFlight = {
      project,
      id,
      taskKey,
      boardSignature: boardRow ? this.boardCoreSignature(boardRow) : null,
      subject: new ReplaySubject<TaskCoreResult>(1),
      subscription: null,
      lookahead,
      readers: 0,
      superseded: false,
      evicted: false,
    };
    this.coreFlights.set(key, flight);
    const held = this.cores.get(key) ?? null;
    const settle = (result: TaskCoreResult) => {
      if (this.coreFlights.get(key) === flight) this.coreFlights.delete(key);
      flight.subject.next(result);
      flight.subject.complete();
    };
    flight.subscription = this.jobService.getCore(id, project, held?.etag).subscribe({
      next: (response) => settle(this.acceptCoreResponse(key, flight, response, held)),
      error: (err: unknown) => {
        const result = this.acceptCoreError(key, project, flight, err, held);
        if (result) {
          settle(result);
          return;
        }
        if (this.coreFlights.get(key) === flight) this.coreFlights.delete(key);
        flight.subject.error(err);
      },
    });
    return flight;
  }

  private acceptCoreResponse(
    key: string,
    flight: CoreFlight,
    response: HttpResponse<TaskCore>,
    held: CoreEntry | null,
  ): TaskCoreResult {
    const body = response.body;
    // Deleted or access-revoked while in flight: never store or paint it.
    if (flight.evicted) return { state: 'missing', core: null };
    if (response.status === 202 || !body || (body.state !== 'ready' && body.state !== 'stale')) {
      // Warming: identity known, core not hydrated yet. Keep any older core
      // for paint but never present it as current.
      if (held) this.markCoreStale(key, false);
      return { state: 'warming', core: null };
    }
    const length = Number(response.headers.get('Content-Length'));
    const entry: CoreEntry = {
      core: body,
      etag: response.headers.get('ETag'),
      bytes: Number.isFinite(length) && length > 0 ? length : JSON.stringify(body).length,
      // A reply that raced a change of the same task is kept for paint and
      // revalidated on the next read instead of being trusted as current.
      stale: body.state === 'stale' || flight.superseded,
    };
    this.storeCore(key, entry);
    // A superseded reply is reported as stale so its reader revalidates.
    return { state: entry.stale ? 'stale' : body.state, core: body };
  }

  private acceptCoreError(
    key: string,
    project: string,
    flight: CoreFlight,
    err: unknown,
    held: CoreEntry | null,
  ): TaskCoreResult | null {
    if (!(err instanceof HttpErrorResponse)) return null;
    if (flight.evicted) return { state: 'missing', core: null };
    if (err.status === 304) {
      const entry = this.cores.get(key) ?? held;
      if (!entry) return null;
      entry.etag = err.headers.get('ETag') ?? entry.etag;
      entry.stale = entry.core.state === 'stale' || flight.superseded;
      this.storeCore(key, entry);
      return { state: entry.stale ? 'stale' : 'ready', core: entry.core };
    }
    if (err.status === 404) {
      this.evictCore(key);
      return { state: 'missing', core: null };
    }
    if (err.status === 403) {
      // Access to the project was revoked: nothing private from it may stay.
      for (const [otherKey, entry] of [...this.cores]) {
        if (otherKey === key || otherKey.startsWith(taskCoreKey(project, ''))
          || entry.core.projectId === project) this.evictCore(otherKey);
      }
      for (const [otherKey, otherFlight] of this.coreFlights) {
        if (otherFlight.project === project) this.evictCore(otherKey);
      }
      this.evictCore(key);
      return { state: 'denied', core: null };
    }
    return null;
  }

  private storeCore(key: string, entry: CoreEntry): void {
    const previous = this.cores.get(key);
    if (previous) {
      this.coreBytes -= previous.bytes;
      this.cores.delete(key);
    }
    this.cores.set(key, entry);
    this.coreBytes += entry.bytes;
    for (const oldest of this.cores.keys()) {
      if (this.cores.size <= TaskDetailPrefetchService.MAX_CORE_ENTRIES
        && this.coreBytes <= TaskDetailPrefetchService.MAX_CORE_BYTES) break;
      if (oldest === key) continue;
      this.dropCoreEntry(oldest);
    }
  }

  private touchCore(key: string, entry: CoreEntry): void {
    this.cores.delete(key);
    this.cores.set(key, entry);
  }

  /** Capacity eviction: the task is fine, it just no longer fits. */
  private dropCoreEntry(key: string): CoreEntry | undefined {
    const entry = this.cores.get(key);
    if (entry) {
      this.coreBytes -= entry.bytes;
      this.cores.delete(key);
    }
    return entry;
  }

  /** Correctness eviction (delete, 404, access loss); an in-flight reply is dropped too. */
  private evictCore(key: string): void {
    const entry = this.dropCoreEntry(key);
    const flight = this.coreFlights.get(key);
    if (flight) flight.evicted = true;
    if (entry) this.coreInvalidatedSubject.next(key);
  }

  private markCoreStale(key: string, notify = true): void {
    const entry = this.cores.get(key);
    if (entry) entry.stale = true;
    const flight = this.coreFlights.get(key);
    if (flight) flight.superseded = true;
    if (notify && (entry || flight)) this.coreInvalidatedSubject.next(key);
  }

  /**
   * Keys of cached or in-flight cores for one board task. With a `taskKey` the
   * match is exact: the registry handle and the board key both name the
   * project, so an identical slug elsewhere never matches. Without one (a
   * `jobMoved` push carries only the id) every core with that id matches;
   * such a match may only revalidate, never evict.
   */
  private coreKeysFor(id: string, taskKey: string | null): string[] {
    const keys = new Set<string>();
    for (const [key, entry] of this.cores) {
      if (taskKey ? entry.core.taskKey === taskKey : entry.core.id === id) keys.add(key);
    }
    for (const [key, flight] of this.coreFlights) {
      if (taskKey ? flight.taskKey === taskKey : flight.id === id) keys.add(key);
    }
    return [...keys];
  }

  /** Patch the lane-level facts a board record already carries. */
  private patchCore(key: string, info: Pick<TaskInfo, 'state'> & Partial<TaskInfo>): void {
    const entry = this.cores.get(key);
    if (!entry) return;
    entry.core = {
      ...entry.core,
      lane: info.state,
      ...(info.title !== undefined ? { title: info.title } : {}),
      ...(info.order !== undefined ? { order: info.order } : {}),
      ...(info.archiveState !== undefined ? { archiveState: info.archiveState } : {}),
    };
  }

  /** Compare the runtime and pin facts published in both the board and core. */
  private boardCoreFactsChanged(info: TaskInfo, core: TaskCore): boolean {
    const pins = core.pins;
    const runtime = core.runtime;
    return (info.archiveState !== undefined && (info.archiveState ?? null) !== (core.archiveState ?? null))
      || (info.enteredLaneAt !== undefined && (info.enteredLaneAt ?? null) !== (core.enteredLaneAt ?? null))
      || (info.released ?? false) !== core.released
      || (info.pendingIntent != null) !== core.pendingIntent
      || (info.model ?? null) !== (pins.model ?? null)
      || (info.modelExplicit ?? false) !== pins.modelExplicit
      || (info.thinkingLevel ?? null) !== (pins.thinkingLevel ?? null)
      || (info.thinkingLevelExplicit ?? false) !== pins.thinkingLevelExplicit
      || (info.cliType ?? null) !== (pins.cliType ?? null)
      || (info.contextMode ?? null) !== (pins.contextMode ?? null)
      || (info.useOwnSession ?? null) !== (pins.useOwnSession ?? null)
      || (info.allowWebAccess ?? false) !== pins.allowWebAccess
      || (info.noBranchExpected ?? false) !== pins.noBranchExpected
      || (info.execution?.status ?? null) !== (runtime.executionStatus ?? null)
      || JSON.stringify(info.runActivity ?? null) !== JSON.stringify(runtime.activity ?? null)
      || (info.executionLocation?.executionKind ?? 'none') !== runtime.location
      || (info.executionLocation?.runnerId ?? null) !== (runtime.runnerId ?? null)
      || (info.executionLocation?.lastHeartbeat ?? null) !== (runtime.heartbeatAt ?? null);
  }

  /** Only facts that a grouped refresh can use to supersede a core read. */
  private boardCoreSignature(info: TaskInfo): string {
    return JSON.stringify([
      info.state, info.title, info.order,
      info.archiveState ?? null, info.enteredLaneAt ?? null,
      info.released ?? false, info.pendingIntent != null,
      info.model ?? null, info.modelExplicit ?? false,
      info.thinkingLevel ?? null, info.thinkingLevelExplicit ?? false,
      info.cliType ?? null, info.contextMode ?? null, info.useOwnSession ?? null,
      info.allowWebAccess ?? false, info.noBranchExpected ?? false,
      info.execution?.status ?? null, info.runActivity ?? null,
      info.executionLocation?.executionKind ?? 'none',
      info.executionLocation?.runnerId ?? null,
      info.executionLocation?.lastHeartbeat ?? null,
    ]);
  }

  private onTaskEvent(event: TaskStoreEvent): void {
    switch (event.kind) {
      case 'upserted': {
        for (const key of this.coreKeysFor(event.info.id, event.info.taskKey)) {
          this.patchCore(key, event.info);
          this.markCoreStale(key);
        }
        return;
      }
      case 'deleted': {
        if (!event.taskKey) {
          // The deleted task's project is unknown: revalidate every core with
          // that id. The deleted one answers 404 and is evicted then; a twin
          // in another project answers 304 and stays.
          for (const key of this.coreKeysFor(event.id, null)) this.markCoreStale(key);
          return;
        }
        for (const key of this.coreKeysFor(event.id, event.taskKey)) this.evictCore(key);
        return;
      }
      case 'moved':
        for (const key of this.coreKeysFor(event.id, null)) this.markCoreStale(key);
        return;
      case 'mutated': {
        const taskKey = event.watchPath ? `${event.watchPath}::${event.id}` : null;
        for (const key of this.coreKeysFor(event.id, taskKey)) {
          // An id-only match may name a same-slug core in another project:
          // revalidate it, but patch the lane only on an exact project match.
          if (event.lane && taskKey) this.patchCore(key, { state: event.lane });
          this.markCoreStale(key);
        }
        return;
      }
      case 'bulk':
      case 'reconnected':
        // Revalidate, never discard: an unchanged core answers 304.
        for (const entry of this.cores.values()) entry.stale = true;
        for (const flight of this.coreFlights.values()) flight.superseded = true;
        this.coreInvalidatedSubject.next(null);
        return;
      case 'snapshot': {
        if (this.cores.size === 0 && this.coreFlights.size === 0) return;
        const board = new Map(this.jobService.jobs().map((job) => [job.taskKey, job]));
        for (const [key, flight] of [...this.coreFlights]) {
          const info = board.get(flight.taskKey);
          if (info && flight.boardSignature !== this.boardCoreSignature(info)) {
            this.markCoreStale(key);
          }
        }
        // Invalidation can synchronously make selection touch and reinsert a
        // core for recency. Iterate a fixed list so it is visited only once.
        for (const [key, entry] of [...this.cores]) {
          const info = board.get(entry.core.taskKey);
          if (!info) continue;
          if (info.state === entry.core.lane && info.title === entry.core.title
            && info.order === entry.core.order
            && !this.boardCoreFactsChanged(info, entry.core)) continue;
          this.patchCore(key, info);
          this.markCoreStale(key);
        }
        return;
      }
    }
  }
}
