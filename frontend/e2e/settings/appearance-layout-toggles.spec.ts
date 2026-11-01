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
      await assertSegmentMetrics(page);
      await page.screenshot({ path: join(output, `appearance-system-${width}-dark--real.png`), animations: 'disabled' });
      await page.emulateMedia({ colorScheme: 'light' });
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'light');
      await assertSegmentMetrics(page);
      await page.screenshot({ path: join(output, `appearance-system-${width}-light--real.png`), animations: 'disabled' });
    }
  });

  test('create dialog stays within 390 px and its thumb beats light panel rules', async ({ page }) => {
    const output = process.env.JOB_RESULTS_DIR ?? 'test-results';
    mkdirSync(output, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await page.setViewportSize({ width: 1280, height: 844 });
      await page.goto('/');
      await page.evaluate(value => localStorage.setItem('atp.studio.theme', value), theme);
      await page.reload();
      await page.getByRole('button', { name: /add task/i }).first().click();
      const dialog = page.locator('.create-dialog');
      await expect(dialog).toBeVisible();
      await page.setViewportSize({ width: 390, height: 844 });
      const bounds = await dialog.boundingBox();
      expect(bounds).not.toBeNull();
      expect(bounds!.x).toBeGreaterThanOrEqual(0);
      expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(391);
      const typePicker = page.getByTestId('create-task-type');
      await expect(typePicker.getByRole('button', { name: 'Chore' })).toHaveAttribute('aria-pressed', 'true');
      await dialog.screenshot({ path: join(output, `create-dialog-${theme}--real.png`), animations: 'disabled' });
      if (theme === 'light') {
        // Apply the real light .panel cascade to a migrated control.
        await typePicker.evaluate(el => el.classList.add('panel'));
        const colors = await typePicker.evaluate(el => {
          const active = el.querySelector<HTMLButtonElement>('[aria-pressed="true"]')!;
          const inactive = el.querySelector<HTMLButtonElement>('[aria-pressed="false"]')!;
          return [getComputedStyle(active).backgroundColor, getComputedStyle(inactive).backgroundColor];
        });
        expect(colors[0]).not.toBe(colors[1]);
      }
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

async function assertSegmentMetrics(page: import('@playwright/test').Page) {
  const readMetrics = () => page.getByRole('group', { name: 'Theme' }).evaluate(group => {
    const active = group.querySelector<HTMLButtonElement>('[aria-pressed="true"]')!;
    const inactive = group.querySelector<HTMLButtonElement>('[aria-pressed="false"]')!;
    const activeStyle = getComputedStyle(active);
    const inactiveStyle = getComputedStyle(inactive);
    const trackStyle = getComputedStyle(group);
    // Computed colors may use rgb() or CSS Color 4 color(srgb ...).
    function color(value: string): [number, number, number, number] {
      if (value.startsWith('#')) {
        const hex = value.slice(1);
        const parts = hex.length === 3 ? [...hex].map(ch => ch + ch) : hex.match(/../g)!;
        return [parseInt(parts[0], 16) / 255, parseInt(parts[1], 16) / 255, parseInt(parts[2], 16) / 255, 1];
      }
      if (value.startsWith('color(srgb')) {
        const nums = value.match(/[\d.]+/g)!.map(Number);
        return [nums[0], nums[1], nums[2], nums[3] ?? 1];
      }
      const nums = value.match(/[\d.]+/g)!.map(Number);
      return [nums[0] / 255, nums[1] / 255, nums[2] / 255, nums[3] ?? 1];
    }
    function over(fg: number[], bg: number[]): number[] {
      return [0, 1, 2].map(i => fg[i] * (fg[3] ?? 1) + bg[i] * (1 - (fg[3] ?? 1)));
    }
    function light(rgb: number[]): number {
      const c = rgb.map(x => x <= 0.04045 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4);
      return c[0] * 0.2126 + c[1] * 0.7152 + c[2] * 0.0722;
    }
    function contrast(a: number[], b: number[]): number {
      const values = [light(a), light(b)].sort((x, y) => y - x);
      return (values[0] + 0.05) / (values[1] + 0.05);
    }
    const surface = color(activeStyle.backgroundColor);
    const track = over(color(trackStyle.backgroundColor), surface);
    const thumb = over(color(activeStyle.backgroundColor), track);
    return {
      optionHeight: active.getBoundingClientRect().height,
      controlHeight: group.getBoundingClientRect().height,
      paddingLeft: parseFloat(activeStyle.paddingLeft),
      paddingRight: parseFloat(activeStyle.paddingRight),
      activeContrast: contrast(color(activeStyle.color), thumb),
      inactiveContrast: contrast(color(inactiveStyle.color), track),
      overflow: group.getBoundingClientRect().right > innerWidth,
    };
  });
  // Text and thumb colors transition for 120 ms when the OS theme flips.
  await expect.poll(async () => (await readMetrics()).activeContrast, { timeout: 5_000 })
    .toBeGreaterThanOrEqual(4.5);
  const metrics = await readMetrics();
  expect(metrics.optionHeight).toBeGreaterThanOrEqual(24);
  expect(metrics.controlHeight).toBeGreaterThanOrEqual(28);
  expect(metrics.paddingLeft).toBe(12);
  expect(metrics.paddingRight).toBe(12);
  expect(metrics.activeContrast).toBeGreaterThanOrEqual(4.5);
  expect(metrics.inactiveContrast).toBeGreaterThanOrEqual(4.5);
  expect(metrics.overflow).toBe(false);
}
