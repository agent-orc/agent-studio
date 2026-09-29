import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, moveJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  // Route via the api() helper so the X-Client-Id header is sent; bare fetch()
  // skips it and the ClientIdentityMiddleware rejects the mutation silently.
  // Retry once on 404: JobIndexCache invalidation lags slightly after a bulk
  // archive (FE click -> backend move -> cache refresh), and a cleanup DELETE
  // landing in that window sees a 404 even though the folder still exists.
  const path = `/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`;
  try {
    await api<void>(path, { method: 'DELETE' });
  } catch (e: unknown) {
    if (!/->\s*404\b/.test((e as Error).message)) throw e;
    await new Promise(r => setTimeout(r, 750));
    await api<void>(path, { method: 'DELETE' });
  }
}

function uid() {
  return `e2e-arch-load-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function dismissErrorOverlay(page: import('@playwright/test').Page) {
  const overlay = page.locator('.overlay--error');
  if (await overlay.isVisible({ timeout: 500 }).catch(() => false)) {
    await overlay.click({ force: true }).catch(() => {});
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {});
  }
}

/**
 * Regression coverage for the "Archive all" loading indicator:
 *
 *   - While the bulk archive POSTs are in flight, the button must show
 *     the in-flight signal (data-archiving="true", aria-busy="true",
 *     visible spinner, "Archiving…" label) and be disabled so a panicked
 *     double-click can't fire a second batch.
 *   - Once the POSTs resolve, the button must return to its idle state
 *     (no data-archiving attr, "Archive all" label, enabled).
 *
 * The move requests are intentionally delayed via `page.route` so the
 * loading state is observable from the browser. Without the delay the
 * archive completes faster than Playwright's polling cadence and the
 * assertion would race with the response.
 */
test.describe('Archive-all loading indicator', () => {
  test('shows spinner + disabled while archive is in flight, then resets', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;

    const idA = uid();
    const idB = uid();
    // fixture: false makes these jobs visible in the default /api/tasks/grouped
    // response the board polls. With the default (fixture: true) they would be
    // filtered out and `filteredGrouped().completed` stays at 0, which short-
    // circuits archiveAllCompleted and the loading state never appears.
    const jobA = await createJob({ id: idA, title: `e2e-arch-load-${idA}`, watchPath, targetState: '2-ready', fixture: false });
    const jobB = await createJob({ id: idB, title: `e2e-arch-load-${idB}`, watchPath, targetState: '2-ready', fixture: false });
    // Land both jobs directly in 6-completed. We deliberately skip the
    // 3-progress detour: any folder sitting in 3-progress qualifies for
    // pickup by the project runner (ADR-0028), which would race the test by
    // spinning a CLI invocation against the fixture and possibly moving it
    // back out before the click. JobTransitionService accepts a direct
    // 2-ready -> 6-completed jump for synthetic fixtures.
    for (const id of [jobA.id, jobB.id]) {
      await moveJob(id, watchPath, '6-completed');
    }
    // Wait until the backend index reflects both jobs in 6-completed. The
    // JobIndexCache refreshes off FileSystemWatcher events with a small lag;
    // page.goto() right after a move can land in the window where the disk
    // is updated but the cache still serves the previous lane, which makes
    // the board paint an empty Completed column and short-circuits the click.
    await expect(async () => {
      const jobs = await api<Array<{ id: string; state: string }>>('/api/tasks?includeFixtures=true');
      const a = jobs.find(j => j.id === jobA.id);
      const b = jobs.find(j => j.id === jobB.id);
      expect(a?.state).toBe('6-completed');
      expect(b?.state).toBe('6-completed');
    }).toPass({ timeout: 5_000, intervals: [100, 200, 500] });

    let movePostCount = 0;
    let resolveGate: (() => void) | null = null;
    const gate = new Promise<void>(r => { resolveGate = r; });

    // Hold the move POSTs until the loading-state assertions have run.
    // `page.route` intercepts the browser-side fetch and lets us pause
    // before forwarding it.
    await page.route('**/api/v1/projects/*/tasks/*/move*', async (route, request) => {
      if (request.method() !== 'POST') return route.continue();
      movePostCount += 1;
      await gate;
      return route.continue();
    });

    try {
      await page.goto('/');
      await dismissErrorOverlay(page);

      const btn = page.getByTestId('archive-all-btn');
      await expect(btn).toBeVisible({ timeout: 10_000 });
      // Idle baseline.
      await expect(btn).not.toHaveAttribute('data-archiving', /.+/);
      await expect(btn).toBeEnabled();

      // Ensure the board actually shows at least our two seeded jobs before
      // clicking. The cache-warm in setup confirms the backend sees both in
      // 6-completed, but the board has its own polling cadence on top of
      // that; clicking before the lane repaints dispatches
      // archiveAllCompleted([]), which short-circuits and the loading state
      // never appears.
      const lane = page.locator('[data-testid="lane-6-completed"] app-job-card');
      await expect.poll(() => lane.count(), { timeout: 10_000, intervals: [200, 500, 1000] })
        .toBeGreaterThanOrEqual(2);

      await btn.click();

      // In-flight: disabled, archiving attr set, spinner visible, label switched.
      await expect(btn).toHaveAttribute('data-archiving', 'true', { timeout: 2_000 });
      await expect(btn).toHaveAttribute('aria-busy', 'true');
      await expect(btn).toBeDisabled();
      await expect(btn).toContainText(/Archiving/i);
      await expect(btn.locator('.column__archive-all__spinner')).toBeVisible();

      // Double-click during the loading state must not fan out additional
      // POSTs: the service's `archiving` guard suppresses re-entry, and the
      // disabled button blocks the click at the DOM layer.
      const before = movePostCount;
      await btn.click({ force: true }).catch(() => {});
      await page.waitForTimeout(150);
      expect(movePostCount).toBe(before);

      // Release the gate and let the POSTs land.
      resolveGate?.();

      // Idle again. Use a slightly longer timeout because the button only
      // resets after both moves resolve + the board refresh repaints.
      await expect(btn).not.toHaveAttribute('data-archiving', /.+/, { timeout: 10_000 });
      await expect(btn).toBeEnabled();
      await expect(btn).toContainText(/Archive all/i);
    } finally {
      await page.unroute('**/api/v1/projects/*/tasks/*/move*').catch(() => {});
      // resolveGate may not have been called if an assertion failed early;
      // release it so the route handler doesn't keep the page hung on cleanup.
      resolveGate?.();
      // Delete by known id+watchPath. listJobs() does NOT include fixture jobs
      // in its default response, so a list-then-delete loop silently misses
      // them when state moves (auto-commit, watcher writes) flip the field.
      for (const id of [jobA.id, jobB.id]) {
        try {
          await deleteJob(id, watchPath);
        } catch (e: unknown) {
          // eslint-disable-next-line no-console
          console.warn(`[archive-all-loading] cleanup DELETE failed for ${id}: ${(e as Error).message}`);
        }
      }
    }
  });
});
