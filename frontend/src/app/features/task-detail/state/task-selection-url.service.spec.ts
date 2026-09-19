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
    history.replaceState(null, '', '/studio?view=git#/tasks/AGT-2124');

    selection.restoreFromUrl();

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2124'));
    expect(request.request.params.has('watchPath')).toBe(false);
    request.flush(detail);

    expect(selection.selected()?.info.key).toBe('AGT-2124');
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

  it('uses pushState for user navigation and clears selection on browser Back', () => {
    const push = vi.spyOn(history, 'pushState');

    selection.openDetail(info);

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'));
    expect(request.request.params.has('watchPath')).toBe(false);
    expect(request.request.params.get('project')).toBe('Agent Studio');
    request.flush(detail);
    expect(push).toHaveBeenCalled();
    expect(location.hash).toBe('#/tasks/AGT-2124');

    history.replaceState(null, '', '/?view=board');
    window.dispatchEvent(new PopStateEvent('popstate'));

    expect(selection.selected()).toBeNull();
    expect(selection.browserRouteCleared()).toBe(1);
    expect(location.search).toBe('?view=board');
  });

  it('pushes an advance and restores its prior task, lane anchor, and pager position on popstate', () => {
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
    http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2125'))
      .flush({ info: nextInfo } as TaskDetail);
    expect(pager.position()).toBe(1);
    expect(pager.total()).toBe(1);

    history.replaceState(firstState, '', firstUrl);
    window.dispatchEvent(new PopStateEvent('popstate', { state: firstState }));
    const archivedInfo = { ...info, state: '7-archive' } as TaskInfo;
    http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2124'))
      .flush({ info: archivedInfo } as TaskDetail);

    expect(selection.selected()?.info).toMatchObject({ key: 'AGT-2124', state: '7-archive' });
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
    history.replaceState(null, '', '/#/tasks/AGT-2124');
    selection.restoreFromUrl(true);
    http.expectOne(req => req.url.endsWith('/api/tasks/AGT-2124')).flush(detail);

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

    http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug')).flush(detail);
    expect(selection.selected()).toEqual(detail);
    expect(selection.detailPreview()).toBeNull();
    expect(selection.detailLoading()).toBe(false);
  });

  it('turns a stalled detail request into an honest retryable timeout', async () => {
    vi.useFakeTimers();
    try {
      selection.openDetail(info);
      const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'));

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

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'));
    expect(request.request.params.get('project')).toBe('Agent Studio');
    expect(request.request.params.has('watchPath')).toBe(false);
    request.flush({ info: staleInfo } as TaskDetail);

    expect(selection.detailLoading()).toBe(false);
    expect(selection.detailLoadError()).toBeNull();
    expect(selection.selected()?.info.id).toBe('human-readable-slug');
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

    const request = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'));
    expect(request.request.params.get('project')).toBe('PROJ-001');
    expect(request.request.params.has('watchPath')).toBe(false);
    request.flush(detail);
  });

  it('ends a failed tab load with a retryable error state', () => {
    tasks.jobs.set([info]);

    selection.openDetailByTaskKey(info.taskKey);
    http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'))
      .flush({ title: 'Temporary failure' }, { status: 503, statusText: 'Unavailable' });

    expect(selection.detailLoading()).toBe(false);
    expect(selection.detailLoadError()?.taskLabel).toBe('AGT-2124');

    selection.retryDetailLoad();
    const retry = http.expectOne(req => req.url.endsWith('/api/tasks/human-readable-slug'));
    retry.flush(detail);

    expect(selection.detailLoadError()).toBeNull();
    expect(selection.selected()).toEqual(detail);
  });
});
