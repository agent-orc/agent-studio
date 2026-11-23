import { test, expect } from '@playwright/test';

// AGT-3988 Dossier capture (docs/red-test-isolation): the mocked red receipt of
// frontend/e2e/board/develop-state.spec.ts, captured as a header strip instead of the element alone.
test('studio header with a red develop receipt', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.route('**/api/develop-states', async route => {
    await route.fulfill({ json: [
      { project: 'Agent Studio Worktree', developState: 'red', lastGreenSha: 'a'.repeat(40) },
    ] });
  });
  await page.goto('/');
  await expect(page.getByTestId('studio-board')).toBeVisible();
  const indicator = page.getByTestId('board-develop-state');
  await expect(indicator).toHaveText('Develop red');
  const out = process.env.CAPTURE_DIR!;
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'light');
  await expect(page.getByTestId('offline-banner')).toBeHidden({ timeout: 30_000 });
  const box = await indicator.boundingBox();
  const clip = { x: 0, y: 0, width: 1280, height: Math.ceil((box!.y + box!.height) + 24) };
  await page.screenshot({ path: `${out}/light.png`, clip });
  await page.getByTestId('studio-titlebar-theme').click();
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
  await expect(indicator).toHaveText('Develop red');
  await expect(page.getByTestId('offline-banner')).toBeHidden({ timeout: 30_000 });
  await page.screenshot({ path: `${out}/dark.png`, clip });
  console.log('indicator box', JSON.stringify(box));
});
