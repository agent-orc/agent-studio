import { test, expect, type Locator, type Page } from '@playwright/test';
import http from 'node:http';
import { AddressInfo } from 'node:net';
import path from 'node:path';
import fs from 'node:fs';

import { contrastRatio } from '../helpers/contrast';

/**
 * HUC-S2 shared usage chips (docs/header-usage-cockpit/index.html).
 *
 * Mounts the REAL `app-usage-cli-chip`, `app-usage-cost-chip` and
 * `app-usage-slot-chip` through the backend-free `usage-chips-mockup` app with
 * pinned synthetic fixtures, then checks geometry, accessible names, focus and
 * state labels in both themes and writes screenshots. The header integration
 * is HUC-S4 and is not exercised here.
 *
 * Build the bundle first:  npm run build:mockup:usage
 * Screenshots land in JOB_RESULTS_DIR/usage-chips when set, otherwise in
 * test-results/usage-chips. Evidence label: `--mocked` (synthetic data, real
 * components and compiled CSS, no backend).
 */

const DIST_DIR = path.resolve(__dirname, '..', '..', 'dist', 'usage-chips-mockup', 'browser');
const RESULTS_DIR = path.join(
  process.env.JOB_RESULTS_DIR?.trim() || path.resolve(__dirname, '..', '..', 'test-results'),
  'usage-chips',
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

async function open(page: Page, theme: 'light' | 'dark'): Promise<string[]> {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()); });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.setViewportSize({ width: 1024, height: 900 });
  await page.goto(`${baseUrl}?theme=${theme}`);
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
  await expect(page.getByTestId('gallery-normal').getByRole('button').first()).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  return errors;
}

interface ChipGeometry {
  height: number;
  radius: string;
  backgroundImage: string;
  boxShadow: string;
  borderLeft: string;
  borderRight: string;
  clipped: boolean;
  nestedSurfaces: number;
}

function chipGeometry(button: Locator): Promise<ChipGeometry> {
  return button.evaluate((el) => {
    const s = getComputedStyle(el);
    // A nested mini pill is any descendant that paints its own border or fill.
    const nested = Array.from(el.querySelectorAll<HTMLElement>('*')).filter((child) => {
      const c = getComputedStyle(child);
      const filled = c.backgroundColor !== 'rgba(0, 0, 0, 0)' && c.backgroundColor !== 'transparent';
      const bordered = parseFloat(c.borderTopWidth) > 0 || parseFloat(c.borderLeftWidth) > 0;
      const placeholder = child.getAttribute('aria-hidden') === 'true' && child.children.length === 0 && !child.textContent?.trim();
      return (filled || bordered) && !placeholder;
    }).length;
    return {
      height: el.getBoundingClientRect().height,
      radius: s.borderTopLeftRadius,
      backgroundImage: s.backgroundImage,
      boxShadow: s.boxShadow,
      borderLeft: `${s.borderLeftWidth} ${s.borderLeftStyle} ${s.borderLeftColor}`,
      borderRight: `${s.borderRightWidth} ${s.borderRightStyle} ${s.borderRightColor}`,
      clipped: el.scrollWidth > el.clientWidth + 0.5,
      nestedSurfaces: nested,
    };
  });
}

for (const theme of ['light', 'dark'] as const) {
  test.describe(`@mockup usage chips (real components), ${theme}`, () => {
    test('normal chips share height, radius and baseline with flat surfaces', async ({ page }) => {
      const errors = await open(page, theme);
      const buttons = page.locator('app-usage-cli-chip button, app-usage-cost-chip button, app-usage-slot-chip button');
      const count = await buttons.count();
      expect(count).toBe(21);
      const geometries = await Promise.all(Array.from({ length: count }, (_, i) => chipGeometry(buttons.nth(i))));
      const first = geometries[0];
      expect(first.height).toBeGreaterThanOrEqual(32);
      for (const g of geometries) {
        expect(g.height).toBe(first.height);
        expect(g.radius).toBe(first.radius);
        expect(g.backgroundImage).toBe('none');
        expect(g.boxShadow).toBe('none');
        expect(g.borderLeft).toBe(g.borderRight);
        expect(g.clipped).toBe(false);
        expect(g.nestedSurfaces).toBe(0);
      }

      // Labels and values share one text baseline across the normal row.
      const normal = page.getByTestId('gallery-normal');
      const baselines = await normal.locator('button').evaluateAll((els) => els.map((el) => {
        const spans = Array.from(el.querySelectorAll<HTMLElement>('span'))
          .filter((span) => span.children.length === 0 && span.textContent?.trim());
        return spans.map((span) => {
          const probe = document.createElement('span');
          probe.style.display = 'inline-block';
          probe.style.width = '0';
          probe.style.height = '0';
          span.appendChild(probe);
          const y = probe.getBoundingClientRect().top;
          probe.remove();
          return Math.round(y * 2) / 2;
        });
      }));
      const all = baselines.flat();
      expect(all.length).toBeGreaterThanOrEqual(12);
      expect(new Set(all).size).toBe(1);
      expect(errors).toEqual([]);
    });

    test('every chip is one native button with a complete accessible name', async ({ page }) => {
      await open(page, theme);
      const normal = page.getByTestId('gallery-normal');
      await expect(normal.getByRole('button', {
        name: 'Codex, weekly 15 percent used, current five-hour window 32 percent used. Open usage.', exact: true,
      })).toHaveAttribute('aria-haspopup', 'dialog');
      await expect(normal.getByRole('button', {
        name: 'Claude, weekly 7 percent used, current five-hour window 19 percent used. Open usage.', exact: true,
      })).toBeVisible();
      await expect(normal.getByRole('button', {
        name: "Today's cost, $12.48 USD ledger estimate. Open cost detail.", exact: true,
      })).toBeVisible();
      for (const host of await page.locator('app-usage-cli-chip, app-usage-cost-chip, app-usage-slot-chip').all()) {
        await expect(host.locator('button')).toHaveCount(1);
        await expect(host.locator('a, input, [tabindex]')).toHaveCount(0);
        expect((await host.locator('button').getAttribute('aria-label'))?.length ?? 0).toBeGreaterThan(20);
      }
    });

    test('state labels, and missing values never read as zero', async ({ page }) => {
      await open(page, theme);
      const loading = page.getByTestId('gallery-loading');
      await expect(loading.getByRole('button', { name: 'Codex, usage loading. Open usage.', exact: true })).toHaveAttribute('data-state', 'loading');
      await expect(loading.getByRole('button', { name: "Today's cost, loading. Open cost detail.", exact: true })).toHaveAttribute('data-state', 'loading');
      expect(await loading.innerText()).not.toMatch(/\d/);

      const unknown = page.getByTestId('gallery-unknown');
      await expect(unknown.getByRole('button', {
        name: 'Gemini, usage unavailable. Unavailable: No quota snapshot has been recorded. Open usage.', exact: true,
      })).toContainText('Unavailable');
      const noSession = unknown.getByRole('button', {
        name: 'Codex, weekly 15 percent used, current session not reported. Open usage.', exact: true,
      });
      await expect(noSession.getByTestId('usage-cli-chip-codex-session')).toHaveText('N/A');
      await expect(noSession.getByTestId('usage-cli-chip-codex-weekly')).toHaveText('15%');
      const unknownCost = unknown.getByTestId('usage-cost-chip');
      await expect(unknownCost).toHaveAttribute('data-state', 'unknown');
      await expect(unknownCost.getByTestId('usage-cost-chip-value')).toHaveText('N/A');
      expect(await unknown.innerText()).not.toMatch(/\$0\.00|(^|\s)0%/);

      const stale = page.getByTestId('gallery-stale');
      await expect(stale.getByTestId('usage-cli-chip-codex')).toHaveAttribute('data-state', 'stale');
      await expect(stale.getByTestId('usage-cli-chip-codex')).toHaveAttribute('aria-label',
        /Stale: The latest probe failed; showing the last good values\. Last updated Fri 25 Sept?, 15:40 Europe\/Berlin \(Fri 25 Sept?, 13:40 UTC\)\. Open usage\.$/);
      await expect(stale.getByTestId('usage-cost-chip')).toHaveAttribute('aria-label', /Stale: /);

      const suspicious = page.getByTestId('gallery-suspicious');
      await expect(suspicious.getByTestId('usage-cli-chip-claude')).toHaveAttribute('aria-label',
        /Unverified: Implausible downward jump awaiting a confirmation probe\./);
      await expect(suspicious.getByTestId('usage-cost-chip').first()).toHaveAttribute('aria-label',
        /Partial: Some models have no historical USD price\./);
      // A complete ledger with no spend is a real zero, distinct from unknown.
      await expect(suspicious.getByTestId('usage-cost-chip').nth(1)).toHaveAttribute('data-state', 'normal');
      await expect(suspicious.getByTestId('usage-cost-chip-value').nth(1)).toHaveText('$0.00');

      await page.screenshot({ path: path.join(RESULTS_DIR, `usage-chips-states--${theme}--mocked.png`), fullPage: true });
    });

    test('long values keep full numeric units and exact detail', async ({ page }) => {
      await open(page, theme);
      const long = page.getByTestId('gallery-long');
      await expect(long.getByTestId('usage-cli-chip-codex-weekly')).toHaveText('104.3%');
      await expect(long.getByTestId('usage-cli-chip-codex-session')).toHaveText('137%');
      await expect(long.getByTestId('usage-cli-chip-claude-weekly')).toHaveText('7.5%');
      await expect(long.getByTestId('usage-cli-chip-claude-session')).toHaveText('0.4%');
      await expect(long.getByTestId('usage-cli-chip-claude')).toHaveAttribute('aria-label', /current session 0\.4 percent used/);
      await expect(long.getByRole('button', { name: /^enterprise-gateway-provider, weekly 64\.5 percent used/ })).toContainText('enterprise-gateway-provider');
      const costs = long.getByTestId('usage-cost-chip');
      await expect(costs.nth(0).getByTestId('usage-cost-chip-value')).toHaveText('$12.5K');
      await expect(costs.nth(0)).toHaveAttribute('aria-label', "Today's cost, $12,480.37 USD ledger estimate. Open cost detail.");
      await expect(costs.nth(1).getByTestId('usage-cost-chip-value')).toHaveText('$1.2M');
      await expect(costs.nth(1)).toHaveAttribute('aria-label', "Today's cost, $1,234,567.89 USD ledger estimate. Open cost detail.");

      // Exact values are also on hover and keyboard focus (shared tooltip).
      await costs.nth(1).focus();
      await expect(page.getByText("Today's cost: $1,234,567.89 USD, token-ledger estimate")).toBeVisible();
      await page.screenshot({ path: path.join(RESULTS_DIR, `usage-chips-long-values--${theme}--mocked.png`), fullPage: true });
    });

    test('keyboard focus ring, activation and contrast', async ({ page }) => {
      await open(page, theme);
      const normal = page.getByTestId('gallery-normal');
      const codex = normal.getByTestId('usage-cli-chip-codex');
      await page.keyboard.press('Tab');
      await expect(codex).toBeFocused();
      const ring = await codex.evaluate((el) => {
        const s = getComputedStyle(el);
        return { width: s.outlineWidth, style: s.outlineStyle, color: s.outlineColor };
      });
      expect(ring.width).toBe('2px');
      expect(ring.style).toBe('solid');
      await page.keyboard.press('Tab');
      await expect(normal.getByTestId('usage-cli-chip-claude')).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(normal.getByTestId('usage-cost-chip')).toBeFocused();

      await page.keyboard.press('Shift+Tab');
      await page.keyboard.press('Shift+Tab');
      await expect(codex).toHaveAttribute('aria-expanded', 'false');
      await page.keyboard.press('Enter');
      await expect(codex).toHaveAttribute('aria-expanded', 'true');
      await expect(codex).toHaveAttribute('aria-controls', 'fixture-detail');
      await page.keyboard.press('Space');
      await expect(codex).toHaveAttribute('aria-expanded', 'false');

      // Text and focus-ring contrast against the chip and row surfaces.
      const colours = await codex.evaluate((el) => {
        const value = el.querySelector('[data-testid="usage-cli-chip-codex-weekly"]')!;
        const name = el.querySelector('span span')!;
        const row = el.closest('.gallery__row')!;
        return {
          chipBg: getComputedStyle(el).backgroundColor,
          rowBg: getComputedStyle(row).backgroundColor,
          value: getComputedStyle(value).color,
          name: getComputedStyle(name).color,
          ring: getComputedStyle(el).outlineColor,
        };
      });
      const measured = {
        theme,
        valueOnChip: contrastRatio(colours.value, colours.chipBg),
        nameOnChip: contrastRatio(colours.name, colours.chipBg),
        focusRingOnRow: contrastRatio(colours.ring, colours.rowBg),
      };
      expect(measured.valueOnChip).toBeGreaterThanOrEqual(4.5);
      expect(measured.nameOnChip).toBeGreaterThanOrEqual(4.5);
      expect(measured.focusRingOnRow).toBeGreaterThanOrEqual(3);
      fs.writeFileSync(path.join(RESULTS_DIR, `contrast--${theme}.json`), JSON.stringify(measured, null, 2) + '\n');

      await codex.focus();
      await normal.screenshot({ path: path.join(RESULTS_DIR, `usage-chips-focus--${theme}--mocked.png`) });
    });

    test('slot chip lives only in the expanded view and toggles in place', async ({ page }) => {
      await open(page, theme);
      await expect(page.getByTestId('gallery-normal').locator('app-usage-slot-chip')).toHaveCount(0);
      const expanded = page.getByTestId('gallery-expanded');
      const slot = expanded.getByRole('button', {
        name: 'Slots, remote 2 of 3 in use, review 1 of 3 in use, auto 10 of 17 in use. Show slot pools.', exact: true,
      });
      await expect(slot).not.toHaveAttribute('aria-haspopup');
      await expect(slot.getByTestId('usage-slot-chip-auto')).toHaveText('auto10/17');
      await slot.click();
      await expect(slot).toHaveAttribute('aria-expanded', 'true');
      await expect(page.locator('#fixture-slot-pools')).toBeVisible();
      const partial = expanded.getByRole('button', { name: /^Slots, remote unavailable, review 1 of 3 in use/ });
      await expect(partial.getByTestId('usage-slot-chip-remote')).toHaveText('remoteN/A');
      await expect(expanded.getByRole('button', { name: 'Slots, loading. Show slot pools.', exact: true })).toBeVisible();

      await page.screenshot({ path: path.join(RESULTS_DIR, `usage-chips-overview--${theme}--mocked.png`), fullPage: true });
    });
  });
}
