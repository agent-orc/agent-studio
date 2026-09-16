import { describe, expect, it } from 'vitest';
import type { WorkbenchOverviewItem } from '../models/project-docs.model';
import {
  buildDossierIndex,
  dossierReferenceCandidates,
  dossierRoute,
  resolveDossierReference,
} from './dossier-reference.util';

const overviewItem = (
  overrides: Partial<WorkbenchOverviewItem['workbench']> = {},
  projectName = 'Agent Studio',
): WorkbenchOverviewItem => ({
  projectName,
  workbench: {
    id: 'decision-cards',
    key: 'AGT-W54',
    title: 'Decision cards',
    summary: '',
    status: 'decision-pending',
    phase: 'decision-ready',
    updatedAtUtc: '2026-09-01T00:00:00Z',
    entryPath: 'docs/operations/decision-cards/index.html',
    valid: true,
    error: null,
    sourceTaskKeys: ['AGT-2795'],
    ...overrides,
  },
});

const index = buildDossierIndex([
  overviewItem(),
  overviewItem({ id: 'quota-probe', key: 'QS-W3', title: 'Quota probe', status: 'decided',
    phase: null, entryPath: 'docs/quality/quota-probe/overview.html' }, 'Quality System'),
]);

describe('resolveDossierReference', () => {
  it('resolves a project-scoped Dossier key, case-insensitively', () => {
    expect(resolveDossierReference('AGT-W54', index)?.id).toBe('decision-cards');
    expect(resolveDossierReference('agt-w54', index)?.title).toBe('Decision cards');
    expect(resolveDossierReference('QS-W3', index)?.projectName).toBe('Quality System');
  });

  it('resolves the Dossier directory with or without a trailing slash', () => {
    expect(resolveDossierReference('docs/operations/decision-cards/', index)?.key).toBe('AGT-W54');
    expect(resolveDossierReference('docs/operations/decision-cards', index)?.key).toBe('AGT-W54');
    expect(resolveDossierReference('./docs/operations/decision-cards/', index)?.key).toBe('AGT-W54');
  });

  it('resolves the HTML entry point, including a non-index entry name', () => {
    expect(resolveDossierReference('docs/operations/decision-cards/index.html', index)?.key)
      .toBe('AGT-W54');
    expect(resolveDossierReference('docs/quality/quota-probe/overview.html', index)?.key)
      .toBe('QS-W3');
  });

  it('resolves the workbench.json descriptor through its folder', () => {
    expect(resolveDossierReference('docs/operations/decision-cards/workbench.json', index)?.key)
      .toBe('AGT-W54');
  });

  it('leaves an unknown path, a parent folder, and a bare descriptor unresolved', () => {
    expect(resolveDossierReference('docs/operations/', index)).toBeNull();
    expect(resolveDossierReference('docs/operations/setup/README.md', index)).toBeNull();
    expect(resolveDossierReference('workbench.json', index)).toBeNull();
    expect(resolveDossierReference('AGT-2795', index)).toBeNull();
    expect(resolveDossierReference('AGT-W99', index)).toBeNull();
  });

  it('needs a project hint when two projects share one repo-relative path', () => {
    const ambiguous = buildDossierIndex([
      overviewItem({ key: 'AGT-W54' }, 'Agent Studio'),
      overviewItem({ key: 'QS-W9' }, 'Quality System'),
    ]);
    const path = 'docs/operations/decision-cards/index.html';
    expect(resolveDossierReference(path, ambiguous)).toBeNull();
    expect(resolveDossierReference(path, ambiguous, 'Quality System')?.key).toBe('QS-W9');
  });
});

describe('dossierReferenceCandidates', () => {
  it('finds keys and paths in prose without overlapping them', () => {
    expect(dossierReferenceCandidates(
      'See AGT-W54 in docs/operations/decision-cards/index.html.',
    )).toEqual([
      { start: 4, end: 11, token: 'AGT-W54' },
      { start: 15, end: 56, token: 'docs/operations/decision-cards/index.html' },
    ]);
  });

  it('reads a folder named after its key as one path candidate', () => {
    expect(dossierReferenceCandidates('docs/operations/agt-w54/')).toEqual([
      { start: 0, end: 24, token: 'docs/operations/agt-w54/' },
    ]);
  });

  it('ignores task keys, bare words, and URL paths', () => {
    expect(dossierReferenceCandidates('AGT-2795 and workbench.json')).toEqual([]);
    expect(dossierReferenceCandidates('https://example.com/docs/a/b')).toEqual([]);
  });
});

describe('dossierRoute', () => {
  it('builds the same hash route the Dossier list writes', () => {
    expect(dossierRoute('proj-002', 'decision-cards'))
      .toBe('#/projects/PROJ-002/workbenches/decision-cards');
  });
});
