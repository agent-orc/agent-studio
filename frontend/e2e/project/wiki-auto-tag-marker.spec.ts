import { test, expect } from '@playwright/test';

const PROJECT = 'Agent Studio';
const REL = 'concepts/tagging-example.md';

// The served frontend is the worktree build. Existing project metadata comes
// from stable read endpoints; only the tree row under test is mocked.
test('wiki tree distinguishes proposed and applied article tags in both themes', async ({ page }) => {
  await page.addInitScript(() => {
    for (const key of Object.keys(localStorage))
      if (key.startsWith('atp.projectWiki.v1.') || key.startsWith('atp.projectShell.v1.')) localStorage.removeItem(key);
  });
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/crash-recovery/pending')
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ pending: [] }) });
    if (/^\/api\/cli\/[^/]+\/models$/.test(url.pathname))
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ models: [] }) });
    const response = await route.fetch({ url: `http://127.0.0.1:5031${url.pathname}${url.search}` });
    await route.fulfill({ response });
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
  await page.goto('/#/projects/agent-studio/wiki');
  const tree = page.getByTestId('project-wiki-tree');
  await expect(tree).toBeVisible({ timeout: 15_000 });
  const folder = page.getByTestId('project-wiki-node-concepts');
  await folder.getByTestId('project-wiki-chevron-concepts').click({ force: true });
  const proposed = page.getByTestId(`project-wiki-tagging-${REL}`);
  const tagged = page.getByTestId('project-wiki-tagging-concepts/tagged-example.md');
  await expect(proposed).toHaveText('Tags proposed');
  await expect(tagged).toHaveText('Tagged');
  await expect(page.getByTestId('project-wiki-tagging-concepts/future-example.md')).toHaveCount(0);
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await expect(proposed).toBeVisible();
    await expect(tagged).toBeVisible();
    await tree.screenshot({ path: `${process.env.JOB_RESULTS_DIR ?? 'test-results'}/auto-tag-wiki-${theme}.png` });
  }
  await page.unrouteAll({ behavior: 'ignoreErrors' });
});
