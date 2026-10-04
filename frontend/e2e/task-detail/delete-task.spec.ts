import { test, expect, Page } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob, listJobs } from '../helpers/jobs';

interface WatchPath { name: string; path: string; rootPath: string; }

async function getFirstWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function deleteJobApi(jobId: string, watchPath: string): Promise<void> {
  await fetch(`${BACKEND}/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`, {
    method: 'DELETE',
    headers: { 'x-client-id': process.env.PW_CLIENT_ID?.trim() || 'local-default' }
  });
}

async function cleanupTestJobs(watchPath: string): Promise<void> {
  const all = await listJobs();
  const stale = all.filter(j => j.watchPath === watchPath && j.id.startsWith('e2e-del-'));
  await Promise.all(stale.map(j => deleteJobApi(j.id, j.watchPath).catch(() => {})));
}

function uid() {
  return `e2e-del-${Date.now()}-${Math.floor(Math.random() * 9999)}`;
}

/** Find the kanban card belonging to a specific job id. */
function cardLocator(page: Page, jobId: string) {
  return page.locator('app-job-card', { hasText: jobId });
}

/** AGT-2020: open a card's context menu and click its destructive Delete row. */
async function deleteViaCardMenu(page: Page, card: ReturnType<typeof cardLocator>) {
  await card.locator('[data-testid="task-card"]').click({ button: 'right' });
  const deleteItem = page.locator('[data-testid="card-ctx-item-delete-task"]');
  await expect(deleteItem).toBeVisible({ timeout: 3_000 });
  await deleteItem.click();
}

test.describe('Delete task', () => {
  test('context-menu Delete on a kanban card prompts and removes the job', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    await cleanupTestJobs(watchPath);

    const id = uid();
    const title = `e2e-del-card-${id}`;
    const job = await createJob({
      id,
      title,
      watchPath,
      targetState: '1-preparation',
      promptMarkdown: 'Fixture for delete-from-card test.',
      fixture: false
    });

    await page.goto('/');
    const card = cardLocator(page, job.id);
    await expect(card).toBeVisible({ timeout: 10_000 });

    await deleteViaCardMenu(page, card);

    // Unified confirm dialog replaces window.confirm; click the danger
    // button to accept.
    const confirmDialog = page.getByTestId('confirm-dialog-panel');
    await expect(confirmDialog).toBeVisible({ timeout: 5_000 });
    await expect(confirmDialog.getByTestId('confirm-dialog-detail')).toContainText(title);
    await page.getByTestId('confirm-dialog-confirm').click();

    // Card should disappear after the backend confirms + refresh tick.
    await expect(card).toHaveCount(0, { timeout: 10_000 });

    const after = await listJobs();
    expect(after.find(j => j.id === job.id)).toBeUndefined();
  });

  test('detail context menu Delete item prompts, deletes, and either closes the detail view or auto-advances past the deleted job', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    await cleanupTestJobs(watchPath);

    const id = uid();
    const job = await createJob({
      id,
      title: `e2e-del-detail-${id}`,
      watchPath,
      targetState: '1-preparation',
      promptMarkdown: 'Fixture for delete-from-detail test.',
      fixture: false
    });

    await page.goto('/');
    const card = cardLocator(page, job.id);
    await expect(card).toBeVisible({ timeout: 10_000 });
    await card.click();

    const menuBtn = page.getByTestId('detail-menu-btn');
    await expect(menuBtn).toBeVisible({ timeout: 10_000 });
    await menuBtn.click();

    const deleteItem = page.getByTestId('detail-menu-delete');
    await expect(deleteItem).toBeVisible();
    await deleteItem.click();

    const confirmDialog = page.getByTestId('confirm-dialog-panel');
    await expect(confirmDialog).toBeVisible({ timeout: 5_000 });
    const deleteResponse = page.waitForResponse(resp =>
      resp.request().method() === 'DELETE'
      && resp.url().includes(`/api/v1/projects/-/tasks/${encodeURIComponent(job.id)}`)
    );
    await page.getByTestId('confirm-dialog-confirm').click();
    await expect((await deleteResponse).ok()).toBeTruthy();

    // Acceptance: the user must never be left looking at the deleted job.
    // When the captured lane had more entries the detail-view advances to
    // the next captured slug; when the deleted job was the only one in the
    // iteration the panel falls back to the kanban dashboard. Either way
    // the deleted job's id must leave the URL.
    await expect.poll(async () => page.url().includes(`job=${encodeURIComponent(job.id)}`), {
      timeout: 10_000,
    }).toBe(false);

    await expect.poll(async () => {
      const after = await listJobs();
      return after.find(j => j.id === job.id);
    }, { timeout: 10_000 }).toBeUndefined();
  });

  test('cancelling the confirm dialog leaves the task in place', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    await cleanupTestJobs(watchPath);

    const id = uid();
    const job = await createJob({
      id,
      title: `e2e-del-cancel-${id}`,
      watchPath,
      targetState: '1-preparation',
      promptMarkdown: 'Fixture for cancel-delete test.',
      fixture: false
    });

    await page.goto('/');
    const card = cardLocator(page, job.id);
    await expect(card).toBeVisible({ timeout: 10_000 });
    await deleteViaCardMenu(page, card);

    const confirmDialog = page.getByTestId('confirm-dialog-panel');
    await expect(confirmDialog).toBeVisible({ timeout: 5_000 });
    await page.getByTestId('confirm-dialog-cancel').click();
    await expect(confirmDialog).toBeHidden({ timeout: 5_000 });

    // Wait briefly to make sure no delete went through.
    await page.waitForTimeout(500);
    const after = await listJobs();
    expect(after.find(j => j.id === job.id)).toBeDefined();

    // Final cleanup so the spec leaves no fixture behind.
    await deleteJobApi(job.id, watchPath);
  });

  test('Esc on the unified confirm dialog dismisses it without deleting', async ({ page }) => {
    const wp = await getFirstWatchPath();
    const watchPath = wp.path;
    await cleanupTestJobs(watchPath);

    const id = uid();
    const job = await createJob({
      id,
      title: `e2e-del-esc-${id}`,
      watchPath,
      targetState: '1-preparation',
      promptMarkdown: 'Fixture for Esc-dismiss confirm test.',
      fixture: false
    });

    await page.goto('/');
    const card = cardLocator(page, job.id);
    await expect(card).toBeVisible({ timeout: 10_000 });
    await deleteViaCardMenu(page, card);

    const confirmDialog = page.getByTestId('confirm-dialog-panel');
    await expect(confirmDialog).toBeVisible({ timeout: 5_000 });
    await page.keyboard.press('Escape');
    await expect(confirmDialog).toBeHidden({ timeout: 5_000 });

    await page.waitForTimeout(300);
    const after = await listJobs();
    expect(after.find(j => j.id === job.id)).toBeDefined();

    await deleteJobApi(job.id, watchPath);
  });
});
