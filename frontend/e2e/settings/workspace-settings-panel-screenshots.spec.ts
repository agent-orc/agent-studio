import { test, expect } from '@playwright/test';

/**
 * F47 evidence capture — opens the Settings panel and screenshots the
 * Workspaces section in light + dark themes. Outputs land in the F47
 * job folder so they show up alongside the rest of the review evidence.
 */
const JOB_RESULTS = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard\\3-progress\\f47-cleanup-watchpath-deprecation-settings-panel-adr\\results';

test('captures light-theme screenshot of Settings Workspaces section', async ({ page }) => {
  await page.addInitScript(() => {
    try { localStorage.setItem('atp.studio.theme', 'light'); } catch { /* ignore */ }
  });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(400);
  await page.getByTestId('studio-titlebar-settings').click();
  await page.getByTestId('workspace-settings-rail-workspaces').click({ timeout: 10_000 });
  await expect(page.getByTestId('settings-workspaces')).toBeVisible({ timeout: 10_000 });
  await page.waitForFunction(
    () => document.querySelector('[data-testid="settings-workspaces"]')?.getAttribute('aria-busy') !== 'true',
    null,
    { timeout: 5_000 },
  ).catch(() => { /* capture anyway */ });
  await expect(page.getByTestId('settings-workspaces-list')).toBeVisible();
  await page.getByTestId('workspace-management').screenshot({
    path: `${JOB_RESULTS}\\f47-settings-workspaces-light.png`,
  });
});

test('captures dark-theme screenshot of Settings Workspaces section', async ({ page }) => {
  await page.addInitScript(() => {
    try { localStorage.setItem('atp.studio.theme', 'dark'); } catch { /* ignore */ }
  });
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(400);
  await page.getByTestId('studio-titlebar-settings').click();
  await page.getByTestId('workspace-settings-rail-workspaces').click({ timeout: 10_000 });
  await expect(page.getByTestId('settings-workspaces')).toBeVisible({ timeout: 10_000 });
  await page.waitForFunction(
    () => document.querySelector('[data-testid="settings-workspaces"]')?.getAttribute('aria-busy') !== 'true',
    null,
    { timeout: 5_000 },
  ).catch(() => { /* capture anyway */ });
  await expect(page.getByTestId('settings-workspaces-list')).toBeVisible();
  await page.getByTestId('workspace-management').screenshot({
    path: `${JOB_RESULTS}\\f47-settings-workspaces-dark.png`,
  });
});
