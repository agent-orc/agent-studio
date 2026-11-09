import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, getJob } from '../helpers/jobs';
import { setTheme } from '../helpers/theme';
import path from 'node:path';

interface WatchPath { name: string; path: string; rootPath: string; }

test('card header shows the active attention holder in both themes', async ({ page }) => {
  const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
  const watch = paths.find(item => /playwright/i.test(item.name)) ?? paths[0];
  expect(watch).toBeTruthy();
  const id = `e2e-attention-${Date.now()}`;
  const card = await createJob({ id, title: id, watchPath: watch.path,
    targetState: '2-ready', mode: 'concept', requiresIntegration: false });
  try {
    await expect.poll(async () => (await getJob(card.id, watch.path)).id,
      { timeout: 15_000 }).toBe(card.id);
    const response = await api<{ lease: { holder: string } }>(
      `/api/tasks/${encodeURIComponent(card.id)}/attention?watchPath=${encodeURIComponent(watch.path)}`,
      { method: 'POST' });
    await page.goto(`/?job=${encodeURIComponent(card.id)}&watchPath=${encodeURIComponent(watch.path)}`);
    const holder = page.getByTestId('studio-attention-holder');
    await expect(holder).toContainText(response.lease.holder, { timeout: 15_000 });
    await expect(page.getByRole('heading', { name: id })).toBeVisible();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(holder).toBeVisible();
      await page.screenshot({ path: path.join(process.env.JOB_RESULTS_DIR ?? 'test-results',
        `operator-attention-${theme}.png`) });
    }
  } finally {
    await api(`/api/tasks/${encodeURIComponent(card.id)}?watchPath=${encodeURIComponent(watch.path)}`,
      { method: 'DELETE' }).catch(() => {});
  }
});
