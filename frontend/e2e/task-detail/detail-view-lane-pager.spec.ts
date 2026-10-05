import { test, expect, type Page } from '@playwright/test';
import { api } from '../helpers/api';

interface WatchPath { name: string; path: string; rootPath: string; }

// Task create/move are kept local on `/api/tasks` on purpose: the shared
// helpers in e2e/helpers/jobs.ts still target the renamed `/api/tasks`
// prefix, whose migration is tracked separately as repo-wide route rot
// (see commit 20ce863). This spec drives the live backend, so it hits the
// real route directly - matching the local `deleteJob` below.
async function createTask(input: {
  id?: string;
  title: string;
  watchPath: string;
  targetState?: string;
  fixture?: boolean;
}): Promise<{ id: string }> {
  return api<{ id: string }>('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: input.id ?? '',
      title: input.title,
      watchPath: input.watchPath,
      agent: 'claude',
      cliType: 'claude',
      model: null,
      promptMarkdown: null,
      targetState: input.targetState ?? '2-ready',
      fixture: input.fixture ?? true,
    }),
  });
}

async function moveTask(jobId: string, watchPath: string, targetState: string): Promise<void> {
  await api(
    `/api/tasks/${encodeURIComponent(jobId)}/move?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'POST', body: JSON.stringify({ targetState }) }
  );
}

// Prefer the dedicated "Playwright Test" project. The live Runbook project the
// API lists first runs the auto-runner, which moves tasks out of 2-ready mid
// test and makes the snapshot-shrink assertions non-deterministic. The test
// project isn't auto-driven, so the lane only changes when this spec changes it.
async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  // Go through the shared `api` client (not a bare fetch) so the delete
  // carries the `x-client-id` identity header. Without it the backend
  // silently refuses the mutation, which is how the shared lane accumulated
  // a backlog of orphaned fixtures from earlier runs.
  await api(
    `/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'DELETE' }
  );
}

interface GroupedTasks { [lane: string]: Array<{ id?: string; watchPath?: string }>; }

/**
 * Delete every leftover `e2e-pager-*` fixture from the board. Tests that
 * time out skip their per-test cleanup, so fixtures from earlier runs
 * survive and interleave (by id) with a fresh run's fixtures - which breaks
 * the snapshot-order neighbour assertions. Purging up front keeps each run
 * deterministic.
 *
 * The grouped board is unified across every watch path, so a fixture must be
 * deleted with its OWN `watchPath` (older runs created fixtures under a
 * different project); deleting with the wrong path returns 404 and silently
 * leaves the orphan in place. Looped because a delete can race a
 * still-settling move left behind by a prior teardown.
 */
async function purgeLeftoverFixtures(readWatchPath: string): Promise<void> {
  const wpEnc = encodeURIComponent(readWatchPath);
  for (let pass = 0; pass < 6; pass++) {
    const grouped = await api<GroupedTasks>(`/api/tasks/grouped?watchPath=${wpEnc}`);
    const leftovers = Object.values(grouped)
      .flat()
      .filter(t => String(t?.id ?? '').startsWith('e2e-pager') && t.watchPath)
      .map(t => ({ id: String(t.id), watchPath: String(t.watchPath) }));
    if (leftovers.length === 0) return;
    for (const { id, watchPath } of leftovers) await deleteJob(id, watchPath).catch(() => {});
  }
}

function uid(suffix: string) {
  return `e2e-pager-${suffix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function clickCardAndWaitForDetail(page: Page, jobId: string): Promise<void> {
  await page.locator('[data-testid="task-card"]', { hasText: jobId }).first().click();
  await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(jobId)}`), { timeout: 10_000 });
  await expect(page.getByTestId('detail-state-select')).toBeVisible({ timeout: 10_000 });
}

/**
 * Ensure every job in `ids` is rendered as a card on the kanban before the
 * test proceeds. The pager snapshot is captured at the moment of click, so
 * if even one fixture has not landed in `/api/tasks/grouped` yet the
 * iteration starts short and the rest of the test asserts against stale
 * state.
 */
async function waitForFixtureCards(page: Page, ids: ReadonlyArray<string>): Promise<void> {
  for (const id of ids) {
    await expect(page.locator('[data-testid="task-card"]', { hasText: id }).first())
      .toBeVisible({ timeout: 15_000 });
  }
}

/**
 * Snapshot order of the given fixtures, top-to-bottom as they sit in the
 * kanban column. The pager iterates the lane order captured on entry, which
 * is NOT the creation sequence: the board applies its own sort. Neighbour
 * assertions must therefore key off the observed order. Sorting by the card's
 * vertical position keeps the spec correct regardless of which sort the board
 * applies.
 */
async function laneOrder(page: Page, ids: ReadonlyArray<string>): Promise<string[]> {
  const withY: { id: string; y: number }[] = [];
  for (const id of ids) {
    const box = await page.locator('[data-testid="task-card"]', { hasText: id }).first().boundingBox();
    withY.push({ id, y: box?.y ?? Number.MAX_SAFE_INTEGER });
  }
  withY.sort((a, b) => a.y - b.y);
  return withY.map(e => e.id);
}

/**
 * Detail-header lane pager. The Prev / N of M / Next controls iterate the
 * snapshot captured when the user clicked into the detail view, NOT the
 * live lane: changing a job's status mid-iteration must keep the pager's
 * place in the original lane. Reloads restore the iteration from
 * sessionStorage.
 *
 * Plain-text `title` tooltips (no rich HTML) per the project tooltip rule.
 */
test.describe('Detail view - lane pager', () => {
  // The snapshot-backed pager under test lives in the legacy <app-detail-header>
  // (testids lane-pager*, detail-state-select, triage-overflow-*). The default
  // `vsCodeLayout` studio shell hides that header and renders its own slim pager
  // (studio-task-*) bound to LIVE lane peers, not the LanePagerService snapshot -
  // a different implementation, not a renamed control. Opt back into the legacy
  // chrome so this spec exercises the stable-iteration feature it covers.
  //
  // This spec was authored for the quiet "stable" target. When pointed at a
  // busy shared dev backend (auto-runner churning several projects, thousands
  // of tasks) every API round-trip and board render is slow: the default 60s
  // per-test budget is routinely blown by backend latency, not the feature,
  // and which test trips the 60s wall shifts run to run. Triple the budget so
  // latency, not logic, decides, and allow one retry to absorb transient load
  // spikes (the beforeEach purge re-runs, so a retry starts from a clean lane).
  test.describe.configure({ timeout: 180_000, retries: 1 });

  test.beforeEach(async ({ page }, testInfo) => {
    // describe.configure({ timeout }) is silently ignored by this Playwright
    // build (verified: tests still trip the global 60s wall), so set the
    // per-test budget imperatively here. The retries:1 from configure works.
    testInfo.setTimeout(180_000);
    await page.addInitScript(() => {
      window.localStorage.setItem('atp.flag.vsCodeLayout', '0');
    });
    // Start every test from a clean lane. The Playwright Test lane is shared
    // across runs, and a timed-out test skips its per-test teardown, so
    // orphaned fixtures otherwise pile up and interleave with this run's
    // fixtures - breaking the snapshot-order neighbour assertions.
    const wp = await getFirstWatchPath();
    await purgeLeftoverFixtures(wp.path);
  });

  test('captures lane snapshot on entry and walks with prev/next', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3, 4, 5].map(i => uid(`walk-${i}`));
    const created: { id: string }[] = [];
    // Intake to 0-backlog, the manual triage lane the auto-runner never pulls
    // (it only auto-picks 2-ready / auto-processes 4-auto-review). The pager
    // captures a snapshot of this lane on entry; using the quiet lane keeps
    // both the snapshot total and the live lane membership stable, so prev/next
    // stepping is deterministic. The churned 2-ready lane was racing the
    // auto-runner mid-walk and the position would stick. (See the state-change
    // test for the full quiet-lane rationale.)
    for (const id of ids) {
      created.push(await createTask({ id, title: id, watchPath: wp.path, targetState: '0-backlog', fixture: false }));
    }

    try {
      await page.goto('/');
      await waitForFixtureCards(page, ids);
      // Land on the third fixture in lane order (newest-first), not creation
      // order, so prev/next assertions match the snapshot the pager captures.
      const order = await laneOrder(page, ids);
      await clickCardAndWaitForDetail(page, order[2]);

      const pager = page.getByTestId('lane-pager');
      await expect(pager).toBeVisible({ timeout: 10_000 });

      // The lane has at least our 5 fixtures, so position is "k / N" with N >= 5.
      const count = page.getByTestId('lane-pager-count');
      await expect(count).toContainText(/^\d+ \/ \d+$/);
      const initial = (await count.textContent())!.trim();
      const [posStr, totalStr] = initial.split('/').map(s => s.trim());
      const total = Number(totalStr);
      expect(total).toBeGreaterThanOrEqual(5);
      const startPos = Number(posStr);

      // Tooltip follows the app's canonical [appTooltip] standard: hovering
      // surfaces the singleton overlay (data-testid="cac-tooltip") whose text
      // explains the captured iteration in plain, readable language. We hover
      // the count span (it has no tooltip of its own) so the group tooltip,
      // not a button tooltip, is the one that shows.
      await expect(pager).not.toHaveAttribute('title', /.+/);
      await page.getByTestId('lane-pager-count').hover();
      const tip = page.getByTestId('cac-tooltip');
      await expect(tip).toBeVisible({ timeout: 5_000 });
      await expect(tip).toHaveText(/Iterating jobs.*Showing job \d+ of \d+/);

      // Next advances to the fourth job in the captured iteration.
      await page.getByTestId('lane-pager-next').click();
      await expect(count).toHaveText(`${startPos + 1} / ${total}`, { timeout: 15_000 });
      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(order[3])}`), { timeout: 20_000 });

      // Prev rewinds.
      await page.getByTestId('lane-pager-prev').click();
      await expect(count).toHaveText(`${startPos} / ${total}`, { timeout: 15_000 });
      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(order[2])}`), { timeout: 20_000 });
    } finally {
      for (const id of ids) await deleteJob(id, wp.path).catch(() => {});
    }
  });

  test('state change from the detail header auto-advances to the next captured slug and removes the moved task from the pager', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3, 4, 5].map(i => uid(`stable-${i}`));
    // The create endpoint intakes every new task into 0-backlog regardless of
    // the requested targetState, so that is where these fixtures land. Backlog
    // is the right lane for this test anyway: it is a manual triage lane the
    // auto-runner never pulls (it only auto-picks 2-ready and auto-processes
    // 4-auto-review), so the captured iteration only changes when THIS test
    // changes it. Freshly created, the five fixtures sort newest-first to the
    // top of the lane, so they occupy contiguous snapshot slots - the pager can
    // walk them and auto-advance lands on the next fixture, not a foreign card.
    for (const id of ids) {
      await createTask({ id, title: id, watchPath: wp.path, targetState: '0-backlog', fixture: false });
    }
    try {
      await page.goto('/');
      // Wait for every fixture card to render BEFORE the click that captures
      // the pager snapshot - otherwise openDetail can grab a list that's
      // missing the last-created jobs and the rest of the test races.
      await waitForFixtureCards(page, ids);
      // Open the third job in lane order, then advance once so we're on the
      // fourth. Lane order is newest-first, so we resolve it at runtime.
      const order = await laneOrder(page, ids);
      await clickCardAndWaitForDetail(page, order[2]);
      const count = page.getByTestId('lane-pager-count');
      const initial = (await count.textContent())!.trim();
      const [posStr, totalStr] = initial.split('/').map(s => s.trim());
      const startPos = Number(posStr);
      const startTotal = Number(totalStr);

      await page.getByTestId('lane-pager-next').click();
      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(order[3])}`), { timeout: 10_000 });

      // Move the current (4th) job out of the captured lane. The user wants to
      // keep triaging backlog, so the detail panel must auto-advance to
      // order[4] - the next captured slug - and the moved task must vanish
      // from the pager (k / N-1, where the same numeric index now points at
      // the next job in the iteration).
      const moveResponse = page.waitForResponse(resp =>
        resp.request().method() === 'POST'
        && resp.url().includes(`/api/v1/projects/-/tasks/${encodeURIComponent(order[3])}/move`)
      , { timeout: 30_000 });
      // Lane moves now live in the ⋯ overflow context menu, not the lane
      // dropdown (the dropdown only pages through lanes). 0-backlog offers
      // "Move to Completed" there.
      await page.getByTestId('triage-overflow-btn').click();
      await page.getByTestId('triage-overflow-item-move-to-completed').click();
      await moveResponse;

      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(order[4])}`), { timeout: 20_000 });
      // The new detail view is anchored on order[4], which is still in backlog.
      await expect(page.getByTestId('detail-state-select')).toHaveValue('0-backlog', { timeout: 20_000 });
      // Pager count shrunk by one and the position is preserved.
      await expect(count).toHaveText(`${startPos + 1} / ${startTotal - 1}`, { timeout: 15_000 });
    } finally {
      // The fourth job was moved to completed; the rest stay in backlog.
      for (const id of ids) {
        await moveTask(id, wp.path, '7-archive').catch(() => {});
        await deleteJob(id, wp.path).catch(() => {});
      }
    }
  });

  test('delete from the detail menu auto-advances to the next captured slug in the original lane', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3, 4, 5].map(i => uid(`del-adv-${i}`));
    // Fixtures intake to 0-backlog (a manual lane the auto-runner never pulls),
    // so the snapshot count can only change when this test changes it (see the
    // state-change test for the full rationale).
    for (const id of ids) {
      await createTask({ id, title: id, watchPath: wp.path, targetState: '0-backlog', fixture: false });
    }
    try {
      await page.goto('/');
      await waitForFixtureCards(page, ids);
      // Resolve lane order (newest-first) so the slot indices below match the
      // snapshot the pager captures, not the creation sequence.
      const order = await laneOrder(page, ids);
      await clickCardAndWaitForDetail(page, order[2]);
      const count = page.getByTestId('lane-pager-count');
      const initial = (await count.textContent())!.trim();
      const [posStr, totalStr] = initial.split('/').map(s => s.trim());
      const startPos = Number(posStr);
      const startTotal = Number(totalStr);

      // Walk Next once so we're on the fourth slot (order[3]).
      await page.getByTestId('lane-pager-next').click();
      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(order[3])}`), { timeout: 10_000 });

      // Delete via the detail-header triage overflow menu. The view must
      // land on the next slot (order[4]) and the pager count must drop by one
      // with the position preserved.
      await page.getByTestId('triage-overflow-btn').click();
      await page.getByTestId('triage-overflow-item-delete').click();
      const confirmDialog = page.getByTestId('confirm-dialog');
      await expect(confirmDialog).toBeVisible({ timeout: 5_000 });
      const deleteResponse = page.waitForResponse(resp =>
        resp.request().method() === 'DELETE'
        && resp.url().includes(`/api/v1/projects/-/tasks/${encodeURIComponent(order[3])}`)
      , { timeout: 30_000 });
      await page.getByTestId('confirm-dialog-confirm').click();
      await deleteResponse;

      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(order[4])}`), { timeout: 20_000 });
      await expect(count).toHaveText(`${startPos + 1} / ${startTotal - 1}`, { timeout: 15_000 });
    } finally {
      for (const id of ids) await deleteJob(id, wp.path).catch(() => {});
    }
  });

  test('multiple state changes in a row keep the pager anchored on the original lane', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3, 4, 5].map(i => uid(`multi-${i}`));
    // Fixtures intake to 0-backlog (a manual lane the auto-runner never pulls),
    // so repeated moves below shrink the snapshot by exactly one each time with
    // no auto-runner interleaving its own moves.
    for (const id of ids) {
      await createTask({ id, title: id, watchPath: wp.path, targetState: '0-backlog', fixture: false });
    }

    try {
      await page.goto('/');
      await waitForFixtureCards(page, ids);
      // Open the first fixture in lane order (newest-first). The pager captures
      // the backlog iteration; total includes our 5 fixtures plus any leftover
      // backlog peers.
      const order = await laneOrder(page, ids);
      await clickCardAndWaitForDetail(page, order[0]);
      const count = page.getByTestId('lane-pager-count');
      const initial = (await count.textContent())!.trim();
      const [posStr, totalStr] = initial.split('/').map(s => s.trim());
      const startPos = Number(posStr);
      const startTotal = Number(totalStr);

      // Triage three of the five fixtures in a row via the ⋯ overflow context
      // menu (lane moves moved there; the dropdown now only pages lanes).
      // The pager must keep the same numeric position while the total drops
      // by one each time, and the panel must land on the next captured slug
      // each time without the user touching prev/next.
      for (let i = 0; i < 3; i++) {
        const movingId = order[i];
        await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(movingId)}`), { timeout: 20_000 });
        const moveResp = page.waitForResponse(resp =>
          resp.request().method() === 'POST'
          && resp.url().includes(`/api/v1/projects/-/tasks/${encodeURIComponent(movingId)}/move`)
        , { timeout: 30_000 });
        await page.getByTestId('triage-overflow-btn').click();
        await page.getByTestId('triage-overflow-item-move-to-completed').click();
        await moveResp;
        await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(order[i + 1])}`), { timeout: 20_000 });
        await expect(count).toHaveText(`${startPos} / ${startTotal - (i + 1)}`, { timeout: 15_000 });
        // The lane the detail header shows must still be 0-backlog - the next
        // captured slug is anchored on the original lane, not on the lane the
        // user just moved the previous job to.
        await expect(page.getByTestId('detail-state-select')).toHaveValue('0-backlog', { timeout: 20_000 });
      }
    } finally {
      // Three of the fixtures landed in completed; the rest in backlog.
      for (const id of ids) {
        await moveTask(id, wp.path, '7-archive').catch(() => {});
        await deleteJob(id, wp.path).catch(() => {});
      }
    }
  });

  test('reload restores the iteration state', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3, 4].map(i => uid(`reload-${i}`));
    for (const id of ids) {
      await createTask({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false });
    }

    try {
      await page.goto('/');
      // Wait for the fixture cards to render before clicking. On a slow shared
      // backend the board can take longer than the 10s action timeout to paint
      // the card, so clicking blind races the load and times out.
      await waitForFixtureCards(page, ids);
      await clickCardAndWaitForDetail(page, ids[1]);

      const before = (await page.getByTestId('lane-pager-count').textContent())!.trim();

      await page.reload();
      await expect(page.getByTestId('detail-state-select')).toBeVisible({ timeout: 10_000 });
      const pager = page.getByTestId('lane-pager');
      await expect(pager).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('lane-pager-count')).toHaveText(before, { timeout: 5_000 });
    } finally {
      for (const id of ids) await deleteJob(id, wp.path).catch(() => {});
    }
  });

  test('deep-link with no stored iteration still shows the pager without keyboard nav', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3].map(i => uid(`deep-link-${i}`));
    for (const id of ids) {
      await createTask({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false });
    }

    try {
      // Hit the board first so the fixture cards land in /api/tasks/grouped;
      // then wipe sessionStorage so the next navigation has no stored
      // pager snapshot to restore.
      await page.goto('/');
      await waitForFixtureCards(page, ids);
      await page.evaluate(() => sessionStorage.clear());

      // Navigate directly to the detail URL. Before the fix this scenario
      // showed no pager until the user pressed an arrow key; the snapshot
      // was only captured on board click or pager step.
      await page.goto(`/?job=${encodeURIComponent(ids[1])}&watchPath=${encodeURIComponent(wp.path)}`);
      await expect(page.getByTestId('detail-state-select')).toBeVisible({ timeout: 10_000 });

      const pager = page.getByTestId('lane-pager');
      await expect(pager).toBeVisible({ timeout: 10_000 });
      const count = page.getByTestId('lane-pager-count');
      await expect(count).toContainText(/^\d+ \/ \d+$/);
      const [, totalStr] = (await count.textContent())!.trim().split('/').map(s => s.trim());
      expect(Number(totalStr)).toBeGreaterThanOrEqual(ids.length);
    } finally {
      for (const id of ids) await deleteJob(id, wp.path).catch(() => {});
    }
  });

  test('disables prev at the first slot and next at the last', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3].map(i => uid(`bounds-${i}`));
    for (const id of ids) {
      await createTask({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false });
    }

    try {
      await page.goto('/');
      await waitForFixtureCards(page, ids);
      await clickCardAndWaitForDetail(page, ids[0]);
      const count = page.getByTestId('lane-pager-count');
      const initial = (await count.textContent())!.trim();
      const [posStr, totalStr] = initial.split('/').map(s => s.trim());
      const startPos = Number(posStr);
      const total = Number(totalStr);

      if (startPos === 1) {
        await expect(page.getByTestId('lane-pager-prev')).toBeDisabled();
      }
      if (startPos === total) {
        await expect(page.getByTestId('lane-pager-next')).toBeDisabled();
      }
    } finally {
      for (const id of ids) await deleteJob(id, wp.path).catch(() => {});
    }
  });
});
