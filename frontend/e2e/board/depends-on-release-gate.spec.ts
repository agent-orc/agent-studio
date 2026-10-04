import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Release gate fixture';
const WATCH_PATH = '/fixtures/release-gate';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR;

function task(id: string, key: string, title: string, waitsOn: Record<string, unknown>) {
  return {
    id,
    key,
    displayKey: key,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state: '2-ready',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-08-03T08:00:00Z',
    lastActivity: '2026-08-03T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/2-ready/${id}`,
    ownerClientId: 'local-default',
    commits: [],
    tags: [],
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    waitsOn,
  };
}

const RELEASE_WAIT = task('release-wait', 'APP-1', 'Terminal approval gate', {
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
    targetTitle: 'Library acceptance',
    targetState: '6-completed',
    targetWatchPath: '/fixtures/library',
  }],
});

const COMPLETION_WAIT = task('completion-wait', 'APP-2', 'Ordinary completion gate', {
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
    targetTitle: 'Library implementation',
    targetState: '5-human-review',
    targetWatchPath: '/fixtures/library',
  }],
});

const TASKS = [RELEASE_WAIT, COMPLETION_WAIT];

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function openBoard(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await page.route('**/api/**', (route) => json(route, []));
  await page.route('**/api/v1/studio/auth/status', (route) => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/tasks/archive**', (route) => json(route, { items: [], total: 0 }));
  await page.route(/\/api\/tasks(\?|$)/, (route) => json(route, TASKS));
  await page.route('**/api/v1/studio/board**', (route) => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: TASKS,
    progress: [], failedPickup: [], codeNotComplete: [], autoReview: [],
    review: [], humanReview: [], escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', (route) => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', (route) => json(route, {
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) => json(route, {
    projects: {
      [PROJECT]: {
        projectName: PROJECT,
        mode: 'manual',
        activeJobId: null,
        activeExecution: null,
        queuedJobIds: [],
      },
    },
  }));

  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
}

test.describe('dependsOn release gate board status', () => {
  test('distinguishes waiting for release from waiting for completion', async ({ page }) => {
    await openBoard(page);

    const releaseChip = page.getByTestId('task-card').filter({ hasText: RELEASE_WAIT.title })
      .getByTestId('task-card-waiting-on');
    const completionChip = page.getByTestId('task-card').filter({ hasText: COMPLETION_WAIT.title })
      .getByTestId('task-card-waiting-on');

    await expect(releaseChip).toContainText('waits for release: LIB-1');
    await expect(completionChip).toContainText('waits for completion: LIB-2');
    await releaseChip.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('completed, release pending');
  });

  for (const theme of ['light', 'dark'] as const) {
    test(`captures both waiting reasons in ${theme} theme`, async ({ page }) => {
      await openBoard(page);
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      await expect(page.getByText('waits for release: LIB-1')).toBeVisible();
      await expect(page.getByText('waits for completion: LIB-2')).toBeVisible();

      if (RESULTS_DIR) {
        mkdirSync(RESULTS_DIR, { recursive: true });
        await page.screenshot({
          path: `${RESULTS_DIR}/depends-on-release-gate-${theme}--mocked.png`,
          fullPage: false,
        });
      }
    });
  }
});
