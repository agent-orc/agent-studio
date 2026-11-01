import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { getCliUsage } from '../helpers/quota';

/**
 * CLI-usage entry path after the loose `<app-cli-usage-sheet>` sidesheet
 * was retired (its quota glance + per-CLI session inventory folded into
 * the Workspace-settings home's CLI-Management section). The status-bar
 * "Usage" button now opens that single hub instead of a parallel modal.
 *
 * This spec verifies:
 *  - the backend `/api/cli/usage` report remains available for project settings
 *  - clicking `status-bar-usage` opens the home at the CLI-Management
 *    ("caps") section (`cli-admin-overlay`)
 */

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.CLI_USAGE_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'cli-usage');
})();

test.beforeAll(() => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
});

test.describe('CLI usage hub (status-bar → settings home)', () => {
  test('backend returns the CLI usage report for project settings', async () => {
    const report = await getCliUsage();
    expect(Array.isArray(report.sections)).toBe(true);
    expect(report.sections.map(section => section.cliType)).toEqual(expect.arrayContaining(['claude', 'codex']));
    for (const section of report.sections) {
      expect(typeof section.available).toBe('boolean');
      expect(Array.isArray(section.projects)).toBe(true);
    }
  });

  test('status-bar Usage opens CLI Management', async ({ page }) => {
    await page.goto('/');

    // The Usage button is the only CLI-usage entry point now; it opens the
    // global Workspace-settings home at the "caps" (CLI Management) section.
    await page.getByTestId('status-bar-usage').click();

    const overlay = page.getByTestId('cli-admin-overlay');
    await expect(overlay).toBeVisible();
    await expect(page.getByTestId('cli-admin-panel')).toBeVisible();

    // Evidence of the CLI-Management hub landing (catalog + caps + contracts).
    await overlay.screenshot({ path: path.join(SCREENSHOT_DIR, '01-cli-management-top.png') });

    await expect(page.getByTestId('workspace-settings-rail-caps')).toHaveAttribute('aria-current', 'page');
  });

  test('the status-bar Usage button reflects the home open state', async ({ page }) => {
    await page.goto('/');

    const usageBtn = page.getByTestId('status-bar-usage');
    await expect(usageBtn).toHaveAttribute('aria-pressed', 'false');

    await usageBtn.click();
    await expect(page.getByTestId('cli-admin-overlay')).toBeVisible();
    // The trigger reflects the home being open (no parallel modal of its own).
    await expect(usageBtn).toHaveAttribute('aria-pressed', 'true');

    // Modal layout uses a close button; Studio layout toggles the editor tab.
    const close = page.getByTestId('workspace-settings-close');
    if (await close.count()) await close.click();
    else await usageBtn.click();
    await expect(page.getByTestId('cli-admin-overlay')).not.toBeVisible();
    await expect(page.getByTestId('workspace-settings-overlay')).not.toBeVisible();
    await expect(usageBtn).toHaveAttribute('aria-pressed', 'false');
  });
});
