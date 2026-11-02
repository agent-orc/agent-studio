import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { getCliUsage } from '../helpers/quota';

/** CLI usage remains available to projects and the status-bar opens CLI Management. */

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.CLI_USAGE_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'cli-usage');
})();

test.beforeAll(() => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
});

test.describe('CLI usage hub (status-bar → settings home)', () => {
  test('backend reports the CLI usage section types', async () => {
    const report = await getCliUsage();
    const types = report.sections.map(s => s.cliType).sort();
    expect(types).toEqual(expect.arrayContaining(['claude', 'codex', 'gemini']));
    for (const section of report.sections) {
      expect(section.projects).toBeInstanceOf(Array);
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

  });

  test('the status-bar Usage button reflects the home open state', async ({ page }) => {
    await page.goto('/');

    const usageBtn = page.getByTestId('status-bar-usage');
    await expect(usageBtn).toHaveAttribute('aria-pressed', 'false');

    await usageBtn.click();
    await expect(page.getByTestId('cli-admin-overlay')).toBeVisible();
    // The trigger reflects the home being open (no parallel modal of its own).
    await expect(usageBtn).toHaveAttribute('aria-pressed', 'true');

    // The home is a full-screen modal; it closes via its own close control,
    // after which the Usage trigger goes inactive again — no orphaned overlay.
    await page.getByTestId('workspace-settings-close').click();
    await expect(page.getByTestId('cli-admin-overlay')).not.toBeVisible();
    await expect(page.getByTestId('workspace-settings-overlay')).not.toBeVisible();
    await expect(usageBtn).toHaveAttribute('aria-pressed', 'false');
  });
});
