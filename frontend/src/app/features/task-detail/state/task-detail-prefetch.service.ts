import { DestroyRef, Injectable, inject } from '@angular/core';
import { HttpErrorResponse, type HttpResponse } from '@angular/common/http';
import { Observable, of, ReplaySubject, Subject, type Subscription } from 'rxjs';
import { TaskDetail, type TaskInfo } from '../../../models/task.model';
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
  subject: ReplaySubject<TaskCoreResult>;
  subscription: Subscription | null;
  /** Lookahead flights are cancellable until a foreground read joins them. */
  lookahead: boolean;
  /** Set when the task changed while this request was in flight. */
  superseded: boolean;
  /** Set when the task was deleted or its project left view mid-flight. */
  evicted: boolean;
}

/**
 * Tiny in-memory prefetch + cache for `TaskDetail` payloads keyed by
 * `watchPath::id` (`taskKey`). Owns three jobs:
 *
 * 1. **Prefetch** the next 1-2 peers in the active lane-pager iteration
 *    while the user is reading the current task, so the accept → next-task
 *    navigation feels instant: when the user clicks Mark-as-Done, the
 *    detail for the next peer is already in memory and the panel
 *    re-renders without waiting for a roundtrip.
 * 2. **Coalesce** in-flight fetches: a `prefetch` while the same key has
 *    a pending response is a no-op, and a parallel `take` subscriber on
 *    the same key shares the existing response stream.
 * 3. **Stale-guard**: each cache entry stores a wall-clock timestamp; reads
 *    older than `TTL_MS` are treated as a miss so the caller fetches a
 *    fresh detail. The TTL is short on purpose - detail payloads include
 *    log / status that move under polling, and we'd rather pay one extra
 *    GET than render an obviously-stale panel.
 *
 * Not a general-purpose cache: the only entry point that populates it
 * is the lane-pager iteration's "what's next?" question, and the only
 * consumer is the triage / pager navigation path.
 *
 * AGT-2956 adds a second, independent store: the bounded task core
 * (`/api/tasks/{id}/core`). Its contract is documented in
 * `docs/system/domains/frontend.md` (Task core cache):
 *
 * - Keyed by registry project handle plus task id, never by the full-detail
 *   `watchPath::id` key, so full-detail TTL expiry never drops a core.
 * - Bounded by entry count and estimated bytes; least recently used first.
 * - Concurrent reads of one key share one request; lookahead requests are
 *   cancelled once they leave the lookahead window.
 * - Invalidation is per task and per resource. A board-store event marks only
 *   that task's core stale; the next read revalidates with `If-None-Match`.
 *   Git state, sidecar generations and full-detail changes never touch it.
 *   Deletes of an exactly identified task, 404 and 403 evict; a delete that
 *   names only the id revalidates instead.
 */
@Injectable({ providedIn: 'root' })
export class TaskDetailPrefetchService {
  private readonly jobService = inject(TaskService);

  private static readonly TTL_MS = 30_000;
  /** A visited core is at most 16 KiB; 48 of them stay under the byte bound. */
  static readonly MAX_CORE_ENTRIES = 48;
  static readonly MAX_CORE_BYTES = 512 * 1024;
  /** The lookahead warms at most the next two pager cores. */
  static readonly CORE_LOOKAHEAD = 2;

  private readonly cache = new Map<string, { detail: TaskDetail; cachedAt: number }>();
  private readonly inFlight = new Map<string, ReplaySubject<TaskDetail>>();

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

  private keyOf(id: string, watchPath: string): string {
    return `${watchPath}::${id}`;
  }

  /**
   * Fire-and-forget prefetch. Idempotent: skipped when the key is
   * already cached fresh, or another prefetch is in flight. Errors are
   * swallowed - prefetch is a best-effort hint, not a contract.
   */
  prefetch(id: string, watchPath: string): void {
    const key = this.keyOf(id, watchPath);
    const cached = this.cache.get(key);
    if (cached && Date.now() - cached.cachedAt < TaskDetailPrefetchService.TTL_MS) return;
    if (this.inFlight.has(key)) return;

    const subject = new ReplaySubject<TaskDetail>(1);
    this.inFlight.set(key, subject);
    this.jobService.getDetail(id, watchPath).subscribe({
      next: (detail) => {
        this.cache.set(key, { detail, cachedAt: Date.now() });
        subject.next(detail);
        subject.complete();
        this.inFlight.delete(key);
      },
      error: () => {
        // Treat as a soft miss; the caller's eventual real fetch will
        // surface the error if it still applies.
        subject.complete();
        this.inFlight.delete(key);
      },
    });
  }

  /**
   * Synchronous peek. Returns the cached detail when fresh, otherwise
   * null. Use this when you need the instant-paint path and have a real
   * fetch lined up as the source-of-truth fallback (the move / pager
   * paths do exactly this: peek to repaint instantly, refetch to
   * reconcile drift). Peek (not consume) so a quick back-nav or retry
   * within the same lane walk still paints instantly without firing a
   * second prefetch round.
   */
  take(id: string, watchPath: string): TaskDetail | null {
    const key = this.keyOf(id, watchPath);
    const entry = this.cache.get(key);
    if (!entry) return null;
    if (Date.now() - entry.cachedAt >= TaskDetailPrefetchService.TTL_MS) {
      this.cache.delete(key);
      return null;
    }
    return entry.detail;
  }

  /**
   * Observable read. Returns the cached detail when fresh (sync via
   * `of`); subscribes to an in-flight prefetch when one is pending;
   * otherwise issues a fresh GET. The result is cached on success so a
   * subsequent `take` lands the same payload without re-fetching.
   */
  getOrFetch(id: string, watchPath: string): Observable<TaskDetail> {
    const cached = this.take(id, watchPath);
    if (cached) return of(cached);

    const key = this.keyOf(id, watchPath);
    const pending = this.inFlight.get(key);
    if (pending) return pending.asObservable();

    const subject = new ReplaySubject<TaskDetail>(1);
    this.inFlight.set(key, subject);
    this.jobService.getDetail(id, watchPath).subscribe({
      next: (detail) => {
        this.cache.set(key, { detail, cachedAt: Date.now() });
        subject.next(detail);
        subject.complete();
        this.inFlight.delete(key);
      },
      error: (err) => {
        subject.error(err);
        this.inFlight.delete(key);
      },
    });
    return subject.asObservable();
  }

  /**
   * Drop a single entry. Use after a mutation that we know stales the
   * cached detail (e.g. the user just acted on the job - the next
   * render needs the post-mutation state, not the prefetched snapshot).
   */
  invalidate(id: string, watchPath: string): void {
    this.cache.delete(this.keyOf(id, watchPath));
  }

  /**
   * Drop every full-detail entry. Cores are a separate resource and survive:
   * they are invalidated per task, never by a global reset.
   */
  clear(): void {
    this.cache.clear();
    // In-flight prefetches keep going; their results just won't be
    // consumed. Cheap enough that we don't bother aborting.
  }

  // ---- Task core ---------------------------------------------------------

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
      return of({ state: entry.core.state, core: entry.core });
    }
    const flight = this.coreFlights.get(key);
    if (flight) {
      flight.lookahead = false;
      return flight.subject.asObservable();
    }
    return this.startCoreRequest(key, project, id, taskKey, false).subject.asObservable();
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
    const flight: CoreFlight = {
      project,
      id,
      taskKey,
      subject: new ReplaySubject<TaskCoreResult>(1),
      subscription: null,
      lookahead,
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
      return { state: entry.stale ? 'stale' : entry.core.state, core: entry.core };
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
        this.cache.delete(event.taskKey);
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
        if (this.cores.size === 0) return;
        const board = new Map(this.jobService.jobs().map((job) => [job.taskKey, job]));
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
