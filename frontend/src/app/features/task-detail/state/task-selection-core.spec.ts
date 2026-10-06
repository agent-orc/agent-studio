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
    watchPath: `C:/${projectNames[projectId]}`,
    folderPath: `C:/${projectNames[projectId]}/${id}`,
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
    coreVersion: '1',
    ...overrides,
  };
}

class JobsHubClientStub {
  readonly connected = signal(false);
  handlers: JobsHubHandlers | null = null;
  start(handlers: JobsHubHandlers): void { this.handlers = handlers; }
  stop(): void { return undefined; }
}

const grouped = (r: HttpRequest<unknown>) => r.url === '/api/v1/studio/board';
const anyCore = (r: HttpRequest<unknown>) => r.url.endsWith('/core');
const core = (project: string, id: string) => (r: HttpRequest<unknown>) =>
  r.url === `/api/tasks/${id}/core` && r.params.get('project') === project;
const legacyDetail = (r: HttpRequest<unknown>) => /\/api\/v1\/projects\/[^/]+\/tasks\/[^/]+$/.test(r.url);
const anyResource = (r: HttpRequest<unknown>) => r.url.includes('/details/');

function task(project: 'Alpha' | 'Beta', id: string, order: number): TaskInfo {
  const watchPath = `C:/${project}`;
  return {
    id, taskKey: `${watchPath}::${id}`, key: `${project.toUpperCase()}-${order}`, title: `${project} ${id}`,
    state: '5-human-review', order, watchPath, projectName: project, model: 'sonnet', modelExplicit: true,
    cliType: 'claude', useOwnSession: null, execution: null,
  } as unknown as TaskInfo;
}

/**
 * AGT-2956 navigation contract on the AGT-2955 core-first selection: a task
 * switch reads only missing cores through the shared cache, warms the next
 * two pager cores, never asks for the grouped board or the legacy full
 * detail, and revalidates the painted core when the board store says it
 * changed.
 */
describe('TaskSelectionService · board record reuse', () => {
  let selection: TaskSelectionService;
  let tasks: TaskService;
  let cache: TaskDetailPrefetchService;
  let hub: JobsHubClientStub;
  let http: HttpTestingController;

  const lane = ['a', 'b', 'c', 'd'].map((id, i) => task('Alpha', id, i));
  const betaTwin = task('Beta', 'a', 0);

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
      selection.closeDetail();
      // Enrichment of the selected task is covered by task-selection-url.service.spec.ts.
      http.match(anyResource).forEach(r => r.flush(null, { status: 503, statusText: 'Unavailable' }));
      http.expectNone(legacyDetail);
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

  /** Answer every live lookahead core; aborted lookahead is dropped. */
  function answerLookahead(): string[] {
    const requests = http.match(anyCore).filter(r => !r.cancelled);
    for (const r of requests) r.flush(makeCore(r.request.params.get('project')!, r.request.url.split('/')[3]));
    return requests.map(r => r.request.url);
  }

  it('board -> A -> B -> board reads each core once and never the grouped board', () => {
    const board = tasks.grouped();

    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    expect(selection.selectedCore()).toMatchObject({ id: 'a', projectId: 'PROJ-A' });
    expect(answerLookahead()).toEqual(['/api/tasks/b/core', '/api/tasks/c/core']);

    open(lane[1]);
    // B was warmed by the lookahead: painted at once, no request.
    expect(selection.selectedCore()).toMatchObject({ id: 'b' });
    http.expectNone(core('PROJ-A', 'b'));
    answerLookahead();

    selection.closeDetail();
    expect(selection.selectedCore()).toBeNull();
    http.expectNone(grouped);
    // The board store was never replaced: filters, sort and scroll render
    // from the same resident snapshot.
    expect(tasks.grouped()).toBe(board);
  });

  it('pager steps reuse visited and lookahead cores without a request', () => {
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    answerLookahead();

    selection.pagerStep(1);
    TestBed.tick();
    expect(selection.selectedCore()).toMatchObject({ id: 'b' });
    // Window is now c, d: c is resident, only d is requested.
    expect(answerLookahead()).toEqual(['/api/tasks/d/core']);

    selection.pagerStep(-1);
    TestBed.tick();
    expect(selection.selectedCore()).toMatchObject({ id: 'a' });
    http.expectNone(anyCore);
    http.expectNone(grouped);
  });

  it('A -> D -> A aborts the superseded reads and paints only A', () => {
    open(lane[0]);
    const firstA = http.expectOne(core('PROJ-A', 'a'));
    answerLookahead();
    open(lane[3]);
    const coreD = http.expectOne(core('PROJ-A', 'd'));
    expect(firstA.cancelled).toBe(true);
    open(lane[0]);
    expect(coreD.cancelled).toBe(true);

    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    expect(selection.selectedCore()).toMatchObject({ id: 'a' });
    expect(cache.peekCore('PROJ-A', 'd')).toBeNull();
    answerLookahead();
    http.expectNone(grouped);
  });

  it('identical slugs in two projects select two different cores', () => {
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a', { title: 'alpha a' }));
    answerLookahead();
    open(betaTwin);
    expect(selection.selectedCore()).toBeNull();
    http.expectOne(core('PROJ-B', 'a')).flush(makeCore('PROJ-B', 'a', { title: 'beta a' }));
    expect(selection.selectedCore()?.title).toBe('beta a');
    expect(cache.peekCore('PROJ-A', 'a')?.title).toBe('alpha a');
  });

  it('reconnect revalidates the visible core conditionally without dropping it', () => {
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a', {
      pins: { model: 'sonnet', modelExplicit: true, cliType: 'claude', thinkingLevelExplicit: false,
        allowWebAccess: false, noBranchExpected: false },
    }), { headers: { ETag: '"core-a"' } });
    answerLookahead();
    const painted = selection.selectedCore();

    hub.handlers?.reconnected?.();
    // Reconnect is an actual invalidation: the board resync still runs.
    http.expectOne(grouped).flush({ humanReview: [...lane, betaTwin], gitStateAt: null, stale: false });
    http.expectOne('/api/v1/studio/runner/status').flush({ projects: {} });
    expect(selection.selectedCore()).toBe(painted);
    const revalidate = http.match(core('PROJ-A', 'a'));
    expect(revalidate).toHaveLength(1);
    expect(revalidate[0].request.headers.get('If-None-Match')).toBe('"core-a"');
    revalidate[0].flush(null, { status: 304, statusText: 'Not Modified' });
    expect(selection.selectedCore()).toMatchObject({ id: 'a' });
    expect(cache.isCoreCurrent('PROJ-A', 'a')).toBe(true);
    answerLookahead();
  });

  it('a grouped pin change revalidates the selected core once', () => {
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a', {
      pins: { model: 'sonnet', modelExplicit: true, cliType: 'claude', thinkingLevelExplicit: false,
        allowWebAccess: false, noBranchExpected: false },
    }), { headers: { ETag: '"core-a1"' } });
    answerLookahead();

    tasks.refresh(true);
    http.expectOne(grouped).flush({ humanReview: [{ ...lane[0], model: 'opus' }, ...lane.slice(1), betaTwin],
      gitStateAt: null, stale: false });
    http.expectOne('/api/v1/studio/runner/status').flush({ projects: {} });

    const revalidate = http.expectOne(core('PROJ-A', 'a'));
    expect(revalidate.request.headers.get('If-None-Match')).toBe('"core-a1"');
    revalidate.flush(makeCore('PROJ-A', 'a', {
      pins: { model: 'opus', modelExplicit: true, cliType: 'claude', thinkingLevelExplicit: false,
        allowWebAccess: false, noBranchExpected: false },
    }));
    expect(selection.selectedCore()?.pins.model).toBe('opus');
    http.expectNone(core('PROJ-A', 'a'));
  });

  it('a push during the selected read triggers one conditional revalidation', () => {
    open(lane[0]);
    const first = http.expectOne(core('PROJ-A', 'a'));
    hub.handlers?.jobUpdated?.({ ...lane[0], title: 'renamed' });
    first.flush(makeCore('PROJ-A', 'a'), { headers: { ETag: '"core-a1"' } });
    // The raced reply paints, but is not trusted as current.
    expect(selection.selectedCore()).toMatchObject({ id: 'a', title: 'Alpha a' });
    const revalidate = http.expectOne(core('PROJ-A', 'a'));
    expect(revalidate.request.headers.get('If-None-Match')).toBe('"core-a1"');
    revalidate.flush(makeCore('PROJ-A', 'a', { title: 'renamed' }), { headers: { ETag: '"core-a2"' } });
    expect(selection.selectedCore()?.title).toBe('renamed');
    http.expectNone(core('PROJ-A', 'a'));
    answerLookahead();
  });

  it('a deleted selected task revokes its core content', () => {
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    answerLookahead();

    hub.handlers?.jobDeleted?.({ id: 'a', watchPath: 'C:/Alpha' });
    expect(cache.peekCore('PROJ-A', 'a')).toBeNull();
    http.expectOne(core('PROJ-A', 'a')).flush({ state: 'missing' }, { status: 404, statusText: 'Not Found' });
    expect(selection.selectedCore()).toBeNull();
    expect(selection.selected()).toBeNull();
    expect(selection.detailLoadError()).not.toBeNull();
  });

  it('denied access shows no core and evicts the project', () => {
    cache.getCore('PROJ-A', 'b', lane[1].taskKey).subscribe();
    http.expectOne(core('PROJ-A', 'b')).flush(makeCore('PROJ-A', 'b'));
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(null, { status: 403, statusText: 'Forbidden' });
    expect(selection.selectedCore()).toBeNull();
    expect(selection.detailLoadError()).not.toBeNull();
    expect(cache.peekCore('PROJ-A', 'b')).toBeNull();
    for (const r of http.match(anyCore)) r.flush(null, { status: 403, statusText: 'Forbidden' });
  });

  it('a project removed from the registry evicts and revokes the visible core', () => {
    open(lane[0]);
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a'));
    answerLookahead();

    TestBed.inject(ProjectLookupService).setWorkspaces([{
      id: 'WS', displayName: 'WS', projects: [{ id: 'PROJ-B', displayName: 'Beta', storageLocation: 'C:/Beta' }],
    }] as unknown as RegistryWorkspaceListItem[]);
    TestBed.tick();
    expect(cache.peekCore('PROJ-A', 'a')).toBeNull();
    // The revalidation is answered by the server's access check.
    for (const r of http.match(anyCore)) r.flush(null, { status: 403, statusText: 'Forbidden' });
    expect(selection.selectedCore()).toBeNull();
  });

  it('removing one project ID evicts its core even when another project has the same display name', () => {
    cache.getCore('PROJ-A', 'a', lane[0].taskKey).subscribe();
    cache.getCore('PROJ-B', 'a', betaTwin.taskKey).subscribe();
    http.expectOne(core('PROJ-A', 'a')).flush(makeCore('PROJ-A', 'a', { projectName: 'Shared' }));
    http.expectOne(core('PROJ-B', 'a')).flush(makeCore('PROJ-B', 'a', { projectName: 'Shared' }));

    TestBed.inject(ProjectLookupService).setWorkspaces([{
      id: 'WS', displayName: 'WS', projects: [{ id: 'PROJ-B', displayName: 'Shared', storageLocation: 'C:/Beta' }],
    }] as unknown as RegistryWorkspaceListItem[]);
    TestBed.tick();

    expect(cache.peekCore('PROJ-A', 'a')).toBeNull();
    expect(cache.peekCore('PROJ-B', 'a')).not.toBeNull();
  });
});
