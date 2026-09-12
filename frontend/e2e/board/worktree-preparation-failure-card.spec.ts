import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Worktree recovery drill';
const WATCH_PATH = 'C:/fixtures/worktree-recovery';
const TITLE = 'WEB-19 follow-up pickup';
const RESULTS = process.env.JOB_RESULTS_DIR;

const failedReadyCard = {
  id: 'web-19-follow-up',
  taskKey: `${WATCH_PATH}::web-19-follow-up`,
  key: 'WEB-19',
  title: TITLE,
  state: '2-ready',
  order: 1,
  agent: 'codex',
  cliType: null,
  model: 'gpt-5.6-codex',
  createdAt: '2026-09-12T11:31:00Z',
  lastActivity: '2026-09-12T11:35:00Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/2-ready/web-19-follow-up`,
  execution: null,
  commit: null,
  commits: [],
  ownerClientId: null,
  tags: [],
  outcomeIssue: {
    kind: 'worktree-preparation-failed',
    label: 'worktree-preparation-failed',
    severity: 'High',
    summary: 'attempt=2/5 path=C:\\Temp\\ass-worktrees\\Agent-Studio-Website\\studio-website-model-ref-0468892e gitMessage=fatal: path is not a working tree',
    lastSeenAt: '2026-09-12T11:35:00Z',
  },
};

function json(route: Route, body: unknown) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const pathname = new URL(route.request().url()).pathname;
    if (pathname === '/api/tasks/grouped') {
      return json(route, {
        backlog: [], preparation: [], orchestratorPrep: [], ready: [failedReadyCard],
        progress: [], failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
        humanReview: [], escalated: [], completed: [], archive: [],
      });
    }
    if (pathname === '/api/tasks') return json(route, [failedReadyCard]);
    if (pathname === '/api/tasks/archive') return json(route, { items: [], total: 0 });
    if (pathname === '/api/watch-paths') {
      return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]);
    }
    if (pathname === '/api/auth/status') {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (pathname === '/api/environment') return json(route, { isDev: false, devTools: {} });
    if (pathname === '/api/runner/status') return json(route, { projects: {} });
    if (pathname === '/api/cli/quota') {
      return json(route, { at: '2026-09-12T11:35:00Z', ttlSeconds: 600, snapshots: [] });
    }
    return json(route, []);
  });
}

async function captureCard(page: Page, testInfo: TestInfo, theme: 'light' | 'dark') {
  await setTheme(page, theme);
  const card = page.getByTestId('task-card').filter({ hasText: TITLE }).first();
  await expect(card).toBeVisible();
  await expect(card.getByTestId('task-card-outcome-issue')).toHaveText('worktree-preparation-failed');
  await dismissDevErrorDialog(page);
  const name = `worktree-preparation-failure-card--mocked-${theme}.png`;
  if (RESULTS) {
    mkdirSync(RESULTS, { recursive: true });
    await card.screenshot({ path: join(RESULTS, name) });
    await testInfo.attach(name, { path: join(RESULTS, name), contentType: 'image/png' });
  } else {
    await testInfo.attach(name, { body: await card.screenshot(), contentType: 'image/png' });
  }
}

test('Ready card keeps the bounded worktree preparation failure visible', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await dismissDevErrorDialog(page);

  await captureCard(page, testInfo, 'light');
  await captureCard(page, testInfo, 'dark');
});
