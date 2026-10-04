import { test, expect, type Page } from '@playwright/test';

/**
 * Bug `bug-f5-reload-lands-on-job-detail-instead-of-current-view`:
 * opening a task detail writes `?job=<id>&watchPath=<wp>` to the URL via
 * `history.replaceState`. The close/leave path only partially cleared it,
 * so the stale `?job=` lingered after the user switched back to the board.
 * On F5 the boot-time `restoreFromUrl()` then re-opened that stale detail —
 * the user was thrown onto a task instead of the view they were on.
 *
 * The fix makes the active studio tab the single source of truth for the
 * selection + the `?job=` param (app.ts active-tab→selection effect +
 * TaskSelectionService.openDetailByTaskKey / clearSelectionForTabSwitch):
 *
 *   - Active tab is a task  → `?job=` is (re)written so the deep-link is
 *     honest and a reload restores that task.
 *   - Active tab is NOT a task (board / project / …) → the selection is
 *     dropped and `?job=` is stripped, so the next F5 returns to the board.
 *
 * Each test maps to an acceptance bullet: F5 on a task → task; F5 after
 * leaving the task → board (no stale re-open); deep-link still opens.
 */

const STICKY_TAB_KEY = 'board:__all__';

// taskKey === `${watchPath}::${id}` (see TaskService). The studio task tab
// is keyed `task:<taskKey>`.
const TASK_ID = 'reload-fix-task';
const WATCH_PATH = 'demo-project';
const TASK_KEY = `${WATCH_PATH}::${TASK_ID}`;
const TASK_TAB_KEY = `task:${TASK_KEY}`;

/** Empty-but-valid GroupedJobs payload (mirrors the service signal default). */
const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [], progress: [], failedPickup: [], codeNotComplete: [],
  review: [], autoReview: [], humanReview: [], completed: [], archive: [],
};

/** Minimal-but-type-complete TaskDetail so `GET /api/tasks/<id>` succeeds. */
const TASK_DETAIL = {
  info: {
    id: TASK_ID,
    taskKey: TASK_KEY,
    key: null,
    title: 'Reload fix demo task',
    state: '3-progress',
    order: 0,
    agent: 'demo-agent',
    createdAt: '2026-06-04T00:00:00Z',
    watchPath: WATCH_PATH,
    projectName: 'demo-project',
    folderPath: '/tmp/demo-project/3-progress/reload-fix-task',
    lastActivity: '2026-06-04T00:00:00Z',
    sessionName: null,
    model: null,
    cliType: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
  },
  promptMarkdown: 'demo prompt',
  promptHistory: [],
  titleHistory: [],
  statusMarkdown: null,
  contextUsage: null,
  log: [],
  summaryState: null,
  reviewEvidence: [],
};

/**
 * Stub the boot endpoints (so no blocking error-dialog overlay appears) plus
 * the task-detail GET (so the active-task tab actually resolves a selection
 * and writes `?job=`). Everything else is left to fail inline / non-blocking,
 * exactly like `navigation-no-deadend.spec.ts`.
 */
async function stubApi(page: Page, opts: { resolveDetail?: boolean } = {}): Promise<void> {
  const { resolveDetail = true } = opts;
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const json = (body: unknown) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    // The task-detail GET is `/api/v1/projects/<project>/tasks/<id>?watchPath=...`;
    // match it before the bare-list rule below (which requires `/api/tasks` to
    // be followed by `?` or end-of-string and so never matches the `/<id>` form).
    if (url.includes(`/api/tasks/${TASK_ID}`)
      || new RegExp(`/api/v1/projects/[^/]+/tasks/${TASK_ID}(\\?|$)`).test(url)) {
      // Tests 1 & 2 (`resolveDetail: false`) assert only on the `?job=` param
      // and the active tab. `openDetailByTaskKey` writes `?job=` *synchronously*
      // before this fetch, the active tab comes from the persisted localStorage
      // state, and the app.ts active-tab effect strips `?job=` on a task→board
      // switch via its `studioActiveTabWasTask` flag — none of which need the
      // detail to resolve. So leaving the GET pending keeps every assertion
      // valid while the heavy <app-job-detail> @defer chunk (slow to compile on
      // the shared dev server under load) never mounts and its sub-resource
      // GETs never 404 into a modal error dialog that would eat the tab click.
      //
      // Test 3 (deep-link) MUST resolve it: there the URL-restore → mirror
      // effect is the only thing that opens the task tab.
      if (!resolveDetail) return; // leave the request hanging (no fulfill)
      return json(TASK_DETAIL);
    }
    if (url.includes('/api/v1/studio/board')) return json(EMPTY_GROUPED);
    if (url.includes('/api/v1/studio/runner/status')) return json({ projects: {} });
    if (/\/api\/tasks(\?|$)/.test(url)) return json([]);
    if (url.includes('/api/watch-paths')) {
      return json([{ name: WATCH_PATH, path: WATCH_PATH }]);
    }
    // Everything else (the detail pane's sub-resource GETs, quota, clients, …)
    // is left to the live backend / inline non-blocking handling, exactly like
    // `navigation-no-deadend.spec.ts`. The fake task's sub-resources 404 and
    // surface a (delayed, ~1s) inline error pane; the assertions below run
    // before it appears and never interact with the detail body, so it does
    // not affect them. Stubbing these with a wrong-shaped success would crash
    // a consumer and break the boot instead.
    return route.continue();
  });
}

function tabBy(page: Page, key: string) {
  return page.locator(`.studio-tab[data-tab-key="${key}"]`);
}

// Booting the studio editor onto an active task tab eagerly loads the
// heavy <app-job-detail> @defer chunks (HMR forces them eager in dev), which
// the dev server can be slow to compile on first hit — so the app-root waits
// here are generous. A production build pre-bundles these chunks. The shared
// dev backend additionally warrants higher budgets (see repo guidance: a
// PW_TARGET=dev run wants ~180s timeouts under load).
const BOOT_TIMEOUT = 60_000;

/** Seed the persisted studio tab state, then reload so the service restores it. */
async function seedTabsAndReload(page: Page, activeKey: string): Promise<void> {
  await seedStudioTabsAndReload(page, [
    { kind: 'board', projectName: '__all__' },
    { kind: 'task', taskKey: TASK_KEY },
  ], activeKey);
}

async function seedStudioTabsAndReload(
  page: Page,
  tabs: Array<Record<string, unknown>>,
  activeKey: string,
  hash = '',
): Promise<void> {
  await page.evaluate(({ payload, nextHash }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(payload));
    history.replaceState(null, '', nextHash ? `/${nextHash}` : '/');
  }, { payload: { v: 1, tabs, activeKey }, nextHash: hash });
  await page.reload();
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
}

test.describe('studio-shell · reload restores the current view (F5 bug)', () => {
  test.setTimeout(180_000);

  test('reload on a task tab restores that task and writes ?job=', async ({ page }) => {
    await stubApi(page, { resolveDetail: false });
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });

    await seedTabsAndReload(page, TASK_TAB_KEY);

    // The persisted task tab is active and the active-tab→selection effect
    // re-published the deep-link param for it.
    await expect(tabBy(page, TASK_TAB_KEY)).toHaveClass(/studio-tab--active/);
    await expect.poll(() => new URL(page.url()).searchParams.get('job')).toBe(TASK_ID);
  });

  test('leaving the task for the board strips ?job= so a reload stays on the board', async ({ page }) => {
    await stubApi(page, { resolveDetail: false });
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });

    await seedTabsAndReload(page, TASK_TAB_KEY);
    // Precondition: we are on the task and the URL carries its `?job=`.
    await expect.poll(() => new URL(page.url()).searchParams.get('job')).toBe(TASK_ID);

    // Switch to the board tab — the exact action that used to leave a stale
    // `?job=` behind.
    await tabBy(page, STICKY_TAB_KEY).click();
    await expect(tabBy(page, STICKY_TAB_KEY)).toHaveClass(/studio-tab--active/);
    await expect.poll(() => new URL(page.url()).searchParams.has('job')).toBe(false);

    // F5: the board must win — no jump back to the stale task detail.
    await page.reload();
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
    await expect(tabBy(page, STICKY_TAB_KEY)).toHaveClass(/studio-tab--active/);
    await expect(tabBy(page, TASK_TAB_KEY)).not.toHaveClass(/studio-tab--active/);
    await expect.poll(() => new URL(page.url()).searchParams.has('job')).toBe(false);
  });

  test('deep-link ?job= still opens the task with no persisted task tab', async ({ page }) => {
    await stubApi(page);
    // No seeded tab state — only the sticky default board exists. The detail
    // must come up purely from the URL (shared/bookmarked link).
    await page.goto(`/?job=${TASK_ID}&watchPath=${WATCH_PATH}`);
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });

    await expect(tabBy(page, TASK_TAB_KEY)).toBeVisible();
    await expect(tabBy(page, TASK_TAB_KEY)).toHaveClass(/studio-tab--active/);
  });

  test('drops a persisted legacy Backlog tab and keeps the 0-backlog board lane', async ({ page }) => {
    const pageErrors: string[] = [];
    page.on('pageerror', error => pageErrors.push(error.message));

    await stubApi(page, { resolveDetail: false });
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });

    await seedStudioTabsAndReload(page, [
      { kind: 'board', projectName: '__all__' },
      { kind: 'backlog', projectName: null },
    ], 'backlog:__all__');

    await expect(tabBy(page, STICKY_TAB_KEY)).toHaveClass(/studio-tab--active/);
    await expect(page.locator('[data-testid^="studio-tab-backlog:"]')).toHaveCount(0);
    await expect(page.getByTestId('studio-ab-backlog')).toHaveCount(0);
    await expect(page.locator('[data-testid^="studio-explorer-project-backlog-"]')).toHaveCount(0);
    await expect(page.getByTestId('lane-0-backlog')).toBeVisible();

    const persisted = await page.evaluate(() => localStorage.getItem('atp.studio.tabs.v1'));
    expect(persisted).not.toContain('backlog');
    expect(pageErrors).toEqual([]);

    const resultsDir = process.env.JOB_RESULTS_DIR;
    if (resultsDir) {
      await page.screenshot({
        path: `${resultsDir}/legacy-backlog-tab-discarded-board-lane-retained--mocked.png`,
        fullPage: false,
      });
      await page.getByTestId('lane-0-backlog').screenshot({
        path: `${resultsDir}/board-0-backlog-lane-retained--mocked.png`,
      });
    }
  });
});
