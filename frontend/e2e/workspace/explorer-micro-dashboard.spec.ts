import { test, expect, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const resultsDir = process.env.RESULTS_DIR ?? path.join(process.cwd(), 'test-results');
const projectName = 'Micro Dashboard';

function job(id: string, state: string, order: number) {
  return {
    id, taskKey: id, jobKey: `C:/fixtures/${projectName}::${id}`, title: id,
    state, order, projectName, watchPath: `C:/fixtures/${projectName}`,
    folderPath: `C:/fixtures/${projectName}/tasks/${id}`, createdAt: '2026-07-10T08:00:00Z',
    lastActivity: '2026-07-10T09:00:00Z', agent: 'codex', cliType: 'codex',
    sessionName: null, model: null, useOwnSession: null, lastUsage: null,
    execution: null, commit: null, commits: [], ownerClientId: 'local-default',
    tags: [], pendingIntent: null, autoLoop: null, summaryState: null,
  };
}

function grouped() {
  const lanes: Record<string, ReturnType<typeof job>[]> = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], review: [], autoReview: [], humanReview: [], escalated: [],
    completed: [], archive: [],
  };
  for (let i = 0; i < 6; i++) lanes['ready'].push(job(`ready-${i}`, '2-ready', i));
  for (let i = 0; i < 4; i++) lanes['progress'].push(job(`progress-${i}`, '3-progress', i));
  for (let i = 0; i < 12; i++) lanes['humanReview'].push(job(`review-${i}`, '5-human-review', i));
  return lanes;
}

function allJobs() {
  const lanes = grouped();
  return Object.values(lanes).flat();
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/v1/studio/board**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped()) }));
  await page.route('**/api/tasks', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(allJobs()) }));
  await page.route('**/api/tasks/archive**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], total: 0, offset: 0, limit: 50 }) }));
  await page.route('**/api/v1/workspaces**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
    id: 'WS-MICRO', displayName: 'Experiments', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-07-10T08:00:00Z', projects: [{
      id: 'PROJ-MICRO', displayName: projectName, shortCode: 'MIC', workspaceId: 'WS-MICRO',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
      storageLocation: `C:/fixtures/${projectName}`, archived: false, createdAt: '2026-07-10T08:00:00Z', urls: [],
    }],
  }]) }));
  await page.route('**/api/watch-paths**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{
    name: projectName, path: `C:/fixtures/${projectName}`, rootPath: `C:/fixtures/${projectName}`, repositoryPath: `C:/fixtures/${projectName}`,
  }]) }));
  await page.route('**/api/environment**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isDev: false, devTools: {} }) }));
  await page.route('**/api/cli/usage**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-07-10T08:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-07-10T08:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }));
}

async function openStudio(page: Page): Promise<void> {
  await page.addInitScript(name => {
    localStorage.setItem('atp.studio.explorer.expanded', JSON.stringify([name]));
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'board', projectName: name }], activeKey: `board:${name}`,
    }));
    localStorage.removeItem('atp.studio.explorer.metrics');
  }, projectName);
  await installRoutes(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  const counts = page.getByTestId(`studio-explorer-project-board-counts-${projectName}`);
  await expect(counts).toBeVisible({ timeout: 15_000 });
}

test('numbers default, dots toggle, cap, order, a11y, and both themes', async ({ page }) => {
  fs.mkdirSync(resultsDir, { recursive: true });
  await openStudio(page);
  const sidebar = page.getByTestId('studio-sidebar');

  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await sidebar.screenshot({ path: path.join(resultsDir, `tree-numbers-${theme}--mocked.png`) });
  }

  await page.getByTestId('studio-ab-settings').click();
  await page.getByTestId('settings-tree-metrics-dots').click();
  await expect.poll(() => page.evaluate(() => localStorage.getItem('atp.studio.explorer.metrics'))).toBe('dots');
  await page.getByTestId('studio-ab-explorer').click();

  const dots = page.getByTestId(`studio-explorer-project-board-dots-${projectName}`);
  await expect(dots).toHaveAttribute('aria-label', '6 ready, 4 in progress, 12 human review');
  await expect(dots.locator('[data-lane]')).toHaveCount(15);
  await expect(dots.locator('.studio-board-lane-dots__overflow')).toHaveText('+7');
  expect(await dots.locator('[data-lane]').evaluateAll(nodes => nodes.map(node => node.getAttribute('data-lane')))).toEqual([
    ...Array(6).fill('ready'), ...Array(4).fill('progress'), ...Array(5).fill('humanReview'),
  ]);

  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await sidebar.screenshot({ path: path.join(resultsDir, `tree-dots-${theme}--mocked.png`) });
  }
});
