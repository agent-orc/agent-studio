import { test, expect, type Page } from '@playwright/test';
import { dismissDevErrorDialog } from '../helpers/theme';

/** Mocked board payload for review priority display and operator actions. */

const PROJECT = 'fixture-pill-lane';
const WATCH_PATH = 'C:/fixtures/pill-lane-repo';

function runningExecution(id: string) {
  return {
    jobId: id,
    taskKey: `${WATCH_PATH}::${id}`,
    processId: 4242,
    startedAt: '2026-05-28T10:00:00Z',
    status: 'running',
    exitCode: null,
    durationSeconds: null,
    model: 'claude-opus-4-7',
    runOutcome: null,
  };
}

function makeTask(id: string, state: string, title: string, order: number) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state,
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-05-28T09:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/${state}/${id}`,
    lastActivity: '2026-05-28T11:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    // The bug condition: a live running snapshot on every card regardless
    // of lane. The lane guard is what must stop it surfacing.
    execution: runningExecution(id),
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
  };
}

// Titles are crafted so none is a substring of another - Playwright `hasText`
// does substring matching.
const PROGRESS_TASK = makeTask('pill-lane-A-progress', '3-progress', 'Pill lane progress alpha', 1);
const AUTO_REVIEW_TASK = {
  ...makeTask('pill-lane-B-auto', '4-auto-review', 'Pill lane auto review bravo', 1),
  reviewPriority: 'urgent',
  reviewQueuePosition: 1,
};
const NORMAL_REVIEW_TASK = {
  ...makeTask('pill-lane-E-auto', '4-auto-review', 'Pill lane normal review echo', 2),
  reviewPriority: 'normal',
  reviewQueuePosition: 2,
};
const HUMAN_REVIEW_TASK = makeTask('pill-lane-C-human', '5-human-review', 'Pill lane human review charlie', 1);
const COMPLETED_TASK = makeTask('pill-lane-D-done', '6-completed', 'Pill lane completed delta', 1);

const NON_PROGRESS = [AUTO_REVIEW_TASK, NORMAL_REVIEW_TASK, HUMAN_REVIEW_TASK, COMPLETED_TASK];

const GROUPED_PAYLOAD = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [PROGRESS_TASK],
  failedPickup: [],
  review: [],
  autoReview: [AUTO_REVIEW_TASK, NORMAL_REVIEW_TASK],
  humanReview: [HUMAN_REVIEW_TASK],
  completed: [COMPLETED_TASK],
  archive: [],
};

async function installRoutes(page: Page) {
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    if (url.endsWith('/api/tasks')) {
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });

  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED_PAYLOAD) }));

  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }) }));

  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));

  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-05-28T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-05-28T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
        },
      }),
    }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
}

async function gotoBoard(page: Page): Promise<void> {
  await seedBoardTab(page);
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible({ timeout: 15_000 });
  await expect(page.locator('[data-testid="task-card"]').first()).toBeVisible({ timeout: 15_000 });
}

function cardByTitle(page: Page, title: string) {
  return page.locator('[data-testid="task-card"]', { hasText: title });
}

test.describe('Review priority on the board', () => {
  test.use({ serviceWorkers: 'block' });
  test('shows queue position and both review actions in light and dark themes', async ({ page }, testInfo) => {
    await gotoBoard(page);
    await dismissDevErrorDialog(page);
    const card = cardByTitle(page, AUTO_REVIEW_TASK.title);
    await expect(card.getByTestId('task-card-review-queue')).toHaveText('Urgent · Review #1');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);
      await expect(page.getByTestId('error-dialog-overlay')).toBeHidden();
      const image = await card.screenshot();
      await testInfo.attach(`urgent-review-card-${theme}--mocked.png`, { body: image, contentType: 'image/png' });
      const resultsDir = process.env.JOB_RESULTS_DIR;
      if (resultsDir) await card.screenshot({ path: `${resultsDir}/urgent-review-card-${theme}--mocked.png` });
    }
    await card.dispatchEvent('contextmenu', { clientX: 200, clientY: 200, button: 2 });
    await expect(page.getByRole('menuitem', { name: 'Review first' })).toBeDisabled();
    await expect(page.getByRole('menuitem', { name: 'Normal review order' })).toBeEnabled();
  });

  test('sends the dedicated route for urgent and normal priority', async ({ page }) => {
    await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ negotiateVersion: 1, connectionId: 'review-priority-fixture',
        connectionToken: 'review-priority-fixture',
        availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }] }),
    }));
    await page.routeWebSocket('**/hubs/v1/studio**', ws => {
      ws.onMessage(message => { if (String(message).includes('"protocol"')) ws.send('{}\x1e'); });
    });
    await gotoBoard(page);
    await dismissDevErrorDialog(page);
    const requests: { method: string; body: string }[] = [];
    await page.route(/\/review-priority(?:\?|$)/, route => {
      requests.push({ method: route.request().method(), body: route.request().postData() ?? '' });
      return route.fulfill({ status: 200, contentType: 'application/json',
        body: JSON.stringify({ priority: 'urgent', position: 1 }) });
    });
    const normal = cardByTitle(page, NORMAL_REVIEW_TASK.title);
    await expect(normal.getByTestId('task-card-review-queue')).toHaveText('Review #2');
    await normal.dispatchEvent('contextmenu', { clientX: 200, clientY: 200, button: 2 });
    await dismissDevErrorDialog(page);
    await page.getByRole('menuitem', { name: 'Review first' }).click();
    await expect.poll(() => requests.length).toBe(1);
    const urgent = cardByTitle(page, AUTO_REVIEW_TASK.title);
    await urgent.dispatchEvent('contextmenu', { clientX: 200, clientY: 200, button: 2 });
    await dismissDevErrorDialog(page);
    await page.getByRole('menuitem', { name: 'Normal review order' }).click();
    await expect.poll(() => requests.length).toBe(2);
    expect(requests.map(request => ({ method: request.method, priority: JSON.parse(request.body).priority })))
      .toEqual([{ method: 'POST', priority: 'urgent' }, { method: 'POST', priority: 'normal' }]);
  });
});
