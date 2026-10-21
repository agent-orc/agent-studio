import { test, expect, type Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, moveJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths.find(path => /agent studio worktree|playwright/i.test(path.name)) ?? paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await api(`/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`,
    { method: 'DELETE' });
}

function uid(suffix: string) {
  return `e2e-extlane-${suffix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

function projectBoardUrl(name: string): string {
  const slug = name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  return `/#/projects/${slug}/board`;
}

async function clickCardAndWaitForDetail(page: Page, jobId: string): Promise<void> {
  await page.getByTestId('task-card').filter({ hasText: jobId }).first().click();
  await expect(page).toHaveURL(/#\/tasks\/[^/?]+/, { timeout: 10_000 });
  await expect(page.getByRole('tab', { name: jobId })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByTestId('detail-state-select')).toBeVisible({ timeout: 10_000 });
}

async function waitForFixtureCards(page: Page, ids: ReadonlyArray<string>): Promise<void> {
  for (const id of ids) {
    await expect(page.getByTestId('task-card').filter({ hasText: id }).first())
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
      await expect.poll(async () =>
        (await api<Array<{ id: string }>>('/api/tasks?includeFixtures=true')).map(job => job.id),
      { timeout: 15_000 }).toContain(ids[0]);
      await page.goto(projectBoardUrl(wp.name));
      await waitForFixtureCards(page, ids);

      // Open the third job. Pager captures the 2-ready lane.
      await clickCardAndWaitForDetail(page, ids[2]);
      const selectedUrl = page.url();
      if (process.env.JOB_RESULTS_DIR)
        await page.screenshot({ path: `${process.env.JOB_RESULTS_DIR}/task-detail-ready.png` });
      const count = page.getByTestId('lane-pager-count');
      await expect(count).toContainText(/^\d+ \/ \d+$/, { timeout: 5_000 });
      const initial = (await count.textContent())!.trim();
      const [, totalStr] = initial.split('/').map(s => s.trim());
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
      await expect(page).toHaveURL(selectedUrl);
      await expect(page.getByRole('tab', { name: ids[2] })).toHaveAttribute('aria-selected', 'true');

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
        await expect(page).not.toHaveURL(selectedUrl, { timeout: 10_000 });
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

  test('user-initiated move via the action menu still auto-advances', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const ids = [1, 2, 3].map(i => uid(`userinit-${i}`));
    for (const id of ids) {
      await createJob({ id, title: id, watchPath: wp.path, targetState: '2-ready', fixture: false });
    }

    try {
      await page.goto(projectBoardUrl(wp.name));
      await waitForFixtureCards(page, ids);
      await clickCardAndWaitForDetail(page, ids[0]);
      const selectedUrl = page.url();

      const count = page.getByTestId('lane-pager-count');
      await expect(count).toContainText(/^\d+ \/ \d+$/, { timeout: 5_000 });
      const initial = (await count.textContent())!.trim();
      const [, totalStr] = initial.split('/').map(s => s.trim());
      const startTotal = Number(totalStr);

      // Move through the task action menu. The state selector pages through lanes.
      await page.getByTestId('studio-triage-overflow-btn').click();
      await page.getByTestId('studio-triage-overflow-item-move-to-completed').click();
      await expect.poll(async () => (await api<{ info: { state: string } }>(
        `/api/tasks/${encodeURIComponent(ids[0])}?watchPath=${encodeURIComponent(wp.path)}`
      )).info.state, { timeout: 10_000 }).toBe('6-completed');

      // The detail advances to a remaining Ready card and the pager shrinks.
      await expect(page).not.toHaveURL(selectedUrl, { timeout: 10_000 });
      await expect(count).toContainText(new RegExp(`^\\d+ \\/ ${startTotal - 1}$`), { timeout: 5_000 });
    } finally {
      for (const id of ids) {
        await moveJob(id, wp.path, '7-archive').catch(() => {});
        await deleteJob(id, wp.path).catch(() => {});
      }
    }
  });
});
