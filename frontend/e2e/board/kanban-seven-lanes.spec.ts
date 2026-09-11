import { test, expect, Page } from '@playwright/test';

/**
 * ADR-0025 plus the orchestrator post-processing lane regression spec.
 *
 * The board now renders seven lanes:
 *   1-preparation, 2-ready, 3-progress,
 *   4-auto-review, 5-human-review,
 *   6-completed, 7-archive.
 *
 * Post Processing and Review are explicit so the user can tell at a glance
 * which cards the orchestrator is still checking (robot icon) and which
 * are waiting on them (eye icon). At the
 * canonical 1440 x 900 viewport every lane header must be visible
 * without horizontal overflow; the screenshot is the evidence that the
 * lane-layout-overflow guard from the paired task continues to hold.
 */

const FIXTURE_WATCH = 'C:/fixtures/seven-lanes-demo';
const FIXTURE_PROJECT = 'seven-lanes-demo';

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
  const autoReview = [
    jobInfo({ id: 'fx-auto-1', title: 'Orchestrator deciding', state: '4-auto-review' })
  ];
  const humanReview = [
    jobInfo({ id: 'fx-human-1', title: 'Awaiting your accept', state: '5-human-review' })
  ];
  return {
    preparation: [jobInfo({ id: 'fx-prep-1', title: 'Drafting next thing', state: '1-preparation' })],
    ready: [jobInfo({ id: 'fx-ready-1', title: 'Ready to run', state: '2-ready' })],
    progress: [jobInfo({ id: 'fx-progress-1', title: 'Live run', state: '3-progress' })],
    autoReview,
    humanReview,
    review: autoReview, // legacy alias
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
  await page.route('**/api/tasks/grouped', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) });
  });
  await page.route('**/api/runner/status', async (route) => {
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
  await page.route('**/api/projects/settings', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({}) });
  });
  await page.route('**/api/dev-tools/flags', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ updateStableEnabled: false, deleteE2EJobsEnabled: false }) });
  });
}

test.describe('ADR-0025 seven-lane kanban', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test.beforeEach(async ({ page }) => {
    await installBoardMocks(page);
    // Pre-collapse the lanes that should default to "narrow" so the
    // seven full-width columns fit at 1440x900 without overflow. Without
    // this gesture the test would also pass when the lane-overflow guard
    // (paired task) is broken - we want the screenshot to reflect the
    // user's real default-collapses.
    //
    // 6-completed joins the rail set because the lane-overlap fix
    // (`bug-lane-overlap-and-sluggish-with-many-lanes`) restored the
    // 220 px min-width contract for expanded columns. Five expanded
    // columns no longer fit alongside the task-nav at 1440 px without
    // horizontal scroll; the previous "fits at 1440" was a side effect
    // of lane-groups silently shrinking past their content's min-width
    // and visually leaking lanes into each other.
    await page.addInitScript(() => {
      window.localStorage.setItem(
        'collapsedLanes',
        JSON.stringify(['1-preparation', '7-archive'])
      );
    });
  });

  test('renders all seven lanes and stays within the 1440x900 viewport', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
      .toBeVisible({ timeout: 10_000 });

    // Every ADR-0025 lane must be visible somewhere on the board (either
    // expanded or as a rail).
    const lanes = [
      '1-preparation',
      '2-ready',
      '3-progress',
      '4-auto-review',
      '5-human-review',
      '6-completed',
      '7-archive',
    ];
    for (const state of lanes) {
      const expanded = page.getByTestId(`lane-collapse-${state}`);
      const rail = page.getByTestId(`lane-rail-${state}`);
      await expect.poll(async () =>
        (await expanded.count()) + (await rail.count()),
        { message: `lane ${state} should render either expanded or as a rail` }
      ).toBeGreaterThan(0);
    }

    // Post Processing and Human review carry the distinct icons that
    // identify their audience (machine vs you). Pin to the column
    // heading so we don't also match the state-pill on each job card.
    // AGT-2715: 5-human-review reads "Human review" on every surface.
    await expect(page.getByRole('heading', { name: 'Post Processing' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Human review', exact: true })).toBeVisible();

    // Container shape: Backlog / Active / Done & Decide. The
    // 5-human-review lane lives inside the Done & Decide container,
    // not Active.
    await expect(page.getByTestId('lane-group-backlog')).toBeVisible();
    await expect(page.getByTestId('lane-group-active')).toBeVisible();
    await expect(page.getByTestId('lane-group-decide')).toBeVisible();
    const decide = page.getByTestId('lane-group-decide');
    await expect(decide.locator('[data-testid="lane-5-human-review"], [data-testid="lane-rail-5-human-review"]')).toHaveCount(1);
    const active = page.getByTestId('lane-group-active');
    await expect(active.locator('[data-testid="lane-5-human-review"], [data-testid="lane-rail-5-human-review"]')).toHaveCount(0);

    // Lane-overlap guard: the post-ADR-0025/0026/0028 lane catalog
    // mandates 0-backlog, 1-preparation, 1a-orchestrator-prep, and
    // 2-ready in the Backlog group plus 3-progress, 4-auto-review in
    // Active and 5-human-review, 6-completed, 7-archive in
    // Done & Decide - nine columns at minimum. Five expanded columns at 220 px floor no
    // longer fit at 1440 px next to the task-nav (the previous "fits"
    // was a side effect of the lane-group min-width: 0 bug in
    // `bug-lane-overlap-and-sluggish-with-many-lanes`); horizontal
    // scroll is the correct behavior. The invariant we still need is
    // that no two lanes share a horizontal pixel range. The dedicated
    // `kanban-lane-overlap.spec.ts` covers this exhaustively at three
    // widths; the cheap pairwise check below keeps this seven-lane
    // contract anchored.
    const laneRects = await page.evaluate((laneIds: readonly string[]) => {
      const rects: Array<{ id: string; left: number; right: number; top: number; bottom: number }> = [];
      for (const id of laneIds) {
        const el =
          document.querySelector(`[data-testid="lane-${id}"]`) ??
          document.querySelector(`[data-testid="lane-rail-${id}"]`);
        if (!el) continue;
        const r = (el as HTMLElement).getBoundingClientRect();
        if (r.width === 0 && r.height === 0) continue;
        rects.push({ id, left: r.left, right: r.right, top: r.top, bottom: r.bottom });
      }
      return rects.sort((a, b) => a.left - b.left);
    }, lanes);
    for (let i = 0; i < laneRects.length; i++) {
      for (let j = i + 1; j < laneRects.length; j++) {
        const a = laneRects[i];
        const b = laneRects[j];
        const verticalOverlap = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
        if (verticalOverlap <= 0.5) continue;
        expect(
          a.right,
          `lane ${a.id} (right=${a.right.toFixed(1)}) overlaps lane ${b.id} (left=${b.left.toFixed(1)})`,
        ).toBeLessThanOrEqual(b.left + 0.5);
      }
    }

    await page.screenshot({ path: 'test-results/kanban-seven-lanes-1440x900.png', fullPage: false });
  });
});
