import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { HttpHeaders, provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { TaskDetailPrefetchService } from './task-detail-prefetch.service';
import type { TaskCoreResult } from '../../../models/task-core.model';

/**
 * Selection-facing behaviour of the shared core cache that the progressive
 * task switch (AGT-2955) relies on. Cache invariants of AGT-2956 (bounds,
 * store events, eviction) are covered by `task-core-cache.spec.ts`.
 *
 *   1. Only the bounded `/core` route is read, never the legacy full detail.
 *   2. A superseded foreground read aborts its request once no reader is left.
 *   3. `invalidate` makes the next read revalidate conditionally.
 *   4. `revalidateCore` revalidates even a current core.
 */
describe('TaskDetailPrefetchService', () => {
  let service: TaskDetailPrefetchService;
  let http: HttpTestingController;

  const core = (id: string, coreVersion = '4') =>
    ({ state: 'ready', id, projectId: 'PROJ-001', projectName: 'p', taskKey: `C:/p::${id}`, coreVersion });
  const coreUrl = (id: string) => (r: { url: string }) => r.url.endsWith(`/api/tasks/${id}/core`);

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
  });

  it('reads only the bounded core and aborts a superseded foreground read', () => {
    const first = service.getCore('PROJ-001', 'job-a', 'C:/p::job-a').subscribe();
    const second = service.getCore('PROJ-001', 'job-a', 'C:/p::job-a').subscribe();
    const request = http.expectOne(coreUrl('job-a'));
    expect(request.request.params.get('project')).toBe('PROJ-001');
    http.expectNone(r => r.url.endsWith('/tasks/job-a'));

    first.unsubscribe();
    expect(request.cancelled).toBe(false);
    second.unsubscribe();
    expect(request.cancelled).toBe(true);
    expect(service.coreCacheSize().inFlight).toBe(0);
  });

  it('`invalidate` revalidates only the matching task with If-None-Match', () => {
    service.prefetchCores([
      { project: 'PROJ-001', id: 'job-d', taskKey: 'C:/p::job-d' },
      { project: 'PROJ-001', id: 'job-e', taskKey: 'C:/p::job-e' },
    ]);
    http.expectOne(coreUrl('job-d')).flush(core('job-d'), { headers: new HttpHeaders({ ETag: '"d4"' }) });
    http.expectOne(coreUrl('job-e')).flush(core('job-e'));

    service.invalidate('job-d');
    expect(service.isCoreCurrent('PROJ-001', 'job-d')).toBe(false);
    expect(service.isCoreCurrent('PROJ-001', 'job-e')).toBe(true);
    // The stale core still paints until its revalidation answers.
    expect(service.peekCore('PROJ-001', 'job-d')?.id).toBe('job-d');

    const results: TaskCoreResult[] = [];
    service.getCore('PROJ-001', 'job-d', 'C:/p::job-d').subscribe(result => results.push(result));
    const revalidation = http.expectOne(coreUrl('job-d'));
    expect(revalidation.request.headers.get('If-None-Match')).toBe('"d4"');
    revalidation.flush(null, { status: 304, statusText: 'Not Modified' });
    expect(results).toEqual([{ state: 'ready', core: expect.objectContaining({ id: 'job-d' }) }]);
    expect(service.isCoreCurrent('PROJ-001', 'job-d')).toBe(true);
  });

  it('`revalidateCore` asks the server even for a current core', () => {
    service.prefetchCores([{ project: 'PROJ-001', id: 'job-r', taskKey: 'C:/p::job-r' }]);
    http.expectOne(coreUrl('job-r')).flush(core('job-r'));
    expect(service.isCoreCurrent('PROJ-001', 'job-r')).toBe(true);

    const results: TaskCoreResult[] = [];
    service.revalidateCore('PROJ-001', 'job-r', 'C:/p::job-r').subscribe(result => results.push(result));
    http.expectOne(coreUrl('job-r')).flush(core('job-r', '5'));
    expect(results.map(result => result.core?.coreVersion)).toEqual(['5']);
  });

  it('does not cache a warming core', () => {
    const results: TaskCoreResult[] = [];
    service.getCore('PROJ-001', 'job-w', 'C:/p::job-w').subscribe(result => results.push(result));
    http.expectOne(coreUrl('job-w')).flush({ state: 'warming' }, { status: 202, statusText: 'Accepted' });
    expect(results).toEqual([{ state: 'warming', core: null }]);
    expect(service.peekCore('PROJ-001', 'job-w')).toBeNull();
  });
});
