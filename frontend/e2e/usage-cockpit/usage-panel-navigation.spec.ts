import { test, expect, type Page } from '@playwright/test';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';
import { SNAPSHOT, FIXTURE_NOW } from '../../src/mockups/usage-chips/app/usage-chips.fixtures';

// AGT-3794-F14: the usage panel opened from the cost chip names its content,
// closes after following one of its own links, and the token-usage rail marks
// the page actually shown. Times carry an explicit zone.
const shots = process.env.JOB_RESULTS_DIR ?? 'test-results';

async function installRoutes(page: Page) {
  await page.route('**/api/**', route => route.fulfill({ json: [] }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ json: {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  } }));
  await page.route('**/api/v1/studio/board**', route => route.fulfill({ json: {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], review: [], autoReview: [], humanReview: [], escalated: [],
    completed: [], archive: [],
  } }));
  await page.route('**/api/v1/studio/runner/status', route => route.fulfill({ json: { projects: {} } }));
  await page.route('**/api/usage/cockpit**', route => route.fulfill({ json: structuredClone(SNAPSHOT) }));
}

for (const theme of ['light', 'dark'] as const) {
  test(`usage panel title, close on navigation and rail destination in ${theme}`, async ({ page }) => {
    test.setTimeout(120_000);
    await page.clock.setFixedTime(FIXTURE_NOW);
    await installRoutes(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await dismissDevErrorDialog(page);
    await setTheme(page, theme);

    const chip = page.getByTestId('footer-usage-claude');
    await expect(chip).toBeVisible({ timeout: 60_000 });
    await page.getByTestId('footer-usage-cost').click();
    const surface = page.getByTestId('usage-detail-surface');
    await expect(surface).toBeVisible();
    // The panel lists every CLI, so its title is not "Claude usage".
    await expect(surface.locator('.usage-surface__title')).toHaveText('Cost and usage across all projects');
    await expect(surface).not.toContainText('Claude usage');
    // Every instant names its zone.
    await expect(surface.getByTestId('usage-detail-cli-claude')).toContainText(/Updated .*\d{2}:\d{2} \S+/);
    await expect(surface).not.toContainText(/\d{1,2}\/\d{1,2}\/\d{4}/);
    await surface.screenshot({ path: join(shots, `usage-panel-${theme}.png`) });

    await surface.getByTestId('usage-detail-cli-account-claude').click();
    await expect(surface).toBeHidden();
    await expect(page).toHaveURL(/#\/workspace\/settings\/tokens\/claude$/);
    await expect(page.getByTestId('token-usage-nav-claude')).toHaveAttribute('aria-current', 'page');

    // Back on the board, follow the ledger link of the cost section.
    await page.goto('/');
    await dismissDevErrorDialog(page);
    await expect(chip).toBeVisible({ timeout: 60_000 });
    await page.getByTestId('footer-usage-cost').click();
    await expect(surface).toBeVisible();
    await surface.getByTestId('usage-detail-ledger-week').click();
    await expect(surface).toBeHidden();
    await expect(page).toHaveURL(/#\/workspace\/settings\/tokens&/);
    await expect(page.getByTestId('token-usage-nav-workspace')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('token-usage-nav-claude')).not.toHaveAttribute('aria-current', 'page');
    await page.screenshot({ path: join(shots, `usage-ledger-destination-${theme}.png`) });
  });
}

test('quota reset table names a UTC workspace once', async ({ page }) => {
  test.setTimeout(90_000);
  await installRoutes(page);
  await page.route('**/api/usage/cockpit**', route => route.fulfill({ json: {
    ...structuredClone(SNAPSHOT), timeZone: 'UTC',
  } }));
  await page.goto('/');
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('footer-usage-claude')).toBeVisible({ timeout: 30_000 });
  await dismissDevErrorDialog(page);
  await page.getByTestId('footer-usage-cost').click();
  const table = page.getByTestId('usage-detail-windows-claude');
  await expect(table.locator('thead th')).toHaveText(['Window', 'Used', 'Left', 'Resets (UTC)']);
  await expect(table.getByTestId('usage-detail-reset-utc')).toHaveCount(0);
  await expect(table.getByTestId('usage-detail-reset-local').first()).not.toContainText('UTC');
});
