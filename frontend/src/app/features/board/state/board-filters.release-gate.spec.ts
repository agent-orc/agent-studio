import { describe, it, expect, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { BoardFiltersService } from './board-filters.service';
import { TaskService } from '../../../services/task.service';
import type { GroupedJobs, TaskInfo, WaitsOnStatus } from '../../../models/task.model';

/**
 * AGT-2709 "waiting for release" board filter.
 *
 * A release gate has two sides and the operator needs both in one view: the
 * dependent that reports `waitingForRelease`, and the terminal target that
 * still has to be released. The target is the awkward half - it sits in
 * Delivered looking finished, so without this filter it is only findable by
 * opening the blocked card and following the chip.
 */
function makeJob(
  id: string,
  state: string,
  extra: { key?: string; waitsOn?: WaitsOnStatus } = {},
): TaskInfo {
  return {
    id,
    taskKey: `proj::${id}`,
    key: extra.key,
    title: id,
    state,
    order: 0,
    watchPath: 'wp/proj',
    projectName: 'Proj',
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-09-01T00:00:00Z',
    lastActivity: null,
    execution: null,
    waitsOn: extra.waitsOn ?? null,
  } as unknown as TaskInfo;
}

function makeGrouped(jobs: TaskInfo[]): GroupedJobs {
  const byState = (s: string) => jobs.filter(j => j.state === s);
  return {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: byState('2-ready'),
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    autoReview: [],
    humanReview: byState('5-human-review'),
    review: [],
    completed: byState('6-completed'),
    archive: byState('7-archive'),
  } as unknown as GroupedJobs;
}

const WAITING_FOR_RELEASE: WaitsOnStatus = {
  blocked: true,
  cycleDetected: false,
  items: [{
    key: 'LIB-1',
    resolved: true,
    fulfilled: false,
    releaseGate: true,
    targetReleased: false,
    waitingForRelease: true,
    targetJobId: 'lib-1',
    targetState: '6-completed',
    targetWatchPath: 'wp/proj',
  }],
};

const WAITING_FOR_COMPLETION: WaitsOnStatus = {
  blocked: true,
  cycleDetected: false,
  items: [{
    key: 'LIB-2',
    resolved: true,
    fulfilled: false,
    releaseGate: false,
    targetReleased: false,
    waitingForRelease: false,
    targetJobId: 'lib-2',
    targetState: '5-human-review',
    targetWatchPath: 'wp/proj',
  }],
};

describe('BoardFiltersService waiting-for-release filter', () => {
  let svc: BoardFiltersService;
  let jobs: TaskService;

  beforeEach(() => {
    localStorage.clear();
    history.replaceState(null, '', '/#/board');
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    jobs = TestBed.inject(TaskService);
    svc = TestBed.inject(BoardFiltersService);
    jobs.grouped.set(makeGrouped([
      makeJob('app-1', '2-ready', { key: 'APP-1', waitsOn: WAITING_FOR_RELEASE }),
      makeJob('app-2', '2-ready', { key: 'APP-2', waitsOn: WAITING_FOR_COMPLETION }),
      makeJob('app-3', '2-ready', { key: 'APP-3' }),
      makeJob('lib-1', '6-completed', { key: 'LIB-1' }),
      makeJob('lib-2', '5-human-review', { key: 'LIB-2' }),
      makeJob('lib-3', '6-completed', { key: 'LIB-3' }),
    ]));
  });

  it('is off by default and keeps the whole board visible', () => {
    expect(svc.waitingForReleaseOnly()).toBe(false);
    const g = svc.filteredGrouped();
    expect(g.ready.length).toBe(3);
    expect(g.completed.length).toBe(2);
  });

  it('keeps both sides of a pending gate and drops everything else', () => {
    svc.setWaitingForReleaseOnly(true);
    const g = svc.filteredGrouped();

    // The blocked dependent...
    expect(g.ready.map(j => j.id)).toEqual(['app-1']);
    // ...and the terminal target that still needs the explicit release.
    expect(g.completed.map(j => j.id)).toEqual(['lib-1']);
    // A card waiting for plain completion is a different problem, and an
    // unrelated Delivered card is not pending anything.
    expect(g.humanReview.length).toBe(0);
  });

  it('counts as one active filter and renders a removable pill', () => {
    svc.setWaitingForReleaseOnly(true);

    expect(svc.activeFilterCount()).toBe(1);
    expect(svc.hasActiveFilters()).toBe(true);
    const pill = svc.activeFilterPills().find(p => p.kind === 'release');
    expect(pill?.label).toBe('release:waiting');

    svc.removeFilterPill(pill!);
    expect(svc.waitingForReleaseOnly()).toBe(false);
  });

  it('round-trips through the filter hash so the view is shareable', () => {
    svc.toggleWaitingForReleaseOnly();
    expect(window.location.hash).toContain('release%3Awaiting');

    history.replaceState(null, '', '/#/board&filters=release%3Awaiting');
    svc.hydrateFromUrl();
    expect(svc.waitingForReleaseOnly()).toBe(true);
  });

  it('is cleared by clear-all like every other facet', () => {
    svc.setWaitingForReleaseOnly(true);
    svc.clearAllFilters();
    expect(svc.waitingForReleaseOnly()).toBe(false);
  });
});
