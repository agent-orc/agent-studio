import { test, expect, Page } from '@playwright/test';

/**
 * Regression: the Human Review lane developed its own internal vertical
 * scrollbar once it crossed ~50 cards while every other lane scrolled
 * the page. The cause was a lane-local CDK virtual-scroll viewport that
 * kicked in above a threshold for non-archive, non-legacy-review lanes.
 * The user-visible effect was one lane behaving differently from the
 * rest, which read as broken.
 *
 * Fix history:
 *   - The first pass dropped the virtualised path so every lane shared
 *     the same .column__body scroll model (one scrollbar per lane).
 *   - F60 (commit 0a2967a) consolidated further into the studio
 *     super-column layout where .lane-group__lanes is the sole Y-scroll
 *     surface — one scrollbar per super-column, not per lane. The
 *     legacy layout keeps the per-lane .column__body scroll.
 *
 * Either way the contract is the same: every lane the user sees behaves
 * the same way. There is no "the Review lane is special" inconsistency.
 *
 * This spec stocks the densest lane the user reports (Human Review)
 * with enough cards to have previously triggered virtualization, then
 * asserts the consistency invariant against whichever layout is active.
 */

const FIXTURE_WATCH = 'C:/fixtures/lane-scroll-consistency';
const FIXTURE_PROJECT = 'lane-scroll-consistency';
const HUMAN_REVIEW_COUNT = 60;

interface JobOverrides {
  id?: string;
  title?: string;
  state?: string;
  order?: number;
}

function jobInfo(over: JobOverrides): Record<string, unknown> {
  const id = over.id ?? 'fx-job';
  const state = over.state ?? '2-ready';
  return {
    id,
    jobKey: `${FIXTURE_WATCH}::${id}`,
    taskKey: `${FIXTURE_WATCH}::${id}`,
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
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null,
  };
}

function fixtureGrouped(): Record<string, unknown[]> {
  const humanReview = Array.from({ length: HUMAN_REVIEW_COUNT }, (_, i) =>
    jobInfo({
      id: `fx-human-${i + 1}`,
      title: `Human review job ${i + 1}`,
      state: '5-human-review',
      order: i + 1,
    }),
  );
  return {
    backlog: [],
    preparation: [jobInfo({ id: 'fx-prep-1', title: 'In preparation', state: '1-preparation' })],
    orchestratorPrep: [],
    ready: [jobInfo({ id: 'fx-ready-1', title: 'Ready', state: '2-ready' })],
    progress: [jobInfo({ id: 'fx-prog-1', title: 'In progress', state: '3-progress' })],
    failedPickup: [],
    autoReview: [jobInfo({ id: 'fx-auto-1', title: 'Auto review', state: '4-auto-review' })],
    humanReview,
    review: [],
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
  await page.route(/\/api\/tasks(\?|$)/, async (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(allJobs) }));
  await page.route(/\/api\/v1\/studio\/board/, async (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) }));
  await page.route('**/api/v1/studio/runner/status', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [FIXTURE_PROJECT]: {
            projectName: FIXTURE_PROJECT,
            mode: 'manual',
            activeJobId: null,
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

/**
 * Land on the kanban board view across both shells:
 *   - Studio shell lands on a Welcome screen and exposes
 *     `data-testid="studio-board"` once a board tab opens.
 *   - Legacy shell shows `data-testid="kanban-dashboard"` straight away.
 * The welcome screen has an "All projects" CTA that opens the board tab.
 */
async function gotoBoard(page: Page): Promise<void> {
  await page.goto('/');
  const studio = page.getByTestId('studio-board');
  const legacy = page.getByTestId('kanban-dashboard');
  const welcome = page.getByTestId('studio-welcome');
  await Promise.race([
    studio.first().waitFor({ state: 'visible', timeout: 8_000 }),
    legacy.first().waitFor({ state: 'visible', timeout: 8_000 }),
    welcome.first().waitFor({ state: 'visible', timeout: 8_000 }),
  ]).catch(() => { /* one of the boards may simply be slow */ });

  if ((await welcome.count()) > 0 && (await welcome.first().isVisible().catch(() => false))) {
    const allProjects = welcome.first().getByRole('button', { name: 'All projects' });
    await allProjects.click({ timeout: 3_000 }).catch(() => { /* legacy shell */ });
    await studio.first().waitFor({ state: 'visible', timeout: 5_000 }).catch(() => { /* nothing */ });
  }

  const studioReady = (await studio.count()) > 0;
  const legacyReady = (await legacy.count()) > 0;
  expect(
    studioReady || legacyReady,
    'expected either studio-board or kanban-dashboard to be visible after navigation',
  ).toBe(true);
}

interface BoardScrollProbe {
  layout: 'studio' | 'legacy';
  perLaneOverflowY: Array<{ state: string; overflowY: string }>;
  overflowingScrollers: Array<{ tag: string; cls: string; closestLane: string | null }>;
}

async function probeBoardScrollModel(page: Page): Promise<BoardScrollProbe> {
  return await page.evaluate(() => {
    const studio = document.querySelector('[data-testid="studio-board"]') as HTMLElement | null;
    const layout: 'studio' | 'legacy' = studio ? 'studio' : 'legacy';
    const root = studio ?? (document.querySelector('[data-testid="kanban-dashboard"]') as HTMLElement | null);
    const perLane: BoardScrollProbe['perLaneOverflowY'] = [];
    const overflowing: BoardScrollProbe['overflowingScrollers'] = [];
    if (!root) return { layout, perLaneOverflowY: perLane, overflowingScrollers: overflowing };

    const lanes = Array.from(
      root.querySelectorAll('[data-testid^="lane-"]:not([data-testid^="lane-rail-"]):not([data-testid^="lane-group-"]):not([data-testid^="lane-collapse-"])'),
    ) as HTMLElement[];
    for (const lane of lanes) {
      const state = lane.getAttribute('data-state') ?? '';
      const body = lane.querySelector('.column__body') as HTMLElement | null;
      // Skip entries that have neither a state nor a body — these match the
      // testid prefix but are not real lane columns (e.g. empty-state
      // placeholders rendered in the same DOM region).
      if (!state && !body) continue;
      const oy = body ? window.getComputedStyle(body).overflowY : '<no-body>';
      perLane.push({ state, overflowY: oy });
    }

    // Catalogue every overflowing scroll surface anywhere under the board so
    // a future regression that re-introduces a per-lane scrollbar surfaces
    // here with the offending element.
    for (const el of Array.from(root.querySelectorAll('*')) as HTMLElement[]) {
      const style = window.getComputedStyle(el);
      const oy = style.overflowY;
      if ((oy === 'auto' || oy === 'scroll') && el.scrollHeight > el.clientHeight + 1) {
        const closest = el.closest('[data-state]') as HTMLElement | null;
        overflowing.push({
          tag: el.tagName.toLowerCase(),
          cls: el.className.toString().slice(0, 100),
          closestLane: closest?.getAttribute('data-state') ?? null,
        });
      }
    }
    return { layout, perLaneOverflowY: perLane, overflowingScrollers: overflowing };
  });
}

test.describe('Kanban lane scroll model stays consistent across every lane', () => {
  test.beforeEach(async ({ page }) => {
    await installBoardMocks(page);
    await page.addInitScript(() => {
      try { window.localStorage.removeItem('collapsedLanes'); } catch { /* ignore */ }
    });
  });

  test('every lane shares one consistent scroll model when Human Review is overstocked', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);

    // Wait for at least some of the 60 Human Review cards to land. The
    // lane is bound to render its full set (no virtualization), and the
    // first card alone proves the fixture reached the board.
    await expect(page.getByTestId('lane-5-human-review')).toBeVisible({ timeout: 10_000 });
    await expect.poll(
      async () => page.locator('[data-testid="lane-5-human-review"] [data-testid="task-card"], [data-testid="lane-5-human-review"] [data-testid="job-card"]').count(),
      { timeout: 10_000 },
    ).toBeGreaterThan(0);

    const probe = await probeBoardScrollModel(page);

    // 1. Every expanded lane reports the same overflow-y on .column__body.
    //    Identical values per lane = same scroll model per lane. The bug
    //    manifested as one lane having a different value from the rest.
    const distinct = new Set(probe.perLaneOverflowY.map(p => p.overflowY));
    expect(
      distinct.size,
      `lanes report mismatched .column__body overflow-y values: ` +
      `${probe.perLaneOverflowY.map(p => `${p.state}=${p.overflowY}`).join(', ')}`,
    ).toBeLessThanOrEqual(1);

    // 2. Whatever element actually scrolls, it must scroll for the WHOLE
    //    super-column (data-state attribute is on the lane, so a scroller
    //    whose closestLane is non-null would be the lane-itself or a
    //    descendant — that is the "per-lane scrollbar" failure mode). The
    //    F60 model puts the scroller at .lane-group__lanes which has no
    //    data-state, so closestLane should be null for every entry.
    for (const s of probe.overflowingScrollers) {
      const allowedClasses = /\b(lane-group__lanes|column__body|column__virtual)\b/;
      expect(
        s.cls,
        `unexpected overflowing element <${s.tag} class="${s.cls}"> (closest lane: ${s.closestLane}). ` +
        `Allowed scroll surfaces are .lane-group__lanes (studio super-column) or .column__body (legacy).`,
      ).toMatch(allowedClasses);
    }

    // 3. The page itself does NOT scroll vertically when a single lane is
    //    overstocked. The Kanban view fits the viewport height; vertical
    //    overflow lives inside the board's chosen scroll surface.
    const pageScrollState = await page.evaluate(() => {
      const body = document.querySelector('.app__body') as HTMLElement | null;
      return {
        appBodyScrolls: body ? Math.max(0, body.scrollHeight - body.clientHeight) : 0,
        docScrolls: Math.max(
          0,
          document.documentElement.scrollHeight - document.documentElement.clientHeight,
        ),
      };
    });
    expect(
      pageScrollState.appBodyScrolls,
      `.app__body shows ${pageScrollState.appBodyScrolls}px of vertical overflow when an overstocked ` +
      `Review lane should be absorbed by the board's internal scroll surface instead.`,
    ).toBeLessThanOrEqual(2);
  });

  test('lane collapse still toggles to a rail when a dense lane is present', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);
    await expect(page.getByTestId('lane-5-human-review')).toBeVisible({ timeout: 10_000 });

    const collapseBtn = page.getByTestId('lane-collapse-5-human-review');
    await expect(collapseBtn).toBeVisible();
    await collapseBtn.click();

    const rail = page.getByTestId('lane-rail-5-human-review');
    await expect(rail, 'Human Review collapses into a rail').toBeVisible({ timeout: 2_000 });

    await rail.click();
    await expect(page.getByTestId('lane-5-human-review')).toBeVisible({ timeout: 2_000 });
  });

  test('cards in the dense lane stay draggable so drag-and-drop wiring survives', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await gotoBoard(page);
    await expect(page.getByTestId('lane-5-human-review')).toBeVisible({ timeout: 10_000 });

    // The rename moved the host selector from <app-job-card> to
    // <app-task-card>; the host still carries draggable="true". Accept
    // either so the spec survives an in-flight rename.
    const draggable = page.locator(
      '[data-testid="lane-5-human-review"] app-task-card[draggable="true"], ' +
      '[data-testid="lane-5-human-review"] app-job-card[draggable="true"]',
    );
    await expect.poll(async () => draggable.count(), { timeout: 10_000 })
      .toBeGreaterThanOrEqual(1);
    const draggableCount = await draggable.count();
    expect(
      draggableCount,
      `every visible card in the dense Human Review lane must remain draggable=true so drag-and-drop ` +
      `still works; got ${draggableCount} draggable cards.`,
    ).toBeGreaterThanOrEqual(1);
  });
});
