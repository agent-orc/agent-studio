import { Page } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { api } from '../helpers/api';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
test.setTimeout(240_000);

/**
 * Completed-lane primary = "Archive & Next" (feature: rename the detail
 * view's Complete-and-advance primary on 6-completed).
 *
 * The Completed primary remains visible, but a delivery without integration
 * cannot be archived through the ordinary action.
 *
 * This spec talks to `/api/tasks` directly rather than the `helpers/jobs`
 * client: those helpers still call the pre-rename `/api/tasks` group, which
 * 404s on the current backend. The DTO shapes are identical, so the inline
 * helpers below mirror them against the live route.
 */

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

function uid(prefix: string) {
  return `e2e-${prefix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function createTask(input: { id: string; title: string; watchPath: string; targetState: string }): Promise<{ id: string }> {
  return api<{ id: string }>('/api/tasks', {
    method: 'POST',
    body: JSON.stringify({
      id: input.id,
      title: input.title,
      watchPath: input.watchPath,
      agent: 'claude',
      cliType: 'claude',
      model: null,
      promptMarkdown: null,
      targetState: input.targetState,
      mode: 'concept',
      requiresIntegration: false,
      // fixture:false so the cards land in `/api/tasks/grouped` and the
      // lane-pager snapshot can capture them as peers — the auto-advance
      // reads that snapshot. Completed cards are terminal, so the runner
      // never auto-starts them and the lane stays stable for the test.
      fixture: false,
    }),
  });
}

async function getTaskState(id: string, watchPath: string): Promise<string> {
  const detail = await api<{ info: { state: string } }>(
    `/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`,
  );
  return detail.info.state;
}

async function taskExists(id: string, watchPath: string): Promise<boolean> {
  try { await getTaskState(id, watchPath); return true; } catch { return false; }
}

async function deleteTask(id: string, watchPath: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
}

/**
 * Plant code-free completed cards through the API, then project an old pending
 * integration verdict onto the browser view to exercise the archive notice.
 */
async function plantCompletedTasks(wp: WatchPath, count: number): Promise<{ id: string; title: string }[]> {
  const tasks: { id: string; title: string }[] = [];
  for (let i = 0; i < count; i++) {
    const id = uid(`archive-${i}`);
    const title = `archive fixture ${i} ${id}`;
    const created = await createTask({ id, title, watchPath: wp.path, targetState: '2-ready' });
    for (let attempt = 0; attempt < 25; attempt++) {
      if (await taskExists(created.id, wp.path)) break;
      await new Promise(r => setTimeout(r, 200));
    }
    await api(`/api/tasks/${encodeURIComponent(created.id)}/concept-dossier?watchPath=${encodeURIComponent(wp.path)}`, {
      method: 'POST',
      body: JSON.stringify({ noDossierNeeded: true, reason: 'Code-free archive fixture.' }),
    });
    await api(`/api/tasks/${encodeURIComponent(created.id)}/move?watchPath=${encodeURIComponent(wp.path)}`, {
      method: 'POST', body: JSON.stringify({ targetState: '6-completed' }),
    });
    tasks.push({ id: created.id, title });
  }
  return tasks;
}

/**
 * Clear anything that can sit on top of the studio tab-bar action cluster
 * and swallow a click: the pinned CLI-usage quota modal (a click-open
 * overlay that may be left open from a prior session) and the toast stack.
 * Also parks the mouse in the corner so a usage-pill hover panel does not
 * re-open between the dismiss and the click.
 */
async function dismissOverlays(page: Page): Promise<void> {
  for (let i = 0; i < 5; i++) {
    const modalClose = page.getByTestId('cli-usage-detail-close').first();
    if (!(await modalClose.isVisible({ timeout: 200 }).catch(() => false))) break;
    await modalClose.click({ timeout: 1_000 }).catch(() => undefined);
  }
  for (let i = 0; i < 5; i++) {
    const closeBtn = page.getByTestId('notification-close').first();
    if (!(await closeBtn.isVisible({ timeout: 200 }).catch(() => false))) break;
    await closeBtn.click({ timeout: 1_000 }).catch(() => undefined);
  }
  await page.mouse.move(0, 0).catch(() => undefined);
}

async function openTaskInDetail(page: Page, id: string, watchPath: string) {
  await page.goto(`/?job=${encodeURIComponent(id)}&watchPath=${encodeURIComponent(watchPath)}`);
  await expect(page.getByTestId('studio-triage-panel')).toBeVisible({ timeout: 10_000 });
  await dismissOverlays(page);
}

test.describe('Completed lane primary is "Archive & Next"', () => {
  test('non-integrated archive stays blocked after the notice', async ({ page, devBackend }, testInfo) => {
    void devBackend;
    const wp = await getFirstWatchPath();
    const tasks = await plantCompletedTasks(wp, 2);
    try {
      await page.route(new RegExp(`/api/tasks/${tasks[0].id}(\\?|$)`), async route => {
        const response = await route.fetch();
        const detail = await response.json();
        detail.info.requiresIntegration = true;
        detail.info.integration = {
          status: 'no-branch', deliveryRef: null, sha: null,
          integrationBranch: 'main', detail: 'No delivery ref or attributed commit to integrate.',
        };
        await route.fulfill({ response, json: detail });
      });
      await openTaskInDetail(page, tasks[0].id, wp.path);

      // Wait for the slim pager to anchor on the open card with at least
      // one peer behind it (denominator >= 2). advanceToNextInLane falls
      // back to the previous peer when the open card is last, so any lane
      // with >= 2 cards guarantees a deterministic advance (never the
      // "Lane cleared." close path).
      const slimPagerPos = page.getByTestId('studio-task-pager-position');
      await expect(slimPagerPos).toBeVisible({ timeout: 30_000 });
      await expect.poll(
        async () => {
          const txt = (await slimPagerPos.textContent())?.trim() ?? '';
          const m = txt.match(/^(\d+)\s*\/\s*(\d+)$/);
          if (!m) return false;
          return Number(m[1]) >= 1 && Number(m[2]) >= 2;
        },
        { timeout: 30_000, intervals: [200, 500, 1000, 2000, 2000] },
      ).toBe(true);
      await page.waitForTimeout(750);
      await dismissOverlays(page);

      // Acceptance #2a: the Completed-lane primary is labelled "Archive & Next".
      const archiveBtn = page.getByTestId('studio-triage-action-archive');
      await expect(archiveBtn).toBeVisible({ timeout: 10_000 });
      await expect(archiveBtn).toBeEnabled();
      await expect(archiveBtn).toHaveText(/Archive & Next/);

      // Dispatch through the element so a late notification toast cannot
      // intercept the click after the overlay cleanup above.
      await archiveBtn.evaluate((element: HTMLButtonElement) => element.click());

      const confirm = page.getByTestId('confirm-dialog');
      await expect(confirm).toBeVisible();
      await expect(page.getByTestId('confirm-dialog-message')).toContainText('not in main');
      await expect(page.getByTestId('confirm-dialog-confirm')).toHaveText('Keep in Delivered');
      const evidenceDir = resolve(process.env.JOB_RESULTS_DIR ?? join('..', 'results', 'AGT-2920'));
      mkdirSync(evidenceDir, { recursive: true });
      for (const theme of ['light', 'dark'] as const) {
        await page.emulateMedia({ colorScheme: theme });
        const path = join(evidenceDir, `archive-guard-blocked-${theme}.png`);
        await confirm.screenshot({ path });
        await testInfo.attach(`archive-guard-blocked-${theme}`, { path, contentType: 'image/png' });
      }
      await page.getByTestId('confirm-dialog-confirm').click();
      await expect(page.getByTestId('studio-triage-panel')).toBeVisible();
      await expect.poll(async () => getTaskState(tasks[0].id, wp.path)).toBe('6-completed');
    } finally {
      for (const t of tasks) await deleteTask(t.id, wp.path).catch(() => {});
    }
  });
});
