import { test, expect, Page } from '@playwright/test';

/**
 * Kanban container header / focus-expand spec.
 *
 * The board renders three containers - Backlog, Active, Done & Decide.
 * Container headers do not have collapse toggles. The right-side
 * focus-expand button remains and is reversible on a second click.
 * Keyboard `1`/`2`/`3` focuses Backlog/Active/Decide; `0` exits focus.
 */

const FIXTURE_WATCH = 'C:/fixtures/lane-containers-demo';
const FIXTURE_PROJECT = 'lane-containers-demo';

function jobInfo(over: Partial<Record<string, unknown>>): Record<string, unknown> {
  const id = String(over['id'] ?? 'fx-job');
  const state = String(over['state'] ?? '2-ready');
  return {
    id,
    jobKey: `${FIXTURE_WATCH}::${id}`,
    title: String(over['title'] ?? id),
    state,
    order: Number(over['order'] ?? 1),
    agent: String(over['agent'] ?? 'claude'),
    createdAt: '2026-05-05T08:00:00Z',
    watchPath: FIXTURE_WATCH,
    projectName: FIXTURE_PROJECT,
    folderPath: `${FIXTURE_WATCH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-05T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    cliType: (over['cliType'] ?? 'claude') as string | null,
    useOwnSession: null,
    lastUsage: null,
    execution: over['execution'] ?? null,
    commit: null,
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null
  };
}

function fixtureGrouped(): Record<string, unknown[]> {
  return {
    backlog: [jobInfo({ id: 'fx-back-1', title: 'Idea', state: '0-backlog' })],
    preparation: [jobInfo({ id: 'fx-prep-1', title: 'Drafting', state: '1-preparation' })],
    orchestratorPrep: [],
    ready: [jobInfo({ id: 'fx-ready-1', title: 'Ready to run', state: '2-ready' })],
    progress: [jobInfo({ id: 'fx-progress-1', title: 'Live run', state: '3-progress' })],
    failedPickup: [],
    autoReview: [jobInfo({ id: 'fx-auto-1', title: 'Orchestrator deciding', state: '4-auto-review' })],
    humanReview: [jobInfo({ id: 'fx-human-1', title: 'Awaiting your accept', state: '5-human-review' })],
    review: [],
    completed: [jobInfo({ id: 'fx-done-1', title: 'Wrapped up', state: '6-completed' })],
    archive: [jobInfo({ id: 'fx-arch-1', title: 'Old work', state: '7-archive' })]
  };
}

async function installBoardMocks(page: Page): Promise<void> {
  const grouped = fixtureGrouped();
  const allJobs = ([] as unknown[]).concat(...Object.values(grouped));

  await page.route('**/api/**', async (route) => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    await route.fallback();
  });
  await page.route('**/api/watch-paths', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify([{ name: FIXTURE_PROJECT, path: FIXTURE_WATCH, rootPath: FIXTURE_WATCH }]) });
  });
  await page.route('**/api/tasks', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(allJobs) });
  });
  await page.route('**/api/v1/studio/board', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) });
  });
  await page.route('**/api/v1/studio/runner/status', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ projects: { [FIXTURE_PROJECT]: { projectName: FIXTURE_PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }) });
  });
  await page.route('**/api/clients/**', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/environment', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) });
  });
  await page.route('**/api/git/summary', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/cli/quota', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-05T09:00:00Z', ttlSeconds: 600, snapshots: [] }) });
  });
  await page.route('**/api/cli/usage', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-05T09:00:00Z', sections: [] }) });
  });
  await page.route('**/api/git/projects', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/orchestrator/global', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ session: null }) });
  });
  await page.route('**/api/projects/*/settings', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ autoCommit: false, runnerMode: 'manual', orchestratorModel: null }) });
  });
  await page.route('**/api/dev-tools/flags', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ updateStableEnabled: false, deleteE2EJobsEnabled: false }) });
  });
}

async function laneCount(page: Page, container: 'backlog' | 'active' | 'decide'): Promise<number> {
  const c = page.getByTestId(`lane-group-${container}`);
  return await c.locator('app-job-column').count();
}

test.describe('Kanban container header / focus-expand', () => {
  test.use({ viewport: { width: 1600, height: 900 } });

  test.beforeEach(async ({ page }) => {
    await installBoardMocks(page);
    // Each Playwright test runs in its own browser context with empty
    // localStorage, so no explicit reset is needed. Avoid an init
    // script that wipes containers on every navigation - that would
    // also wipe the value across `page.reload()` and break the
    // persistence test.
  });

  test('default = all containers expanded with no header collapse toggles or summary strips', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    for (const id of ['backlog', 'active', 'decide'] as const) {
      await expect(page.getByTestId(`lane-group-${id}`)).toBeVisible();
      await expect(page.getByTestId(`lane-group-toggle-${id}`)).toHaveCount(0);
      await expect(page.getByTestId(`lane-group-strip-${id}`)).toHaveCount(0);
      expect(await laneCount(page, id)).toBeGreaterThan(0);
    }
  });

  test('focus-expand hides the other two; second click restores', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('lane-group-focus-active').click();
    await expect(page.getByTestId('lane-group-active')).toBeVisible();
    await expect(page.getByTestId('lane-group-backlog')).toBeHidden();
    await expect(page.getByTestId('lane-group-decide')).toBeHidden();
    expect(await laneCount(page, 'active')).toBeGreaterThan(0);

    await page.getByTestId('lane-group-focus-active').click();
    await expect(page.getByTestId('lane-group-backlog')).toBeVisible();
    await expect(page.getByTestId('lane-group-decide')).toBeVisible();
  });

  test('keyboard 1/2/3/0 drives container focus + reset', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    await page.locator('body').click();
    await page.keyboard.press('2');
    await expect(page.getByTestId('lane-group-active')).toBeVisible();
    await expect(page.getByTestId('lane-group-backlog')).toBeHidden();
    await expect(page.getByTestId('lane-group-decide')).toBeHidden();

    // `3` swaps focus to Decide.
    await page.keyboard.press('3');
    await expect(page.getByTestId('lane-group-decide')).toBeVisible();
    await expect(page.getByTestId('lane-group-backlog')).toBeHidden();
    await expect(page.getByTestId('lane-group-active')).toBeHidden();

    // `0` resets to all expanded.
    await page.keyboard.press('0');
    await expect(page.getByTestId('lane-group-backlog')).toBeVisible();
    await expect(page.getByTestId('lane-group-active')).toBeVisible();
    await expect(page.getByTestId('lane-group-decide')).toBeVisible();
  });

  test('focus state does not persist across reload', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('lane-group-focus-active').click();
    await expect(page.getByTestId('lane-group-backlog')).toBeHidden();

    await page.reload();
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('lane-group-backlog')).toBeVisible();
    await expect(page.getByTestId('lane-group-active')).toBeVisible();
    await expect(page.getByTestId('lane-group-decide')).toBeVisible();
  });

  test('focus-expand stays under the longtask budget', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });

    // Install a longtask observer; the spec requires < 50 ms per
    // focus-expand transition. By definition a
    // `longtask` is >= 50 ms - so the budget guard here is "no
    // longtasks fire during the transition window at all". Initial
    // render can produce its own longtask, so we mark a baseline
    // BEFORE clicking and only count entries logged after.
    await page.evaluate(() => {
      const w = window as unknown as { __longTasks: number[]; __longTasksBaseline: number };
      w.__longTasks = [];
      const obs = new PerformanceObserver((list) => {
        for (const e of list.getEntries()) {
          w.__longTasks.push(e.startTime);
        }
      });
      obs.observe({ entryTypes: ['longtask'] });
      w.__longTasksBaseline = performance.now();
    });
    // Settle a tick before transitions so first-paint longtasks land
    // before the baseline.
    await page.waitForTimeout(200);
    await page.evaluate(() => {
      (window as unknown as { __longTasksBaseline: number }).__longTasksBaseline = performance.now();
    });

    await page.getByTestId('lane-group-focus-active').click();
    await page.getByTestId('lane-group-focus-active').click();

    const transitionLongTasks = await page.evaluate(() => {
      const w = window as unknown as { __longTasks: number[]; __longTasksBaseline: number };
      return w.__longTasks.filter(t => t >= w.__longTasksBaseline);
    });
    expect(
      transitionLongTasks.length,
      `${transitionLongTasks.length} longtasks (>= 50ms) fired during container focus transitions; budget is 0`
    ).toBe(0);
  });
});
