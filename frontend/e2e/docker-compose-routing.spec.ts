import { expect, test } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';

test('one-box Docker web serves Studio and the Task Server protocol', async ({ page }, testInfo) => {
  const home = await page.goto('/');
  expect(home?.ok()).toBeTruthy();
  await expect(page.locator('app-root')).toHaveCount(1);

  const protocol = await page.request.get('/api/v1/protocol');
  expect(protocol.ok()).toBeTruthy();
  expect((await protocol.json()).current).toBeGreaterThan(0);

  const evidenceDir = process.env.JOB_RESULTS_DIR;
  if (evidenceDir) {
    await mkdir(evidenceDir, { recursive: true });
  }
  await page.screenshot({
    path: evidenceDir ? join(evidenceDir, 'compose-studio-home.png') : testInfo.outputPath('compose-studio-home.png'),
    fullPage: true,
  });
});
