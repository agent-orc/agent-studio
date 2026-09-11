import { describe, expect, it } from 'vitest';
import {
  isProjectHubRoute,
  parseProjectHubRoute,
  projectHubRoute,
  withProjectHubRoute,
  type ProjectHubRouteProject,
} from './project-hub-route';

const PROJECTS: readonly ProjectHubRouteProject[] = [
  { id: 'PROJ-002', displayName: 'Agent Studio' },
  { id: 'PROJ-017', displayName: 'Runbook' },
];

describe('Project Hub URL contract', () => {
  it('builds an id-based canonical route with an optional rail', () => {
    expect(projectHubRoute('proj-002', 'overview')).toBe('/projects/PROJ-002');
    expect(projectHubRoute('PROJ-002', 'wiki')).toBe('/projects/PROJ-002/wiki');
  });

  it('resolves immutable project ids and preserves a rail-owned query', () => {
    expect(parseProjectHubRoute(
      '#/projects/PROJ-002/wiki?page=concepts%2Foverview.md&filters=type%3Abug',
      PROJECTS,
    )).toEqual({
      project: PROJECTS[0],
      section: 'wiki',
      legacySlug: false,
      query: '?page=concepts%2Foverview.md',
      workbenchId: null,
    });
  });

  it('accepts the former display-name slug as an input-only alias', () => {
    expect(parseProjectHubRoute('#/projects/agent-studio/settings', PROJECTS)).toEqual({
      project: PROJECTS[0],
      section: 'settings',
      legacySlug: true,
      query: '',
      workbenchId: null,
    });
  });

  it('resolves a canonical Dossier detail under the Project Hub', () => {
    expect(parseProjectHubRoute(
      '#/projects/PROJ-002/workbenches/task-detail-usage-panel', PROJECTS,
    )).toEqual({
      project: PROJECTS[0], section: 'workbenches', legacySlug: false,
      query: '', workbenchId: 'task-detail-usage-panel',
    });
  });

  it('rejects unknown projects, rails, and malformed identifiers', () => {
    expect(parseProjectHubRoute('#/projects/PROJ-999', PROJECTS)).toBeNull();
    expect(parseProjectHubRoute('#/projects/PROJ-002/not-a-rail', PROJECTS)).toBeNull();
    expect(parseProjectHubRoute('#/projects/%E0%A4%A', PROJECTS)).toBeNull();
  });

  it('replaces only the route segment and retains independent hash state', () => {
    expect(withProjectHubRoute(
      '#/workspace/settings&filters=projects%3AAgent%20Studio',
      'PROJ-002',
      'pipeline',
    )).toBe('#/projects/PROJ-002/pipeline&filters=projects%3AAgent%20Studio');
    expect(isProjectHubRoute('#/projects/PROJ-002')).toBe(true);
    expect(isProjectHubRoute('#/workspace/settings')).toBe(false);
  });
});
