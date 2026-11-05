import { expect, test } from '@playwright/test';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

// The public-demo Playwright profile starts an isolated seeded backend behind
// the local HTTPS frontend proxy. No API response is mocked in this spec.
test('public demo banner leaves the titlebar and project picker operable', async ({ page }) => {
  test.skip(process.env.PW_E2E_PROFILE !== 'public-demo', 'Run with the public-demo Playwright profile.');

  await page.goto('/');
  const banner = page.getByTestId('public-demo-banner');
  const titlebar = page.getByTestId('studio-titlebar');
  await expect(banner).toBeVisible();
  await expect(banner).toHaveAttribute('role', 'status');
  await expect(banner).not.toHaveAttribute('aria-live', /.+/);
  await expect(banner).toHaveCount(1);
  await expect(titlebar).toBeVisible();
  await expect(page.getByTestId('studio-board-add-task')).toBeDisabled();

  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 720 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      const geometry = await page.evaluate(() => {
        const banner = document.querySelector<HTMLElement>('[data-testid="public-demo-banner"]')!.getBoundingClientRect();
        const titlebar = document.querySelector<HTMLElement>('[data-testid="studio-titlebar"]')!.getBoundingClientRect();
        const center = document.elementFromPoint(titlebar.left + titlebar.width / 2, titlebar.top + titlebar.height / 2);
        return {
          bannerBottom: banner.bottom,
          titlebarTop: titlebar.top,
          titlebarBottom: titlebar.bottom,
          centerBelongsToTitlebar: !!center && document.querySelector('[data-testid="studio-titlebar"]')!.contains(center),
        };
      });
      expect(geometry.bannerBottom, `${width}px ${theme}: banner must end above titlebar`).toBeLessThanOrEqual(geometry.titlebarTop);
      expect(geometry.titlebarBottom, `${width}px ${theme}: titlebar must remain in viewport`).toBeLessThanOrEqual(720);
      expect(geometry.centerBelongsToTitlebar, `${width}px ${theme}: titlebar center must receive pointer events`).toBe(true);

      const screenshot = `public-demo-after-${width}-${theme}.png`;
      await page.screenshot({
        path: process.env.JOB_RESULTS_DIR ? join(process.env.JOB_RESULTS_DIR, screenshot) : `test-results/${screenshot}`,
        fullPage: false,
      });

      if (width === 1280) {
        await page.getByTestId('studio-project-picker-trigger').click();
      } else {
        await page.getByRole('button', { name: 'More navigation' }).click();
        await page.getByRole('menuitem', { name: 'Switch project' }).click();
      }
      await expect(page.getByTestId('studio-project-picker-panel')).toBeVisible();
      await page.keyboard.press('Escape');
      await expect(page.getByTestId('studio-project-picker-panel')).toHaveCount(0);
    }
  }
});
