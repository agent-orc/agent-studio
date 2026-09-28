import { Page } from '@playwright/test';
import { test, expect } from '../fixtures/dev-backend';
import { api } from '../helpers/api';
import { mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

/**
 * Completed-lane primary = "Archive & Next" (feature: rename the detail
 * view's Complete-and-advance primary on 6-completed).
 *
 * Acceptance:
 *   1. Review lanes are unchanged — covered by the unit spec
 *      (`triage-actions.model.spec.ts`).
 *   2. The 6-completed primary reads "Archive & Next"; clicking it moves
 *      the open card to 7-archive AND advances the detail to the next
 *      card in 6-completed. Both are asserted here.
 *   3. Sensible fallback when the lane has no further card — the generic
 *      `advanceToNextInLane` "Lane cleared." path already covers it and
 *      is exercised by the accept-to-next sweep specs.
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

async function moveTask(id: string, watchPath: string, targetState: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(id)}/move?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'POST', body: JSON.stringify({ targetState }),
  });
}

async function deleteTask(id: string, watchPath: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
}

/**
 * Plant `count` cards in 6-completed. Create at 2-ready first, wait for the
 * scanner to see the folder, then move — mirrors the create→settle→move
 * sequence the other detail specs use so a fresh scanner cache does not
 * 404 the move.
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
    await moveTask(created.id, wp.path, '6-completed');
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
  test('non-integrated archive warns, then archives and advances after confirmation', async ({ page, devBackend }) => {
    void devBackend;
    const wp = await getFirstWatchPath();
    const tasks = await plantCompletedTasks(wp, 2);
    try {
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

      const beforeUrl = page.url();
      const departingId = tasks[0].id;
      // Dispatch through the element so a late notification toast cannot
      // intercept the click after the overlay cleanup above.
      await archiveBtn.evaluate((element: HTMLButtonElement) => element.click());

      const confirm = page.getByTestId('confirm-dialog');
      await expect(confirm).toBeVisible();
      await expect(page.getByTestId('confirm-dialog-message')).toContainText('not integrated');
      await expect(page.getByTestId('confirm-dialog-confirm')).toHaveText('Archive anyway');
      const evidenceDir = resolve(process.env.JOB_RESULTS_DIR ?? join('..', 'results', 'AGT-2425'));
      mkdirSync(evidenceDir, { recursive: true });
      await confirm.screenshot({ path: join(evidenceDir, 'archive-guard-after-warning.png') });
      const movePromise = page.waitForResponse(
        r => r.url().includes(`/api/v1/projects/-/tasks/${encodeURIComponent(tasks[0].id)}/move`),
        { timeout: 10_000 },
      );
      await page.getByTestId('confirm-dialog-confirm').click();

      // Acceptance #2b: the detail advances off the archived card to the
      // next completed peer. We assert the URL leaves the departing slug
      // (not a specific next id — the live lane order may include other
      // completed cards from prior runs).
      await page.waitForFunction(
        ({ before, departing }) =>
          window.location.href !== before &&
          !window.location.href.includes(`job=${encodeURIComponent(departing)}`),
        { before: beforeUrl, departing: departingId },
        { timeout: 5_000 },
      );

      const moveResponse = await movePromise;
      expect(moveResponse.status(), 'move POST should be 200').toBe(200);

      // The panel stayed open on a peer — proves "advance to next" rather
      // than the lane-cleared close.
      await expect(page.getByTestId('studio-triage-panel')).toBeVisible();

      // Acceptance #2c: the archived card persisted to 7-archive.
      await expect.poll(
        async () => getTaskState(tasks[0].id, wp.path),
        { timeout: 10_000 },
      ).toBe('7-archive');
    } finally {
      for (const t of tasks) await deleteTask(t.id, wp.path).catch(() => {});
    }
  });
});
