import { test, expect } from '@playwright/test';

/**
 * Evidence screenshot for the status-bar default-CLI + default-model
 * homogenisation. The two separate pickers collapsed into the single
 * shared `<app-cli-model-selector>` chip; this spec opens it and shows
 * the CLI pills + model pills inside one popover. See
 * `docs/quality/frontend/audits/cli-model-selector-audit.md`.
 */
test.describe('Status bar default picker (unified chip)', () => {
  test('opens the unified CLI+model defaults picker', async ({ page }) => {
    await page.route('**/api/cli/*/models*', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          models: [
            { id: 'claude-sonnet-4-6', label: 'Sonnet 4.6', isDefault: true, multiplier: 1 },
            { id: 'claude-opus-4-7', label: 'Opus 4.7', isDefault: false, multiplier: 5 },
            { id: 'claude-haiku-4-5', label: 'Haiku 4.5', isDefault: false, multiplier: 0.2 },
          ],
          defaultModel: 'claude-sonnet-4-6',
          source: 'mock',
        }),
      });
    });

    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);

    const trigger = page.getByTestId('studio-titlebar-defaults');
    await expect(trigger).toBeVisible();
    await trigger.click();

    const picker = page.getByTestId('studio-titlebar-defaults-picker');
    await expect(picker).toBeVisible();
    await expect(picker).toHaveAttribute('role', 'dialog');

    // Both sections present in one popover.
    await expect(picker.getByTestId('studio-titlebar-defaults-picker-cli-pills')).toBeVisible();
    await expect(picker.getByTestId('studio-titlebar-defaults-picker-model-pills')).toBeVisible();

    await page.screenshot({
      path: 'test-results/studio-titlebar-defaults-picker-open.png',
      fullPage: false,
    });

    // Esc closes the picker.
    await page.keyboard.press('Escape');
    await expect(picker).not.toBeVisible();
  });
});
