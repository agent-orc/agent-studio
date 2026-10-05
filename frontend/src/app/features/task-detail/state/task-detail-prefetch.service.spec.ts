import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskDetailPrefetchService } from './task-detail-prefetch.service';
import type { TaskDetail } from '../../../models/task.model';

/**
 * Covers the lane-pager prefetch cache that backs the
 * "Accept → next-task feels instant" path:
 *
 *   1. `prefetch` issues one GET per (id, watchPath) and stores the result.
 *   2. A second `prefetch` while the first is still in flight coalesces
 *      onto the same request (no fan-out).
 *   3. `take` peeks the cached detail without consuming it, so a quick
 *      back-nav or retry inside the same lane walk still paints instantly.
 *   4. `invalidate` drops a single entry without touching siblings.
 *
 * These are the invariants the triage-move and pager-step paths depend on;
 * without them the optimistic navigation could either fan out N parallel
 * GETs or serve the same stale snapshot twice in a row.
 */
describe('TaskDetailPrefetchService', () => {
  let service: TaskDetailPrefetchService;
  let http: HttpTestingController;

  const makeDetail = (id: string, watchPath: string): TaskDetail =>
    ({
      info: {
        id,
        taskKey: `${watchPath}::${id}`,
        title: id,
        state: '5-human-review',
        watchPath,
      },
      promptMarkdown: null,
      promptHistory: [],
      titleHistory: [],
      statusMarkdown: null,
      contextUsage: null,
      log: [],
      summaryState: null,
      reviewEvidence: [],
    }) as unknown as TaskDetail;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    service = TestBed.inject(TaskDetailPrefetchService);
    http = TestBed.inject(HttpTestingController);
    // `providedIn: 'root'` plus vitest's worker-level module cache means
    // an entry could carry over from a sibling spec file; clear so each
    // assertion about cache occupancy here is independent of file order.
    service.clear();
  });

  it('prefetches once and caches the result for subsequent `take`', () => {
    service.prefetch('job-a', '/wp');
    const req = http.expectOne(r => r.url === '/api/v1/projects/-/tasks/job-a');
    req.flush(makeDetail('job-a', '/wp'));

    const cached = service.take('job-a', '/wp');
    expect(cached?.info.id).toBe('job-a');
    // Within TTL the cache keeps serving the same payload — `take` is a
    // peek (not a consume) so a back-nav or retry within the same lane
    // walk still paints instantly without a second prefetch round.
    expect(service.take('job-a', '/wp')?.info.id).toBe('job-a');
  });

  it('coalesces parallel `prefetch` calls onto a single HTTP request', () => {
    service.prefetch('job-b', '/wp');
    service.prefetch('job-b', '/wp');
    service.prefetch('job-b', '/wp');
    // Only one request is in flight; the others piggyback the ReplaySubject.
    const reqs = http.match(r => r.url === '/api/v1/projects/-/tasks/job-b');
    expect(reqs).toHaveLength(1);
    reqs[0].flush(makeDetail('job-b', '/wp'));
  });

  it('skips `prefetch` when a fresh entry already exists', () => {
    service.prefetch('job-c', '/wp');
    const first = http.expectOne(r => r.url === '/api/v1/projects/-/tasks/job-c');
    first.flush(makeDetail('job-c', '/wp'));
    // Trigger a second prefetch immediately - within TTL the service should
    // short-circuit without firing an HTTP call.
    service.prefetch('job-c', '/wp');
    http.expectNone(r => r.url === '/api/v1/projects/-/tasks/job-c');
  });

  it('`invalidate` drops only the matching key', () => {
    service.prefetch('job-d', '/wp');
    service.prefetch('job-e', '/wp');
    http.expectOne(r => r.url === '/api/v1/projects/-/tasks/job-d')
      .flush(makeDetail('job-d', '/wp'));
    http.expectOne(r => r.url === '/api/v1/projects/-/tasks/job-e')
      .flush(makeDetail('job-e', '/wp'));

    service.invalidate('job-d', '/wp');
    expect(service.take('job-d', '/wp')).toBeNull();
    expect(service.take('job-e', '/wp')?.info.id).toBe('job-e');
  });

  it('`take` returns null after an error swallows the prefetch', () => {
    service.prefetch('job-f', '/wp');
    http.expectOne(r => r.url === '/api/v1/projects/-/tasks/job-f')
      .error(new ProgressEvent('error'));
    expect(service.take('job-f', '/wp')).toBeNull();
  });
});
