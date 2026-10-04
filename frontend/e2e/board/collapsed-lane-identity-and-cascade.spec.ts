import { test, expect, type Page } from '@playwright/test';

/**
 * Regression spec for two collapsed-lane bugs (2026-05-27):
 *
 * Issue 1 — Collapsed lane loses identity: the rail must show icon,
 *           rotated title, count badge, and expand caret.
 * Issue 2 — Cascading collapse: collapsing one lane must NOT collapse
 *           any other lane.
 *
 * Both issues are tested in the studio (VS Code) layout which stacks
 * lanes vertically inside super-columns.
 */

const PROJECT = 'fixture-collapse-identity';
const WATCH_PATH = 'C:/fixtures/collapse-identity';

function makeJob(id: string, state: string, order: number) {
  return {
    id,
    jobKey: `${WATCH_PATH}::${id}`,
    title: `Job ${id}`,
    state,
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-05-27T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-27T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
  };
}

const GROUPED = {
  backlog: [],
  preparation: [makeJob('prep-1', '1-preparation', 1)],
  orchestratorPrep: [makeJob('op-1', '1a-orchestrator-prep', 1)],
  ready: [makeJob('ready-1', '2-ready', 1), makeJob('ready-2', '2-ready', 2)],
  progress: [makeJob('prog-1', '3-progress', 1)],
  failedPickup: [],
  review: [],
  autoReview: [makeJob('ar-1', '4-auto-review', 1)],
  humanReview: [makeJob('hr-1', '5-human-review', 1)],
  completed: [makeJob('done-1', '6-completed', 1)],
  archive: [makeJob('arch-1', '7-archive', 1)],
};

async function installRoutes(page: Page) {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined));

  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED) }));

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
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-27T08:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-27T08:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
    localStorage.removeItem('collapsedLanes');
  });
}

async function waitForBoard(page: Page): Promise<void> {
  await expect(page.locator('[data-testid="studio-board"]').first())
    .toBeVisible({ timeout: 10_000 });
  await expect(page.locator('[data-testid="job-card"]').first()).toBeVisible({ timeout: 10_000 });
  // Dismiss any toast overlay that might block clicks (e.g. "Update failed").
  const dismiss = page.getByText('Dismiss', { exact: true });
  if (await dismiss.isVisible({ timeout: 500 }).catch(() => false)) {
    await dismiss.click();
    await expect(dismiss).toBeHidden({ timeout: 2_000 });
  }
}

test.describe('Collapsed lane identity + cascade regression', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('Issue 2: collapsing one lane does NOT collapse siblings', async ({ page }, testInfo) => {
    await seedBoardTab(page);
    await installRoutes(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await waitForBoard(page);

    // All lanes should start expanded (we cleared collapsedLanes in localStorage).
    // Verify at least the Backlog group lanes are expanded.
    const readyLane = page.getByTestId('lane-2-ready');
    const prepLane = page.getByTestId('lane-1-preparation');
    const orchPrepLane = page.getByTestId('lane-1a-orchestrator-prep');
    await expect(readyLane).toBeVisible({ timeout: 3_000 });
    await expect(prepLane).toBeVisible({ timeout: 3_000 });

    // Verify Active group lanes are expanded.
    const progressLane = page.getByTestId('lane-3-progress');
    const autoReviewLane = page.getByTestId('lane-4-auto-review');
    await expect(progressLane).toBeVisible({ timeout: 3_000 });
    await expect(autoReviewLane).toBeVisible({ timeout: 3_000 });

    // Verify Done group lanes are expanded.
    const humanReviewLane = page.getByTestId('lane-5-human-review');
    const completedLane = page.getByTestId('lane-6-completed');
    await expect(humanReviewLane).toBeVisible({ timeout: 3_000 });
    await expect(completedLane).toBeVisible({ timeout: 3_000 });

    // Take a "before" screenshot.
    const beforeScreenshot = await page.screenshot({ fullPage: false });
    await testInfo.attach('before-collapse.png', { body: beforeScreenshot, contentType: 'image/png' });

    // Collapse the In Progress lane.
    const collapseBtn = page.getByTestId('lane-collapse-3-progress');
    await expect(collapseBtn).toBeVisible({ timeout: 3_000 });
    await collapseBtn.click();

    // The In Progress lane should now be a rail.
    const progressRail = page.getByTestId('lane-rail-3-progress');
    await expect(progressRail).toBeVisible({ timeout: 2_000 });

    // CRITICAL: All other lanes must remain EXPANDED (not collapsed into rails).
    // This is the cascade regression check.
    await expect(readyLane).toBeVisible({ timeout: 1_000 });
    await expect(autoReviewLane).toBeVisible({ timeout: 1_000 });
    await expect(humanReviewLane).toBeVisible({ timeout: 1_000 });
    await expect(completedLane).toBeVisible({ timeout: 1_000 });

    // Verify rails do NOT exist for the non-collapsed lanes.
    await expect(page.getByTestId('lane-rail-2-ready')).toHaveCount(0);
    await expect(page.getByTestId('lane-rail-4-auto-review')).toHaveCount(0);
    await expect(page.getByTestId('lane-rail-5-human-review')).toHaveCount(0);
    await expect(page.getByTestId('lane-rail-6-completed')).toHaveCount(0);

    const afterScreenshot = await page.screenshot({ fullPage: false });
    await testInfo.attach('after-single-collapse.png', { body: afterScreenshot, contentType: 'image/png' });
    if (process.env.RESULTS_DIR) {
      const fs = await import('fs');
      const path = await import('path');
      fs.mkdirSync(process.env.RESULTS_DIR, { recursive: true });
      fs.writeFileSync(path.join(process.env.RESULTS_DIR, 'after-single-collapse.png'), afterScreenshot);
    }
  });

  test('Issue 1: collapsed rail shows icon, title, count, and expand caret', async ({ page }, testInfo) => {
    await seedBoardTab(page);
    await installRoutes(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await waitForBoard(page);

    // Collapse the In Progress lane (has 1 job).
    const collapseBtn = page.getByTestId('lane-collapse-3-progress');
    await expect(collapseBtn).toBeVisible({ timeout: 3_000 });
    await collapseBtn.click();
    const rail = page.getByTestId('lane-rail-3-progress');
    await expect(rail).toBeVisible({ timeout: 2_000 });

    // The rail must show the lane icon.
    const icon = rail.locator('.column-rail__icon');
    await expect(icon).toBeVisible();
    const iconText = await icon.textContent();
    expect(iconText?.trim()).toBeTruthy();

    // The rail must show the lane title.
    const title = rail.locator('.column-rail__title');
    await expect(title).toBeVisible();
    const titleText = await title.textContent();
    expect(titleText?.trim().length).toBeGreaterThan(0);

    // The rail must show the count badge.
    const count = rail.locator('.column-rail__count');
    await expect(count).toBeVisible();
    const countText = await count.textContent();
    expect(countText?.trim()).toBe('1');

    // The rail must show the expand caret.
    const expand = rail.locator('.column-rail__expand');
    await expect(expand).toBeVisible();

    // Verify the title and icon have non-zero rendered dimensions.
    const titleBox = await title.boundingBox();
    expect(titleBox).not.toBeNull();
    expect(titleBox!.width).toBeGreaterThan(0);
    expect(titleBox!.height).toBeGreaterThan(0);

    const iconBox = await icon.boundingBox();
    expect(iconBox).not.toBeNull();
    expect(iconBox!.width).toBeGreaterThan(0);
    expect(iconBox!.height).toBeGreaterThan(0);

    const railScreenshot = await page.screenshot({ fullPage: false });
    await testInfo.attach('collapsed-rail-identity.png', { body: railScreenshot, contentType: 'image/png' });
    if (process.env.RESULTS_DIR) {
      const fs = await import('fs');
      const path = await import('path');
      fs.mkdirSync(process.env.RESULTS_DIR, { recursive: true });
      fs.writeFileSync(path.join(process.env.RESULTS_DIR, 'collapsed-rail-identity.png'), railScreenshot);
    }
  });

  test('collapsed state persists across reload per lane-id', async ({ page }) => {
    // Seed tab config AND pre-collapsed state via addInitScript. Unlike
    // the other tests we do NOT clear collapsedLanes on reload - the
    // whole point is to verify persistence.
    await page.addInitScript(() => {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1,
        tabs: [{ kind: 'board', projectName: '__all__' }],
        activeKey: 'board:__all__',
      }));
      // Seed one collapsed lane as if the user had collapsed it before.
      // The init script runs before Angular boots, so the service reads
      // this value from localStorage and starts with the lane collapsed.
      localStorage.setItem('collapsedLanes', JSON.stringify(['3-progress']));
    });
    await installRoutes(page);
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await expect(page.locator('[data-testid="studio-board"]').first())
      .toBeVisible({ timeout: 10_000 });
    // Dismiss toast if present.
    const dismiss = page.getByText('Dismiss', { exact: true });
    if (await dismiss.isVisible({ timeout: 500 }).catch(() => false)) {
      await dismiss.click();
    }

    // In-progress should be collapsed (rail visible, expanded column hidden).
    await expect(page.getByTestId('lane-rail-3-progress')).toBeVisible({ timeout: 3_000 });
    await expect(page.getByTestId('lane-3-progress')).toHaveCount(0);

    // Other lanes must still be expanded.
    await expect(page.getByTestId('lane-2-ready')).toBeVisible({ timeout: 3_000 });
    await expect(page.getByTestId('lane-4-auto-review')).toBeVisible({ timeout: 3_000 });

    // Reload and verify same state persists.
    await page.reload();
    await page.waitForLoadState('domcontentloaded');
    await expect(page.locator('[data-testid="studio-board"]').first())
      .toBeVisible({ timeout: 10_000 });
    const dismiss2 = page.getByText('Dismiss', { exact: true });
    if (await dismiss2.isVisible({ timeout: 500 }).catch(() => false)) {
      await dismiss2.click();
    }

    await expect(page.getByTestId('lane-rail-3-progress')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByTestId('lane-2-ready')).toBeVisible({ timeout: 3_000 });
    await expect(page.getByTestId('lane-4-auto-review')).toBeVisible({ timeout: 3_000 });
  });
});
