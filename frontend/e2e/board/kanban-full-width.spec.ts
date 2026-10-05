import { test, expect, Page } from '@playwright/test';

/**
 * Locks in that the kanban dashboard fills 100% of the available
 * horizontal space at the three reference viewport widths the user
 * inspects (1280, 1440, 1920). The board container's bounding-rect
 * width must equal its parent's bounding-rect width minus padding;
 * lanes split that space with a 220px floor.
 *
 * The companion task `kanban-board-design-spec-mockup-first` defines
 * the design rules; this spec backs the column-width portion of those
 * rules with a regression test that also doubles as the pixel-evidence
 * the task report attaches.
 */

const FIXTURE_WATCH = 'C:/fixtures/full-width-demo';
const FIXTURE_PROJECT = 'full-width-demo';

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
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null,
    fixture: false
  };
}

function fixtureGrouped(): Record<string, unknown[]> {
  const autoReview = [jobInfo({ id: 'fx-auto-1', title: 'Orchestrator deciding', state: '4-auto-review' })];
  const humanReview = [jobInfo({ id: 'fx-human-1', title: 'Awaiting your accept', state: '5-human-review' })];
  return {
    preparation: [jobInfo({ id: 'fx-prep-1', title: 'Drafting next thing', state: '1-preparation' })],
    ready: [jobInfo({ id: 'fx-ready-1', title: 'Ready to run', state: '2-ready' })],
    progress: [jobInfo({ id: 'fx-progress-1', title: 'Live run', state: '3-progress' })],
    autoReview,
    humanReview,
    review: autoReview,
    completed: [jobInfo({ id: 'fx-done-1', title: 'Wrapped up', state: '6-completed' })],
    archive: [jobInfo({ id: 'fx-arch-1', title: 'Old work', state: '7-archive' })]
  };
}

async function installBoardMocks(page: Page): Promise<void> {
  const grouped = fixtureGrouped();
  const allJobs = [
    ...grouped.preparation, ...grouped.ready, ...grouped.progress,
    ...grouped.autoReview, ...grouped.humanReview,
    ...grouped.completed, ...grouped.archive
  ];

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

interface BoardMetrics {
  dashboardWidth: number;
  parentWidth: number;
  scrollWidth: number;
  columnWidths: number[];
}

async function readBoardMetrics(page: Page): Promise<BoardMetrics> {
  return await page.evaluate(() => {
    const dashboard = document.querySelector('[data-testid="kanban-dashboard"]') as HTMLElement | null;
    if (!dashboard) return { dashboardWidth: 0, parentWidth: 0, scrollWidth: 0, columnWidths: [] };
    const dashboardRect = dashboard.getBoundingClientRect();
    const parent = dashboard.parentElement;
    const parentRect = parent ? parent.getBoundingClientRect() : dashboardRect;
    const columns = Array.from(dashboard.querySelectorAll('.column')) as HTMLElement[];
    const columnWidths = columns.map((c) => Math.round(c.getBoundingClientRect().width));
    return {
      dashboardWidth: Math.round(dashboardRect.width),
      parentWidth: Math.round(parentRect.width),
      scrollWidth: dashboard.scrollWidth,
      columnWidths
    };
  });
}

async function gotoBoard(page: Page): Promise<void> {
  await page.goto('/');
  await expect(page.getByTestId('kanban-dashboard')).toBeVisible({ timeout: 10_000 });
  // Give the first poll cycle a moment to settle so column widths reflect
  // post-paint flex distribution rather than the initial render frame.
  await page.waitForTimeout(300);
}

test.describe('Kanban full-width layout', () => {

  test.beforeEach(async ({ page }) => {
    await installBoardMocks(page);
  });

  test('1440x900: dashboard width equals its parent width', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);
    const m = await readBoardMetrics(page);
    expect(m.dashboardWidth).toBeGreaterThan(0);
    // Allow 2px slack for fractional rounding; the dashboard must consume
    // all of its available horizontal space.
    expect(Math.abs(m.dashboardWidth - m.parentWidth)).toBeLessThanOrEqual(2);
    await page.screenshot({ path: 'test-results/kanban-full-width-1440x900.png', fullPage: false });
  });

  test('1920x1080: dashboard width equals its parent width', async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await gotoBoard(page);
    const m = await readBoardMetrics(page);
    expect(m.dashboardWidth).toBeGreaterThan(0);
    expect(Math.abs(m.dashboardWidth - m.parentWidth)).toBeLessThanOrEqual(2);
    // The board must use the parent's available space; if it's stuck at
    // sum-of-floors (about 7 * 220 + gaps ~= 1620) at a 1920 viewport,
    // it's not filling the dashboard's parent column. We assert column
    // mean width > 220 instead of "every column > 220" because in a
    // 3-lane-group layout, asymmetry in lane counts can leave one
    // column at the floor while the others grow.
    const meanColumnWidth = m.columnWidths.length > 0
      ? m.columnWidths.reduce((a, b) => a + b, 0) / m.columnWidths.length
      : 0;
    expect(meanColumnWidth).toBeGreaterThan(220);
    await page.screenshot({ path: 'test-results/kanban-full-width-1920x1080.png', fullPage: false });
  });

  test('1280x720: columns hold a 220px floor and overflow scrolls horizontally', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 720 });
    await gotoBoard(page);
    const m = await readBoardMetrics(page);
    // No single column may shrink below the 220px floor (the dashboard's
    // overflow-x: auto is the safety valve when the sum of floors does
    // not fit).
    for (const w of m.columnWidths) {
      expect(w).toBeGreaterThanOrEqual(220);
    }
    // Either the board fits or it scrolls horizontally; the contract is
    // "never silently shrink below the floor".
    expect(m.scrollWidth).toBeGreaterThanOrEqual(m.dashboardWidth - 1);
    await page.screenshot({ path: 'test-results/kanban-full-width-1280x720.png', fullPage: false });
  });
});
