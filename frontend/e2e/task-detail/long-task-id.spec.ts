import { test, expect } from '../fixtures/dev-backend';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

test.setTimeout(10 * 60_000);

test('opens a board card whose id exceeds the old core limit', async ({ page, devBackend }) => {
  void devBackend;
  const paths = await api<{ path: string }[]>('/api/watch-paths?includeFixtures=true');
  const watchPath = paths[0]?.path;
  expect(watchPath).toBeTruthy();
  const id = `e2e-long-${Date.now()}-${'x'.repeat(178)}`;
  const title = `Long task id ${Date.now()}`;
  const created = await createJob({ id, title, watchPath: watchPath!, targetState: '2-ready', fixture: false });
  expect(created.id.length).toBeGreaterThan(128);

  try {
    const coreReads: { status: number; id: string }[] = [];
    page.on('response', async response => {
      const url = new URL(response.url());
      if (!url.pathname.endsWith(`/${created.id}/core`) || response.status() !== 200) return;
      const body = await response.json();
      coreReads.push({ status: response.status(), id: body.id });
    });
    await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 60_000 });
    const card = page.locator('[data-testid="task-card"], [data-testid="job-card"]')
      .filter({ hasText: title }).first();
    await expect(card).toBeVisible({ timeout: 30_000 });
    await card.click();
    await expect(page.getByTestId('studio-task')).toContainText(title);
    await expect.poll(() => coreReads.some(read => read.status === 200 && read.id === created.id),
      { timeout: 30_000 }).toBe(true);
    await expect(page.getByText('Loading task context…')).toHaveCount(0, { timeout: 30_000 });
    await expect(page.getByTestId('task-detail-load-error')).toHaveCount(0);
    if (process.env.JOB_RESULTS_DIR)
      await page.screenshot({ path: `${process.env.JOB_RESULTS_DIR}/long-task-detail.png` });
  } finally {
    await api(`/api/tasks/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(watchPath!)}`,
      { method: 'DELETE' });
  }
});
