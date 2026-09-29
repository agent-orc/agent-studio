import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';

/**
 * Visual proof for the "Folder-Move scheitert 'in use by another process'"
 * bug. The user-facing half of the fix (requirement #3) is that a folder
 * move blocked by an open log handle / orphan agent process no longer pops
 * a bare 500 "Failed to move task" box — the backend now returns the typed
 * 423 DirectoryLocked outcome whose body carries an actionable diagnosis
 * ("Task folder is locked by another process ... Close the active CLI/log
 * handle and retry"), and the board surfaces that text in the error dialog.
 *
 * The spec seeds one throwaway Backlog card (Backlog is not auto-picked, so
 * the orchestrator never races us), mocks `/api/tasks/<id>/move` to return
 * exactly the 423 + diagnosis body the backend now produces, drags the card
 * across lanes to trigger a move, and captures the resulting dialog so the
 * diagnosis is provable rather than asserted. The card is deleted via the
 * API in `finally`, so the user's stable board is left clean.
 */
const DIAGNOSIS =
  'Task folder is locked by another process after 8 move attempts. ' +
  'Close the active CLI/log handle and retry. Last error: ' +
  'The process cannot access the file because it is being used by another process.';

interface WatchPath { name: string; path: string; }

test.describe('Locked folder move surfaces a clear diagnosis', () => {
  test('move blocked by a held handle shows the actionable 423 message, not a bare 500', async ({ page }) => {
    const paths = await api<WatchPath[]>('/api/watch-paths');
    const wp = paths.find((p) => p.name === 'Playwright Test') ?? paths[0];
    const title = `e2e-locked-move-${Date.now()}`;
    let jobId: string | null = null;

    try {
      // Seed a real Backlog card so there is something to drag. Backlog
      // (0-backlog) is a parked lane the auto-runner never pulls from.
      const created = await api<{ id: string }>('/api/tasks/', {
        method: 'POST',
        body: JSON.stringify({
          title,
          watchPath: wp.path,
          targetState: '0-backlog',
          promptMarkdown: 'Throwaway fixture for the locked-move diagnosis capture.',
          fixture: false,
        }),
      });
      jobId = created.id;

      // Answer every move POST with the real DirectoryLocked shape
      // (423 + { error: <diagnosis> }). No backend folder is touched.
      await page.route('**/api/v1/projects/*/tasks/*/move**', async (route) => {
        if (route.request().method() !== 'POST') return route.continue();
        await route.fulfill({
          status: 423,
          contentType: 'application/json',
          body: JSON.stringify({ error: DIAGNOSIS }),
        });
      });

      await page.goto('/');
      await expect(page.locator('.column__title').first()).toBeVisible({ timeout: 15_000 });

      // Wait for the seeded card to render somewhere on the board.
      const card = page.locator('app-job-card', { hasText: title });
      await expect(card).toBeVisible({ timeout: 15_000 });

      // Locate the seeded card's lane and any *different* lane with a drop
      // zone, then dispatch the synthetic DataTransfer drag/drop the board's
      // drag wiring listens for (mirrors cross-lane-drop-position.spec.ts).
      const plan = await page.evaluate((cardTitle) => {
        const columns = Array.from(document.querySelectorAll('.column')) as HTMLElement[];
        const laneTitle = (c: HTMLElement) =>
          c.querySelector('.column__title')?.textContent?.trim() ?? '';
        const sourceCol = columns.find((c) =>
          Array.from(c.querySelectorAll('app-job-card')).some(
            (jc) => jc.textContent?.includes(cardTitle),
          ),
        );
        if (!sourceCol) return null;
        const targetCol = columns.find(
          (c) => c !== sourceCol && c.querySelector('.column__drop-zone'),
        );
        if (!targetCol) return null;
        return { sourceTitle: laneTitle(sourceCol), targetTitle: laneTitle(targetCol) };
      }, title);

      expect(plan, 'seeded card and a second lane with a drop zone must both be present').not.toBeNull();

      await page.evaluate(({ sourceTitle, targetTitle, cardTitle }) => {
        const columns = Array.from(document.querySelectorAll('.column')) as HTMLElement[];
        const byTitle = (t: string) =>
          columns.find((c) => c.querySelector('.column__title')?.textContent?.trim() === t)!;
        const sourceCol = byTitle(sourceTitle);
        const targetCol = byTitle(targetTitle);
        const card = Array.from(sourceCol.querySelectorAll('app-job-card')).find((jc) =>
          jc.textContent?.includes(cardTitle),
        ) as HTMLElement;
        const dropZone =
          (targetCol.querySelector('.column__drop-zone--last') as HTMLElement | null) ??
          (targetCol.querySelector('.column__drop-zone') as HTMLElement);

        const dataTransfer = new DataTransfer();
        card.dispatchEvent(new DragEvent('dragstart', { bubbles: true, cancelable: true, dataTransfer }));
        dropZone.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer }));
        dropZone.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer }));
        card.dispatchEvent(new DragEvent('dragend', { bubbles: true, cancelable: true, dataTransfer }));
      }, { ...plan!, cardTitle: title });

      // The error dialog must show the actionable diagnosis — proving the
      // bug's bare-500 symptom is gone.
      await expect(page.locator('[data-testid="error-dialog"]')).toBeVisible({ timeout: 10_000 });
      const message = page.locator('[data-testid="error-dialog-message"]');
      await expect(message).toContainText('locked by another process');
      await expect(message).toContainText('Close the active CLI/log handle and retry');

      await page.screenshot({ path: 'test-results/move-locked-diagnosis.png', fullPage: true });
    } finally {
      if (jobId) {
        await api(`/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(wp.path)}`, {
          method: 'DELETE',
        }).catch(() => {});
      }
    }
  });
});
