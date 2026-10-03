import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpRequest, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { TaskDetailPrefetchService } from './task-detail-prefetch.service';
import { TaskService } from '../../../services/task.service';
import { JobsHubClient, type JobsHubHandlers } from '../../../services/jobs-hub-client.service';
import type { TaskCore, TaskCoreResult } from '../../../models/task-core.model';
import type { TaskInfo } from '../../../models/task.model';

class JobsHubClientStub {
  readonly connected = signal(false);
  handlers: JobsHubHandlers | null = null;
  start(handlers: JobsHubHandlers): void { this.handlers = handlers; }
  stop(): void { return undefined; }
}

function makeCore(projectId: string, id: string, overrides: Partial<TaskCore> = {}): TaskCore {
  return {
    state: 'ready',
    projectId,
    projectName: projectId,
    id,
    taskKey: `C:/${projectId}::${id}`,
    key: null,
    title: id,
    kind: 'task',
    taskType: 'chore',
    lane: '5-human-review',
    enteredLaneAt: '2026-09-28T10:00:00Z',
    order: 0,
    mode: 'coding',
    released: false,
    pendingIntent: false,
    pins: { modelExplicit: false, thinkingLevelExplicit: false, allowWebAccess: false, noBranchExpected: false },
    actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
    blocking: { dependencyBlocked: false, dependencyState: 'ready', dependencies: [], dependsOn: [], blockedBy: [] },
    runtime: { location: 'none', leaseState: 'none' },
    runtimeVersion: 'r0',
    statusSummary: { state: 'missing', originalBytes: 0 },
    prompt: { state: 'ready', text: `prompt of ${id}`, originalBytes: 12 },
    timeline: { state: 'missing', events: [], originalBytes: 0 },
    coreVersion: 1,
    ...overrides,
  };
}

const coreRequest = (project: string, id: string) => (request: HttpRequest<unknown>) =>
  request.url === `/api/tasks/${id}/core` && request.params.get('project') === project;
const anyCore = (request: HttpRequest<unknown>) => request.url.endsWith('/core');
const grouped = (request: HttpRequest<unknown>) => request.url === '/api/tasks/grouped';

/**
 * AGT-2956 core cache invariants: one shared bounded store keyed by project
 * and task identity, coalesced reads, conditional revalidation, and per-task
 * invalidation that never collapses into a global reset.
 */
describe('TaskDetailPrefetchService · task core cache', () => {
  let cache: TaskDetailPrefetchService;
  let tasks: TaskService;
  let hub: JobsHubClientStub;
  let http: HttpTestingController;

  const results: TaskCoreResult[] = [];
  const read = (project: string, id: string) =>
    cache.getCore(project, id, `C:/${project}::${id}`).subscribe((result) => results.push(result));

  beforeEach(async () => {
    results.length = 0;
    await TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: JobsHubClient, useClass: JobsHubClientStub },
      ],
    }).compileComponents();
    cache = TestBed.inject(TaskDetailPrefetchService);
    tasks = TestBed.inject(TaskService);
    hub = TestBed.inject(JobsHubClient) as unknown as JobsHubClientStub;
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    try {
      tasks.stopLiveUpdates();
      http.verify();
    } finally {
      TestBed.resetTestingModule();
    }
  });

  function startHub(): void {
    tasks.startLiveUpdates();
    http.expectOne('/api/projects/settings').flush({});
  }

  function flushReconnectResync(): void {
    http.expectOne(grouped).flush({});
    http.expectOne('/api/runner/status').flush({ projects: {} });
  }

  it('coalesces concurrent reads and serves a visited core without a request', () => {
    read('PROJ-A', 'fix-login');
    read('PROJ-A', 'fix-login');
    const requests = http.match(anyCore);
    expect(requests).toHaveLength(1);
    requests[0].flush(makeCore('PROJ-A', 'fix-login'), { headers: { ETag: '"core-1"' } });
    expect(results.map((r) => r.core?.id)).toEqual(['fix-login', 'fix-login']);

    read('PROJ-A', 'fix-login');
    http.expectNone(anyCore);
    expect(results).toHaveLength(3);
  });

  it('a foreground read joins an in-flight lookahead instead of duplicating it', () => {
    cache.prefetchCores([{ project: 'PROJ-A', id: 'next', taskKey: 'C:/PROJ-A::next' }]);
    read('PROJ-A', 'next');
    // The window moved on, but the joined request is no longer lookahead.
    cache.prefetchCores([]);
    const request = http.expectOne(coreRequest('PROJ-A', 'next'));
    expect(request.cancelled).toBe(false);
    request.flush(makeCore('PROJ-A', 'next'));
    expect(results[0].core?.id).toBe('next');
  });

  it('keeps identical slugs in different projects apart', () => {
    read('PROJ-A', 'fix-login');
    read('PROJ-B', 'fix-login');
    http.expectOne(coreRequest('PROJ-A', 'fix-login')).flush(makeCore('PROJ-A', 'fix-login', { title: 'A' }));
    http.expectOne(coreRequest('PROJ-B', 'fix-login')).flush(makeCore('PROJ-B', 'fix-login', { title: 'B' }));
    expect(cache.peekCore('PROJ-A', 'fix-login')?.title).toBe('A');
    expect(cache.peekCore('PROJ-B', 'fix-login')?.title).toBe('B');
    expect(cache.coreCacheSize().entries).toBe(2);
  });

  it('prefetches at most two cores and aborts lookahead that left the window', () => {
    cache.prefetchCores([
      { project: 'PROJ-A', id: 'one', taskKey: 'C:/PROJ-A::one' },
      { project: 'PROJ-A', id: 'two', taskKey: 'C:/PROJ-A::two' },
      { project: 'PROJ-A', id: 'three', taskKey: 'C:/PROJ-A::three' },
    ]);
    const first = http.match(anyCore);
    expect(first.map((r) => r.request.url)).toEqual(['/api/tasks/one/core', '/api/tasks/two/core']);

    cache.prefetchCores([{ project: 'PROJ-A', id: 'two', taskKey: 'C:/PROJ-A::two' }, { project: 'PROJ-A', id: 'three', taskKey: 'C:/PROJ-A::three' }]);
    expect(first[0].cancelled).toBe(true);
    expect(first[1].cancelled).toBe(false);
    const third = http.expectOne(coreRequest('PROJ-A', 'three'));
    first[1].flush(makeCore('PROJ-A', 'two'));
    third.flush(makeCore('PROJ-A', 'three'));
    expect(cache.coreCacheSize()).toMatchObject({ entries: 2, inFlight: 0 });
  });

  it('bounds the cache by entry count and by estimated bytes', () => {
    const max = TaskDetailPrefetchService.MAX_CORE_ENTRIES;
    for (let i = 0; i < max + 5; i++) {
      read('PROJ-A', `t${i}`);
      http.expectOne(coreRequest('PROJ-A', `t${i}`)).flush(makeCore('PROJ-A', `t${i}`));
    }
    expect(cache.coreCacheSize().entries).toBe(max);
    // Least recently used goes first.
    expect(cache.peekCore('PROJ-A', 't0')).toBeNull();
    expect(cache.peekCore('PROJ-A', `t${max + 4}`)).not.toBeNull();

    const big = 'x'.repeat(15 * 1024);
    for (let i = 0; i < 40; i++) {
      read('PROJ-B', `b${i}`);
      http.expectOne(coreRequest('PROJ-B', `b${i}`)).flush(
        makeCore('PROJ-B', `b${i}`, { prompt: { state: 'ready', text: big, originalBytes: big.length } }));
    }
    expect(cache.coreCacheSize().bytes).toBeLessThanOrEqual(TaskDetailPrefetchService.MAX_CORE_BYTES);
    expect(cache.coreCacheSize().entries).toBeLessThan(40);
  });

  it('revalidates with If-None-Match after reconnect and keeps the core on 304', () => {
    startHub();
    read('PROJ-A', 'a');
    http.expectOne(coreRequest('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'), { headers: { ETag: '"core-a"' } });

    hub.handlers?.reconnected?.();
    flushReconnectResync();
    expect(cache.isCoreCurrent('PROJ-A', 'a')).toBe(false);
    expect(cache.peekCore('PROJ-A', 'a')).not.toBeNull();

    read('PROJ-A', 'a');
    const revalidate = http.expectOne(coreRequest('PROJ-A', 'a'));
    expect(revalidate.request.headers.get('If-None-Match')).toBe('"core-a"');
    revalidate.flush(null, { status: 304, statusText: 'Not Modified', headers: { ETag: '"core-a"' } });
    expect(results[1]).toMatchObject({ state: 'ready', core: { id: 'a' } });
    expect(cache.isCoreCurrent('PROJ-A', 'a')).toBe(true);
  });

  it('a Git generation or unchanged grouped snapshot leaves every core current', () => {
    startHub();
    const info = { id: 'a', taskKey: 'C:/PROJ-A::a', state: '5-human-review', title: 'a', order: 0 } as TaskInfo;
    read('PROJ-A', 'a');
    read('PROJ-A', 'b');
    http.expectOne(coreRequest('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    http.expectOne(coreRequest('PROJ-A', 'b')).flush(makeCore('PROJ-A', 'b'));

    hub.handlers?.gitStateChanged?.({ gitStateAt: '2026-09-28T12:00:00Z' } as never);
    tasks.refresh(true);
    http.expectOne(grouped).flush({ humanReview: [info], gitStateAt: null, stale: false });
    http.expectOne('/api/runner/status').flush({ projects: {} });

    expect(cache.isCoreCurrent('PROJ-A', 'a')).toBe(true);
    expect(cache.isCoreCurrent('PROJ-A', 'b')).toBe(true);
  });

  it('a grouped snapshot revalidates cores when only runtime or pins change', () => {
    read('PROJ-A', 'runtime');
    read('PROJ-A', 'pins');
    read('PROJ-A', 'unchanged');
    for (const id of ['runtime', 'pins', 'unchanged']) {
      http.expectOne(coreRequest('PROJ-A', id)).flush(makeCore('PROJ-A', id));
    }

    const row = (id: string) => ({
      id, taskKey: `C:/PROJ-A::${id}`, state: '5-human-review', title: id, order: 0,
      projectName: 'PROJ-A', model: null, execution: null,
    });
    tasks.refresh(true);
    http.expectOne(grouped).flush({ humanReview: [
      { ...row('runtime'), execution: { status: 'running' } },
      { ...row('pins'), model: 'opus', modelExplicit: true },
      row('unchanged'),
    ], gitStateAt: null, stale: false });
    http.expectOne('/api/runner/status').flush({ projects: {} });

    expect(cache.isCoreCurrent('PROJ-A', 'runtime')).toBe(false);
    expect(cache.isCoreCurrent('PROJ-A', 'pins')).toBe(false);
    expect(cache.isCoreCurrent('PROJ-A', 'unchanged')).toBe(true);
  });

  it('a pushed row invalidates only its own core and patches its lane', () => {
    startHub();
    read('PROJ-A', 'a');
    read('PROJ-A', 'b');
    http.expectOne(coreRequest('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    http.expectOne(coreRequest('PROJ-A', 'b')).flush(makeCore('PROJ-A', 'b'));

    hub.handlers?.jobUpdated?.({
      id: 'a', taskKey: 'C:/PROJ-A::a', watchPath: 'C:/PROJ-A', state: '7-archive', title: 'a', order: 3,
    } as TaskInfo);

    expect(cache.isCoreCurrent('PROJ-A', 'a')).toBe(false);
    expect(cache.peekCore('PROJ-A', 'a')?.lane).toBe('7-archive');
    expect(cache.isCoreCurrent('PROJ-A', 'b')).toBe(true);
  });

  it('a mutation reply patches the moved lane and marks only that core stale', () => {
    read('PROJ-A', 'a');
    http.expectOne(coreRequest('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    tasks.moveJob('a', '2-ready', 'C:/PROJ-A').subscribe();
    http.expectOne((r) => r.url === '/api/tasks/a/move').flush({});
    expect(cache.peekCore('PROJ-A', 'a')?.lane).toBe('2-ready');
    expect(cache.isCoreCurrent('PROJ-A', 'a')).toBe(false);
  });

  it('a mutation reply without a project only revalidates same-slug cores, never patches their lane', () => {
    read('PROJ-A', 'fix-login');
    read('PROJ-B', 'fix-login');
    http.expectOne(coreRequest('PROJ-A', 'fix-login')).flush(makeCore('PROJ-A', 'fix-login'));
    http.expectOne(coreRequest('PROJ-B', 'fix-login')).flush(makeCore('PROJ-B', 'fix-login'));

    tasks.moveJob('fix-login', '2-ready').subscribe();
    http.expectOne((r) => r.url === '/api/tasks/fix-login/move').flush({});

    for (const project of ['PROJ-A', 'PROJ-B']) {
      expect(cache.peekCore(project, 'fix-login')?.lane).toBe('5-human-review');
      expect(cache.isCoreCurrent(project, 'fix-login')).toBe(false);
    }
  });

  it('a reply that raced a change of its task is not trusted as current', () => {
    startHub();
    read('PROJ-A', 'a');
    const inFlight = http.expectOne(coreRequest('PROJ-A', 'a'));
    hub.handlers?.jobUpdated?.({ id: 'a', taskKey: 'C:/PROJ-A::a', state: '2-ready', title: 'a', order: 0 } as TaskInfo);
    inFlight.flush(makeCore('PROJ-A', 'a'));
    expect(cache.isCoreCurrent('PROJ-A', 'a')).toBe(false);
  });

  it('evicts deleted tasks from both the core and full-detail stores', () => {
    startHub();
    read('PROJ-A', 'a');
    http.expectOne(coreRequest('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    cache.prefetch('a', 'C:/PROJ-A');
    http.expectOne((r) => r.url === '/api/tasks/a').flush({ info: { id: 'a', taskKey: 'C:/PROJ-A::a' } });

    hub.handlers?.jobDeleted?.({ id: 'a', watchPath: 'C:/PROJ-A' });

    expect(cache.peekCore('PROJ-A', 'a')).toBeNull();
    expect(cache.take('a', 'C:/PROJ-A')).toBeNull();
  });

  it('a delete during the first read keeps the late reply out of the cache', () => {
    startHub();
    read('PROJ-A', 'a');
    const inFlight = http.expectOne(coreRequest('PROJ-A', 'a'));
    hub.handlers?.jobDeleted?.({ id: 'a', watchPath: 'C:/PROJ-A' });
    inFlight.flush(makeCore('PROJ-A', 'a'));
    expect(results[0]).toEqual({ state: 'missing', core: null });
    expect(cache.peekCore('PROJ-A', 'a')).toBeNull();
  });

  it('answers missing on 404 and drops the stale entry', () => {
    startHub();
    read('PROJ-A', 'gone');
    http.expectOne(coreRequest('PROJ-A', 'gone')).flush(makeCore('PROJ-A', 'gone'));
    hub.handlers?.reconnected?.();
    flushReconnectResync();

    read('PROJ-A', 'gone');
    http.expectOne(coreRequest('PROJ-A', 'gone'))
      .flush({ state: 'missing' }, { status: 404, statusText: 'Not Found' });
    expect(results[1]).toEqual({ state: 'missing', core: null });
    expect(cache.peekCore('PROJ-A', 'gone')).toBeNull();
  });

  it('a denied read evicts every core of that project and nothing else', () => {
    read('PROJ-A', 'a1');
    read('PROJ-A', 'a2');
    read('PROJ-B', 'b1');
    http.expectOne(coreRequest('PROJ-A', 'a1')).flush(makeCore('PROJ-A', 'a1'));
    http.expectOne(coreRequest('PROJ-A', 'a2')).flush(makeCore('PROJ-A', 'a2'));
    http.expectOne(coreRequest('PROJ-B', 'b1')).flush(makeCore('PROJ-B', 'b1'));

    read('PROJ-A', 'a3');
    http.expectOne(coreRequest('PROJ-A', 'a3')).flush(null, { status: 403, statusText: 'Forbidden' });

    expect(results[3]).toEqual({ state: 'denied', core: null });
    expect(cache.peekCore('PROJ-A', 'a1')).toBeNull();
    expect(cache.peekCore('PROJ-A', 'a2')).toBeNull();
    expect(cache.peekCore('PROJ-B', 'b1')).not.toBeNull();
  });

  it('a denied read rejects other in-flight cores from that project', () => {
    read('PROJ-A', 'denied');
    read('PROJ-A', 'late');
    read('PROJ-B', 'late');
    const denied = http.expectOne(coreRequest('PROJ-A', 'denied'));
    const revoked = http.expectOne(coreRequest('PROJ-A', 'late'));
    const allowed = http.expectOne(coreRequest('PROJ-B', 'late'));

    denied.flush(null, { status: 403, statusText: 'Forbidden' });
    revoked.flush(makeCore('PROJ-A', 'late'));
    allowed.flush(makeCore('PROJ-B', 'late'));

    expect(results[0]).toEqual({ state: 'denied', core: null });
    expect(results[1]).toEqual({ state: 'missing', core: null });
    expect(cache.peekCore('PROJ-A', 'late')).toBeNull();
    expect(cache.isCoreCurrent('PROJ-B', 'late')).toBe(true);
  });

  it('a project leaving the visible registry evicts its cores', () => {
    read('PROJ-A', 'a');
    read('PROJ-B', 'b');
    http.expectOne(coreRequest('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    http.expectOne(coreRequest('PROJ-B', 'b')).flush(makeCore('PROJ-B', 'b'));
    cache.retainCoreProjects((projectId) => projectId === 'PROJ-B');
    expect(cache.peekCore('PROJ-A', 'a')).toBeNull();
    expect(cache.peekCore('PROJ-B', 'b')).not.toBeNull();
  });

  it('a project leaving the visible registry rejects its in-flight cores', () => {
    read('PROJ-A', 'late');
    read('PROJ-B', 'late');
    const revoked = http.expectOne(coreRequest('PROJ-A', 'late'));
    const allowed = http.expectOne(coreRequest('PROJ-B', 'late'));

    cache.retainCoreProjects((projectId) => projectId === 'PROJ-B');
    revoked.flush(makeCore('PROJ-A', 'late'));
    allowed.flush(makeCore('PROJ-B', 'late'));

    expect(results[0]).toEqual({ state: 'missing', core: null });
    expect(cache.peekCore('PROJ-A', 'late')).toBeNull();
    expect(cache.isCoreCurrent('PROJ-B', 'late')).toBe(true);
  });

  it('reports warming without caching or presenting a core as current', () => {
    read('PROJ-A', 'cold');
    http.expectOne(coreRequest('PROJ-A', 'cold'))
      .flush({ state: 'warming', jobId: 'cold', projectId: 'PROJ-A' }, { status: 202, statusText: 'Accepted' });
    expect(results[0]).toEqual({ state: 'warming', core: null });
    expect(cache.peekCore('PROJ-A', 'cold')).toBeNull();
  });

  it('clearing full-detail entries never discards cores', () => {
    read('PROJ-A', 'a');
    http.expectOne(coreRequest('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    cache.clear();
    expect(cache.isCoreCurrent('PROJ-A', 'a')).toBe(true);
  });

  it('a delete in one project never evicts the in-flight core of the same slug in another', () => {
    startHub();
    read('PROJ-A', 'fix-login');
    read('PROJ-B', 'fix-login');
    const alpha = http.expectOne(coreRequest('PROJ-A', 'fix-login'));
    const beta = http.expectOne(coreRequest('PROJ-B', 'fix-login'));

    hub.handlers?.jobDeleted?.({ id: 'fix-login', watchPath: 'C:/PROJ-A' });
    alpha.flush(makeCore('PROJ-A', 'fix-login'));
    beta.flush(makeCore('PROJ-B', 'fix-login'));

    expect(results[0]).toEqual({ state: 'missing', core: null });
    expect(results[1]).toMatchObject({ state: 'ready', core: { projectId: 'PROJ-B' } });
    expect(cache.peekCore('PROJ-A', 'fix-login')).toBeNull();
    expect(cache.isCoreCurrent('PROJ-B', 'fix-login')).toBe(true);
  });

  it('a delete that names only an ambiguous id revalidates instead of evicting', () => {
    read('PROJ-A', 'fix-login');
    read('PROJ-B', 'fix-login');
    http.expectOne(coreRequest('PROJ-A', 'fix-login')).flush(makeCore('PROJ-A', 'fix-login'));
    http.expectOne(coreRequest('PROJ-B', 'fix-login')).flush(makeCore('PROJ-B', 'fix-login'));
    tasks.jobs.set([
      { id: 'fix-login', taskKey: 'C:/PROJ-A::fix-login', watchPath: 'C:/PROJ-A' },
      { id: 'fix-login', taskKey: 'C:/PROJ-B::fix-login', watchPath: 'C:/PROJ-B' },
    ] as TaskInfo[]);

    tasks.deleteJob('fix-login').subscribe();
    http.expectOne((r) => r.method === 'DELETE' && r.url === '/api/tasks/fix-login').flush(null);

    for (const project of ['PROJ-A', 'PROJ-B']) {
      expect(cache.peekCore(project, 'fix-login')).not.toBeNull();
      expect(cache.isCoreCurrent(project, 'fix-login')).toBe(false);
    }
  });

  it('a delete by id alone resolves a unique board record and evicts exactly it', () => {
    read('PROJ-A', 'fix-login');
    read('PROJ-B', 'fix-login');
    http.expectOne(coreRequest('PROJ-A', 'fix-login')).flush(makeCore('PROJ-A', 'fix-login'));
    http.expectOne(coreRequest('PROJ-B', 'fix-login')).flush(makeCore('PROJ-B', 'fix-login'));
    tasks.jobs.set([{ id: 'fix-login', taskKey: 'C:/PROJ-A::fix-login', watchPath: 'C:/PROJ-A' }] as TaskInfo[]);

    tasks.deleteJob('fix-login').subscribe();
    http.expectOne((r) => r.method === 'DELETE' && r.url === '/api/tasks/fix-login').flush(null);

    expect(cache.peekCore('PROJ-A', 'fix-login')).toBeNull();
    expect(cache.isCoreCurrent('PROJ-B', 'fix-login')).toBe(true);
  });

  it('publishes a pushed delete after the board store removed the task', () => {
    startHub();
    const row = { id: 'a', taskKey: 'C:/PROJ-A::a', watchPath: 'C:/PROJ-A', state: '2-ready' } as TaskInfo;
    tasks.jobs.set([row]);
    tasks.grouped.set({ ready: [row] } as never);
    const seen: { jobs: number; lane: number }[] = [];
    const subscription = tasks.taskEvents.subscribe((event) => {
      if (event.kind === 'deleted') seen.push({ jobs: tasks.jobs().length, lane: tasks.grouped().ready.length });
    });

    hub.handlers?.jobDeleted?.({ id: 'a', watchPath: 'C:/PROJ-A' });
    subscription.unsubscribe();

    expect(seen).toEqual([{ jobs: 0, lane: 0 }]);
  });
});
