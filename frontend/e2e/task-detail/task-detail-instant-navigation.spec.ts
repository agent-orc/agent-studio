import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { test, expect, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const WATCH_PATH = 'C:/fixtures/task-detail-navigation';
const TASK_ID = 'agt-2577-heavy';
const TASK_KEY = `${WATCH_PATH}::${TASK_ID}`;

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function task() {
  return {
    id: TASK_ID,
    taskKey: TASK_KEY,
    key: 'AGT-2577',
    displayKey: 'AGT-2577',
    title: 'Heavy task with many runs and artifacts',
    state: '5-human-review',
    kind: 'task',
    mode: 'coding',
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5.2-codex',
    order: 1,
    createdAt: '2026-08-11T08:00:00Z',
    lastActivity: '2026-08-11T10:00:00Z',
    watchPath: WATCH_PATH,
    projectName: 'fixture',
    folderPath: `${WATCH_PATH}/${TASK_ID}`,
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    estimatedTokens: 0,
  };
}

function detail() {
  return {
    info: task(),
    promptMarkdown: '# Heavy task\n\nLoaded detail content.',
    statusMarkdown: 'Ready for review.',
    contextUsage: null,
    log: [],
    promptHistory: [],
    titleHistory: [],
    reviewEvidence: [],
    summaryState: null,
  };
}

function grouped() {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [task()],
    escalated: [], completed: [], archive: [],
  };
}

async function mockApplication(page: Page): Promise<void> {
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { id: 'fixture', name: 'fixture', shortCode: 'FIX', path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, route => json(route, [{
    id: 'workspace', displayName: 'Workspace', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-08-11T08:00:00Z',
    projects: [{
      sourceType: 'local-folder', id: 'fixture', displayName: 'fixture', shortCode: 'FIX',
      workspaceId: 'workspace', color: null, cliDefault: 'codex', modelDefault: null,
      sortOrder: 0, storageLocation: WATCH_PATH, repositoryPath: WATCH_PATH,
      rootPath: WATCH_PATH, repositoryUrl: null, urls: [], archived: false,
      createdAt: '2026-08-11T08:00:00Z',
    }],
  }]));
  await page.route('**/api/cli/usage**', route => json(route, { items: [] }));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-08-11T10:00:00Z', snapshots: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/tasks', route => json(route, [task()]));
  await page.route('**/api/v1/studio/board**', route => json(route, grouped()));
  await page.route(`**/api/tasks/${TASK_ID}/pipeline**`, route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null,
    executions: [],
    config: {},
    cost: null,
  }));
}

async function clickCard(page: Page): Promise<void> {
  const card = page.locator('[data-testid="task-card"], [data-testid="job-card"]')
    .filter({ hasText: 'Heavy task with many runs and artifacts' });
  await expect(card).toBeVisible();
  const box = await card.boundingBox();
  if (!box) throw new Error('Task card has no layout box');
  await card.click({ position: { x: box.width / 2, y: box.height - 4 }, force: true });
}

async function firstHeadPaintMs(page: Page): Promise<number> {
  await page.waitForFunction(() => performance.getEntriesByName('task-head-first-paint').length > 0);
  return page.evaluate(() => {
    const click = performance.getEntriesByName('task-card-click').at(-1);
    const paint = performance.getEntriesByName('task-head-first-paint').at(-1);
    if (!click || !paint) throw new Error('Task navigation performance marks are missing');
    return Math.round(paint.startTime - click.startTime);
  });
}

test('opens the task head before heavy detail data resolves', async ({ page }) => {
  await mockApplication(page);
  let detailRequested = false;
  let releaseDetail!: () => void;
  const detailGate = new Promise<void>(resolve => { releaseDetail = resolve; });
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${TASK_ID}(\\?|$)`), async route => {
    detailRequested = true;
    await detailGate;
    await json(route, detail());
  });

  try {
    await page.goto('/');
    await dismissDevErrorDialog(page);
    await setTheme(page, 'light');
    await page.evaluate(() => {
      document.addEventListener('click', () => performance.mark('task-card-click'), {
        capture: true,
        once: true,
      });
      const observer = new MutationObserver(() => {
        if (!document.querySelector('[data-testid="task-detail-head"]')) return;
        observer.disconnect();
        requestAnimationFrame(() => performance.mark('task-head-first-paint'));
      });
      observer.observe(document.body, { childList: true, subtree: true });
    });

    await clickCard(page);
    const firstTaskHeadPaintMs = await firstHeadPaintMs(page);
    const head = page.getByTestId('task-detail-head');
    await expect(head).toBeVisible();
    await expect(head).toContainText('AGT-2577');
    await expect(head).toContainText('Heavy task with many runs and artifacts');

    expect(firstTaskHeadPaintMs, 'task head must paint within the immediate-navigation budget').toBeLessThan(100);
    await expect.poll(() => detailRequested).toBe(true);
    await expect(page.getByTestId('task-detail-load-sections').locator('[aria-busy="true"]')).toHaveCount(3);
    await expect(page.getByTestId('studio-board')).toHaveCount(0);

    const resultsDir = process.env.JOB_RESULTS_DIR;
    if (resultsDir) {
      mkdirSync(resultsDir, { recursive: true });
      writeFileSync(
        path.join(resultsDir, 'task-detail-navigation-after.json'),
        `${JSON.stringify({ firstTaskHeadPaintMs, budgetMs: 100, detailPendingAtPaint: detailRequested }, null, 2)}\n`,
      );
      await page.screenshot({
        path: path.join(resultsDir, 'task-detail-navigation-after-loading-light--mocked.png'),
        fullPage: true,
      });
      await setTheme(page, 'dark');
      await page.screenshot({
        path: path.join(resultsDir, 'task-detail-navigation-after-loading-dark--mocked.png'),
        fullPage: true,
      });
    }

    releaseDetail();
    await expect(page.getByTestId('task-detail-load-sections')).toHaveCount(0);
  } finally {
    releaseDetail();
  }
});

test('keeps the task head and gives every failed section a retry', async ({ page }) => {
  await mockApplication(page);
  let attempt = 0;
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${TASK_ID}(\\?|$)`), route => {
    attempt++;
    return attempt === 1
      ? json(route, { title: 'Temporary failure' }, 503)
      : json(route, detail());
  });

  await page.goto('/');
  await dismissDevErrorDialog(page);
  await setTheme(page, 'light');
  await clickCard(page);

  await expect(page.getByTestId('task-detail-head')).toContainText('Heavy task with many runs and artifacts');
  const sectionErrors = page.getByTestId('task-detail-load-sections').locator('[role="alert"]');
  await expect(sectionErrors).toHaveCount(3);
  await expect(sectionErrors.first()).toContainText('The detail request failed');
  await expect(page.locator('app-detail-load-error')).toHaveCount(0);

  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (resultsDir) {
    await page.screenshot({
      path: path.join(resultsDir, 'task-detail-navigation-section-error-light--mocked.png'),
      fullPage: true,
    });
  }

  await page.getByTestId('task-detail-section-retry-activity').click();
  await expect(page.getByTestId('task-detail-load-sections')).toHaveCount(0);
  expect(attempt).toBe(2);
});
