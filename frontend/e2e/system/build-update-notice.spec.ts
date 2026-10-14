import { test, expect, type APIResponse } from '@playwright/test';
import path from 'node:path';

test('a stale Studio tab shows one reload notice without blocking the board', async ({ page }) => {
  test.setTimeout(120_000);
  const newerSha = 'b'.repeat(40);
  await page.route('**/api/**', async route => {
    let response: APIResponse;
    try {
      response = await route.fetch({ timeout: 15_000 });
    } catch {
      await route.fulfill({
        status: 503,
        headers: { 'x-agent-studio-commit': newerSha },
        body: 'Unavailable',
      });
      return;
    }
    await route.fulfill({ response, headers: {
      ...response.headers(),
      'x-agent-studio-commit': newerSha,
    } });
  });

  await page.goto('/');
  const notice = page.getByText('Agent Studio was updated – reload to continue');
  await expect(notice).toBeVisible();
  await expect(notice).toHaveCount(1);
  await expect(page.getByTestId('build-update-reload')).toBeVisible();
  await expect(page.getByTestId('notification-stack')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Refresh project list' })).toBeVisible();

  const results = process.env.JOB_RESULTS_DIR;
  if (results) {
    await page.screenshot({
      path: path.join(results, 'build-update-notice-light.png'),
      fullPage: false,
    });
    await page.evaluate(() => { document.documentElement.dataset['studioTheme'] = 'dark'; });
    await page.screenshot({
      path: path.join(results, 'build-update-notice-dark.png'),
      fullPage: false,
    });
  }
});
