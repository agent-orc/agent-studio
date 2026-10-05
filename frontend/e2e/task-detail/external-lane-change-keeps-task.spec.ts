import { test, expect, type Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, moveJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': 'local-default' },
  });
}

function uid(suffix: string) {
  return `e2e-extlane-${suffix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

async function clickCardAndWaitForDetail(page: Page, jobId: string): Promise<void> {
  await page.locator('[data-testid="job-card"]', { hasText: jobId }).first().click();
  await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(jobId)}`), { timeout: 10_000 });
  await expect(page.getByTestId('detail-state-select')).toBeVisible({ timeout: 10_000 });
}

async function waitForFixtureCards(page: Page, ids: ReadonlyArray<string>): Promise<void> {
  for (const id of ids) {
    await expect(page.locator('[data-testid="job-card"]', { hasText: id }).first())
      .toBeVisible({ timeout: 15_000 });
  }
}

test.describe('External lane change keeps task in view', () => {
  test('external move via API keeps the detail view on the same task and shrinks the pager', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3, 4, 5].map(i => uid(`ext-${i}`));
    for (const id of ids) {
      await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false });
    }

    try {
      await page.goto('/');
      await waitForFixtureCards(page, ids);

      // Open the third job. Pager captures the 2-ready lane.
      await clickCardAndWaitForDetail(page, ids[2]);
      const count = page.getByTestId('lane-pager-count');
      await expect(count).toContainText(/^\d+ \/ \d+$/, { timeout: 5_000 });
      const initial = (await count.textContent())!.trim();
      const [posStr, totalStr] = initial.split('/').map(s => s.trim());
      const startTotal = Number(totalStr);
      expect(startTotal).toBeGreaterThanOrEqual(5);

      // Externally move this job to 3-progress via the API (simulates runner
      // auto-pickup or another client). The frontend learns about it on the
      // next poll cycle.
      await moveJob(ids[2], wp.path, '3-progress');

      // The detail view must STAY on the same task (ids[2]).
      // Wait for the pager to reflect the change (poll cycle + effect).
      await expect(count).toContainText(new RegExp(`— \\/ ${startTotal - 1}`), { timeout: 15_000 });

      // URL still points at the same job.
      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(ids[2])}`));

      // The lane dropdown now shows the new state (3-progress).
      await expect(page.getByTestId('detail-state-select')).toHaveValue('3-progress', { timeout: 10_000 });

      // Subtle toast visible.
      const toast = page.locator('.notification', { hasText: /still viewing this task/i });
      await expect(toast).toBeVisible({ timeout: 10_000 });

      // Prev/Next buttons are still functional (navigate remaining peers).
      const nextBtn = page.getByTestId('lane-pager-next');
      const prevBtn = page.getByTestId('lane-pager-prev');
      // At least one of prev/next should be enabled since there are 4+ remaining peers.
      const nextDisabled = await nextBtn.isDisabled();
      const prevDisabled = await prevBtn.isDisabled();
      expect(nextDisabled && prevDisabled).toBe(false);

      // Click Next - should navigate to a peer still in 2-ready.
      if (!nextDisabled) {
        await nextBtn.click();
        // After stepping, we should land on a different job from the original lane.
        await expect(page).not.toHaveURL(new RegExp(`job=${encodeURIComponent(ids[2])}`), { timeout: 10_000 });
        // Pager should now show a numeric position (we're on a member).
        await expect(count).toContainText(/^\d+ \/ \d+$/, { timeout: 5_000 });
      }
    } finally {
      for (const id of ids) {
        await moveJob(id, wp.path, '7-archive').catch(() => {});
        await deleteJob(id, wp.path).catch(() => {});
      }
    }
  });

  test('user-initiated lane change via state dropdown still auto-advances', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3].map(i => uid(`userinit-${i}`));
    for (const id of ids) {
      await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false });
    }

    try {
      await page.goto('/');
      await waitForFixtureCards(page, ids);
      await clickCardAndWaitForDetail(page, ids[0]);

      const count = page.getByTestId('lane-pager-count');
      await expect(count).toContainText(/^\d+ \/ \d+$/, { timeout: 5_000 });
      const initial = (await count.textContent())!.trim();
      const [posStr, totalStr] = initial.split('/').map(s => s.trim());
      const startPos = Number(posStr);
      const startTotal = Number(totalStr);

      // Move via the state dropdown (user-initiated) - should auto-advance.
      const moveResponse = page.waitForResponse(resp =>
        resp.request().method() === 'POST'
        && resp.url().includes(`/api/v1/projects/-/tasks/${encodeURIComponent(ids[0])}/move`)
      );
      await page.getByTestId('detail-state-select').selectOption('4-auto-review');
      await moveResponse;

      // Should auto-advance to the next job (NOT stay on the moved job).
      await expect(page).toHaveURL(new RegExp(`job=${encodeURIComponent(ids[1])}`), { timeout: 10_000 });
      // Pager shrinks by one and position is preserved.
      await expect(count).toHaveText(`${startPos} / ${startTotal - 1}`, { timeout: 5_000 });
    } finally {
      for (const id of ids) {
        await moveJob(id, wp.path, '7-archive').catch(() => {});
        await deleteJob(id, wp.path).catch(() => {});
      }
    }
  });
});
