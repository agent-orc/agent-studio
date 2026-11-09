import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { getCliUsage } from '../helpers/quota';

/** CLI usage remains available through Workspace Settings. */

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.CLI_USAGE_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'cli-usage');
})();

test.beforeAll(() => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
});

test.describe('CLI usage in settings home', () => {
  test('backend reports the CLI usage section types', async () => {
    const report = await getCliUsage();
    const types = report.sections.map(s => s.cliType).sort();
    expect(types).toEqual(expect.arrayContaining(['claude', 'codex', 'gemini']));
    for (const section of report.sections) {
      expect(section.projects).toBeInstanceOf(Array);
    }
  });

  test('Settings opens CLI Management', async ({ page }) => {
    await page.goto('/');

    // Workspace settings contains the CLI Management section.
    await page.getByTestId('studio-titlebar-settings').click();
    await page.getByTestId('workspace-settings-rail-caps').click();

    const overlay = page.getByTestId('cli-admin-overlay');
    await expect(overlay).toBeVisible();
    await expect(page.getByTestId('cli-admin-panel')).toBeVisible();

    // Evidence of the CLI-Management hub landing (catalog + caps + contracts).
    await overlay.screenshot({ path: path.join(SCREENSHOT_DIR, '01-cli-management-top.png') });

  });


});
