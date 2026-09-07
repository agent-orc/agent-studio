import { test, expect } from '@playwright/test';
import type { Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, moveJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

/**
 * AGT-2726 — the board's git-derived state is a background index, and the UI
 * says how fresh it is.
 *
 * The backend answers board and inventory requests from the last capture the
 * `GitStateIndex` made, and stamps every such response with `X-Git-State-At`
 * and `X-Git-State-Stale`. An HTTP interceptor funnels those into
 * `GitStateStampService`, and the status bar renders the reading quietly.
 *
 * These specs prove the whole path end to end:
 *
 *   1. Wire contract - the stamp headers reach the browser on the board poll,
 *      and the grouped payload stays a pure lane map. The stamp is a header on
 *      every endpoint precisely because clients iterate that object's values as
 *      task arrays; a scalar sibling there broke the Explorer's project rows.
 *   2. Status bar    - the reading is rendered, and it is a plain read-only
 *      chip rather than an acute signal.
 *   3. Push refresh  - the coarse `jobsChanged` push the index raises after a
 *      capture makes the board re-pull well inside the 30 s heartbeat, so the
 *      stamp cannot be arriving by poll.
 *   4. Admin panel   - the Performance section renders the per-endpoint
 *      percentiles and the index age from the same telemetry the logs use.
 */

const AT_HEADER = 'x-git-state-at';
const STALE_HEADER = 'x-git-state-stale';

const PREFIX = 'e2e-git-state-stamp-';

// Comfortably below the board's 30 s heartbeat: a refresh inside this window
// proves the push path, not the poll.
const PUSH_WINDOW_MS = 8_000;

interface WatchPath { name: string; path: string; }

async function firstWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length, 'the target stack must have at least one project').toBeGreaterThan(0);
  return paths[0].path;
}

async function deleteJob(id: string, watchPath: string): Promise<void> {
  await api(
    `/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'DELETE' },
  );
}

async function openBoard(page: Page): Promise<void> {
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
}

/**
 * Task-evidence capture. Only runs under the task orchestrator (JOB_RESULTS_DIR
 * set); local runs skip it. Filenames carry the `--real` provenance label
 * required by the evidence conventions: these are captures of the real backend,
 * not mocked routes.
 */
const RESULTS_DIR = process.env['JOB_RESULTS_DIR']?.trim() ?? '';

async function capture(page: Page, name: string): Promise<void> {
  if (!RESULTS_DIR) return;
  await page.screenshot({ path: `${RESULTS_DIR}/screenshots/${name}--real.png`, fullPage: false });
}

test.describe('AGT-2726 — git state freshness stamp', () => {
  test('board responses carry the freshness stamp on headers, and the grouped payload stays a lane map', async ({ page }) => {
    const grouped = page.waitForResponse(
      response => response.url().includes('/api/tasks/grouped') && response.ok(),
    );
    await openBoard(page);

    const response = await grouped;
    const headers = response.headers();
    expect(headers[STALE_HEADER], 'the grouped response must state whether the index is stale').toBeDefined();
    expect(['true', 'false']).toContain(headers[STALE_HEADER]);

    // Every value of the grouped response must stay a task array. The Explorer's
    // project rows iterate Object.entries(grouped) and treat each value as one,
    // so a scalar sibling throws "lane is not iterable" before the shell paints.
    const payload = await response.json();
    for (const [lane, value] of Object.entries(payload)) {
      expect(Array.isArray(value), `grouped.${lane} must stay a task array`).toBe(true);
    }

    // When the index has captured at least one repository it reports when.
    if (headers[AT_HEADER]) {
      expect(Number.isNaN(Date.parse(headers[AT_HEADER]))).toBe(false);
    }
  });

  for (const theme of ['dark', 'light'] as Theme[]) {
    test(`the status bar reports the reading quietly in the ${theme} theme`, async ({ page }) => {
      await openBoard(page);
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);

      const chip = page.getByTestId('status-bar-git-state');
      await expect(chip).toBeVisible({ timeout: 15_000 });
      await expect(chip).toContainText('git state');

      // R4: a freshness reading is historical data, not an acute state. The chip
      // stays a read-only span with no signal tone and no warning affordance.
      await expect(chip).not.toHaveJSProperty('tagName', 'BUTTON');
      await expect(chip.locator('[class*="signal"]')).toHaveCount(0);

      await capture(page, `git-state-stamp-status-bar-${theme}`);
    });
  }

  test('a change pushes the board to re-pull well inside the heartbeat', async ({ page }) => {
    await openBoard(page);
    await expect(page.getByTestId('status-bar-git-state')).toBeVisible({ timeout: 15_000 });

    const watchPath = await firstWatchPath();
    const created = await createJob({
      title: `${PREFIX}${Date.now()}`,
      watchPath,
      targetState: '0-backlog',
    });

    try {
      // A move is the client-side path the git index reuses: a completed
      // capture raises the same coarse `jobsChanged` push, and TaskService
      // answers both with one debounced silent re-pull of the grouped board.
      // A refresh inside this window therefore cannot be the 30 s poll.
      // The task stays in 0-backlog / 1-preparation so no auto-mode project
      // can pick it up and spend real CLI quota.
      const refreshed = page.waitForResponse(
        response => response.url().includes('/api/tasks/grouped') && response.ok(),
        { timeout: PUSH_WINDOW_MS },
      );
      await moveJob(created.id, watchPath, '1-preparation');
      await refreshed;

      // The stamp survives the push refresh rather than being dropped by it.
      await expect(page.getByTestId('status-bar-git-state')).toContainText('git state');
    } finally {
      await deleteJob(created.id, watchPath);
    }
  });

  test('the Admin performance section reports percentiles, spawn rate, and index age', async ({ page }) => {
    await openBoard(page);
    await page.getByTestId('studio-ab-settings').click();
    await page.getByTestId('workspace-settings-rail-performance').click({ timeout: 10_000 });

    const panel = page.getByTestId('workspace-performance-panel');
    await expect(panel).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('workspace-performance-spawn-rate'))
      .toContainText('git processes per minute');

    const endpoints = page.getByTestId('workspace-performance-endpoints');
    await expect(endpoints).toBeVisible({ timeout: 10_000 });
    await expect(endpoints).toContainText('tasks/grouped');

    // The index reports its age per repository, which is the number an operator
    // needs when the board looks out of date.
    await expect(page.getByTestId('workspace-performance-repositories')).toBeVisible();

    // Both themes, per the style-guide hard rules: the table reads from
    // semantic tokens, never a one-theme colour.
    for (const theme of ['dark', 'light'] as Theme[]) {
      await setTheme(page, theme);
      await dismissDevErrorDialog(page);
      await expect(panel).toBeVisible();
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      await capture(page, `git-state-performance-panel-${theme}`);
    }
  });
});
