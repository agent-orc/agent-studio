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
    released: false, pendingIntent: false, coreVersion: 1,
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
});
