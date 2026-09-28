import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpRequest, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import type { GroupedJobs, RegistryWorkspaceListItem, TaskInfo } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import { JobsHubClient, type JobsHubHandlers } from '../../../services/jobs-hub-client.service';
import { ProjectLookupService } from '../../../services/project-lookup.service';
import { TaskSelectionService } from './task-selection.service';
import { TaskDetailPrefetchService } from './task-detail-prefetch.service';
import type { TaskCore } from '../../../models/task-core.model';

const projectNames: Record<string, string> = { 'PROJ-A': 'Alpha', 'PROJ-B': 'Beta' };

function makeCore(projectId: string, id: string, overrides: Partial<TaskCore> = {}): TaskCore {
  return {
    state: 'ready',
    projectId,
    projectName: projectNames[projectId],
    id,
    // The server's core taskKey is the board record's taskKey.
    taskKey: `C:/${projectNames[projectId]}::${id}`,
    key: null,
    title: `${projectNames[projectId]} ${id}`,
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

class JobsHubClientStub {
  readonly connected = signal(false);
  handlers: JobsHubHandlers | null = null;
  start(handlers: JobsHubHandlers): void { this.handlers = handlers; }
  stop(): void { return undefined; }
}

const grouped = (r: HttpRequest<unknown>) => r.url === '/api/tasks/grouped';
const anyCore = (r: HttpRequest<unknown>) => r.url.endsWith('/core');
const core = (project: string, id: string) => (r: HttpRequest<unknown>) =>
  r.url === `/api/tasks/${id}/core` && r.params.get('project') === project;
const detail = (id: string) => (r: HttpRequest<unknown>) => r.url === `/api/tasks/${id}`;

function task(project: 'Alpha' | 'Beta', id: string, order: number): TaskInfo {
  const watchPath = `C:/${project}`;
  return {
    id, taskKey: `${watchPath}::${id}`, key: `${project.toUpperCase()}-${order}`, title: `${project} ${id}`,
    state: '5-human-review', order, watchPath, projectName: project, model: 'sonnet', modelExplicit: true,
    cliType: 'claude', useOwnSession: null, execution: null,
  } as unknown as TaskInfo;
}

/**
 * AGT-2956 navigation contract: selection seeds the core from the resident
 * board, reads only missing cores (coalesced), warms the next two pager cores
 * after paint and never asks for the grouped board.
 */
describe('TaskSelectionService · board record reuse', () => {
  let selection: TaskSelectionService;
  let tasks: TaskService;
  let cache: TaskDetailPrefetchService;
  let hub: JobsHubClientStub;
  let http: HttpTestingController;

  const lane = ['a', 'b', 'c', 'd'].map((id, i) => task('Alpha', id, i));
  const betaTwin = task('Beta', 'a', 0);

  /** One stubbed frame plus the lookahead's trailing macrotask. */
  const afterPaint = async () => {
    for (let i = 0; i < 3; i++) await new Promise(resolve => setTimeout(resolve, 0));
  };

  beforeEach(async () => {
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => setTimeout(() => cb(0), 0));
    sessionStorage.clear();
    localStorage.clear();
    history.replaceState(null, '', '/');
    await TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: JobsHubClient, useClass: JobsHubClientStub },
      ],
    }).compileComponents();
    selection = TestBed.inject(TaskSelectionService);
    tasks = TestBed.inject(TaskService);
    cache = TestBed.inject(TaskDetailPrefetchService);
    hub = TestBed.inject(JobsHubClient) as unknown as JobsHubClientStub;
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(ProjectLookupService).setWorkspaces([{
      id: 'WS', displayName: 'WS', projects: [
        { id: 'PROJ-A', displayName: 'Alpha', shortCode: 'ALP', storageLocation: 'C:/Alpha' },
        { id: 'PROJ-B', displayName: 'Beta', shortCode: 'BET', storageLocation: 'C:/Beta' },
      ],
    }] as unknown as RegistryWorkspaceListItem[]);
    tasks.grouped.set({ humanReview: [...lane, betaTwin] } as unknown as GroupedJobs);
    tasks.jobs.set([...lane, betaTwin]);
    tasks.startLiveUpdates();
    http.expectOne('/api/projects/settings').flush({});
  });

  afterEach(() => {
    try {
      tasks.stopLiveUpdates();
      // Leftover full-detail prefetches are not what these cases assert on.
      http.match(r => !r.url.endsWith('/core')).forEach(r => r.flush({ info: {} }));
      http.verify();
    } finally {
      vi.unstubAllGlobals();
      TestBed.resetTestingModule();
      // The lane pager persists its snapshot; never leak it into later specs.
      sessionStorage.clear();
      localStorage.clear();
      history.replaceState(null, '', '/');
    }
  });

  function open(info: TaskInfo): void {
    selection.openDetail(info);
    TestBed.tick();
  }

  function answerDetail(info: TaskInfo): void {
    for (const request of http.match(detail(info.id))) request.flush({ info });
  }

  it('board -> A -> B -> board reads each core once and never the grouped board', async () => {
    const board = tasks.grouped();

    open(lane[0]);
    // Seeded synchronously from the board record, before any reply.
    expect(selection.selectedCore()).toMatchObject({
      state: 'seeded', core: null,
      seed: { project: 'PROJ-A', id: 'a', lane: '5-human-review', pins: { model: 'sonnet', modelExplicit: true } },
    });
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    expect(selection.selectedCore()?.state).toBe('ready');
    answerDetail(lane[0]);
    await afterPaint();
    for (const r of http.match(anyCore)) r.flush(makeCore('PROJ-A', r.request.url.split('/')[3]));

    open(lane[1]);
    // B was warmed by the lookahead: current, no request.
    expect(selection.selectedCore()).toMatchObject({ state: 'ready', core: { id: 'b' } });
    http.expectNone(core('PROJ-A', 'b'));
    answerDetail(lane[1]);

    selection.closeDetail();
    await afterPaint();
    expect(selection.selectedCore()).toBeNull();
    http.expectNone(grouped);
    // The board store was never replaced: filters, sort and scroll render
    // from the same resident snapshot.
    expect(tasks.grouped()).toBe(board);
  });

  it('pager steps reuse visited and lookahead cores without a request', async () => {
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    answerDetail(lane[0]);
    await afterPaint();
    const lookahead = http.match(anyCore);
    expect(lookahead.map(r => r.request.url)).toEqual(['/api/tasks/b/core', '/api/tasks/c/core']);
    lookahead.forEach(r => r.flush(makeCore('PROJ-A', r.request.url.split('/')[3])));

    selection.pagerStep(1);
    expect(selection.selectedCore()).toMatchObject({ state: 'ready', core: { id: 'b' } });
    answerDetail(lane[1]);
    await afterPaint();
    // Window is now c, d: c is resident, only d is requested.
    http.expectOne(core('PROJ-A', 'd')).flush(makeCore('PROJ-A', 'd'));

    selection.pagerStep(-1);
    expect(selection.selectedCore()).toMatchObject({ state: 'ready', core: { id: 'a' } });
    answerDetail(lane[0]);
    await afterPaint();
    http.expectNone(anyCore);
    http.expectNone(grouped);
  });

  it('A -> B -> A with late replies keeps A and issues no duplicate core read', () => {
    open(lane[0]);
    const coreA = http.expectOne(core('PROJ-A', 'a'));
    open(lane[3]);
    const coreD = http.expectOne(core('PROJ-A', 'd'));
    open(lane[0]);
    // A's first read is still in flight and is joined, not repeated.
    http.expectNone(core('PROJ-A', 'a'));

    coreD.flush(makeCore('PROJ-A', 'd'));
    expect(selection.selectedCore()?.seed.id).toBe('a');
    expect(selection.selectedCore()?.core).toBeNull();
    coreA.flush(makeCore('PROJ-A', 'a'));
    expect(selection.selectedCore()).toMatchObject({ state: 'ready', core: { id: 'a' } });
    // The late D reply still became a retained visited core.
    expect(cache.isCoreCurrent('PROJ-A', 'd')).toBe(true);
    for (const t of [lane[0], lane[3]]) answerDetail(t);
    http.expectNone(grouped);
  });

  it('identical slugs in two projects select two different cores', () => {
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a', { title: 'alpha a' }));
    open(betaTwin);
    expect(selection.selectedCore()).toMatchObject({ state: 'seeded', core: null, seed: { project: 'PROJ-B' } });
    http.expectOne(core('PROJ-B', 'a')).flush(makeCore('PROJ-B', 'a', { title: 'beta a' }));
    expect(selection.selectedCore()?.core?.title).toBe('beta a');
    answerDetail(lane[0]);
  });

  it('reconnect revalidates the visible core conditionally without dropping it', () => {
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'), { headers: { ETag: '"core-a"' } });
    answerDetail(lane[0]);

    hub.handlers?.reconnected?.();
    // Reconnect is an actual invalidation: the board resync still runs.
    http.expectOne(grouped).flush({ humanReview: [...lane, betaTwin], gitStateAt: null, stale: false });
    http.expectOne('/api/runner/status').flush({ projects: {} });
    expect(selection.selectedCore()).toMatchObject({ state: 'stale', core: { id: 'a' } });
    const revalidate = http.expectOne(core('PROJ-A', 'a'));
    expect(revalidate.request.headers.get('If-None-Match')).toBe('"core-a"');
    revalidate.flush(null, { status: 304, statusText: 'Not Modified' });
    expect(selection.selectedCore()).toMatchObject({ state: 'ready', core: { id: 'a' } });
  });

  it('a push during the selected read triggers one conditional revalidation', () => {
    open(lane[0]);
    const first = http.expectOne(core('PROJ-A', 'a'));
    hub.handlers?.jobUpdated?.({ ...lane[0], title: 'renamed' });
    first.flush(makeCore('PROJ-A', 'a'), { headers: { ETag: '"core-a1"' } });
    expect(selection.selectedCore()).toMatchObject({ state: 'stale', core: { id: 'a' } });
    const revalidate = http.expectOne(core('PROJ-A', 'a'));
    expect(revalidate.request.headers.get('If-None-Match')).toBe('"core-a1"');
    revalidate.flush(makeCore('PROJ-A', 'a', { title: 'renamed' }), { headers: { ETag: '"core-a2"' } });
    expect(selection.selectedCore()).toMatchObject({ state: 'ready', core: { title: 'renamed' }, seed: { title: 'renamed' } });
    answerDetail(lane[0]);
  });

  it('a deleted selected task clears its core content', () => {
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    answerDetail(lane[0]);

    hub.handlers?.jobDeleted?.({ id: 'a', watchPath: 'C:/Alpha' });
    expect(selection.selectedCore()).toMatchObject({ core: null });
    http.expectOne(core('PROJ-A', 'a')).flush({ state: 'missing' }, { status: 404, statusText: 'Not Found' });
    expect(selection.selectedCore()).toMatchObject({ state: 'missing', core: null });
  });

  it('denied access shows no core and evicts the project', () => {
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(null, { status: 403, statusText: 'Forbidden' });
    expect(selection.selectedCore()).toMatchObject({ state: 'denied', core: null });
    answerDetail(lane[0]);
  });

  it('a project removed from the registry evicts the visible core', () => {
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    answerDetail(lane[0]);

    TestBed.inject(ProjectLookupService).setWorkspaces([{
      id: 'WS', displayName: 'WS', projects: [{ id: 'PROJ-B', displayName: 'Beta', storageLocation: 'C:/Beta' }],
    }] as unknown as RegistryWorkspaceListItem[]);
    TestBed.tick();
    expect(cache.peekCore('PROJ-A', 'a')).toBeNull();
    expect(selection.selectedCore()?.core).toBeNull();
    // The handle no longer resolves to a visible project; whatever the view
    // requests next is answered by the server's access check.
    for (const r of http.match(anyCore)) r.flush(null, { status: 403, statusText: 'Forbidden' });
  });
});
