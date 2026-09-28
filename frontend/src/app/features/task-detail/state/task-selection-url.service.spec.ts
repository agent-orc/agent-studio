import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import type { RegistryWorkspaceListItem, TaskDetail, TaskInfo } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import { ProjectLookupService } from '../../../services/project-lookup.service';
import { TaskSelectionService } from './task-selection.service';
import { LanePagerService } from './lane-pager.service';
import { TaskDetailPrefetchService } from './task-detail-prefetch.service';
import type { TaskCore } from '../../../models/task-core.model';

describe('TaskSelectionService · stable task URLs', () => {
  let selection: TaskSelectionService;
  let tasks: TaskService;
  let projects: ProjectLookupService;
  let http: HttpTestingController;

  const info = {
    id: 'human-readable-slug',
    key: 'AGT-2124',
    displayKey: 'AGT-2124',
    taskKey: 'C:\\private\\project::human-readable-slug',
    title: 'Stable URL task',
    state: '5-human-review',
    order: 1,
    watchPath: 'C:\\private\\project',
    projectName: 'Agent Studio',
  } as unknown as TaskInfo;

  const detail = { info } as unknown as TaskDetail;
  const coreFor = (task: TaskInfo, projectId = 'PROJ-001') => ({
    state: 'ready', projectId, projectName: task.projectName, id: task.id,
    taskKey: task.taskKey, key: task.key, title: task.title,
    kind: 'task', taskType: 'chore', lane: task.state, archiveState: null,
    enteredLaneAt: '2026-09-28T00:00:00Z', order: task.order, mode: 'coding',
    released: false, pendingIntent: false, coreVersion: '1',
    pins: { model: null, modelExplicit: false, thinkingLevel: null,
      thinkingLevelExplicit: false, cliType: null, contextMode: null,
      useOwnSession: null, allowWebAccess: false, noBranchExpected: false },
    blocking: { dependencyBlocked: false, dependencyState: 'ready', dependencies: [] },
    runtime: { attemptId: null, runnerId: null, runnerName: null, hostname: null,
      executionStatus: null, location: 'none', heartbeatAt: null, leaseState: 'none', leaseId: null },
    runtimeVersion: 'v1',
    statusSummary: { state: 'missing', text: null, originalBytes: 0, hash: null, cursor: null },
    prompt: { state: 'missing', text: null, originalBytes: 0, hash: null, cursor: null, continuationUrl: null },
    timeline: { state: 'missing', events: [], cursor: null, continuationUrl: null },
  });

  beforeEach(async () => {
    sessionStorage.clear();
    history.replaceState(null, '', '/');
    await TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    selection = TestBed.inject(TaskSelectionService);
    tasks = TestBed.inject(TaskService);
    projects = TestBed.inject(ProjectLookupService);
    http = TestBed.inject(HttpTestingController);
  });

  const registry = (...entries: { id: string; shortCode: string | null; storageLocation: string }[]) =>
    projects.setWorkspaces(([{ projects: entries.map(entry => ({ ...entry,
      displayName: entry.id })) }]) as unknown as RegistryWorkspaceListItem[]);
  const afterPaint = () => new Promise(resolve => setTimeout(resolve, 40));
  const documentReply = (task: TaskInfo, name: string, coreVersion = '1') => ({
    id: task.id, taskKey: task.taskKey, projectId: 'Agent Studio', attemptId: null,
    coreVersion, resource: 'documents', version: 'v1', computedAt: null, state: 'ready',
    data: { name, markdown: `${name} markdown`, summaryState: null }, reason: null,
  });

  afterEach(() => {
    http.verify();
    history.replaceState(null, '', '/');
    TestBed.resetTestingModule();
  });

  it('round-trips a canonical key without a watch path or URL normalization', () => {
    projects.setWorkspaces(([{ projects: [{ id: 'PROJ-001', shortCode: 'AGT',
      displayName: 'Agent Studio', storageLocation: 'C:\\private\\project' }] }]
      ) as unknown as RegistryWorkspaceListItem[]);
    history.replaceState(null, '', '/studio?view=git#/tasks/AGT-2124');

    selection.restoreFromUrl();

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2124/core'));
    expect(request.request.params.has('watchPath')).toBe(false);
    request.flush(coreFor(info));

    expect(selection.selectedCore()?.key).toBe('AGT-2124');
    expect(`${location.pathname}${location.search}${location.hash}`)
      .toBe('/studio?view=git#/tasks/AGT-2124');
  });

  it('accepts a legacy locator once and replaces it with the stable key', () => {
    history.replaceState(
      null,
      '',
      '/?job=human-readable-slug&watchPath=C%3A%5Cprivate%5Cproject&view=git#diff',
    );

    selection.restoreFromUrl();

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'));
    expect(request.request.params.get('watchPath')).toBe('C:\\private\\project');
    request.flush(detail);

    expect(`${location.pathname}${location.search}${location.hash}`)
      .toBe('/?view=git#/tasks/AGT-2124&diff');
    expect(location.href).not.toContain('watchPath');
  });

  it('resolves a public task URL after projects arrive without prior board state', async () => {
    history.replaceState(null, '', '/#/tasks/AGT-2124');
    selection.restoreFromUrl();
    http.expectNone(req => req.url.includes('/api/tasks/AGT-2124'));
    projects.setWorkspaces(([{ projects: [{ id: 'PROJ-001', shortCode: 'AGT',
      displayName: 'Agent Studio', storageLocation: 'C:\\private\\project' }] }]
      ) as unknown as RegistryWorkspaceListItem[]);
    TestBed.tick();
    await Promise.resolve();
    http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2124/core'))
      .flush(coreFor(info));
    expect(selection.selectedCore()?.taskKey).toBe(info.taskKey);
  });

  it('uses pushState for user navigation and clears selection on browser Back', () => {
    const push = vi.spyOn(history, 'pushState');

    selection.openDetail(info);

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug/core'));
    expect(request.request.params.has('watchPath')).toBe(false);
    expect(request.request.params.get('project')).toBe('Agent Studio');
    request.flush(coreFor(info, 'Agent Studio'));
    expect(push).toHaveBeenCalled();
    expect(location.hash).toBe('#/tasks/AGT-2124');

    history.replaceState(null, '', '/?view=board');
    window.dispatchEvent(new PopStateEvent('popstate'));

    expect(selection.selected()).toBeNull();
    expect(selection.browserRouteCleared()).toBe(1);
    expect(location.search).toBe('?view=board');
  });

  it('pushes an advance and restores its prior task, lane anchor, and pager position on popstate', () => {
    projects.setWorkspaces(([{ projects: [{ id: 'PROJ-001', shortCode: 'AGT',
      displayName: 'Agent Studio', storageLocation: 'C:\\private\\project' }] }]
      ) as unknown as RegistryWorkspaceListItem[]);
    const nextInfo = {
      ...info,
      id: 'next-task',
      key: 'AGT-2125',
      displayKey: 'AGT-2125',
      taskKey: 'C:\\private\\project::next-task',
      order: 2,
    } as TaskInfo;
    const pager = TestBed.inject(LanePagerService);
    pager.capture(info.state, [info, nextInfo], info.taskKey);
    selection.selected.set(detail);
    selection.triageLaneState = info.state;
    selection.syncTaskUrl(info, 'replace');
    const firstUrl = `${location.pathname}${location.search}${location.hash}`;
    const firstState = structuredClone(history.state);
    const push = vi.spyOn(history, 'pushState');

    expect(selection.advanceAfterMutation(info.taskKey)).toBe(true);
    expect(push).toHaveBeenCalled();
    expect(location.hash).toBe('#/tasks/AGT-2125');
    http.expectOne(req => req.url.endsWith('/api/tasks/next-task/core'))
      .flush(coreFor(nextInfo));
    expect(pager.position()).toBe(1);
    expect(pager.total()).toBe(1);

    history.replaceState(firstState, '', firstUrl);
    window.dispatchEvent(new PopStateEvent('popstate', { state: firstState }));
    const archivedInfo = { ...info, state: '7-archive' } as TaskInfo;
    http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2124/core'))
      .flush(coreFor(archivedInfo));

    expect(selection.selectedCore()).toMatchObject({ key: 'AGT-2124', lane: '7-archive' });
    expect(selection.triageLaneState).toBe('5-human-review');
    expect(pager.position()).toBe(1);
    expect(pager.total()).toBe(2);
    // The restored lane mismatch is suppressed once. A later external move
    // of the same task is no longer mistaken for the history reconciliation.
    expect(selection.consumeBrowserHistorySelection(info.taskKey, archivedInfo.state)).toBe(true);
    expect(selection.triageLaneState).toBe('7-archive');
    expect(selection.consumeBrowserHistorySelection(info.taskKey, archivedInfo.state)).toBe(false);
    expect(selection.consumeTaskTabReplacement(info.taskKey)).toBe(true);
  });

  it('clears the browser-history reconciliation marker when another task is selected', () => {
    projects.setWorkspaces(([{ projects: [{ id: 'PROJ-001', shortCode: 'AGT',
      displayName: 'Agent Studio', storageLocation: 'C:\\private\\project' }] }]
      ) as unknown as RegistryWorkspaceListItem[]);
    history.replaceState(null, '', '/#/tasks/AGT-2124');
    selection.restoreFromUrl(true);
    http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2124/core')).flush(coreFor(info));

    const nextInfo = {
      ...info,
      id: 'next-task',
      key: 'AGT-2125',
      displayKey: 'AGT-2125',
      taskKey: 'C:\\private\\project::next-task',
    } as TaskInfo;
    selection.selectResolvedDetail({ info: nextInfo } as TaskDetail, 'replace');

    expect(selection.consumeBrowserHistorySelection(info.taskKey, info.state)).toBe(false);
  });

  it('publishes the board snapshot before the detail request resolves', () => {
    selection.openDetail(info);

    expect(selection.detailPreview()).toBe(info);
    expect(selection.selected()).toBeNull();
    expect(selection.detailLoading()).toBe(true);

    http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug/core'))
      .flush(coreFor(info, 'Agent Studio'));
    expect(selection.selectedCore()?.id).toBe(info.id);
    expect(selection.detailPreview()?.id).toBe(info.id);
    expect(selection.detailLoading()).toBe(false);
  });

  it('turns a stalled detail request into an honest retryable timeout', async () => {
    vi.useFakeTimers();
    try {
      selection.openDetail(info);
      const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug/core'));

      await vi.advanceTimersByTimeAsync(15_000);

      expect(request.cancelled).toBe(true);
      expect(selection.detailLoading()).toBe(false);
      expect(selection.detailLoadError()?.message).toContain('timed out');
      expect(selection.detailPreview()).toBe(info);
      expect(selection.selected()).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it('rehydrates a search tab through live task identity instead of its stale lane path', () => {
    const staleTaskKey = 'C:\\private\\project\\5e-escalated\\human-readable-slug::human-readable-slug';
    const staleInfo = { ...info, taskKey: staleTaskKey };
    tasks.jobs.set([staleInfo]);

    selection.openDetailByTaskKey(staleTaskKey);

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug/core'));
    expect(request.request.params.get('project')).toBe('Agent Studio');
    expect(request.request.params.has('watchPath')).toBe(false);
    request.flush(coreFor(staleInfo, 'Agent Studio'));

    expect(selection.detailLoading()).toBe(false);
    expect(selection.detailLoadError()).toBeNull();
    expect(selection.selectedCore()?.id).toBe('human-readable-slug');
  });

  it('resolves a cold stale-lane tab through its containing registry project', () => {
    projects.setWorkspaces([{
      projects: [{
        id: 'PROJ-001',
        shortCode: 'AS',
        displayName: 'Agent Studio',
        storageLocation: 'C:\\private\\project',
      }],
    }] as unknown as RegistryWorkspaceListItem[]);
    const staleTaskKey = 'C:\\private\\project\\5e-escalated\\human-readable-slug::human-readable-slug';

    selection.openDetailByTaskKey(staleTaskKey);

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug/core'));
    expect(request.request.params.get('project')).toBe('PROJ-001');
    expect(request.request.params.has('watchPath')).toBe(false);
    request.flush(coreFor(info));
  });

  it('ends a failed tab load with a retryable error state', () => {
    tasks.jobs.set([info]);

    selection.openDetailByTaskKey(info.taskKey);
    http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug/core'))
      .flush({ title: 'Temporary failure' }, { status: 503, statusText: 'Unavailable' });

    expect(selection.detailLoading()).toBe(false);
    expect(selection.detailLoadError()?.taskLabel).toBe('AGT-2124');

    selection.retryDetailLoad();
    const retry = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug/core'));
    retry.flush(coreFor(info, 'Agent Studio'));

    expect(selection.detailLoadError()).toBeNull();
    expect(selection.selectedCore()?.id).toBe(info.id);
  });

  it('keeps the final A selection when A, B, A navigation supersedes late replies', () => {
    const other = { ...info, id: 'other-task', key: 'AGT-2125',
      taskKey: 'C:\\private\\project::other-task', title: 'Other task' } as TaskInfo;
    selection.openDetail(info);
    const firstA = http.expectOne(req => req.url.endsWith('/human-readable-slug/core'));
    selection.openDetail(other);
    const requestB = http.expectOne(req => req.url.endsWith('/other-task/core'));
    selection.openDetail(info);
    const finalA = http.expectOne(req => req.url.endsWith('/human-readable-slug/core'));

    expect(firstA.cancelled).toBe(true);
    expect(requestB.cancelled).toBe(true);
    finalA.flush(coreFor(info, 'Agent Studio'));
    expect(selection.selectedCore()?.taskKey).toBe(info.taskKey);
    expect(selection.detailPreview()?.title).toBe(info.title);
  });

  it('keeps core usable when usage alone fails', () => {
    selection.openDetail(info);
    http.expectOne(req => req.url.endsWith('/human-readable-slug/core'))
      .flush(coreFor(info, 'Agent Studio'));
    selection.loadResource('usage');
    http.expectOne(req => req.url.endsWith('/human-readable-slug/details/usage'))
      .flush({ error: 'usage offline' }, { status: 503, statusText: 'Unavailable' });
    expect(selection.selectedCore()?.id).toBe(info.id);
    expect(selection.resourceStates().usage.phase).toBe('error');
    expect(selection.resourceStates().git.phase).toBe('idle');
  });

  it('loads history and review evidence only when their tab is expanded', () => {
    selection.openDetail(info);
    http.expectOne(req => req.url.endsWith('/human-readable-slug/core'))
      .flush(coreFor(info, 'Agent Studio'));
    selection.loadResourcesForTab('prompt');
    http.expectNone(req => req.url.includes('/details/'));

    selection.loadResourcesForTab('timeline');
    http.expectOne(req => req.url.endsWith('/details/history'));
    selection.loadResourcesForTab('evidence');
    const evidence = http.expectOne(req => req.url.endsWith('/details/review'));
    expect(evidence.request.params.get('evidence')).toBe('true');
    expect(selection.resourceStates().git.phase).toBe('idle');
  });

  describe('server-side resolution fallbacks', () => {
    it('lets the backend resolve a public URL whose prefix matches no project of a multi-project workspace', () => {
      registry({ id: 'PROJ-001', shortCode: 'ONE', storageLocation: 'C:\\one' },
        { id: 'PROJ-002', shortCode: 'TWO', storageLocation: 'C:\\two' });
      history.replaceState(null, '', '/#/tasks/AGT-2124');

      selection.restoreFromUrl(true);

      http.expectNone(req => req.url.endsWith('/core'));
      http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2124')).flush(detail);
      expect(selection.selected()?.info.key).toBe('AGT-2124');
      expect(selection.selectedCore()).toBeNull();
      expect(selection.detailPreview()).toBeNull();
      expect(selection.detailLoadError()).toBeNull();
      expect(selection.consumeTaskTabReplacement(info.taskKey)).toBe(true);
    });

    it('falls back to the backend when the inferred sole project does not own the task', () => {
      registry({ id: 'PROJ-001', shortCode: null, storageLocation: 'C:\\one' });
      history.replaceState(null, '', '/#/tasks/human-readable-slug');

      selection.restoreFromUrl();

      http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug/core'))
        .flush({ state: 'missing' }, { status: 404, statusText: 'Not Found' });
      http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug')).flush(detail);
      expect(selection.selected()?.info.id).toBe(info.id);
      expect(selection.detailLoadError()).toBeNull();
    });

    it('bounds the registry wait of a cold public URL and then resolves on the server', async () => {
      vi.useFakeTimers();
      try {
        history.replaceState(null, '', '/#/tasks/AGT-2124');
        selection.restoreFromUrl();
        http.expectNone(req => req.url.includes('/api/tasks/AGT-2124'));

        await vi.advanceTimersByTimeAsync(3_000);

        http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2124')).flush(detail);
        expect(selection.selected()?.info.key).toBe('AGT-2124');
      } finally {
        vi.useRealTimers();
      }
    });

    it('resolves an unplaceable pager entry by its route key instead of failing the step', () => {
      registry({ id: 'PROJ-001', shortCode: 'ONE', storageLocation: 'C:\\one' },
        { id: 'PROJ-002', shortCode: 'TWO', storageLocation: 'C:\\two' });
      const nextInfo = { ...info, id: 'next-task', key: 'AGT-2125',
        taskKey: 'C:\\private\\project::next-task' } as TaskInfo;
      TestBed.inject(LanePagerService).capture(info.state, [info, nextInfo], info.taskKey);

      expect(selection.pagerStep(1)).toBe(true);

      http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2125')).flush({ info: nextInfo } as TaskDetail);
      expect(selection.selected()?.info.id).toBe('next-task');
      expect(selection.detailLoadError()).toBeNull();
      expect(selection.triageLaneState).toBe(info.state);
    });

    it('resolves an unplaceable post-mutation advance on the server', () => {
      registry({ id: 'PROJ-001', shortCode: 'ONE', storageLocation: 'C:\\one' },
        { id: 'PROJ-002', shortCode: 'TWO', storageLocation: 'C:\\two' });
      const nextInfo = { ...info, id: 'next-task', key: 'AGT-2125',
        taskKey: 'C:\\private\\project::next-task' } as TaskInfo;
      TestBed.inject(LanePagerService).capture(info.state, [info, nextInfo], info.taskKey);
      selection.selected.set(detail);

      expect(selection.advanceAfterMutation(info.taskKey)).toBe(true);

      http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2125')).flush({ info: nextInfo } as TaskDetail);
      expect(selection.selected()?.info.id).toBe('next-task');
      expect(selection.consumeTaskTabReplacement(nextInfo.taskKey)).toBe(true);
    });
  });

  it('drops late A and B replies that reach the handlers after A, B, A navigation', () => {
    // Abort is the first defence; this proves the identity guard holds even
    // when a transport delivers replies for superseded requests anyway.
    vi.spyOn(selection as unknown as { cancelRequests: () => void }, 'cancelRequests')
      .mockImplementation(() => undefined);
    const other = { ...info, id: 'other-task', key: 'AGT-2125',
      taskKey: 'C:\\private\\project::other-task', title: 'Other task' } as TaskInfo;
    selection.openDetail(info);
    const firstA = http.expectOne(req => req.url.endsWith('/human-readable-slug/core'));
    selection.openDetail(other);
    const requestB = http.expectOne(req => req.url.endsWith('/other-task/core'));
    selection.openDetail(info);
    const finalA = http.expectOne(req => req.url.endsWith('/human-readable-slug/core'));

    requestB.flush(coreFor(other, 'Agent Studio'));
    expect(selection.selectedCore()).toBeNull();
    expect(selection.detailPreview()?.id).toBe(info.id);

    finalA.flush(coreFor(info, 'Agent Studio'));
    firstA.flush({ ...coreFor(info, 'Agent Studio'), coreVersion: '9', title: 'Late first A' });

    expect(selection.selectedCore()).toMatchObject({ taskKey: info.taskKey, coreVersion: '1' });
    expect(selection.detailPreview()?.title).toBe(info.title);
    expect(location.hash).toBe('#/tasks/AGT-2124');
  });

  it('echoes a 64-bit core generation beyond the JavaScript safe range verbatim', async () => {
    // Regression: a numeric generation rounded to 7457569699994893000 and every
    // resource read answered 409 against the real backend.
    const generation = '7457569699994892853';
    selection.openDetail(info);
    http.expectOne(req => req.url.endsWith('/human-readable-slug/core'))
      .flush({ ...coreFor(info, 'Agent Studio'), coreVersion: generation });
    await afterPaint();
    const documents = http.match(req => req.url.endsWith('/details/documents'));
    expect(documents.map(request => request.request.params.get('generation'))).toEqual([generation, generation]);
    for (const request of documents)
      request.flush(documentReply(info, request.request.params.get('name')!, generation));
    expect(selection.resourceStates().documents.phase).toBe('ready');
    expect(selection.selected()?.promptMarkdown).toBe('prompt markdown');
    await afterPaint();
    http.expectOne(req => req.url.endsWith('/details/usage'))
      .flush({ error: 'usage offline' }, { status: 503, statusText: 'Unavailable' });
  });

  it('marks an enrichment reply for another core generation stale instead of applying it', async () => {
    selection.openDetail(info);
    http.expectOne(req => req.url.endsWith('/human-readable-slug/core'))
      .flush(coreFor(info, 'Agent Studio'));
    await afterPaint();
    const [prompt, status] = http.match(req => req.url.endsWith('/details/documents'));
    prompt.flush(documentReply(info, 'prompt', '2'));
    status.flush(documentReply(info, 'status', '2'));

    expect(selection.resourceStates().documents).toEqual({ phase: 'stale', reason: 'core-generation-changed' });
    expect(selection.selected()?.promptMarkdown ?? null).toBeNull();
    await afterPaint();
    http.expectOne(req => req.url.endsWith('/details/usage'))
      .flush({ error: 'usage offline' }, { status: 503, statusText: 'Unavailable' });
  });

  it('refreshes a revalidated core in place instead of flipping the rich pane back to the core view', async () => {
    const prefetch = TestBed.inject(TaskDetailPrefetchService);
    prefetch.storeCore(coreFor(info, 'Agent Studio') as unknown as TaskCore, 'Agent Studio');

    selection.openDetail(info);
    const revalidation = http.expectOne(req => req.url.endsWith('/human-readable-slug/core'));
    expect(selection.selectedCore()?.coreVersion).toBe('1');
    await afterPaint();
    for (const request of http.match(req => req.url.endsWith('/details/documents')))
      request.flush(documentReply(info, request.request.params.get('name')!));
    expect(selection.detailPreview()).toBeNull();
    expect(selection.selected()?.promptMarkdown).toBe('prompt markdown');
    await afterPaint();
    const staleUsage = http.expectOne(req => req.url.endsWith('/details/usage'));

    revalidation.flush({ ...coreFor(info, 'Agent Studio'), coreVersion: '2', title: 'Renamed task' });

    expect(staleUsage.cancelled).toBe(true);
    expect(selection.detailPreview()).toBeNull();
    expect(selection.selected()?.info.title).toBe('Renamed task');
    expect(selection.selectedCore()?.coreVersion).toBe('2');
    await afterPaint();
    const reload = http.match(req => req.url.endsWith('/details/documents'));
    expect(reload.map(request => request.request.params.get('generation'))).toEqual(['2', '2']);
    for (const request of reload) request.flush(documentReply(info, request.request.params.get('name')!, '2'));
    expect(selection.detailPreview()).toBeNull();
    expect(selection.selected()?.promptMarkdown).toBe('prompt markdown');
    await afterPaint();
    http.expectOne(req => req.url.endsWith('/details/usage'))
      .flush({ error: 'usage offline' }, { status: 503, statusText: 'Unavailable' });
  });
});
