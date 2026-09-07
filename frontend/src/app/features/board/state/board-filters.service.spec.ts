import { describe, it, expect, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { BoardFiltersService } from './board-filters.service';
import { TaskService } from '../../../services/task.service';
import type { GroupedJobs, TaskInfo } from '../../../models/task.model';

/**
 * Regression test for the cross-project counter "leak": ensure that
 * selectProject() switches the active set rather than stacking entries,
 * and that filteredGrouped() reflects the change immediately. The bug
 * report claimed lane counters showed Agent Task Processor numbers even
 * after selecting Lotta Dashboard; the underlying cause was the chip
 * strip's pure-toggle behaviour leaving both projects active, so the
 * counts only crept by however many Lotta jobs exist.
 */

function makeJob(id: string, projectName: string, state: string): TaskInfo {
  return {
    id,
    taskKey: `${projectName}::${id}`,
    title: id,
    state,
    order: 0,
    watchPath: `wp/${projectName}`,
    projectName,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-01-01T00:00:00Z',
    lastActivity: null,
    execution: null,
  } as unknown as TaskInfo;
}

function makeGrouped(jobs: TaskInfo[]): GroupedJobs {
  const byState = (s: string) => jobs.filter((j) => j.state === s);
  return {
    backlog: byState('0-backlog'),
    preparation: byState('1-preparation'),
    orchestratorPrep: [],
    ready: byState('2-ready'),
    progress: byState('3-progress'),
    failedPickup: [],
    codeNotComplete: [],
    autoReview: byState('4-auto-review'),
    humanReview: byState('5-human-review'),
    review: byState('4-auto-review'),
    completed: byState('6-completed'),
    archive: byState('7-archive'),
  } as unknown as GroupedJobs;
}

describe('BoardFiltersService project selection', () => {
  let svc: BoardFiltersService;
  let jobs: TaskService;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    jobs = TestBed.inject(TaskService);
    svc = TestBed.inject(BoardFiltersService);

    const fixture: TaskInfo[] = [
      // Agent Task Processor - the loud project
      makeJob('atp-r-1', 'Agent Task Processor', '2-ready'),
      makeJob('atp-r-2', 'Agent Task Processor', '2-ready'),
      makeJob('atp-r-3', 'Agent Task Processor', '2-ready'),
      makeJob('atp-p-1', 'Agent Task Processor', '3-progress'),
      makeJob('atp-a-1', 'Agent Task Processor', '7-archive'),
      makeJob('atp-a-2', 'Agent Task Processor', '7-archive'),
      // Lotta Dashboard - the quiet project
      makeJob('lot-r-1', 'Lotta Dashboard', '2-ready'),
      makeJob('lot-a-1', 'Lotta Dashboard', '7-archive'),
    ];
    jobs.grouped.set(makeGrouped(fixture));
  });

  it('no filter shows every project', () => {
    const g = svc.filteredGrouped();
    expect(g.ready.length).toBe(4);
    expect(g.archive.length).toBe(3);
  });

  it('selectProject switches to single-project view by default', () => {
    svc.selectProject('Agent Task Processor', false);
    expect([...svc.activeProjects()]).toEqual(['Agent Task Processor']);
    let g = svc.filteredGrouped();
    expect(g.ready.length).toBe(3);
    expect(g.archive.length).toBe(2);

    // The reported bug: clicking Lotta while ATP active must REPLACE,
    // not add. Pre-fix this stacked and ready.length stayed at 4.
    svc.selectProject('Lotta Dashboard', false);
    expect([...svc.activeProjects()]).toEqual(['Lotta Dashboard']);
    g = svc.filteredGrouped();
    expect(g.ready.length).toBe(1);
    expect(g.archive.length).toBe(1);
  });

  it('selectProject with additive=true extends the active set (legacy multi-select)', () => {
    svc.selectProject('Agent Task Processor', false);
    svc.selectProject('Lotta Dashboard', true);
    expect(svc.activeProjects().has('Agent Task Processor')).toBe(true);
    expect(svc.activeProjects().has('Lotta Dashboard')).toBe(true);
    const g = svc.filteredGrouped();
    expect(g.ready.length).toBe(4);
    expect(g.archive.length).toBe(3);
  });

  it('selectProject on the sole-active chip clears the filter', () => {
    svc.selectProject('Agent Task Processor', false);
    svc.selectProject('Agent Task Processor', false);
    expect(svc.activeProjects().size).toBe(0);
    const g = svc.filteredGrouped();
    expect(g.ready.length).toBe(4);
  });

  it('filteredGrouped never mixes other projects when one is selected', () => {
    svc.selectProject('Lotta Dashboard', false);
    const g = svc.filteredGrouped();
    for (const lane of [g.ready, g.archive, g.progress, g.preparation, g.completed]) {
      for (const j of lane) {
        expect(j.projectName).toBe('Lotta Dashboard');
      }
    }
  });

  it('does not count project scope as an active filter', () => {
    svc.selectProject('Agent Task Processor', false);

    expect(svc.activeFilterCount()).toBe(0);
    expect(svc.hasActiveFilters()).toBe(false);
    expect(svc.hasActiveFiltersOrSearch()).toBe(false);
  });

  it('does not expose a route-owned project scope as a query-filter pill', () => {
    svc.setSoleProject('Agent Task Processor');

    expect(svc.activeFilterPills()).toEqual([]);
    expect(decodeURIComponent(window.location.hash)).not.toContain('projects:');
  });

  it('keeps an explicit sole-project scope in the shareable filter URL', () => {
    window.location.hash = '#/feed';

    svc.setExplicitSoleProject('Agent Task Processor');
    svc.setExplicitSoleProject('Agent Task Processor');

    expect([...svc.activeProjects()]).toEqual(['Agent Task Processor']);
    expect(svc.activeFilterPills().map(pill => pill.label)).toEqual([
      'projects:Agent Task Processor',
    ]);
    expect(decodeURIComponent(window.location.hash)).toContain('projects:Agent Task Processor');
  });

  it('counts only real active filters and search text', () => {
    svc.selectProject('Agent Task Processor', false);
    svc.setSearchQuery('ready');
    svc.setClientFilter('owner-1');
    svc.onSetType('bug');
    svc.toggleTagFilter('important');

    expect(svc.activeFilterCount()).toBe(4);
    expect(svc.hasActiveFilters()).toBe(true);
    expect(svc.hasActiveFiltersOrSearch()).toBe(true);
  });

  it('shows exactly the accepted-integration alert tasks when the filter link changes the hash', () => {
    svc.updateAcceptedIntegrationAlertItems([
      { projectName: 'Agent Task Processor', taskId: 'atp-r-2' },
      { projectName: 'Lotta Dashboard', taskId: 'lot-r-1' },
    ]);
    window.location.hash = '#/board&filters=integration%3Astalled';
    window.dispatchEvent(new HashChangeEvent('hashchange'));

    const grouped = svc.filteredGrouped();
    expect(grouped.ready.map(job => job.id)).toEqual(['atp-r-2', 'lot-r-1']);
    expect(svc.activeFilterPills().map(pill => pill.label)).toContain('integration:stalled');
  });

  it('includes search and project expressions in the visible filter pills', () => {
    svc.setSearchQuery('release gate');
    svc.selectProject('Lotta Dashboard', false);

    expect(svc.activeFilterPills().map(pill => pill.label)).toEqual([
      'Search: release gate',
      'projects:Lotta Dashboard',
    ]);
  });

  it('removes search through the same pill contract and clears q from the URL', () => {
    svc.setSearchQuery('release gate');
    const searchPill = svc.activeFilterPills().find(pill => pill.kind === 'search');
    expect(searchPill).toBeDefined();

    svc.removeFilterPill(searchPill!);

    expect(svc.searchQuery()).toBe('');
    expect(new URL(window.location.href).searchParams.has('q')).toBe(false);
  });
});

/**
 * `filteredGroupedForProject` is the explicit-scope path that the shell's
 * per-project count badges read (e.g. `backlogCount` =
 * `filteredGroupedForProject(activeProjectName()).backlog.length`). The
 * "193" symptom in the bug report was a backlog/count that aggregated across
 * projects; these tests pin the badge source so a single project's backlog
 * count is exactly that project's `0-backlog`, regardless of any stale
 * activeProjects / localStorage filter state.
 */
describe('BoardFiltersService.filteredGroupedForProject (count-badge scope)', () => {
  let svc: BoardFiltersService;
  let jobs: TaskService;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    jobs = TestBed.inject(TaskService);
    svc = TestBed.inject(BoardFiltersService);

    const fixture: TaskInfo[] = [
      // Active project: exactly 3 backlog tasks (mirrors ASS-717/718/719).
      makeJob('atp-b-1', 'Agent Task Processor', '0-backlog'),
      makeJob('atp-b-2', 'Agent Task Processor', '0-backlog'),
      makeJob('atp-b-3', 'Agent Task Processor', '0-backlog'),
      // Foreign backlog tasks that must NOT bleed into the count.
      makeJob('lot-b-1', 'Lotta Dashboard', '0-backlog'),
      makeJob('lot-b-2', 'Lotta Dashboard', '0-backlog'),
      // A pile of foreign non-backlog work (the "156 human-review" symptom).
      makeJob('lot-h-1', 'Lotta Dashboard', '5-human-review'),
      makeJob('lot-h-2', 'Lotta Dashboard', '5-human-review'),
    ];
    jobs.grouped.set(makeGrouped(fixture));
  });

  it('scopes the backlog count to the named project only', () => {
    const g = svc.filteredGroupedForProject('Agent Task Processor');
    expect(g.backlog.length).toBe(3);
    for (const j of g.backlog) expect(j.projectName).toBe('Agent Task Processor');
    // Foreign lanes are emptied too — the badge never sees other projects.
    expect(g.humanReview?.length ?? 0).toBe(0);
  });

  it('ignores a stale activeProjects filter and uses the explicit scope', () => {
    // Simulate a leftover board filter pointing at a different project.
    svc.selectProject('Lotta Dashboard', false);
    const g = svc.filteredGroupedForProject('Agent Task Processor');
    expect(g.backlog.map((j) => j.projectName)).toEqual([
      'Agent Task Processor',
      'Agent Task Processor',
      'Agent Task Processor',
    ]);
  });

  it('re-scopes cleanly between projects (no leak from the previous project)', () => {
    expect(svc.filteredGroupedForProject('Agent Task Processor').backlog.length).toBe(3);
    expect(svc.filteredGroupedForProject('Lotta Dashboard').backlog.length).toBe(2);
  });
});

/**
 * URL-hash round-trip in the presence of a foreign ROUTE segment (an open
 * overlay such as workspace settings). Regression for the hybrid-hash
 * collision (operator report 2026-07-21): the filter writer must upsert only
 * its own `filters=` segment and leave the route segment intact, and the
 * reader must find `filters=` inside a composite hash. See url-hash.util.ts.
 */
describe('BoardFiltersService URL-hash coexistence with a route overlay', () => {
  let svc: BoardFiltersService;

  beforeEach(() => {
    localStorage.clear();
    history.replaceState(null, '', window.location.pathname + window.location.search);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    svc = TestBed.inject(BoardFiltersService);
  });

  it('writing a filter preserves an open overlay route segment', () => {
    history.replaceState(null, '', '/#/workspace/settings');

    svc.selectProject('Agent Studio Marketing', false);

    expect(window.location.hash).toBe(
      '#/workspace/settings&filters=projects%3AAgent%20Studio%20Marketing',
    );
  });

  it('clearing the last filter drops filters= but keeps the route (no empty segment)', () => {
    history.replaceState(null, '', '/#/workspace/settings&filters=projects%3AAgent%20Studio%20Marketing');
    svc.hydrateFromUrl();
    expect([...svc.activeProjects()]).toEqual(['Agent Studio Marketing']);

    svc.clearAllFilters();

    expect(window.location.hash).toBe('#/workspace/settings');
  });

  it('clearing project scope keeps the board route and unrelated filters', () => {
    history.replaceState(
      null,
      '',
      '/#/board&filters=projects%3AAgent%20Studio%20Marketing%3Btype%3Abug',
    );
    svc.hydrateFromUrl();

    svc.clearProjectScope();

    expect(svc.activeProjects().size).toBe(0);
    expect(svc.activeType()).toBe('bug');
    expect(window.location.hash).toBe('#/board&filters=type%3Abug');
  });

  it('hydrates the filter from a composite hash where the route comes first', () => {
    history.replaceState(null, '', '/#/workspace/settings&filters=type%3Abug');

    svc.hydrateFromUrl();

    expect(svc.activeType()).toBe('bug');
  });

  it('hydrates the stalled-integration filter link', () => {
    history.replaceState(null, '', '/#/board&filters=integration%3Astalled');

    svc.hydrateFromUrl();

    expect(svc.stalledIntegrationOnly()).toBe(true);
  });

  it('clears hydrated filters when navigation reaches the board route without filters=', () => {
    history.replaceState(null, '', '/#/board&filters=integration%3Astalled%3Btype%3Abug');
    svc.hydrateFromUrl();
    expect(svc.stalledIntegrationOnly()).toBe(true);
    expect(svc.activeType()).toBe('bug');

    history.replaceState(null, '', '/#/board');
    window.dispatchEvent(new HashChangeEvent('hashchange'));

    expect(svc.stalledIntegrationOnly()).toBe(false);
    expect(svc.activeType()).toBeNull();
    expect(svc.activeFilterPills()).toEqual([]);
  });

  it('hydrates the filter even when the route segment is written after filters=', () => {
    history.replaceState(null, '', '/#filters=type%3Afeature&/workspace/settings');

    svc.hydrateFromUrl();

    expect(svc.activeType()).toBe('feature');
  });
});

/**
 * AGT-2709 "waiting for release": the facet has to surface BOTH sides of a
 * release-gated edge - the dependent that is held back and the terminal target
 * whose release it waits for - so a pending release is discoverable without
 * opening each card.
 */
describe('BoardFiltersService waiting-for-release facet', () => {
  let svc: BoardFiltersService;
  let jobs: TaskService;

  const waitsOnTarget = {
    blocked: true,
    cycleDetected: false,
    items: [{
      key: 'AGT-2372',
      resolved: true,
      fulfilled: false,
      releaseGate: true,
      targetReleased: false,
      waitingForRelease: true,
      targetJobId: 'target',
      targetWatchPath: 'wp/Agent Task Processor',
    }],
  };

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

    const dependent = {
      ...makeJob('dependent', 'Agent Task Processor', '2-ready'),
      waitsOn: waitsOnTarget,
    } as unknown as TaskInfo;
    // The target the dependent waits for; its own `released: false` is
    // indistinguishable from "no gate", so it is matched via the dependent.
    const target = makeJob('target', 'Agent Task Processor', '6-completed');
    const unrelated = makeJob('unrelated', 'Agent Task Processor', '2-ready');
    const openDependency = {
      ...makeJob('open-dep', 'Agent Task Processor', '2-ready'),
      waitsOn: {
        blocked: true,
        cycleDetected: false,
        items: [{ key: 'AGT-9', resolved: true, fulfilled: false, waitingForRelease: false }],
      },
    } as unknown as TaskInfo;

    jobs.grouped.set(makeGrouped([dependent, target, unrelated, openDependency]));
  });

  it('is off by default and leaves the board untouched', () => {
    expect(svc.waitingForReleaseOnly()).toBe(false);
    expect(svc.filteredGrouped().ready.length).toBe(3);
  });

  it('keeps the blocked dependent and its pending-release target, drops the rest', () => {
    svc.setWaitingForReleaseOnly(true);

    const grouped = svc.filteredGrouped();
    expect(grouped.ready.map(j => j.id)).toEqual(['dependent']);
    expect(grouped.completed.map(j => j.id)).toEqual(['target']);
    expect(svc.filteredTaskCount()).toBe(2);
  });

  it('counts as an active filter and renders a removable pill', () => {
    svc.setWaitingForReleaseOnly(true);

    expect(svc.hasActiveFilters()).toBe(true);
    expect(svc.activeFilterCount()).toBe(1);
    const pill = svc.activeFilterPills().find(p => p.kind === 'release');
    expect(pill?.label).toBe('Waiting for release');

    svc.removeFilterPill(pill!);
    expect(svc.waitingForReleaseOnly()).toBe(false);
  });

  it('round-trips through the shareable filter hash', () => {
    svc.toggleWaitingForReleaseOnly();
    expect(window.location.hash).toContain('release%3Awaiting');

    history.replaceState(null, '', '/#/board&filters=release%3Awaiting');
    svc.hydrateFromUrl();
    expect(svc.waitingForReleaseOnly()).toBe(true);
  });

  it('is cleared by "clear all"', () => {
    svc.setWaitingForReleaseOnly(true);
    svc.clearAllFilters();
    expect(svc.waitingForReleaseOnly()).toBe(false);
  });
});
