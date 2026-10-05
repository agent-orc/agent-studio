import { test, expect, Page } from '@playwright/test';

/**
 * Regression: the Ready lane was visibly wider than the other lanes and
 * surfaced an unexplained horizontal scroll indicator at the bottom of
 * the lane, even though the cards fit horizontally.
 *
 * Root cause was the lane-group flex distribution: `.lane-group` used
 * `flex: 1 1 auto`, so the dashboard's leftover horizontal space was
 * split equally across the three groups (Backlog / Active / Done &
 * Decide). Lanes inside groups with fewer expanded lanes therefore
 * grew wider per lane than lanes inside groups with more expanded
 * lanes. The Ready lane sits in the Backlog group, which usually has
 * the fewest expanded lanes when Orch Prep / Needs Clar / Intake are
 * empty, so it was the most visible offender.
 *
 * The acceptance criteria pinned in the bug task:
 *   - All expanded lanes share the same rendered width within 4 px.
 *   - No expanded lane has a horizontal scrollbar (scrollWidth fits
 *     inside clientWidth).
 *   - Vertical content overflow still scrolls the page normally.
 *
 * The fixture seeds 60 jobs into 2-ready so the Ready column is the
 * tallest column on the board, the same shape that produced the
 * screenshot in the bug report.
 */

const FIXTURE_WATCH = 'C:/fixtures/ready-lane-width';
const FIXTURE_PROJECT = 'ready-lane-width';

interface JobOverrides {
  id?: string;
  title?: string;
  state?: string;
  order?: number;
  cliType?: string | null;
  execution?: unknown;
  pendingIntent?: unknown;
}

function jobInfo(over: JobOverrides): Record<string, unknown> {
  const id = over.id ?? 'fx-job';
  const state = over.state ?? '2-ready';
  return {
    id,
    jobKey: `${FIXTURE_WATCH}::${id}`,
    title: over.title ?? id,
    state,
    order: over.order ?? 1,
    agent: 'claude',
    createdAt: '2026-05-05T08:00:00Z',
    watchPath: FIXTURE_WATCH,
    projectName: FIXTURE_PROJECT,
    folderPath: `${FIXTURE_WATCH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-05T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    cliType: over.cliType ?? 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: over.execution ?? null,
    commit: null,
    pendingIntent: over.pendingIntent ?? null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null,
  };
}

function fixtureGrouped(): Record<string, unknown[]> {
  const ready = Array.from({ length: 60 }, (_, i) =>
    jobInfo({
      id: `fx-ready-${i + 1}`,
      title: `Ready job ${i + 1}`,
      state: '2-ready',
      order: i + 1,
    }),
  );
  return {
    backlog: [],
    preparation: [jobInfo({ id: 'fx-prep-1', title: 'In preparation', state: '1-preparation' })],
    orchestratorPrep: [],
    ready,
    progress: [
      jobInfo({
        id: 'fx-progress-1',
        title: 'Live run',
        state: '3-progress',
        cliType: 'claude',
        execution: {
          jobId: 'fx-progress-1',
          jobKey: `${FIXTURE_WATCH}::fx-progress-1`,
          processId: 9001,
          startedAt: '2026-05-05T08:55:00Z',
          status: 'running',
          exitCode: null,
          durationSeconds: null,
          model: 'claude-opus-4-7',
        },
      }),
    ],
    failedPickup: [],
    autoReview: [jobInfo({ id: 'fx-auto-1', title: 'Auto review', state: '4-auto-review' })],
    humanReview: [jobInfo({ id: 'fx-human-1', title: 'Human review', state: '5-human-review' })],
    review: [jobInfo({ id: 'fx-auto-1', title: 'Auto review', state: '4-auto-review' })],
    completed: [jobInfo({ id: 'fx-done-1', title: 'Completed', state: '6-completed' })],
    archive: [jobInfo({ id: 'fx-arch-1', title: 'Archive', state: '7-archive' })],
  };
}

async function installBoardMocks(page: Page): Promise<void> {
  const grouped = fixtureGrouped();
  const allJobs = Object.values(grouped).flat();

  await page.route('**/api/**', async (route) => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    await route.fallback();
  });
  await page.route('**/api/watch-paths', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: FIXTURE_PROJECT, path: FIXTURE_WATCH, rootPath: FIXTURE_WATCH }]),
    });
  });
  await page.route('**/api/tasks', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(allJobs) });
  });
  await page.route('**/api/v1/studio/board', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) });
  });
  await page.route('**/api/v1/studio/runner/status', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [FIXTURE_PROJECT]: {
            projectName: FIXTURE_PROJECT,
            mode: 'manual',
            activeJobId: 'fx-progress-1',
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    });
  });
  await page.route('**/api/clients/**', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/environment', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    });
  });
  await page.route('**/api/git/summary', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/git/projects', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/cli/quota', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-05T09:00:00Z', ttlSeconds: 600, snapshots: [] }),
    });
  });
  await page.route('**/api/cli/usage', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-05T09:00:00Z', sections: [] }),
    });
  });
  await page.route('**/api/orchestrator/global', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ session: null }) });
  });
  await page.route('**/api/projects/*/settings', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ autoCommit: false, runnerMode: 'manual', orchestratorModel: null }),
    });
  });
  await page.route('**/api/dev-tools/flags', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ updateStableEnabled: false, deleteE2EJobsEnabled: false }),
    });
  });
}

interface ColumnMetric {
  state: string;
  width: number;
  scrollWidth: number;
  clientWidth: number;
}

async function readColumnMetrics(page: Page): Promise<ColumnMetric[]> {
  return await page.evaluate(() => {
    const columns = Array.from(document.querySelectorAll('.column')) as HTMLElement[];
    return columns.map((c) => ({
      state: c.getAttribute('data-state') ?? '',
      width: Math.round(c.getBoundingClientRect().width),
      scrollWidth: c.scrollWidth,
      clientWidth: c.clientWidth,
    }));
  });
}

test.describe('Ready lane width parity and lack of horizontal scrollbar', () => {
  test.beforeEach(async ({ page }) => {
    await installBoardMocks(page);
    await page.addInitScript(() => {
      try {
        window.localStorage.removeItem('collapsedLanes');
        // Pre-seed the studio tab so the board renders on startup instead
        // of the Welcome pane (vsCodeLayout is default since 2026).
        localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
          v: 1,
          tabs: [{ kind: 'board', projectName: '__all__' }],
          activeKey: 'board:__all__',
        }));
      } catch {
        /* ignore */
      }
    });
  });

  test('all expanded lanes have equal width and no horizontal overflow at 1920x1080', async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await page.goto('/');
    await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
      .toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('lane-2-ready')).toBeVisible();

    // Wait for the Ready lane's 60 cards to settle.
    await expect.poll(async () =>
      await page.locator('[data-testid="lane-2-ready"] [data-testid="job-card"]').count(),
      { timeout: 5_000 }
    ).toBeGreaterThanOrEqual(60);

    const metrics = await readColumnMetrics(page);
    expect(metrics.length, 'expected at least 6 expanded lanes').toBeGreaterThanOrEqual(6);

    // No expanded lane may render a horizontal scrollbar when the cards
    // fit. scrollWidth <= clientWidth is the contract.
    for (const m of metrics) {
      expect(
        m.scrollWidth,
        `lane ${m.state} has scrollWidth=${m.scrollWidth} > clientWidth=${m.clientWidth}, ` +
        `which renders an unexpected horizontal scrollbar`,
      ).toBeLessThanOrEqual(m.clientWidth);
    }

    // All expanded lanes must share the same rendered width within 4 px.
    // The 4 px tolerance covers fractional flex distribution.
    const widths = metrics.map((m) => m.width);
    const minW = Math.min(...widths);
    const maxW = Math.max(...widths);
    expect(
      maxW - minW,
      `expanded lane widths span ${minW}px..${maxW}px (delta ${maxW - minW}px). ` +
      `By state: ${metrics.map((m) => `${m.state}=${m.width}`).join(', ')}. ` +
      `Lanes must share equal width across all groups; flex-grow on .lane-group ` +
      `must be proportional to expanded-lane count so each lane gets an equal share.`,
    ).toBeLessThanOrEqual(4);
  });
});
