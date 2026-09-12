import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { WorkbenchOverviewItem, WorkbenchStatus } from '../../../../models/project-docs.model';
import { WorkbenchOverviewViewStateService } from './workbench-overview-view-state.service';

function item(
  id: string,
  projectName: string,
  status: WorkbenchStatus,
  updatedAtUtc: string,
  openDecisionCount = 0,
): WorkbenchOverviewItem {
  return {
    projectName,
    workbench: {
      id,
      key: `${projectName.slice(0, 3).toUpperCase()}-${id}`,
      title: `${id} title`,
      summary: `${id} summary`,
      status,
      phase: null,
      updatedAtUtc,
      entryPath: `docs/${id}/index.html`,
      valid: true,
      error: null,
      sourceTaskKeys: [],
      openDecisionCount,
    },
  };
}

describe('WorkbenchOverviewViewStateService', () => {
  beforeEach(() => {
    sessionStorage.clear();
    history.replaceState(null, '', '/#/workbenches');
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        WorkbenchOverviewViewStateService,
      ],
    });
  });

  it('filters visible fields and toggles each requested sort direction', () => {
    const state = TestBed.inject(WorkbenchOverviewViewStateService);
    state.setScope(null);
    const items = [
      item('beta', 'Other', 'active', '2026-08-09T10:00:00Z'),
      item('alpha', 'Demo', 'decision-pending', '2026-08-10T10:00:00Z', 3),
    ];
    const label = (entry: WorkbenchOverviewItem) => entry.workbench.status === 'active' ? 'Active' : 'Decision pending';

    for (const [query, expectedId] of [
      ['oth-beta', 'beta'],
      ['alpha title', 'alpha'],
      ['Other', 'beta'],
      ['Decision pending', 'alpha'],
    ]) {
      state.setQuery(query);
      expect(state.filter(items, label).map(entry => entry.workbench.id)).toEqual([expectedId]);
    }
    expect(location.hash).toContain('dossier=q%3DDecision%2Bpending');

    state.setQuery('');
    const cases = [
      { key: 'status' as const, direction: 'asc' as const, ids: ['beta', 'alpha'] },
      { key: 'updatedAt' as const, direction: 'desc' as const, ids: ['alpha', 'beta'] },
      { key: 'project' as const, direction: 'asc' as const, ids: ['alpha', 'beta'] },
      { key: 'key' as const, direction: 'asc' as const, ids: ['alpha', 'beta'] },
      { key: 'openDecisions' as const, direction: 'desc' as const, ids: ['alpha', 'beta'] },
    ];
    for (const entry of cases) {
      state.selectSort(entry.key);
      expect(state.direction()).toBe(entry.direction);
      expect(state.sort(items, label).map(value => value.workbench.id)).toEqual(entry.ids);
      state.selectSort(entry.key);
      expect(state.direction()).toBe(entry.direction === 'asc' ? 'desc' : 'asc');
      expect(state.sort(items, label).map(value => value.workbench.id)).toEqual([...entry.ids].reverse());
    }
    expect(location.hash).toContain('sort%3DopenDecisions%26dir%3Dasc');
  });

  it('lets a shared URL override session state and keeps global and project scopes separate', () => {
    const state = TestBed.inject(WorkbenchOverviewViewStateService);
    state.setScope(null);
    state.setQuery('global query');
    state.selectSort('key');

    history.replaceState(null, '', '/#/projects/demo/workbenches');
    state.setScope('Demo');
    expect(state.query()).toBe('');
    expect(state.sortKey()).toBe('default');
    state.setQuery('project query');

    history.replaceState(null, '', '/#/workbenches?dossier=q%3Dshared%26sort%3DupdatedAt%26dir%3Dasc');
    state.setScope(null);
    expect(state.query()).toBe('shared');
    expect(state.sortKey()).toBe('updatedAt');
    expect(state.direction()).toBe('asc');

    history.replaceState(null, '', '/#/projects/demo/workbenches');
    state.setScope('Demo');
    expect(state.query()).toBe('project query');
    expect(location.hash).toContain('q%3Dproject%2Bquery');
  });

  it('filters by review state and sorts never-reviewed items as the oldest', () => {
    const state = TestBed.inject(WorkbenchOverviewViewStateService);
    state.setScope(null);
    const never = item('never', 'Demo', 'active', '2026-08-01T10:00:00Z');
    const old = item('old', 'Demo', 'active', '2026-08-02T10:00:00Z');
    old.workbench.review = { verdict: 'historical', supersededBy: [], reviewedAt: '2026-01-01T00:00:00Z', reviewedBy: 'Operator', note: 'Retained.' };
    const current = item('current', 'Demo', 'active', '2026-08-03T10:00:00Z');
    current.workbench.review = { verdict: 'current', supersededBy: [], reviewedAt: new Date().toISOString(), reviewedBy: 'Operator', note: 'Still applies.' };
    const items = [current, old, never];

    state.setReviewFilter('never-reviewed');
    expect(state.filter(items, () => 'Active').map(entry => entry.workbench.id)).toEqual(['never']);
    state.setReviewFilter('historical');
    expect(state.filter(items, () => 'Active').map(entry => entry.workbench.id)).toEqual(['old']);
    state.setReviewFilter('all');
    state.selectSort('reviewAge');
    expect(state.sort(items, () => 'Active').map(entry => entry.workbench.id)).toEqual(['never', 'old', 'current']);
  });
});
