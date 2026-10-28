import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

test('board header projects a red develop receipt in both themes', async ({ page }) => {
  await page.route('**/api/develop-states', async route => {
    await route.fulfill({ json: [
      { project: 'Agent Studio Worktree', developState: 'red', lastGreenSha: 'a'.repeat(40) },
    ] });
  });
  await page.goto('/');
  await expect(page.getByTestId('studio-board')).toBeVisible();
  const indicator = page.getByTestId('board-develop-state');
  await expect(indicator).toHaveText('Develop red');
  await expect(indicator).toHaveAttribute('title', 'Agent Studio Worktree last green develop SHA: ' + 'a'.repeat(40));
  const directory = process.env.JOB_RESULTS_DIR;
  if (directory) {
    mkdirSync(directory, { recursive: true });
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'light');
    await expect(page.getByTestId('offline-banner')).toBeHidden({ timeout: 30_000 });
    await indicator.screenshot({ path: join(directory, 'develop-state-light.png') });
  }
  await page.getByTestId('studio-titlebar-theme').click();
  await expect(indicator).toHaveText('Develop red');
  if (directory) {
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
    await expect(page.getByTestId('offline-banner')).toBeHidden({ timeout: 30_000 });
    await indicator.screenshot({ path: join(directory, 'develop-state-dark.png') });
  }
});
