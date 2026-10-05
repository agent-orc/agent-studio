import type { Page } from '@playwright/test';

export const TAG_PROJECT = 'Tag UI';
export const TAG_DOSSIER_ID = 'runner-contract';

/** Deterministic API boundaries for the shared tag UI; no live workspace mutations. */
export async function installTagUiFixture(page: Page): Promise<void> {
  const project = { sourceType: 'local-folder', id: 'PROJ-002', displayName: TAG_PROJECT, shortCode: 'TAG',
    workspaceId: 'workspace', sortOrder: 0, storageLocation: '/repo', rootPath: '/repo', repositoryPath: '/repo',
    archived: false, createdAt: '2026-01-01T00:00:00Z' };
  const tags = [
    { id: 'execution-and-runner', label: 'Execution and runner', color: '#7fa8da', description: 'Task execution', kind: 'area' },
    { id: 'decision', label: 'Decision', color: '#b1a0cd', description: '', kind: 'facet' },
    { id: 'evidence', label: 'Evidence', color: '#8ca9a1', description: '', kind: 'facet' },
  ];
  const dossier = { id: TAG_DOSSIER_ID, key: 'TAG-W1', title: 'Runner contract', summary: 'Execution terms and decisions.',
    status: 'active', phase: 'testing', updatedAtUtc: '2026-09-25T12:00:00Z',
    entryPath: 'docs/runner-contract/index.html', valid: true, error: null, sourceTaskKeys: [], relatedTaskKeys: [],
    tags: ['execution-and-runner'], openDecisionCount: 0, pattern: 'concept' };
  const card = { id: 'runner-card', taskKey: '/repo::runner-card', title: 'Document the runner contract', state: '2-ready',
    order: 1, agent: 'claude', cliType: 'claude', projectName: TAG_PROJECT, watchPath: '/repo',
    folderPath: '/repo/tasks/2-ready/runner-card', createdAt: '2026-09-25T12:00:00Z', lastActivity: '2026-09-25T12:00:00Z',
    model: null, mode: 'coding', sessionName: null, lastUsage: null, execution: null, commit: null, commits: [],
    tags: ['execution-and-runner', 'decision'] };
  await page.route('**/api/**', async route => {
    const pathname = new URL(route.request().url()).pathname;
    let body: unknown = [];
    if (pathname === '/api/auth/status' || pathname === '/api/v1/studio/auth/status') {
      body = { profile: 'local', bootstrapRequired: false, authenticated: true };
    }
    else if (pathname === '/api/workspaces' || pathname === '/api/v1/workspaces') body = [{ id: 'workspace', displayName: 'Workspace', sortOrder: 0,
      isDefault: true, projects: [project] }];
    else if (pathname === '/api/projects' || pathname === '/api/v1/projects') body = [project];
    else if (pathname === '/api/watch-paths') body = [{ name: TAG_PROJECT, path: '/repo', rootPath: '/repo', repositoryPath: '/repo' }];
    else if (pathname === '/api/tags') body = tags;
    else if (pathname.endsWith('/tags')) body = { items: tags };
    else if (pathname.endsWith('/areas')) body = { items: [{ id: tags[0].id, label: tags[0].label,
      description: tags[0].description, glossaryPath: 'docs/areas/execution-and-runner/glossary.md' }] };
    else if (pathname === '/api/workbenches') body = { projectName: null, count: 1, currentCount: 1, historyCount: 0,
      items: [{ projectName: TAG_PROJECT, workbench: dossier }] };
    else if (pathname.endsWith('/workbenches')) body = { projectName: TAG_PROJECT, items: [dossier] };
    else if (pathname.endsWith('/wiki/tree')) body = { projectName: TAG_PROJECT, baseDir: '/repo/docs', exists: true,
      root: [{ name: 'guide.md', title: 'Runner guide', relPath: 'guide.md', type: 'md', children: [],
        tags: ['execution-and-runner'] }] };
    else if (pathname.endsWith('/wiki/home')) body = { sections: [] };
    else if (pathname.endsWith('/wiki/pulse')) body = null;
    else if (pathname.endsWith('/style-guides')) body = { snapshotId: 'fixture', technologies: [], guides: [], warnings: [] };
    else if (pathname === '/api/crash-recovery/pending') body = { pending: [] };
    else if (pathname === '/api/cli/quota') body = { snapshots: [], ttlSeconds: 600 };
    else if (pathname === '/api/runner/status' || pathname === '/api/v1/studio/runner/status') body = { projects: {} };
    else if (pathname === '/api/tasks/grouped' || pathname === '/api/v1/studio/board') body = { backlog: [], preparation: [], ready: [card], progress: [],
      autoReview: [], humanReview: [], completed: [], archive: [], escalated: [] };
    else if (pathname === '/api/tasks/archive') body = { items: [], total: 0, offset: 0, limit: 50 };
    else if (pathname === '/api/tasks/reference-status') body = { items: [] };
    else if (pathname.endsWith('/models')) body = { models: [] };
    await route.fulfill({ json: body });
  });
}
