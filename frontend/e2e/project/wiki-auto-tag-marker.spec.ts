import { test, expect } from '@playwright/test';

const PROJECT = 'Agent Studio';
const REL = 'concepts/tagging-example.md';

// Classification markers and project metadata use a fixed fixture so live
// project updates cannot replace the tree during screenshot capture.
test('wiki tree distinguishes proposed and applied article tags in both themes', async ({ page }) => {
  await page.addInitScript(() => {
    for (const key of Object.keys(localStorage))
      if (key.startsWith('atp.projectWiki.v1.') || key.startsWith('atp.projectShell.v1.')) localStorage.removeItem(key);
  });
  const project = {
    sourceType: 'local-folder', id: 'PROJ-002', displayName: PROJECT, shortCode: 'AGT',
    workspaceId: 'workspace', sortOrder: 0, storageLocation: '/repo',
    rootPath: '/repo', repositoryPath: '/repo', archived: false,
    createdAt: '2026-01-01T00:00:00Z',
  };
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    let body: unknown = [];
    if (path === '/api/auth/status')
      body = { profile: 'local', bootstrapRequired: false, authenticated: true };
    else if (path === '/api/workspaces')
      body = [{ id: 'workspace', displayName: 'Workspace', sortOrder: 0, isDefault: true, projects: [project] }];
    else if (path === '/api/projects') body = [project];
    else if (path === '/api/watch-paths')
      body = [{ name: PROJECT, path: '/repo', rootPath: '/repo', repositoryPath: '/repo' }];
    else if (path.endsWith('/workbenches')) body = { items: [], projectName: PROJECT };
    else if (path.endsWith('/style-guides'))
      body = { snapshotId: 'fixture', technologies: [], guides: [], warnings: [] };
    else if (path.endsWith('/wiki/home')) body = { sections: [] };
    else if (path.endsWith('/wiki/pulse')) body = null;
    else if (path === '/api/cli/quota') body = { snapshots: [], ttlSeconds: 600 };
    else if (path === '/api/runner/status') body = { projects: {} };
    else if (path === '/api/crash-recovery/pending') body = { pending: [] };
    else if (path === '/api/tasks/grouped')
      body = { backlog: [], preparation: [], ready: [], progress: [], autoReview: [], humanReview: [], completed: [], archive: [] };
    else if (/^\/api\/cli\/[^/]+\/models$/.test(path)) body = { models: [] };
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.route(/\/api\/projects\/[^/]+\/wiki\/tree/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({
      projectName: PROJECT, baseDir: '/repo/docs', exists: true,
      root: [{ name: 'concepts', title: 'concepts', relPath: 'concepts', type: 'folder', children: [
        { name: 'tagging-example.md', title: 'Tagging example', relPath: REL,
          type: 'md', children: [], tags: [], taggingStatus: 'tags-proposed' },
        { name: 'tagged-example.md', title: 'Tagged example', relPath: 'concepts/tagged-example.md',
          type: 'md', children: [], tags: ['observation'], taggingStatus: 'tagged' },
        { name: 'future-example.md', title: 'Future example', relPath: 'concepts/future-example.md',
          type: 'md', children: [], tags: [], taggingStatus: 'future-status' },
      ] }],
    }),
  }));
  await page.goto('/#/projects/PROJ-002/wiki');
  const tree = page.getByTestId('project-wiki-tree');
  await expect(tree).toBeVisible({ timeout: 15_000 });
  const folder = page.getByTestId('project-wiki-node-concepts');
  await folder.getByTestId('project-wiki-chevron-concepts').click();
  const proposed = page.getByTestId(`project-wiki-tagging-${REL}`);
  const tagged = page.getByTestId('project-wiki-tagging-concepts/tagged-example.md');
  await expect(proposed).toHaveText('Tags proposed');
  await expect(tagged).toHaveText('Tagged');
  await expect(page.getByTestId('project-wiki-tagging-concepts/future-example.md')).toHaveCount(0);
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await expect(proposed).toBeVisible();
    await expect(tagged).toBeVisible();
    await tree.screenshot({ path: `${process.env.JOB_RESULTS_DIR ?? 'test-results'}/auto-tag-wiki-${theme}--mocked.png` });
  }
  await page.unrouteAll({ behavior: 'ignoreErrors' });
});
