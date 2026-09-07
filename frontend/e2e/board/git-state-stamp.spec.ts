import { test, expect, Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * Push-path acceptance for the AGT-2726 Git-state freshness stamp.
 *
 * The board header shows a quiet "Git state: Xs ago" stamp sourced from
 * `GET /api/tasks/grouped`'s `gitStateAt`/`stale` fields, which reflect
 * `GitStateIndexService`'s last background index run for the repositories on
 * the board - never something the request itself computed. The backend pushes
 * a `gitStateChanged` SignalR event whenever that background index advances,
 * so an already-open board's stamp should move forward without waiting for
 * the 30 s heartbeat poll.
 *
 * Creating a task writes `task.json` under the watch path, which the indexer
 * treats as a change-driven trigger (`TaskWatcherService.OnJobChanged`) same
 * as a real ref change would, without needing this spec to mutate the shared
 * git repository directly.
 */

const PREFIX = 'e2e-git-state-stamp-';
// Debounce (~400ms) + one board-projection compute + push delivery, still
// comfortably below the 30 s heartbeat poll and the 45 s safety sweep.
const PUSH_WINDOW_MS = 15_000;

interface WatchPath { name: string; path: string; rootPath: string; }
interface TaskRow { id: string; watchPath: string; state: string; }

async function firstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteTask(id: string, watchPath: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
  }).catch(() => {});
}

async function cleanup(watchPath: string): Promise<void> {
  const all = await api<TaskRow[]>('/api/tasks?includeFixtures=true');
  const stale = all.filter(t => t.watchPath === watchPath && t.id.startsWith(PREFIX));
  await Promise.all(stale.map(t => deleteTask(t.id, t.watchPath)));
}

/** Lands on the kanban board for the given project, regardless of whether the
 *  app boots straight to a board or to the studio-welcome project picker. */
async function navigateToBoard(page: Page, projectName: string): Promise<void> {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');

  const anyLane = page.locator('[data-testid^="lane-"]').first();
  if (await anyLane.isVisible({ timeout: 2_000 }).catch(() => false)) return;

  const welcome = page.locator('[data-testid="studio-welcome"]');
  if (await welcome.isVisible({ timeout: 3_000 }).catch(() => false)) {
    await welcome.locator('.studio-welcome__project').filter({ hasText: projectName }).first().click();
  }
  await expect(anyLane).toBeVisible({ timeout: 10_000 });
}

test.describe('Git state stamp - push delivery', () => {
  test.beforeAll(async () => {
    const wp = await firstWatchPath();
    await cleanup(wp.path);
  });

  test.afterAll(async () => {
    const wp = await firstWatchPath();
    await cleanup(wp.path);
  });

  test('the board header shows a Git-state freshness stamp once the background index has run', async ({ page }) => {
    const wp = await firstWatchPath();
    await navigateToBoard(page, wp.name);

    const stamp = page.locator('[data-testid="git-state-stamp"]');
    // The indexer runs an immediate "startup" pass for every known repository,
    // so a freshly booted backend should have produced a snapshot well before
    // this timeout even on a slow CI runner.
    await expect(stamp).toBeVisible({ timeout: 20_000 });
    await expect(stamp).toHaveAttribute('data-git-state-at', /.+/);
    await expect(stamp).toHaveText(/Git state: .+ ago/);
  });

  test('cross-tab: a task-folder change re-indexes the repository and pushes the stamp forward within the push window', async ({ page }) => {
    const wp = await firstWatchPath();
    const id = PREFIX + Date.now();
    const title = 'Git state stamp ' + id;

    await navigateToBoard(page, wp.name);
    const stamp = page.locator('[data-testid="git-state-stamp"]');
    await expect(stamp).toBeVisible({ timeout: 20_000 });
    const before = await stamp.getAttribute('data-git-state-at');

    try {
      // Writing task.json under the watch path is itself one of the
      // indexer's change-driven triggers (TaskWatcherService.OnJobChanged),
      // the same signal an accepted delivery or an integration write would
      // produce - no direct git mutation needed from the test.
      await createJob({ id, title, watchPath: wp.path, targetState: '0-backlog', fixture: true });

      await expect(async () => {
        const after = await stamp.getAttribute('data-git-state-at');
        expect(after).toBeTruthy();
        if (before) expect(new Date(after!).getTime()).toBeGreaterThan(new Date(before).getTime());
      }).toPass({ timeout: PUSH_WINDOW_MS });
    } finally {
      await deleteTask(id, wp.path);
    }
  });
});
