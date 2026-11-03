import { test, expect } from '@playwright/test';

/**
 * Tasks-activity-panel cleanup — asserts the redundant TASKS activity-bar
 * button and its panel content (LANES + OPEN TASKS) are gone. Lane
 * navigation lives on the Board; open tabs live in the Explorer.
 *
 * Hard rules from the cleanup decision (2026-05-28):
 *   - no `studio-ab-tasks` button on the activity bar
 *   - no `.studio-tasks` / `.studio-lane-summary` markup anywhere
 *   - Explorer still owns the "Open tabs" section
 */
test.describe('Tasks activity panel — removed', () => {
  test.beforeEach(async ({ page }) => {
    await page.route('**/update/status', route =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ phase: 'idle', isRunning: false, behindBy: 0 }),
      })
    );

    await page.addInitScript(() => {
      localStorage.setItem('activeProjects', '[]');
    });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(1500);
  });

  test('activity bar no longer renders the Tasks button', async ({ page }) => {
    const activityBar = page.getByTestId('studio-activity-bar');
    await expect(activityBar).toBeVisible();

    // The removed button used to expose data-testid="studio-ab-tasks".
    await expect(page.getByTestId('studio-ab-tasks')).toHaveCount(0);

    // Remaining surfaces are still there so we know we removed only Tasks.
    await expect(page.getByTestId('studio-ab-filters')).toBeVisible();
    await expect(page.getByTestId('studio-titlebar-settings')).toBeVisible();
  });

  test('no LANES or OPEN TASKS sidebar surface lives outside Board/Explorer', async ({ page }) => {
    // The removed `<section class="studio-tasks">` carried the lane
    // summary + duplicate open-tasks list. Neither should exist anywhere.
    await expect(page.locator('.studio-tasks')).toHaveCount(0);
    await expect(page.locator('.studio-lane-summary')).toHaveCount(0);
  });

  test('Explorer still owns the Open tabs section (single source)', async ({ page }) => {
    // Make sure the Explorer panel is the active sidebar surface.
    const explorerBtn = page.getByTestId('studio-ab-explorer');
    await expect(explorerBtn).toBeVisible();
    const sidebar = page.getByTestId('studio-sidebar');
    if (!(await sidebar.isVisible().catch(() => false))) {
      await explorerBtn.click();
    }
    await expect(sidebar).toBeVisible();

    // Open the cross-project board so we have at least one tab — the
    // Explorer's "Open tabs" section is rendered conditionally on tab count.
    const picker = page.getByTestId('studio-project-picker-trigger');
    await expect(picker).toBeVisible({ timeout: 10_000 });
    await picker.click();
    const allItem = page.getByTestId('studio-project-picker-item-__all__');
    await expect(allItem).toBeVisible({ timeout: 5000 });
    await allItem.click();

    // Open-tabs header lives in the Explorer (single source) — not in
    // any removed Tasks panel.
    await expect(page.getByTestId('studio-explorer-open-tabs-head')).toBeAttached();
  });
});
