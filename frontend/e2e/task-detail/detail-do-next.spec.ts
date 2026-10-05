import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }
interface Job { id: string; state: string; order: number; watchPath: string; projectName: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJob(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE'
  });
}

async function listReadyForProject(watchPath: string, projectName: string): Promise<Job[]> {
  const all = await api<Job[]>('/api/tasks?includeFixtures=true');
  return all.filter(j => j.state === '2-ready' && j.watchPath === watchPath && j.projectName === projectName);
}

function uid(suffix: string) {
  return `e2e-do-next-${suffix}-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

/**
 * "Do Next" button on the detail header surfaces while a task is in 2-ready
 * (queued, not yet picked up). Clicking it pushes the task to the head of
 * the project's ready queue so the runner picks it up on the next tick.
 *
 * Implementation reuses the existing /api/tasks/reorder endpoint: the lane is
 * rewritten with this task at index 0, so its `order` ends up the smallest
 * within the project's 2-ready set.
 */
test.describe('Detail view — Do Next', () => {
  test('button is hidden outside 2-ready', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const id = uid('hidden');
    const created = await createJob({
      id,
      title: `do-next ${id}`,
      watchPath: wp.path,
      targetState: '1-preparation'
    });

    try {
      await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(wp.path)}`);
      const select = page.getByTestId('detail-state-select');
      await expect(select).toBeVisible({ timeout: 10_000 });
      await expect(select).toHaveValue('1-preparation');
      await expect(page.getByTestId('do-next-btn')).toHaveCount(0);
    } finally {
      await deleteJob(created.id, wp.path).catch(() => {});
    }
  });

  test('button reorders the ready lane so this task lands at the top', async ({ page }) => {
    const wp = await getFirstWatchPath();

    // Plant a few sibling ready jobs so the lane has something to reorder
    // against. The target job is created last so it naturally sits at the
    // bottom of the ready lane until the user clicks "Do Next".
    const siblingA = await createJob({ id: uid('a'), title: 'sibling-a', watchPath: wp.path, targetState: '2-ready' });
    const siblingB = await createJob({ id: uid('b'), title: 'sibling-b', watchPath: wp.path, targetState: '2-ready' });
    const target   = await createJob({ id: uid('t'), title: 'target',    watchPath: wp.path, targetState: '2-ready' });

    try {
      await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(wp.path)}`);
      const select = page.getByTestId('detail-state-select');
      await expect(select).toBeVisible({ timeout: 10_000 });
      await expect(select).toHaveValue('2-ready');

      // Read the project name + initial order so we can assert correctly.
      const beforeReady = await listReadyForProject(wp.path, await api<{ info: { projectName: string } }>(
        `/api/tasks/${encodeURIComponent(target.id)}?watchPath=${encodeURIComponent(wp.path)}`
      ).then(d => d.info.projectName));
      const projectName = beforeReady.find(j => j.id === target.id)!.projectName;
      const orderedBefore = [...beforeReady].sort((a, b) => a.order - b.order).map(j => j.id);
      expect(orderedBefore).toContain(target.id);
      // Target is the last created, so it should not already be first.
      expect(orderedBefore[0]).not.toBe(target.id);

      const btn = page.getByTestId('do-next-btn');
      await expect(btn).toBeVisible();
      await btn.click();

      // Backend reorders: target becomes the lowest-order job in the project.
      await expect.poll(
        async () => {
          const ready = await listReadyForProject(wp.path, projectName);
          const sorted = [...ready].sort((a, b) => a.order - b.order).map(j => j.id);
          return sorted[0];
        },
        { timeout: 10_000 }
      ).toBe(target.id);

      // Button stays available (the task is still in 2-ready) but no longer
      // disabled mid-flight.
      await expect(btn).toBeVisible();
      await expect(btn).toBeEnabled();
    } finally {
      await deleteJob(target.id, wp.path).catch(() => {});
      await deleteJob(siblingA.id, wp.path).catch(() => {});
      await deleteJob(siblingB.id, wp.path).catch(() => {});
    }
  });

  test('button disables itself while the reorder POST is in flight', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const siblingA = await createJob({ id: uid('a'), title: 'sib-a', watchPath: wp.path, targetState: '2-ready' });
    const target   = await createJob({ id: uid('t'), title: 'do-next-pending', watchPath: wp.path, targetState: '2-ready' });

    try {
      await page.route('**/api/v1/projects/*/tasks/*/move-to-top*', async route => {
        await new Promise(r => setTimeout(r, 800));
        await route.continue();
      });

      await page.goto(`/?job=${encodeURIComponent(target.id)}&watchPath=${encodeURIComponent(wp.path)}`);
      const btn = page.getByTestId('do-next-btn');
      await expect(btn).toBeVisible({ timeout: 10_000 });
      await btn.click();

      await expect(btn).toBeDisabled({ timeout: 2_000 });
      await expect(btn).toBeEnabled({ timeout: 5_000 });
    } finally {
      await page.unroute('**/api/v1/projects/*/tasks/*/move-to-top*').catch(() => {});
      await deleteJob(target.id, wp.path).catch(() => {});
      await deleteJob(siblingA.id, wp.path).catch(() => {});
    }
  });
});
