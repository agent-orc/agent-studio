import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const setKey = 'atp.studio.tokenSet';
const defaultSelection = { version: 1, id: 'studio-default' };

async function expectResolved(page: Page, theme: 'light' | 'dark') {
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
  await expect(page.locator('html')).toHaveAttribute('data-studio-token-set', `studio-default.${theme}`);
  await expect.poll(() => page.evaluate(key => JSON.parse(localStorage.getItem(key) ?? 'null'), setKey))
    .toEqual(defaultSelection);
}

for (const width of [1280, 390]) {
  test.describe(`Runtime token sets at ${width}px`, () => {
    test.use({ viewport: { width, height: 844 } });

    test('theme choices survive reload and System keeps following the OS', async ({ page }) => {
      await page.emulateMedia({ colorScheme: 'dark' });
      await page.goto('/#/workspace/settings/appearance');
      await expect(page.getByTestId('appearance-settings')).toBeVisible();

      for (const preference of ['dark', 'light', 'system'] as const) {
        const resolved = preference === 'system' ? 'dark' : preference;
        await page.getByTestId(`settings-theme-${preference}`).click();
        await expectResolved(page, resolved);
        await page.reload();
        await expectResolved(page, resolved);
        await expect(page.getByTestId(`settings-theme-${preference}`)).toHaveAttribute('aria-pressed', 'true');
        await expect.poll(() => page.evaluate(() => localStorage.getItem('atp.studio.theme'))).toBe(preference);
      }

      await page.emulateMedia({ colorScheme: 'light' });
      await expectResolved(page, 'light');
      await expect(page.getByTestId('settings-theme-system')).toHaveAttribute('aria-pressed', 'true');
      await page.reload();
      await expectResolved(page, 'light');
      await page.emulateMedia({ colorScheme: 'dark' });
      await expectResolved(page, 'dark');

      // Explicit choices ignore subsequent OS changes.
      await page.getByTestId('settings-theme-light').click();
      await page.emulateMedia({ colorScheme: 'light' });
      await page.emulateMedia({ colorScheme: 'dark' });
      await expectResolved(page, 'light');
    });

    test('unknown ids and future persistence versions fall back without changing theme preference', async ({ page }) => {
      await page.goto('/#/workspace/settings/appearance');
      for (const selection of [{ version: 1, id: 'removed-prototype' }, { version: 99, id: 'studio-default' }]) {
        await page.evaluate(({ key, selection }) => {
          localStorage.setItem(key, JSON.stringify(selection));
          localStorage.setItem('atp.studio.theme', 'dark');
        }, { key: setKey, selection });
        await page.reload();
        await expectResolved(page, 'dark');
        await expect(page.getByTestId('settings-theme-dark')).toHaveAttribute('aria-pressed', 'true');
      }
    });

    // This test also runs against the pre-change build for baseline capture.
    // It deliberately uses only the existing theme controls and CSS aliases.
    test('default-set appearance evidence in both themes', async ({ page }, testInfo) => {
      const output = process.env.JOB_RESULTS_DIR ?? testInfo.outputDir;
      mkdirSync(output, { recursive: true });
      await page.goto('/#/workspace/settings/appearance');
      const appearance = page.getByTestId('appearance-settings');
      await expect(appearance).toBeVisible();
      for (const theme of ['light', 'dark'] as const) {
        await page.getByTestId(`settings-theme-${theme}`).click();
        await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
        await appearance.scrollIntoViewIfNeeded();
        const bounds = await appearance.boundingBox();
        expect(bounds).not.toBeNull();
        expect(bounds!.x).toBeGreaterThanOrEqual(0);
        expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width + 1);
        await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await appearance.screenshot({
          path: join(output, `runtime-token-sets-${theme}-${width}--real.png`),
          animations: 'disabled',
        });
      }
    });
  });
}
