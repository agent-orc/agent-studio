import { test, expect, type Locator, type Page } from '@playwright/test';
import http from 'node:http';
import { AddressInfo } from 'node:net';
import path from 'node:path';
import fs from 'node:fs';

/**
 * HUC-S3 usage detail popover and sheet (docs/header-usage-cockpit/index.html).
 *
 * Mounts the REAL chips and `app-usage-detail-surface` through the
 * backend-free `usage-chips-mockup` app (`?view=detail`) with a pinned
 * synthetic snapshot. Proves Enter/Space, Escape, focus return across a
 * breakpoint change, desktop focus exit, phone focus trap and background
 * inertness, reconciliation, model grouping and ledger links, and writes
 * screenshots in both themes.
 *
 * Build the bundle first:  npm run build:mockup:usage
 * Screenshots land in JOB_RESULTS_DIR/usage-detail when set, otherwise in
 * test-results/usage-detail. The report identifies the synthetic fixture.
 */

const DIST_DIR = path.resolve(__dirname, '..', '..', 'dist', 'usage-chips-mockup', 'browser');
const RESULTS_DIR = path.join(
  process.env.JOB_RESULTS_DIR?.trim() || path.resolve(__dirname, '..', '..', 'test-results'),
  'usage-detail',
);

const MIME: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.ico': 'image/x-icon',
  '.svg': 'image/svg+xml',
  '.woff2': 'font/woff2',
};

const DESKTOP = { width: 1280, height: 900 };
const PHONE = { width: 390, height: 844 };

let server: http.Server;
let baseUrl: string;

test.beforeAll(async () => {
  if (!fs.existsSync(path.join(DIST_DIR, 'index.html'))) {
    throw new Error(`Missing build at ${DIST_DIR}. Run "npm run build:mockup:usage" before this spec.`);
  }
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  server = http.createServer((req, res) => {
    const urlPath = decodeURIComponent((req.url ?? '/').split('?')[0]);
    let filePath = path.join(DIST_DIR, urlPath === '/' ? 'index.html' : urlPath);
    if (!fs.existsSync(filePath) && !path.extname(filePath)) filePath = path.join(DIST_DIR, 'index.html');
    if (!fs.existsSync(filePath) || fs.statSync(filePath).isDirectory()) {
      res.statusCode = 404;
      res.end('not found');
      return;
    }
    res.setHeader('Content-Type', MIME[path.extname(filePath)] ?? 'application/octet-stream');
    fs.createReadStream(filePath).pipe(res);
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  baseUrl = `http://127.0.0.1:${(server.address() as AddressInfo).port}/`;
});

test.afterAll(async () => {
  await new Promise<void>((resolve) => server.close(() => resolve()));
});

async function open(page: Page, theme: 'light' | 'dark', size = DESKTOP): Promise<string[]> {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()); });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.setViewportSize(size);
  await page.goto(`${baseUrl}?view=detail&theme=${theme}`);
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
  await expect(page.locator('[data-testid="usage-cost-chip"]:visible')).toHaveCount(1);
  await page.evaluate(() => document.fonts.ready);
  return errors;
}

const surface = (page: Page) => page.getByTestId('usage-detail-surface');
const desktop = (page: Page) => page.getByTestId('harness-desktop');
const phone = (page: Page) => page.getByTestId('harness-phone');

async function isModal(dialog: Locator): Promise<boolean> {
  return dialog.evaluate((el) => el.matches(':modal'));
}

function money(text: string): number {
  return Math.round(Number(text.replace(/[$,]/g, '')) * 100);
}

for (const theme of ['dark', 'light'] as const) {
  test.describe(`usage detail, ${theme} theme`, () => {
    test('desktop: Enter opens a nonmodal popover at the CLI; Escape returns focus', async ({ page }) => {
      const errors = await open(page, theme);
      const codex = desktop(page).getByTestId('usage-cli-chip-codex');
      await codex.focus();
      await page.keyboard.press('Enter');

      const dialog = surface(page);
      await expect(dialog).toBeVisible();
      await expect(dialog).toHaveAttribute('data-mode', 'popover');
      expect(await isModal(dialog)).toBe(false);
      await expect(codex).toHaveAttribute('aria-expanded', 'true');
      await expect(codex).toHaveAttribute('aria-controls', 'usage-detail');
      await expect(dialog.getByRole('heading', { level: 3, name: /^Codex/ })).toBeFocused();
      await expect(dialog.getByRole('button', { name: 'Close usage detail' })).toBeVisible();

      // Anchored under the trigger.
      const chipBox = (await codex.boundingBox())!;
      const dialogBox = (await dialog.boundingBox())!;
      expect(dialogBox.y).toBeGreaterThanOrEqual(chipBox.y + chipBox.height);
      expect(dialogBox.y).toBeLessThan(chipBox.y + chipBox.height + 12);

      // Both quota windows with local and UTC reset instants.
      const windows = dialog.getByTestId('usage-detail-windows-codex');
      await expect(windows.locator('tbody tr')).toHaveCount(2);
      await expect(windows).toContainText('Weekly');
      await expect(windows).toContainText('15% used');
      await expect(windows).toContainText('32% used');
      await expect(windows.getByTestId('usage-detail-reset-local').first()).toHaveText('Tue 29 Sept, 09:00');
      await expect(windows.getByTestId('usage-detail-reset-utc').first()).toHaveText('Tue 29 Sept, 07:00 UTC');
      await expect(windows).toContainText('Europe/Berlin');

      // Every other section stays reachable from this one chip action.
      await expect(dialog.getByTestId('usage-detail-cli-claude')).toBeAttached();
      await expect(dialog.getByTestId('usage-detail-cost')).toBeAttached();
      await expect(dialog.getByTestId('usage-detail-slots')).toBeAttached();

      await page.screenshot({ path: path.join(RESULTS_DIR, `usage-detail-desktop-cli--${theme}.png`) });

      await page.keyboard.press('Escape');
      await expect(dialog).toBeHidden();
      await expect(codex).toBeFocused();
      await expect(codex).toHaveAttribute('aria-expanded', 'false');
      expect(errors).toEqual([]);
    });

    test('desktop: Space opens cost; rows reconcile and Tab leaves the popover', async ({ page }) => {
      await open(page, theme);
      const cost = desktop(page).getByTestId('usage-cost-chip');
      await cost.focus();
      await page.keyboard.press('Space');

      const dialog = surface(page);
      await expect(dialog).toBeVisible();
      await expect(dialog.getByRole('heading', { level: 3, name: /^Cost/ })).toBeFocused();

      // Project rows, unattributed included, plus the rounding row sum to the total.
      const projects = dialog.getByTestId('usage-detail-projects');
      const rows = projects.getByTestId('usage-detail-project-row');
      await expect(rows).toHaveCount(3);
      await expect(rows.last()).toHaveAttribute('data-project', 'unattributed');
      await expect(rows.last()).toContainText('Unattributed');
      const rounding = projects.getByTestId('usage-detail-rounding-row');
      await expect(rounding).toBeVisible();
      for (const col of [1, 2]) {
        const cells = await projects.locator(`tbody tr td:nth-of-type(${col})`).allTextContents();
        const total = await projects.locator(`tfoot td:nth-of-type(${col})`).textContent();
        expect(cells.reduce((sum, t) => sum + money(t), 0)).toBe(money(total!));
      }
      await expect(dialog.getByTestId('usage-detail-today')).toHaveText('$12.48');
      await expect(dialog.getByTestId('usage-detail-week')).toHaveText('$68.20');
      await expect(dialog.getByTestId('usage-detail-day-utc')).toHaveText('Thu 24 Sept, 22:00 UTC to Fri 25 Sept, 22:00 UTC');

      // Live runs: included-in-totals labels, Pending instead of $0.00.
      const live = dialog.getByTestId('usage-detail-live-run');
      await expect(live).toHaveCount(5);
      await expect(dialog.getByText('Live spend is part of the totals above, not added to them.')).toBeVisible();
      await expect(dialog.locator('[data-run-id="run-1"][data-testid="usage-detail-live-run"]')).toContainText("Included in today's total");
      await expect(dialog.locator('[data-run-id="run-3"][data-testid="usage-detail-live-run"]')).toContainText('Not yet in totals');
      await expect(dialog.locator('[data-run-id="run-3"][data-testid="usage-detail-live-run"]')).toContainText('Pending');
      await expect(dialog.getByTestId('usage-detail-live-runs')).not.toContainText('$0.00');

      // Slot pools stay separate.
      await expect(dialog.getByTestId('usage-detail-slots').locator('li')).toHaveCount(3);

      // Ledger links preserve workspace, local-calendar range and zone.
      const today = await dialog.getByTestId('usage-detail-ledger-today').getAttribute('href');
      expect(today).toBe('#/workspace/settings/tokens&ledger-workspace=studio-main&ledger-range=today'
        + '&ledger-from=2026-09-24T22%3A00%3A00Z&ledger-to=2026-09-25T22%3A00%3A00Z&ledger-zone=Europe%2FBerlin');
      const week = await dialog.getByTestId('usage-detail-ledger-week').getAttribute('href');
      expect(week).toContain('ledger-from=2026-09-20T22%3A00%3A00Z&ledger-to=2026-09-27T22%3A00%3A00Z');
      const unattributed = await rows.last().getByRole('link').getAttribute('href');
      expect(unattributed).toContain('ledger-project=unattributed');
      await expect(dialog.getByTestId('usage-detail-manage-clis').first()).toHaveAttribute('href', '#/workspace/settings/caps');

      await dialog.getByTestId('usage-detail-cost').scrollIntoViewIfNeeded();
      await page.screenshot({ path: path.join(RESULTS_DIR, `usage-detail-desktop-cost--${theme}.png`) });

      // Focus exit: Tab past the last control closes and continues after the trigger.
      await dialog.getByTestId('usage-detail-ledger-week').focus();
      await page.keyboard.press('Tab');
      await expect(dialog).toBeHidden();
      await expect(page.getByTestId('harness-after')).toBeFocused();

      // Shift+Tab out of the first control also closes without trapping.
      await cost.focus();
      await page.keyboard.press('Enter');
      await expect(dialog).toBeVisible();
      await dialog.getByRole('button', { name: 'Close usage detail' }).focus();
      await page.keyboard.press('Shift+Tab');
      await expect(dialog).toBeHidden();
    });

    test('desktop: models grouped per run within the CLI, unknown stays explicit', async ({ page }) => {
      await open(page, theme);
      await desktop(page).getByTestId('usage-cli-chip-codex').click();
      const codexModels = surface(page).getByTestId('usage-detail-models-codex');
      const groups = codexModels.getByTestId('usage-detail-model-group');
      await expect(groups).toHaveCount(3);
      await expect(groups.nth(0)).toContainText('gpt-5.4-mini');
      await expect(groups.nth(0)).toContainText('medium');
      await expect(groups.nth(0).getByTestId('usage-detail-route-diff')).toHaveText('Configured codex / gpt-5.5 / high');
      await expect(groups.nth(1).getByTestId('usage-detail-reasoning')).toHaveText('high');
      await expect(groups.nth(1)).toContainText('1 run');
      // run-3 has no reported reasoning: Unknown, not the configured `high`.
      await expect(groups.nth(2).getByTestId('usage-detail-reasoning')).toHaveText('Unknown');
      await expect(groups.nth(2).getByTestId('usage-detail-route-diff')).toHaveText('Configured codex / gpt-5.5 / high');

      const claudeModels = surface(page).getByTestId('usage-detail-models-claude');
      await expect(claudeModels.getByTestId('usage-detail-model')).toHaveText(['claude-opus-5-5', 'Unknown']);
      // Claude runs never leak into Codex.
      await expect(codexModels).not.toContainText('AGT-2955');
    });

    test('phone: modal sheet traps focus, background is inert, Escape returns focus', async ({ page }) => {
      const errors = await open(page, theme, PHONE);
      await expect(desktop(page)).toBeHidden();
      const codex = phone(page).getByTestId('usage-cli-chip-codex');
      await codex.focus();
      await page.keyboard.press('Space');

      const dialog = surface(page);
      await expect(dialog).toBeVisible();
      await expect(dialog).toHaveAttribute('data-mode', 'sheet');
      expect(await isModal(dialog)).toBe(true);
      const sheetBox = (await dialog.boundingBox())!;
      expect(Math.round(sheetBox.y + sheetBox.height)).toBe(PHONE.height);
      const close = dialog.getByRole('button', { name: 'Close usage detail' });
      const closeBox = (await close.boundingBox())!;
      expect(closeBox.width).toBeGreaterThanOrEqual(44);
      expect(closeBox.height).toBeGreaterThanOrEqual(44);

      // Background inert: neither keyboard focus nor programmatic focus lands outside.
      const after = page.getByTestId('harness-after');
      expect(await after.evaluate((el) => { (el as HTMLElement).focus(); return document.activeElement === el; })).toBe(false);
      for (let i = 0; i < 60; i++) {
        await page.keyboard.press(i % 7 === 6 ? 'Shift+Tab' : 'Tab');
        expect(await dialog.evaluate((el) => el.contains(document.activeElement))).toBe(true);
      }

      await dialog.getByRole('heading', { level: 3, name: /^Codex/ }).focus();
      await page.screenshot({ path: path.join(RESULTS_DIR, `usage-detail-phone-sheet--${theme}.png`) });

      await page.keyboard.press('Escape');
      await expect(dialog).toBeHidden();
      await expect(codex).toBeFocused();

      // The explicit Close button closes too.
      await page.keyboard.press('Enter');
      await expect(dialog).toBeVisible();
      await close.click();
      await expect(dialog).toBeHidden();
      await expect(codex).toBeFocused();
      expect(errors).toEqual([]);
    });

    test('breakpoint change: the surface switches mode and focus returns to the visible trigger', async ({ page }) => {
      await open(page, theme);
      // Claude has no chip on phone: focus falls back to the visible primary trigger.
      await desktop(page).getByTestId('usage-cli-chip-claude').focus();
      await page.keyboard.press('Enter');
      const dialog = surface(page);
      await expect(dialog).toHaveAttribute('data-mode', 'popover');
      await page.setViewportSize(PHONE);
      await expect(dialog).toHaveAttribute('data-mode', 'sheet');
      expect(await isModal(dialog)).toBe(true);
      await expect(dialog.getByRole('heading', { level: 3, name: /^Claude/ })).toBeFocused();
      await page.keyboard.press('Escape');
      await expect(dialog).toBeHidden();
      await expect(phone(page).getByTestId('usage-cli-chip-codex')).toBeFocused();

      // Cost opened on phone, closed on desktop: the desktop cost chip gets focus.
      await phone(page).getByTestId('usage-cost-chip').focus();
      await page.keyboard.press('Enter');
      await expect(dialog).toHaveAttribute('data-mode', 'sheet');
      await page.setViewportSize(DESKTOP);
      await expect(dialog).toHaveAttribute('data-mode', 'popover');
      expect(await isModal(dialog)).toBe(false);
      await page.keyboard.press('Escape');
      await expect(dialog).toBeHidden();
      await expect(desktop(page).getByTestId('usage-cost-chip')).toBeFocused();
    });
  });
}
