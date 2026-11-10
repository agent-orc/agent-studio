import { test, expect } from '@playwright/test';
import path from 'node:path';
import { api } from '../helpers/api';
import { createJob, getJob } from '../helpers/jobs';

test('board explains an active batch refusal before explicit withdrawal', async ({ page }) => {
  const watch = (await api<Array<{ path: string }>>('/api/watch-paths'))[0];
  const id = `e2e-batch-withdraw-${Date.now()}`;
  const title = `Batch withdrawal ${id}`;
  await createJob({ id, title, watchPath: watch.path, targetState: '2-ready', fixture: false });
  const requests: Array<{ withdrawFromBatch?: boolean }> = [];
  await page.route(`**/tasks/${id}/move**`, async route => {
    const body = route.request().postDataJSON() as { withdrawFromBatch?: boolean };
    requests.push(body);
    if (!body.withdrawFromBatch) {
      await route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({
        error: 'active-batch-member', batchId: 'batch-ui-proof', phase: 'running',
        otherMembers: ['AGT-3574'],
        message: `${id} belongs to batch batch-ui-proof (running). Other members: AGT-3574. Confirm withdrawal to continue.`,
      }) });
    } else await route.continue();
  });

  try {
    await page.goto('/');
    await expect(page.locator('.column__title').first()).toBeVisible();
    await expect(page.getByText(title, { exact: true })).toBeVisible();
    await expect(page.getByTestId('offline-banner')).toBeHidden({ timeout: 30_000 });
    await page.evaluate(cardTitle => {
      const columns = Array.from(document.querySelectorAll('.column')) as HTMLElement[];
      const ready = columns.find(column => column.querySelector('.column__title')?.textContent?.trim() === 'Ready');
      const backlog = columns.find(column => column.querySelector('.column__title')?.textContent?.trim() === 'Backlog');
      const card = Array.from(ready?.querySelectorAll('app-job-card') ?? []).find(element =>
        element.querySelector('.task-card__title-text')?.textContent?.trim() === cardTitle);
      const zone = backlog?.querySelector('.column__drop-zone--last');
      if (!card || !zone) throw new Error('Expected Ready card and Backlog drop zone');
      const transfer = new DataTransfer();
      card.dispatchEvent(new DragEvent('dragstart', { bubbles: true, dataTransfer: transfer }));
      zone.dispatchEvent(new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: transfer }));
      zone.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: transfer }));
      card.dispatchEvent(new DragEvent('dragend', { bubbles: true, dataTransfer: transfer }));
    }, title);
    await expect(page.getByTestId('confirm-dialog-message')).toContainText('batch-ui-proof');
    await expect(page.getByTestId('confirm-dialog-message')).toContainText('AGT-3574');
    await expect(page.getByTestId('confirm-dialog')).toContainText('Confirm withdrawal');
    if (process.env['JOB_RESULTS_DIR'])
      await page.screenshot({ path: path.join(process.env['JOB_RESULTS_DIR'], 'batch-withdraw-confirm.png') });
    expect(requests).toHaveLength(1);
    await page.getByTestId('confirm-dialog-confirm').click();
    await expect.poll(() => requests.length).toBe(2);
    expect(requests[1].withdrawFromBatch).toBe(true);
    await expect.poll(async () => (await getJob(id, watch.path)).state).toBe('0-backlog');
  } finally {
    await api(`/api/tasks/${id}?watchPath=${encodeURIComponent(watch.path)}`, { method: 'DELETE' }).catch(() => {});
  }
});
