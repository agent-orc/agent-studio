import { test, expect, type Locator, type Page } from '@playwright/test';
import http from 'node:http';
import { AddressInfo } from 'node:net';
import path from 'node:path';
import fs from 'node:fs';

import { contrastRatio } from '../helpers/contrast';

/**
 * HUC-S5 usage alarm transitions (docs/header-usage-cockpit/index.html,
 * "Chip states").
 *
 * Mounts the REAL usage chips, alarm policy, `UsageAlarmStateService` and
 * polite status region through the backend-free `usage-chips-mockup` app
 * (`?view=alarms`) with pinned synthetic fixtures. Checks the chip-state
 * matrix, tint plus text/shape in both themes, a hidden-provider alarm in a
 * phone-width row, once-per-cycle announcements, and that no request leaves
 * the page (the alarms change visibility only, never routing or model pins).
 * The header integration is HUC-S4 and is not exercised here.
 *
 * Build the bundle first:  npm run build:mockup:usage
 * Screenshots land in JOB_RESULTS_DIR/usage-alarms when set, otherwise in
 * test-results/usage-alarms. Evidence label: `--mocked` (synthetic data,
 * real components and compiled CSS, no backend).
 */

const DIST_DIR = path.resolve(__dirname, '..', '..', 'dist', 'usage-chips-mockup', 'browser');
const RESULTS_DIR = path.join(
  process.env.JOB_RESULTS_DIR?.trim() || path.resolve(__dirname, '..', '..', 'test-results'),
  'usage-alarms',
);

const MIME: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.ico': 'image/x-icon',
  '.svg': 'image/svg+xml',
  '.woff2': 'font/woff2',
};

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

interface Opened { errors: string[]; requests: string[] }

async function open(page: Page, theme: 'light' | 'dark', width = 1024): Promise<Opened> {
  const errors: string[] = [];
  const requests: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()); });
  page.on('request', (request) => requests.push(`${request.method()} ${new URL(request.url()).pathname}`));
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.setViewportSize({ width, height: 900 });
  await page.goto(`${baseUrl}?view=alarms&theme=${theme}`);
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
  await expect(page.getByTestId('alarm-quota').getByRole('button').first()).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  return { errors, requests };
}

const chip = (page: Page, id: string) => page.locator(`[data-case="${id}"] button`);

interface Surface {
  bg: string;
  border: string;
  backgroundImage: string;
  boxShadow: string;
  borderLeft: string;
  borderRight: string;
  text: string;
  mark: string | null;
  clipped: boolean;
}

/** Computed chip surface with every colour resolved to rgb() through a canvas. */
function surface(button: Locator): Promise<Surface> {
  return button.evaluate((el) => {
    const ctx = document.createElement('canvas').getContext('2d', { willReadFrequently: true })!;
    const rgb = (css: string) => {
      ctx.clearRect(0, 0, 1, 1);
      ctx.fillStyle = '#000';
      ctx.fillStyle = css;
      ctx.fillRect(0, 0, 1, 1);
      const [r, g, b, a] = ctx.getImageData(0, 0, 1, 1).data;
      return `rgba(${r}, ${g}, ${b}, ${Math.round((a / 255) * 100) / 100})`;
    };
    const s = getComputedStyle(el);
    const value = el.querySelector('[data-testid$="-weekly"], [data-testid="usage-cost-chip-value"], [data-testid="usage-cli-chip-alarm-label"]');
    const mark = el.querySelector('[data-testid$="alarm-mark"], [data-testid="usage-hidden-alarm"]');
    return {
      bg: rgb(s.backgroundColor),
      border: rgb(s.borderTopColor),
      backgroundImage: s.backgroundImage,
      boxShadow: s.boxShadow,
      borderLeft: `${s.borderLeftWidth} ${s.borderLeftStyle} ${s.borderLeftColor}`,
      borderRight: `${s.borderRightWidth} ${s.borderRightStyle} ${s.borderRightColor}`,
      text: rgb(getComputedStyle(value ?? el).color),
      mark: mark ? rgb(getComputedStyle(mark).color) : null,
      clipped: el.scrollWidth > el.clientWidth + 0.5,
    };
  });
}

async function shot(target: Locator | Page, name: string, theme: string) {
  const file = path.join(RESULTS_DIR, `${name}--${theme}--mocked.png`);
  if ('screenshot' in target && 'goto' in target) await target.screenshot({ path: file, fullPage: true });
  else await (target as Locator).screenshot({ path: file });
}

for (const theme of ['light', 'dark'] as const) {
  test.describe(`@mockup usage alarms (real components), ${theme}`, () => {
    test('chip-state matrix: wording, precedence and screenshots', async ({ page }) => {
      const { errors, requests } = await open(page, theme);

      // Quota warning: 80 is headroom, 84.5 and values above 100 warn.
      await expect(chip(page, 'codex-84')).toHaveAttribute('data-alarm', 'warning');
      await expect(chip(page, 'codex-84')).toHaveAttribute('aria-label',
        /^Codex, weekly 84\.5 percent used, current five-hour window 32 percent used\. Warning: Codex weekly quota 84\.5 percent used, above 80 percent\. Open usage\.$/);
      await expect(chip(page, 'codex-84').getByTestId('usage-cli-chip-alarm-mark')).toBeVisible();
      await expect(chip(page, 'claude-80')).not.toHaveAttribute('data-alarm');
      await expect(chip(page, 'codex-over-100').getByTestId('usage-cli-chip-codex-weekly')).toHaveText('104.3%');
      await expect(chip(page, 'codex-over-100')).toHaveAttribute('aria-label', /Warning: Codex weekly quota 104\.3 percent used, above 80 percent\. Warning: Codex current five-hour window quota 137 percent used, above 80 percent\. Open usage\.$/);
      await shot(page.getByTestId('alarm-quota'), 'alarm-quota-warning', theme);

      // Cost warning: strictly above a configured budget; equality and unset budgets stay quiet.
      await expect(chip(page, 'daily-over')).toHaveAttribute('data-alarm', 'warning');
      await expect(chip(page, 'daily-over')).toHaveAttribute('aria-label', /Warning: Today's cost \$52\.30 USD exceeds the daily budget of \$50\.00 USD\./);
      await expect(chip(page, 'weekly-over')).toHaveAttribute('aria-label', /Warning: This week's cost \$214\.75 USD exceeds the weekly budget of \$200\.00 USD\./);
      await expect(chip(page, 'at-budget')).not.toHaveAttribute('data-alarm');
      await expect(chip(page, 'at-budget')).toHaveAttribute('aria-label', /Within budget\./);
      await expect(chip(page, 'no-budget')).not.toHaveAttribute('data-alarm');
      expect(await chip(page, 'no-budget').getAttribute('aria-label')).not.toMatch(/budget/i);
      await shot(page.getByTestId('alarm-cost'), 'alarm-cost-warning', theme);

      // Provider limited at a low percentage; "Limited" in words on the full chip, the mark on the compact one.
      await expect(chip(page, 'claude-limited')).toHaveAttribute('data-alarm', 'limited');
      await expect(chip(page, 'claude-limited').getByTestId('usage-cli-chip-alarm-label')).toHaveText('Limited');
      await expect(chip(page, 'claude-limited').getByTestId('usage-cli-chip-claude-weekly')).toHaveText('3%');
      await expect(chip(page, 'codex-limited-over')).toHaveAttribute('aria-label',
        /Limited: Codex is limited by the provider: Admission paused\. Warning: Codex weekly quota 92 percent used/);
      await expect(chip(page, 'claude-limited-compact').getByTestId('usage-cli-chip-alarm-label')).toHaveCount(0);
      await expect(chip(page, 'claude-limited-compact').getByTestId('usage-cli-chip-alarm-mark')).toBeVisible();
      await shot(page.getByTestId('alarm-limited'), 'alarm-limited', theme);

      // Stale: a confirmed alarm is kept, a stale sample cannot clear a limit, and a stale high reading is not confirmed.
      await expect(chip(page, 'codex-warning-retained')).toHaveAttribute('data-alarm', 'warning');
      await expect(chip(page, 'codex-warning-retained')).toHaveAttribute('data-state', 'stale');
      await expect(chip(page, 'codex-warning-retained')).toHaveAttribute('aria-label', /Warning: .* Stale: /);
      await expect(chip(page, 'claude-limit-retained')).toHaveAttribute('data-alarm', 'limited');
      await expect(chip(page, 'codex-stale-high')).not.toHaveAttribute('data-alarm');
      await expect(chip(page, 'codex-stale-high')).toHaveAttribute('data-state', 'stale');
      await expect(chip(page, 'stale-budget')).toHaveAttribute('aria-label', /Budget: not confirmed while the ledger is not current\./);
      await shot(page.getByTestId('alarm-stale'), 'alarm-stale', theme);

      // Partial: an overrun on a lower bound is confirmed, headroom is never claimed.
      await expect(chip(page, 'partial-under')).not.toHaveAttribute('data-alarm');
      await expect(chip(page, 'partial-under')).toHaveAttribute('aria-label', /Budget: totals are partial, an overrun may not be detected yet\./);
      await expect(chip(page, 'partial-over')).toHaveAttribute('data-alarm', 'warning');
      await shot(page.getByTestId('alarm-partial'), 'alarm-partial', theme);

      // Unknown: no zero, no safety claim.
      await expect(chip(page, 'gemini-unknown')).toHaveAttribute('data-state', 'unknown');
      await expect(chip(page, 'gemini-unknown')).not.toHaveAttribute('data-alarm');
      await expect(chip(page, 'cost-unknown').getByTestId('usage-cost-chip-value')).toHaveText('N/A');
      await expect(chip(page, 'cost-unknown')).toHaveAttribute('aria-label', /Budget: not confirmed/);
      await shot(page.getByTestId('alarm-unknown'), 'alarm-unknown', theme);

      await shot(page, 'alarm-matrix', theme);
      expect(errors).toEqual([]);
      expect(requests.filter((r) => !r.startsWith('GET ') || r.includes('/api/'))).toEqual([]);
    });

    test('semantic tint travels with text or shape; headroom stays neutral and flat', async ({ page }) => {
      await open(page, theme);
      const neutral = await surface(chip(page, 'claude-80'));
      const measured: Record<string, unknown> = { theme };
      for (const id of ['codex-84', 'daily-over', 'claude-limited', 'claude-limited-compact', 'codex-warning-retained']) {
        const s = await surface(chip(page, id));
        expect(s.backgroundImage, id).toBe('none');
        expect(s.boxShadow, id).toBe('none');
        expect(s.borderLeft, id).toBe(s.borderRight);
        expect(s.clipped, id).toBe(false);
        expect(s.bg, `${id} is tinted`).not.toBe(neutral.bg);
        const textContrast = contrastRatio(s.text, s.bg);
        expect(textContrast, `${id} text`).toBeGreaterThanOrEqual(4.5);
        const shape = await chip(page, id).locator('[data-testid="usage-cli-chip-alarm-label"], [data-testid$="alarm-mark"]').count();
        expect(shape, `${id} carries a word or mark`).toBe(1);
        measured[id] = { text: Number(textContrast.toFixed(2)), mark: s.mark ? Number(contrastRatio(s.mark, s.bg).toFixed(2)) : null };
        if (s.mark) expect(contrastRatio(s.mark, s.bg), `${id} mark`).toBeGreaterThanOrEqual(3);
      }
      // Ordinary headroom, exact budget and unset budget share the neutral surface.
      for (const id of ['at-budget', 'no-budget', 'partial-under', 'codex-stale-high']) {
        expect((await surface(chip(page, id))).bg, id).toBe(neutral.bg);
      }
      const limited = await surface(chip(page, 'claude-limited'));
      const warning = await surface(chip(page, 'codex-84'));
      expect(limited.bg).not.toBe(warning.bg);
      fs.writeFileSync(path.join(RESULTS_DIR, `alarm-contrast--${theme}.json`), JSON.stringify(measured, null, 2) + '\n');
    });

    test('phone row: hidden-provider alarm is one nonnumeric mark with an explanation', async ({ page }) => {
      const { errors } = await open(page, theme, 390);
      const row = page.getByTestId('alarm-phone').locator('.gallery__row');
      const primary = chip(page, 'phone-primary');
      await expect(primary).not.toHaveAttribute('data-alarm');
      await expect(primary.getByTestId('usage-cli-chip-codex-weekly')).toHaveText('15%');
      await expect(primary.getByTestId('usage-cli-chip-codex-session')).toHaveCount(0);
      const mark = primary.getByTestId('usage-hidden-alarm');
      await expect(mark).toBeVisible();
      await expect(mark).toHaveAttribute('aria-hidden', 'true');
      await expect(mark).toHaveAttribute('data-severity', 'limited');
      expect((await mark.innerText()).trim()).toBe('');
      await expect(primary).toHaveAttribute('aria-label', new RegExp(
        'Also needs attention: Claude is limited by the provider: Rate limit reached\\. '
        + 'Gemini weekly quota 91 percent used, above 80 percent\\. Open usage\\.$'));

      // Still exactly two numeric usage figures on the phone row, and it fits without scrolling.
      const numbers = (await row.innerText()).match(/\$?[\d.,]+[%KM]?/g) ?? [];
      expect(numbers).toEqual(['15%', '$12.48']);
      // The fixture row is 390 CSS px wide (the gallery around it has its own padding).
      const fit = await row.evaluate((el) => ({ scroll: el.scrollWidth, client: el.clientWidth, width: el.getBoundingClientRect().width }));
      expect(fit.width).toBe(390);
      expect(fit.scroll).toBeLessThanOrEqual(fit.client);
      await shot(row, 'alarm-hidden-provider-phone', theme);
      expect(errors).toEqual([]);
    });

    test('transitions are announced politely once per cycle; stale data cannot clear a limit', async ({ page }) => {
      const { errors, requests } = await open(page, theme);
      const region = page.getByTestId('usage-alarm-status');
      await expect(region).toHaveAttribute('role', 'status');
      await expect(region).toHaveAttribute('aria-live', 'polite');
      await expect(region).toHaveText('');
      const codex = page.getByTestId('alarm-transitions').getByTestId('usage-cli-chip-codex');
      await expect(codex).toHaveAttribute('data-state', 'loading');

      const expected: [string, string | null, string][] = [
        ['1/8: Headroom 70%', null, ''],
        ['2/8: Crosses 80: 81%', 'warning', 'Codex weekly quota 81 percent used, above 80 percent.'],
        ['3/8: Refresh 82.5%', 'warning', 'Codex weekly quota 81 percent used, above 80 percent.'],
        ['4/8: Refresh 83%', 'warning', 'Codex weekly quota 81 percent used, above 80 percent.'],
        ['5/8: Provider limit at 83%', 'limited', 'Codex is limited by the provider: Rate limit reached.'],
        ['6/8: Stale probe says not limited', 'limited', 'Codex is limited by the provider: Rate limit reached.'],
        ['7/8: Refresh, still limited', 'limited', 'Codex is limited by the provider: Rate limit reached.'],
        ['8/8: Trusted recovery', 'warning', 'Codex is no longer limited.'],
      ];
      // Count every mutation of the live region: one per transition, none per refresh.
      await region.evaluate((el) => {
        (window as unknown as { liveMutations: number }).liveMutations = 0;
        new MutationObserver(() => { (window as unknown as { liveMutations: number }).liveMutations++; })
          .observe(el, { childList: true, characterData: true, subtree: true });
      });
      for (const [step, alarm, spoken] of expected) {
        await page.getByTestId('alarm-refresh').click();
        await expect(page.getByTestId('alarm-step')).toHaveText(step);
        if (alarm) await expect(codex).toHaveAttribute('data-alarm', alarm);
        else await expect(codex).not.toHaveAttribute('data-alarm');
        await expect(region).toHaveText(spoken);
        if (step.startsWith('6/8')) {
          await expect(codex).toHaveAttribute('data-state', 'stale');
          await expect(codex.getByTestId('usage-cli-chip-alarm-label')).toHaveText('Limited');
        }
      }
      const mutations = await page.evaluate(() => (window as unknown as { liveMutations: number }).liveMutations);
      expect(mutations).toBe(3);
      await expect(page.getByTestId('alarm-spoken-log').locator('li')).toHaveCount(3);
      await shot(page.getByTestId('alarm-transitions'), 'alarm-transitions', theme);

      expect(errors).toEqual([]);
      // Visibility only: no request leaves the page, so nothing can touch routing or model pins.
      expect(requests.filter((r) => !r.startsWith('GET ') || r.includes('/api/'))).toEqual([]);
    });
  });
}
