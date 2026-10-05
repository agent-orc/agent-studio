import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskDetailPrefetchService } from './task-detail-prefetch.service';

/**
 * Covers the lane-pager core cache that backs the "pager step and
 * Accept → next-task paint instantly" path:
 *
 *   1. `prefetchCore` issues one bounded `/core` GET per (project, id) and
 *      never touches the legacy full-detail route.
 *   2. Parallel prefetches coalesce; a fresh entry short-circuits.
 *   3. `keepLookahead` and `clear` abort obsolete in-flight prefetches.
 *   4. `invalidate` drops one task without touching siblings.
 */
describe('TaskDetailPrefetchService', () => {
  let service: TaskDetailPrefetchService;
  let http: HttpTestingController;

  const core = (id: string, coreVersion = '4') =>
    ({ state: 'ready', id, projectId: 'PROJ-001', coreVersion });

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
    // an entry could carry over from a sibling spec file.
    service.clear();
  });

  it('prefetches only a bounded core and aborts obsolete pager lookahead', () => {
    service.prefetchCore('job-a', 'PROJ-001');
    service.prefetchCore('job-a', 'PROJ-001');
    const first = http.expectOne(r => r.url.endsWith('/api/tasks/job-a/core'));
    expect(first.request.params.get('project')).toBe('PROJ-001');
    http.expectNone(r => r.url.endsWith('/tasks/job-a'));
    service.keepLookahead(new Set());
    expect(first.cancelled).toBe(true);

    service.prefetchCore('job-a', 'PROJ-001');
    http.expectOne(r => r.url.endsWith('/api/tasks/job-a/core')).flush(core('job-a'));
    expect(service.takeCore('job-a', 'PROJ-001')?.coreVersion).toBe('4');
    service.prefetchCore('job-a', 'PROJ-001');
    http.expectNone(r => r.url.endsWith('/api/tasks/job-a/core'));
  });

  it('does not cache a warming core', () => {
    service.prefetchCore('job-w', 'PROJ-001');
    http.expectOne(r => r.url.endsWith('/api/tasks/job-w/core')).flush({ state: 'warming' });
    expect(service.takeCore('job-w', 'PROJ-001')).toBeNull();
  });

  it('`invalidate` drops only the matching task', () => {
    service.prefetchCore('job-d', 'PROJ-001');
    service.prefetchCore('job-e', 'PROJ-001');
    http.expectOne(r => r.url.endsWith('/api/tasks/job-d/core')).flush(core('job-d'));
    http.expectOne(r => r.url.endsWith('/api/tasks/job-e/core')).flush(core('job-e'));

    service.invalidate('job-d');
    expect(service.takeCore('job-d', 'PROJ-001')).toBeNull();
    expect(service.takeCore('job-e', 'PROJ-001')?.id).toBe('job-e');
  });

  it('`clear` aborts in-flight prefetches and empties the cache', () => {
    service.prefetchCore('job-c', 'PROJ-001');
    http.expectOne(r => r.url.endsWith('/api/tasks/job-c/core')).flush(core('job-c'));
    service.prefetchCore('job-f', 'PROJ-001');
    const pending = http.expectOne(r => r.url.endsWith('/api/tasks/job-f/core'));

    service.clear();
    expect(pending.cancelled).toBe(true);
    expect(service.takeCore('job-c', 'PROJ-001')).toBeNull();
  });
});
