import { Injectable, inject } from '@angular/core';
import { Subscription } from 'rxjs';
import { TaskService } from '../../../services/task.service';
import type { TaskCore } from '../../../models/task-core.model';

/**
 * Tiny in-memory prefetch + cache for bounded `TaskCore` projections keyed by
 * `project::id`. Owns three jobs:
 *
 * 1. **Prefetch** the next 1-2 peers in the active lane-pager iteration
 *    while the user is reading the current task, so pager steps and the
 *    accept → next-task navigation paint the complete core without a
 *    roundtrip. Enrichment (documents, Git, usage, review, history) is never
 *    prefetched; it belongs to the selected task only.
 * 2. **Coalesce and abort**: a `prefetchCore` while the same key has a
 *    pending response is a no-op, and `keepLookahead` cancels requests for
 *    entries that left the lookahead window.
 * 3. **Stale-guard**: each cache entry stores a wall-clock timestamp; reads
 *    older than `TTL_MS` are treated as a miss. The selection service always
 *    revalidates a cached core against the server after painting it.
 *
 * Not a general-purpose cache: the only entry point that populates it is the
 * lane-pager iteration's "what's next?" question plus accepted selections,
 * and the only consumer is the task selection path.
 */
@Injectable({ providedIn: 'root' })
export class TaskDetailPrefetchService {
  private readonly jobService = inject(TaskService);

  private static readonly TTL_MS = 30_000;
  private static readonly MAX_CORES = 16;

  private readonly cores = new Map<string, { core: TaskCore; cachedAt: number }>();
  private readonly coreRequests = new Map<string, Subscription>();

  private coreKey(id: string, project: string): string { return `${project}::${id}`; }

  /** Fire-and-forget core prefetch. Errors are swallowed; the real open surfaces them. */
  prefetchCore(id: string, project: string): void {
    const key = this.coreKey(id, project);
    if (this.takeCore(id, project) || this.coreRequests.has(key)) return;
    const request = this.jobService.getCore(id, project).subscribe({
      next: core => { if (core.state !== 'warming') this.storeCore(core, project); },
      error: () => this.coreRequests.delete(key),
      complete: () => this.coreRequests.delete(key),
    });
    if (!request.closed) this.coreRequests.set(key, request);
  }

  /** Abort in-flight prefetches whose key left the pager lookahead window. */
  keepLookahead(keys: ReadonlySet<string>): void {
    for (const [key, request] of this.coreRequests) {
      if (!keys.has(key)) { request.unsubscribe(); this.coreRequests.delete(key); }
    }
  }

  storeCore(core: TaskCore, project: string): void {
    const key = this.coreKey(core.id, project);
    this.cores.delete(key);
    this.cores.set(key, { core, cachedAt: Date.now() });
    while (this.cores.size > TaskDetailPrefetchService.MAX_CORES)
      this.cores.delete(this.cores.keys().next().value!);
  }

  /** Synchronous peek at a fresh core; null on a miss or after the TTL. */
  takeCore(id: string, project: string): TaskCore | null {
    const key = this.coreKey(id, project);
    const entry = this.cores.get(key);
    if (!entry) return null;
    if (Date.now() - entry.cachedAt >= TaskDetailPrefetchService.TTL_MS) {
      this.cores.delete(key); return null;
    }
    return entry.core;
  }

  /**
   * Drop every cached core of one task. Use after a mutation that we know
   * stales it (the user just acted on the task) or after a 403/404.
   */
  invalidate(id: string): void {
    for (const [key, entry] of this.cores) {
      if (entry.core.id === id) this.cores.delete(key);
    }
  }

  /** Drop everything and abort every in-flight prefetch. Used on lane / project change. */
  clear(): void {
    this.cores.clear();
    this.keepLookahead(new Set());
  }
}
