import { expect, test, type Page } from '@playwright/test';

const PROJECT = 'fixture-stalled-progress';
const WATCH_PATH = 'C:/fixtures/stalled-progress';

function task(id: string, title: string, overrides: Record<string, unknown>) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    key: id.toUpperCase(),
    title,
    state: '3-progress',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/3-progress/${id}`,
    createdAt: new Date(Date.now() - 60 * 60_000).toISOString(),
    lastActivity: new Date(Date.now() - 60_000).toISOString(),
    enteredLaneAt: new Date(Date.now() - 60_000).toISOString(),
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...overrides,
  };
}

function groupedPayload() {
  const now = Date.now();
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [],
    progress: [
      task('live', 'Healthy live run', {
        order: 1,
        execution: {
          jobId: 'live', taskKey: `${WATCH_PATH}::live`, processId: 42,
          startedAt: new Date(now - 60_000).toISOString(), status: 'running',
          exitCode: null, durationSeconds: null, model: 'gpt-5',
        },
        runActivity: { kind: 'active', processId: 42, attempt: 0 },
      }),
      task('fresh', 'Freshly claimed task', {
        order: 2,
        runActivity: { kind: 'no-active-run', attempt: 0 },
      }),
      task('failed', 'Failed stranded task', {
        order: 3,
        runActivity: { kind: 'no-active-run', attempt: 0 },
        outcomeIssue: {
          kind: 'classifier-unknown', label: 'Unclear', severity: 'Warn',
          summary: 'Tool router reported an execution error',
          lastSeenAt: new Date(now - 30_000).toISOString(),
        },
      }),
      task('idle', 'Idle stranded task', {
        order: 4,
        enteredLaneAt: new Date(now - 10 * 60_000).toISOString(),
        lastActivity: new Date(now - 10 * 60_000).toISOString(),
        runActivity: { kind: 'no-active-run', attempt: 0 },
      }),
    ],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
    humanReview: [], escalated: [], completed: [], archive: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }));
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(groupedPayload()) }));
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
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT, mode: 'manual', activeJobId: 'live',
            activeExecution: groupedPayload().progress[0].execution, queuedJobIds: [],
          },
        },
      }),
    }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

test('stalled Progress cards and lane subset are visible at a glance', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1440, height: 1200 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');

  const lane = page.locator('[data-testid="lane-3-progress"]');
  await expect(lane).toBeVisible();
  await expect(lane.locator('[data-testid="lane-count-3-progress"]')).toContainText('4');
  await expect(lane.locator('[data-testid="lane-stalled-count"]')).toHaveText('· 2 stalled');

  const stalled = lane.locator('[data-testid="task-card"][data-stalled]');
  await expect(stalled).toHaveCount(2);
  await expect(stalled.locator('[data-testid="task-card-stalled"]')).toHaveCount(2);
  await expect(lane.locator('[data-testid="task-card"]', { hasText: 'Healthy live run' })).not.toHaveAttribute('data-stalled');
  await expect(lane.locator('[data-testid="task-card"]', { hasText: 'Freshly claimed task' })).not.toHaveAttribute('data-stalled');

  // Catch-all API mocks can open unrelated shell diagnostics. Remove them only
  // after the board assertions so the evidence frame stays focused on the lane.
  const mockHarnessDialogs = page.getByRole('dialog').or(page.getByRole('alertdialog'));
  await mockHarnessDialogs.first().waitFor({ state: 'visible', timeout: 2_000 }).catch(() => undefined);
  await mockHarnessDialogs.evaluateAll((nodes) => nodes.forEach((node) => node.parentElement?.remove()));
  await expect(stalled).toHaveCount(2);
  for (const theme of ['dark', 'light'] as const) {
    await page.evaluate((value) => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    const [healthyBackground, stalledBackground] = await Promise.all([
      lane.locator('[data-testid="task-card"]', { hasText: 'Healthy live run' })
        .evaluate((node) => getComputedStyle(node).backgroundColor),
      stalled.first().evaluate((node) => getComputedStyle(node).backgroundColor),
    ]);
    expect(stalledBackground).not.toBe(healthyBackground);
    await testInfo.attach(`stalled-progress-board-${theme}.png`, {
      body: await lane.screenshot(),
      contentType: 'image/png',
    });
  }
});
