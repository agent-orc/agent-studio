import { test, expect, type Page } from '@playwright/test';
import http from 'node:http';
import { AddressInfo } from 'node:net';
import path from 'node:path';
import fs from 'node:fs';

/**
 * HUC-S4 responsive header integration (docs/header-usage-cockpit/index.html
 * "Responsive layout").
 *
 * Mounts the REAL `app-usage-cockpit-header` (with the real CLI and cost
 * chips) through the backend-free `usage-header-mockup` app with pinned
 * synthetic fixtures and a stand-in navigation row that follows the studio
 * shell's `data-nav-inline-from` contract. Verifies row heights, coarse
 * targets, whole-value fit, the More menu, focus order and focus
 * restoration in both themes, and writes screenshots.
 *
 * Build the bundle first:  npm run build:mockup:usage-header
 * Screenshots land in JOB_RESULTS_DIR/usage-header when set, otherwise in
 * test-results/usage-header. Evidence label: `--mocked` (synthetic data, real
 * components and compiled CSS, no backend).
 */

const DIST_DIR = path.resolve(__dirname, '..', '..', 'dist', 'usage-header-mockup', 'browser');
const RESULTS_DIR = path.join(
  process.env.JOB_RESULTS_DIR?.trim() || path.resolve(__dirname, '..', '..', 'test-results'),
  'usage-header',
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
    throw new Error(`Missing build at ${DIST_DIR}. Run "npm run build:mockup:usage-header" before this spec.`);
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

interface OpenOptions {
  width: number;
  theme?: 'light' | 'dark';
  query?: Record<string, string>;
  height?: number;
}

async function open(page: Page, { width, theme = 'dark', query = {}, height = 700 }: OpenOptions): Promise<string[]> {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()); });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.setViewportSize({ width, height });
  const params = new URLSearchParams({ theme, ...query });
  await page.goto(`${baseUrl}?${params}`);
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
  await page.evaluate(() => document.fonts.ready);
  await settle(page);
  return errors;
}

/** Waits until the measured fit stops changing. */
async function settle(page: Page): Promise<void> {
  let previous = '';
  for (let i = 0; i < 20; i++) {
    const state = await page.getByTestId('studio-titlebar').evaluate((el) =>
      [el.getAttribute('data-tier'), el.getAttribute('data-nav-tier'), el.getAttribute('data-layout'),
        el.getAttribute('data-fit'), el.querySelectorAll('[data-testid="cockpit-secondary-cli"]').length].join('|'));
    if (state === previous && !state.startsWith('null')) return;
    previous = state;
    await page.waitForTimeout(60);
  }
}

const header = (page: Page) => page.getByTestId('studio-titlebar');
/** The live usage row; the off-screen measuring copies are excluded. */
const usageRow = (page: Page) => page.getByTestId('cockpit-header-usage-row');

/** Visible usage numbers in the header strip, in DOM order. */
function visibleUsageValues(page: Page): Promise<string[]> {
  return header(page).evaluate((el) =>
    Array.from(el.querySelectorAll<HTMLElement>('.cockpit-header__usage [data-testid^="usage-cli-chip-"][data-testid$="weekly"], .cockpit-header__usage [data-testid$="session"], .cockpit-header__usage [data-testid="usage-cost-chip-value"]'))
      .filter((v) => v.getClientRects().length > 0)
      .map((v) => v.textContent?.trim() ?? ''));
}

interface FitReport {
  docOverflow: number;
  headerOverflow: number;
  problems: string[];
}

/**
 * Whole-value fit: no horizontal scrolling, every visible chip and text
 * control lies inside the header, no control clips its content and no value
 * wraps onto a second line.
 */
function fitReport(page: Page): Promise<FitReport> {
  return page.evaluate(() => {
    const host = document.querySelector<HTMLElement>('[data-testid="studio-titlebar"]')!;
    const box = host.getBoundingClientRect();
    const problems: string[] = [];
    const controls = Array.from(host.querySelectorAll<HTMLElement>('button'))
      .filter((b) => !b.closest('.cockpit-header__measure') && b.getClientRects().length > 0);
    for (const b of controls) {
      const r = b.getBoundingClientRect();
      const name = b.getAttribute('aria-label') ?? b.textContent?.trim() ?? '?';
      if (r.left < box.left - 0.5 || r.right > box.right + 0.5) problems.push(`outside header: ${name}`);
      if (b.scrollWidth > b.clientWidth + 1) problems.push(`clipped: ${name}`);
    }
    const values = Array.from(host.querySelectorAll<HTMLElement>('[class*="__value"], [class*="__name"], [class*="__label"], [class*="__tag"]'))
      .filter((v) => !v.closest('.cockpit-header__measure') && v.getClientRects().length > 0);
    for (const v of values) {
      if (v.getClientRects().length > 1) problems.push(`wrapped: ${v.textContent}`);
      const r = v.getBoundingClientRect();
      if (r.left < box.left - 0.5 || r.right > box.right + 0.5) problems.push(`value outside: ${v.textContent}`);
    }
    return {
      docOverflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      headerOverflow: host.scrollWidth - host.clientWidth,
      problems,
    };
  });
}

function rowHeights(page: Page): Promise<{ nav: number; usage: number; header: number }> {
  return header(page).evaluate((el) => ({
    nav: el.querySelector('.cockpit-header__nav')!.getBoundingClientRect().height,
    usage: el.querySelector('.cockpit-header__usage')!.getBoundingClientRect().height,
    // Border-box minus the bottom hairline.
    header: el.getBoundingClientRect().height - parseFloat(getComputedStyle(el).borderBottomWidth),
  }));
}

async function shot(page: Page, name: string): Promise<void> {
  // Capture the resting header, not a keyboard focus ring left by a prior step.
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await header(page).screenshot({ path: path.join(RESULTS_DIR, `${name}--mocked.png`), animations: 'disabled' });
}

async function moreMenuLabels(page: Page): Promise<string[]> {
  await page.getByTestId('cockpit-header-more').click();
  const rows = page.locator('[data-testid^="cockpit-header-more-menu-item-"]');
  await expect(rows.first()).toBeVisible();
  const labels = (await rows.allTextContents()).map((t) => t.trim());
  await page.keyboard.press('Escape');
  return labels;
}

for (const theme of ['dark', 'light'] as const) {
  test.describe(`@mockup header usage cockpit (real component), ${theme}`, () => {
    test('1728: navigation row above usage row with every CLI as a whole chip', async ({ page }) => {
      const errors = await open(page, { width: 1728, theme });
      await expect(header(page)).toHaveAttribute('data-layout', 'rows');
      await expect(header(page)).toHaveAttribute('data-tier', 'wide');
      const h = await rowHeights(page);
      expect(h.nav).toBeCloseTo(44, 0);
      expect(h.usage).toBeCloseTo(40, 0);
      expect(h.header).toBeCloseTo(84, 0);
      expect(await visibleUsageValues(page)).toEqual(['15%', '32%', '7%', '19%', '$12.48']);
      await expect(page.getByTestId('cockpit-header-details')).toBeVisible();
      await expect(page.getByTestId('cockpit-header-details-count')).toHaveCount(0);
      for (const id of ['nav-project', 'nav-search', 'nav-chat', 'nav-theme']) await expect(page.getByTestId(id)).toBeVisible();
      expect(await moreMenuLabels(page)).toEqual(['Usage details', 'Orchestrator feed', 'Orchestrator settings']);
      expect((await fitReport(page)).problems).toEqual([]);
      await shot(page, `header-1728-${theme}`);
      expect(errors).toEqual([]);
    });

    test('1024: tablet keeps primary, next CLI and Today; Theme moves to More', async ({ page }) => {
      const errors = await open(page, { width: 1024, theme, query: { scenario: 'three' } });
      await expect(header(page)).toHaveAttribute('data-tier', 'tablet');
      const h = await rowHeights(page);
      expect(h.nav).toBeCloseTo(44, 0);
      expect(h.usage).toBeCloseTo(40, 0);
      expect(await visibleUsageValues(page)).toEqual(['15%', '32%', '7%', '19%', '$12.48']);
      await expect(usageRow(page).getByTestId('usage-cli-chip-gemini')).toHaveCount(0);
      await expect(page.getByTestId('cockpit-header-details-count')).toHaveText('+1');
      await expect(page.getByTestId('cockpit-header-details')).toHaveAttribute('aria-label', /Gemini/);
      await expect(page.getByTestId('nav-theme')).toBeHidden();
      await expect(page.getByTestId('nav-chat')).toBeVisible();
      expect(await moreMenuLabels(page)).toEqual(['Switch theme', 'Usage details', 'Orchestrator feed', 'Orchestrator settings']);
      expect((await fitReport(page)).problems).toEqual([]);
      await shot(page, `header-1024-${theme}`);
      expect(errors).toEqual([]);
    });

    test('390: one 48 px row with only primary weekly and today cost', async ({ page }) => {
      const errors = await open(page, { width: 390, theme, query: { scenario: 'three' } });
      await expect(header(page)).toHaveAttribute('data-layout', 'single');
      expect((await rowHeights(page)).header).toBeCloseTo(48, 0);
      expect(await visibleUsageValues(page)).toEqual(['15%', '$12.48']);
      await expect(page.getByTestId('cockpit-header-wordmark')).toHaveText('Studio');
      await expect(page.getByTestId('cockpit-secondary-cli')).toHaveCount(0);
      await expect(page.getByTestId('cockpit-header-details')).toHaveCount(0);
      await expect(usageRow(page).getByTestId('usage-cost-chip')).not.toContainText('USD');
      await expect(usageRow(page).getByTestId('usage-cost-chip')).toHaveAttribute('aria-label', /USD/);
      // The accessible name still carries the session window.
      await expect(usageRow(page).getByTestId('usage-cli-chip-codex')).toHaveAttribute('aria-label', /32/);
      for (const target of ['usage-cli-chip-codex', 'usage-cost-chip', 'cockpit-header-more']) {
        const r = await header(page).getByTestId(target).first().boundingBox();
        expect(r!.height, target).toBeGreaterThanOrEqual(44);
      }
      expect((await fitReport(page)).problems).toEqual([]);
      await shot(page, `header-390-${theme}`);
      expect(errors).toEqual([]);
    });
  });
}

test.describe('@mockup header usage cockpit: whole-value fit', () => {
  test('live wider values replan without a header resize', async ({ page }) => {
    const errors = await open(page, { width: 320 });
    await expect(header(page)).toHaveAttribute('data-fit', 'abbreviated');
    const initialWidth = await header(page).evaluate(el => el.clientWidth);
    await page.getByTestId('harness-load-large').click();
    await expect(usageRow(page).getByTestId('usage-cost-chip-value')).toHaveText('$12.5K');
    await expect(header(page)).toHaveAttribute('data-fit', 'bare');
    expect(await header(page).evaluate(el => el.clientWidth)).toBe(initialWidth);
    const report = await fitReport(page);
    expect(report.problems).toEqual([]);
    expect(report.docOverflow).toBeLessThanOrEqual(0);
    expect(report.headerOverflow).toBeLessThanOrEqual(0);
    await shot(page, 'fit-320-live-value-update');
    expect(errors).toEqual([]);
  });

  const cases: { name: string; width: number; scenario: string; scale?: number }[] = [
    { name: '320-long', width: 320, scenario: 'long' },
    { name: '320-large', width: 320, scenario: 'large' },
    { name: '360-long', width: 360, scenario: 'long' },
    { name: '800-long', width: 800, scenario: 'long' },
    { name: '1280-long', width: 1280, scenario: 'long' },
    // 200% zoom on a 1728 px window and on a 1280 px window.
    { name: 'zoom200-864-long', width: 864, scenario: 'long', scale: 2 },
    { name: 'zoom200-640-large', width: 640, scenario: 'large', scale: 2 },
    { name: '390-loading', width: 390, scenario: 'loading' },
  ];
  for (const c of cases) {
    test(`${c.name}: no clipping, mid-value wrap or horizontal scroll`, async ({ browser }) => {
      const context = await browser.newContext({ deviceScaleFactor: c.scale ?? 1 });
      const page = await context.newPage();
      const errors = await open(page, { width: c.width, query: { scenario: c.scenario } });
      const report = await fitReport(page);
      expect(report.problems).toEqual([]);
      expect(report.docOverflow).toBeLessThanOrEqual(0);
      expect(report.headerOverflow).toBeLessThanOrEqual(0);
      const values = await visibleUsageValues(page);
      if (c.scenario !== 'loading') {
        // The primary weekly percentage and today's cost always survive.
        expect(values.length).toBeGreaterThanOrEqual(2);
        expect(values[values.length - 1]).toMatch(/^\$/);
      }
      await shot(page, `fit-${c.name}`);
      expect(errors).toEqual([]);
      await context.close();
    });
  }

  test('320 long: provider abbreviates and the wordmark goes before any value', async ({ page }) => {
    await open(page, { width: 320, query: { scenario: 'long' } });
    await expect(header(page)).not.toHaveAttribute('data-fit', 'compact');
    await expect(usageRow(page).getByTestId('usage-cli-chip-enterprise-gateway-provider')).toContainText('ENT');
    await expect(usageRow(page).getByTestId('usage-cli-chip-enterprise-gateway-provider')).toHaveAttribute('aria-label', /enterprise-gateway-provider/);
    await expect(usageRow(page).getByTestId('usage-cost-chip-value')).toHaveText('$1.2M');
  });
});

test.describe('@mockup header usage cockpit: priority and container rules', () => {
  test('primary order follows selection, then saved default, then provider order', async ({ page }) => {
    await open(page, { width: 390 });
    await expect(page.getByTestId('cockpit-primary-cli').getByRole('button')).toHaveAttribute('data-testid', 'usage-cli-chip-codex');
    await open(page, { width: 390, query: { default: 'claude' } });
    await expect(page.getByTestId('cockpit-primary-cli').getByRole('button')).toHaveAttribute('data-testid', 'usage-cli-chip-claude');
    await open(page, { width: 390, query: { default: 'claude', selected: 'codex' } });
    await expect(page.getByTestId('cockpit-primary-cli').getByRole('button')).toHaveAttribute('data-testid', 'usage-cli-chip-codex');

    await open(page, { width: 1728, query: { scenario: 'three', default: 'gemini', selected: 'claude' } });
    expect(await usageRow(page).locator('button[data-testid^="usage-cli-chip-"]').evaluateAll(
      (chips) => chips.map((chip) => chip.getAttribute('data-testid')),
    )).toEqual(['usage-cli-chip-claude', 'usage-cli-chip-gemini', 'usage-cli-chip-codex']);
  });

  test('a narrow container follows the same rules as a narrow viewport', async ({ page }) => {
    await open(page, { width: 1728, query: { scenario: 'three', container: '700' } });
    await expect(header(page)).toHaveAttribute('data-layout', 'single');
    expect(await visibleUsageValues(page)).toEqual(['15%', '$12.48']);
    await shot(page, 'container-700-in-1728');

    await open(page, { width: 1728, query: { scenario: 'three', container: '1024' } });
    await expect(header(page)).toHaveAttribute('data-tier', 'tablet');
    await expect(page.getByTestId('nav-theme')).toBeHidden();
    await expect(usageRow(page).getByTestId('usage-cli-chip-gemini')).toHaveCount(0);
    expect((await fitReport(page)).problems).toEqual([]);
  });

  test('hidden chips and navigation leave the focus order', async ({ page }) => {
    await open(page, { width: 1024, query: { scenario: 'three' } });
    await page.getByTestId('nav-project').focus();
    const order: string[] = [];
    for (let i = 0; i < 8; i++) {
      order.push(await page.evaluate(() => {
        const el = document.activeElement as HTMLElement;
        return el.getAttribute('data-testid') ?? el.textContent?.trim() ?? '';
      }));
      await page.keyboard.press('Tab');
    }
    expect(order).toEqual([
      'nav-project', 'nav-search', 'nav-chat', 'cockpit-header-more',
      'usage-cli-chip-codex', 'usage-cli-chip-claude', 'usage-cost-chip', 'cockpit-header-details',
    ]);
  });
});

test.describe('@mockup header usage cockpit: keyboard and focus', () => {
  test('More opens from the keyboard, lists every destination and returns focus', async ({ page }) => {
    await open(page, { width: 390 });
    await page.getByTestId('cockpit-header-more').focus();
    await page.keyboard.press('Enter');
    const rows = page.locator('[data-testid^="cockpit-header-more-menu-item-"]');
    await expect(rows.first()).toBeFocused();
    expect((await rows.allTextContents()).map((t) => t.trim())).toEqual([
      'Switch project', 'Search', 'Project chat', 'Switch theme', 'Usage details', 'Orchestrator feed', 'Orchestrator settings',
    ]);
    // Text-only rows: no icon or image inside a menu row.
    expect(await rows.locator('svg, img, app-studio-icon').count()).toBe(0);
    await page.screenshot({ path: path.join(RESULTS_DIR, 'more-menu-390-page--mocked.png'), clip: { x: 0, y: 0, width: 390, height: 420 } });
    await page.keyboard.press('Escape');
    await expect(rows).toHaveCount(0);
    await expect(page.getByTestId('cockpit-header-more')).toBeFocused();

    await page.keyboard.press('Enter');
    await expect(rows.first()).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(rows.nth(1)).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('harness-last-action')).toHaveText('nav:search');
    await expect(page.getByTestId('cockpit-header-more')).toBeFocused();
  });

  test('usage chips open their section with Enter and Space', async ({ page }) => {
    await open(page, { width: 1728 });
    await usageRow(page).getByTestId('usage-cli-chip-claude').focus();
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('harness-last-action')).toHaveText('usage:cli:claude');
    await usageRow(page).getByTestId('usage-cost-chip').focus();
    await page.keyboard.press('Space');
    await expect(page.getByTestId('harness-last-action')).toHaveText('usage:cost');
    await page.getByTestId('cockpit-header-details').focus();
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('harness-last-action')).toHaveText('usage:details');
  });

  test('focus on a chip that collapses into Details moves to Details', async ({ page }) => {
    await open(page, { width: 1728, query: { scenario: 'three' } });
    await usageRow(page).getByTestId('usage-cli-chip-gemini').focus();
    await page.setViewportSize({ width: 1024, height: 700 });
    await settle(page);
    await expect(usageRow(page).getByTestId('usage-cli-chip-gemini')).toHaveCount(0);
    await expect(page.getByTestId('cockpit-header-details')).toBeFocused();

    await page.getByTestId('cockpit-header-details').focus();
    await page.setViewportSize({ width: 390, height: 700 });
    await settle(page);
    await expect(page.getByTestId('cockpit-header-more')).toBeFocused();
  });
});

test.describe('@mockup header usage cockpit: coarse pointer', () => {
  test.use({ hasTouch: true, isMobile: true });

  for (const theme of ['dark', 'light'] as const) {
    test(`touch tablet uses 48 + 48 px rows and 44 px targets (${theme})`, async ({ page }) => {
      await open(page, { width: 1024, theme });
      expect(await page.evaluate(() => matchMedia('(pointer: coarse)').matches)).toBe(true);
      const h = await rowHeights(page);
      expect(h.nav).toBeGreaterThanOrEqual(48);
      expect(h.usage).toBeGreaterThanOrEqual(48);
      const targets = header(page).locator('button:visible');
      const count = await targets.count();
      expect(count).toBeGreaterThan(5);
      for (let i = 0; i < count; i++) {
        const box = await targets.nth(i).boundingBox();
        expect(box!.height, await targets.nth(i).innerText()).toBeGreaterThanOrEqual(44);
      }
      expect((await fitReport(page)).problems).toEqual([]);
      await shot(page, `touch-1024-${theme}`);
    });
  }
});
