import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import path from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Task key copy';
const WATCH_PATH = '/fixtures/task-key-copy';
const JOB_ID = 'task-key-copy';
const TASK_KEY = 'AGT-2268';
const TITLE = 'Copy the task key';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR;

function screenshotPath(testInfo: TestInfo, fileName: string): string {
  return RESULTS_DIR ? path.join(RESULTS_DIR, fileName) : testInfo.outputPath(fileName);
}

const task = {
  id: JOB_ID,
  taskKey: `${WATCH_PATH}::${JOB_ID}`,
  key: TASK_KEY,
  displayKey: TASK_KEY,
  title: TITLE,
  state: '2-ready',
  order: 1,
  agent: 'codex',
  cliType: 'codex',
  model: 'gpt-5.6-codex',
  createdAt: '2026-07-23T12:00:00Z',
  lastActivity: '2026-07-23T12:00:00Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/2-ready/${JOB_ID}`,
  sessionName: null,
  useOwnSession: null,
  lastUsage: null,
  execution: null,
  commit: null,
  commits: [],
  ownerClientId: 'local-default',
  tags: [],
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    const normalizedPath = decodeURIComponent(url.pathname).replace(/\/+$/, '');

    if (url.pathname === '/api/v1/studio/auth/status') {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.pathname === '/api/v1/studio/board') {
      return json(route, {
        backlog: [],
        preparation: [],
        orchestratorPrep: [],
        ready: [task],
        progress: [],
        failedPickup: [],
        codeNotComplete: [],
        review: [],
        autoReview: [],
        humanReview: [],
        escalated: [],
        completed: [],
        archive: [],
      });
    }
    if (normalizedPath.endsWith('/artifacts')) {
      return json(route, { jobId: JOB_ID, files: [] });
    }
    if (normalizedPath.endsWith('/pipeline')) {
      return json(route, {
        pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
        execution: null,
        cost: null,
        tokensByModel: null,
        config: {},
      });
    }
    if (normalizedPath.endsWith('/runs')) {
      return json(route, {
        runCount: 0,
        firstStartedAt: null,
        lastActivityAt: null,
        hasActiveRun: false,
        runs: [],
        promptEntries: [],
      });
    }
    if (normalizedPath.endsWith('/screenshots')) {
      return json(route, { jobId: JOB_ID, screenshots: [] });
    }
    if (normalizedPath.endsWith('/session-events')) {
      return json(route, { events: [], sessionChain: [] });
    }
    if (normalizedPath.endsWith('/git/status')) {
      return json(route, {
        isRepo: true,
        branch: 'main',
        filesChanged: 0,
        totalAdded: 0,
        totalRemoved: 0,
        files: [],
        error: null,
      });
    }
    const detailId = /^\/api\/v1\/projects\/[^/]+\/tasks\/([^/]+)$/.exec(normalizedPath)?.[1];
    if (detailId && [JOB_ID, TASK_KEY].includes(detailId)) {
      return json(route, {
        info: task,
        promptMarkdown: 'Task key copy fixture.',
        promptHistory: [],
        titleHistory: [],
        statusMarkdown: null,
        contextUsage: null,
        log: [],
        summaryState: null,
        reviewEvidence: [],
      });
    }
    if (url.pathname === '/api/tasks/archive') return json(route, { items: [], total: 0 });
    if (url.pathname === '/api/tasks' || url.pathname === '/api/jobs') return json(route, [task]);
    if (url.pathname === '/api/watch-paths') {
      return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]);
    }
    if (url.pathname.startsWith('/api/clients')) {
      return json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance' }]);
    }
    if (url.pathname === '/api/v1/studio/runner/status') return json(route, { projects: {} });
    if (url.pathname === '/api/environment') {
      return json(route, { isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } });
    }
    if (url.pathname.startsWith('/api/cli/')) return json(route, { snapshots: [], sessions: [] });
    return json(route, []);
  });
}

test.describe('Task key click-to-copy', () => {
  test.use({ serviceWorkers: 'block' });

  test.beforeEach(async ({ context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  });

  test('copies the key from the board card and task-detail header with inline feedback', async ({ page }, testInfo) => {
    await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    })));
    await installRoutes(page);
    await page.goto('/');
    await page.addStyleTag({ content: 'app-error-dialog { display: none !important; }' });

    const card = page.locator('[data-testid="task-card"]', { hasText: TITLE });
    await expect(card).toBeVisible({ timeout: 15_000 });
    await setTheme(page, 'light');

    const cardKey = card.getByTestId('task-card-key');
    await expect(cardKey).toHaveAttribute('title', 'Click to copy');
    await cardKey.click();

    await expect(card.getByTestId('task-card-key-copy-feedback')).toHaveText('✓ Copied');
    await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(TASK_KEY);
    await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first()).toBeVisible();
    const boardScreenshot = screenshotPath(testInfo, 'task-key-copy-board-feedback-light.png');
    await card.screenshot({ path: boardScreenshot });
    await testInfo.attach('task-key-copy-board-feedback-light.png', {
      path: boardScreenshot,
      contentType: 'image/png',
    });

    await card.getByTestId('task-card-title').click();
    await expect(page.getByTestId('studio-task')).toBeVisible({ timeout: 15_000 });
    await setTheme(page, 'dark');

    const detailHeader = page.getByTestId('overview-title-block');
    const detailKey = detailHeader.getByTestId('overview-title-key');
    await expect(detailKey).toHaveAttribute('title', 'Click to copy');

    await detailKey.click();

    await expect(detailKey).toContainText('Copied', { timeout: 1_500 });
    await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(TASK_KEY);
    await expect(page.getByTestId('studio-task')).toBeVisible();
    const detailScreenshot = screenshotPath(testInfo, 'task-key-copy-detail-feedback-dark.png');
    await detailHeader.screenshot({ path: detailScreenshot });
    await testInfo.attach('task-key-copy-detail-feedback-dark.png', {
      path: detailScreenshot,
      contentType: 'image/png',
    });
  });
});
