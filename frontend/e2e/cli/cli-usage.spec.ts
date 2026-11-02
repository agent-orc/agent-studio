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
 *  - the backend `/api/cli/usage` report is sane (Copilot / Claude / Codex)
 *  - Workspace Settings opens the CLI-Management
 *    ("caps") section (`cli-admin-overlay`)
 *  - the encapsulated `CLI sessions` settings page (split out of the
 *    CLI-Management hub in AGT-2101) renders the `cli-sessions-panel` with
 *    the available CLI labels and no error
 *
 * The session list is the preserved feature — it must still render, now from
 * its own `cli-sessions` rail page reachable from the same Settings home the
 * Usage trigger opens.
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
  test('backend reports all three CLIs as available', async () => {
    const report = await getCliUsage();
    const types = report.sections.map(s => s.cliType).sort();
    expect(types).toEqual(expect.arrayContaining(['claude', 'codex', 'copilot']));
    for (const t of ['claude', 'codex', 'copilot']) {
      const sec = report.sections.find(s => s.cliType === t)!;
      expect(sec.available, `${t} should be available`).toBe(true);
      expect(sec.error, `${t} should have no error`).toBeFalsy();
      expect(sec.version, `${t} should report a version string`).toBeTruthy();
    }
  });

  test('Settings opens CLI Management; the CLI sessions page renders the inventory', async ({ page }) => {
    await page.goto('/');

    // Workspace settings contains the CLI Management section.
    await page.getByTestId('studio-titlebar-settings').click();
    await page.getByTestId('workspace-settings-rail-caps').click();

    const overlay = page.getByTestId('cli-admin-overlay');
    await expect(overlay).toBeVisible();
    await expect(page.getByTestId('cli-admin-panel')).toBeVisible();

    // Evidence of the CLI-Management hub landing (catalog + caps + contracts).
    await overlay.screenshot({ path: path.join(SCREENSHOT_DIR, '01-cli-management-top.png') });

    // The per-CLI per-project session inventory now lives on its own
    // encapsulated "CLI sessions" rail page (AGT-2101).
    await page.getByTestId('workspace-settings-rail-cli-sessions').click();
    const sessionsSection = page.getByTestId('workspace-cli-sessions');
    await expect(sessionsSection).toBeVisible();
    const sessions = page.getByTestId('cli-sessions-panel');
    await expect(sessions).toBeVisible();

    // The panel lazy-loads `/api/cli/usage`; wait out the loading state, then
    // the revamped tool renders its search + filter toolbar and a summary.
    await expect(sessions.getByTestId('cli-sessions-toolbar')).toBeVisible({ timeout: 15_000 });
    await expect(sessions.getByTestId('cli-sessions-summary')).toBeVisible();
    await expect(sessions.getByTestId('cli-sessions-search')).toBeVisible();
    await expect(sessions.getByTestId('cli-filter-all')).toBeVisible();

    // The virtualised list renders at least one session row (real machine has
    // thousands of transcripts on disk).
    await expect(sessions.getByTestId('cli-session-row').first()).toBeVisible();

    // No error surfaced from the session load.
    await expect(sessions.locator('.sessions__error')).toHaveCount(0);

    // Scroll the preserved session inventory's header into view and capture
    // the viewport for visual evidence that the feature still renders from
    // its own encapsulated page. (A full-element shot would be tens of
    // thousands of px tall on a machine with many real sessions.)
    await sessionsSection.scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-cli-sessions-panel.png') });
  });


});
