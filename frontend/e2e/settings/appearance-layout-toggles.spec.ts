import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Settings panel — Appearance (Theme) + Layout (Activity bar) segmented
 * toggles (ASS-712 shared SegmentedControl).
 *
 * Pins the polish contract the cards were filed for:
 *   1. Each toggle is a labelled `role="group"` of buttons (not two glued
 *      words), with exactly one option carrying `aria-pressed="true"`.
 *   2. Clicking the inactive option flips the selection AND drives the real
 *      side effect — the Theme switch flips `<html data-studio-theme>`.
 *   3. The Activity bar switch flips its pressed option the same way.
 *
 * Theme + activity-bar side persist to localStorage only, and Playwright
 * gives each test a fresh context, so no explicit cleanup is required.
 */
test.describe('Settings — Appearance/Layout segmented toggles', () => {
  test('System theme follows OS changes at desktop and phone widths', async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('atp.studio.theme', 'system'));
    const output = process.env.JOB_RESULTS_DIR ?? 'test-results';
    mkdirSync(output, { recursive: true });
    for (const width of [1280, 390]) {
      await page.setViewportSize({ width, height: 800 });
      await page.emulateMedia({ colorScheme: 'dark' });
      await page.goto('/#/workspace/settings/appearance');
      if (width === 390) {
        await page.getByTestId('studio-ab-explorer').click();
        await expect(page.getByTestId('studio-sidebar')).toBeHidden();
      }
      const appearance = page.getByTestId('appearance-settings');
      await appearance.scrollIntoViewIfNeeded();
      await expect(appearance).toBeInViewport();
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
      await expect(page.getByTestId('settings-theme-system')).toHaveAttribute('aria-pressed', 'true');
      await page.screenshot({ path: join(output, `appearance-system-${width}-dark.png`), animations: 'disabled' });
      await page.emulateMedia({ colorScheme: 'light' });
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'light');
      await page.screenshot({ path: join(output, `appearance-system-${width}-light.png`), animations: 'disabled' });
    }
  });

  test('Theme toggle: labelled group, single pressed option, flips the document theme', async ({ page }) => {
    await page.addInitScript(() => {
      try { localStorage.setItem('atp.studio.theme', 'light'); } catch { /* ignore */ }
    });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    // AGT-2035: the gear opens the consolidated Settings view; Theme lives in
    // the Appearance section (Global group).
    await page.getByTestId('studio-ab-settings').click();
    await page.getByTestId('workspace-settings-rail-appearance').click({ timeout: 10_000 });

    const themeGroup = page.getByRole('group', { name: 'Theme' });
    await expect(themeGroup).toBeVisible({ timeout: 10_000 });

    const dark = page.getByTestId('settings-theme-dark');
    const light = page.getByTestId('settings-theme-light');

    // Light is active on load.
    await expect(light).toHaveAttribute('aria-pressed', 'true');
    await expect(dark).toHaveAttribute('aria-pressed', 'false');
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'light');

    // Switching to Dark flips both the pressed state and the document theme.
    await dark.click();
    await expect(dark).toHaveAttribute('aria-pressed', 'true');
    await expect(light).toHaveAttribute('aria-pressed', 'false');
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
  });

  test('Activity bar toggle: labelled group, single pressed option, flips on click', async ({ page }) => {
    await page.addInitScript(() => {
      try { localStorage.setItem('atp.studio.activityBarSide', 'left'); } catch { /* ignore */ }
    });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.getByTestId('studio-ab-settings').click();
    await page.getByTestId('workspace-settings-rail-appearance').click({ timeout: 10_000 });

    const abGroup = page.getByRole('group', { name: 'Activity bar' });
    await expect(abGroup).toBeVisible({ timeout: 10_000 });

    const left = page.getByTestId('settings-activitybar-left');
    const right = page.getByTestId('settings-activitybar-right');

    await expect(left).toHaveAttribute('aria-pressed', 'true');
    await expect(right).toHaveAttribute('aria-pressed', 'false');

    await right.click();
    await expect(right).toHaveAttribute('aria-pressed', 'true');
    await expect(left).toHaveAttribute('aria-pressed', 'false');
  });
});
