import { test, expect, type Page } from '../fixtures/dev-backend';
import { mkdirSync, writeFileSync } from 'node:fs';
import * as path from 'node:path';
import { setTheme, sampleColours, dismissDevErrorDialog } from '../helpers/theme';
import { contrastRatio } from '../helpers/contrast';
import { SNAPSHOT, FIXTURE_NOW } from '../../src/mockups/usage-chips/app/usage-chips.fixtures';

/** HUC-S7: actual Studio UI over the isolated ADR-0056 demo workspace. The
 * cockpit HTTP response is pinned synthetic data, not live provider telemetry.
 * Earlier slice specs own detailed policy, focus and board protocol matrices.
 */
test.use({ demoWorkspace: true, reducedMotion: 'reduce', trace: 'off', video: 'off' });
test.describe.configure({ timeout: 600_000 });

const OUT = process.env.HUC_RESULTS_DIR || path.join(process.env.JOB_RESULTS_DIR || path.resolve('test-results'), 'cockpit-matrix');
const WIDTHS = [390, 1024, 1728] as const;
const THEMES = ['light', 'dark'] as const;
const CARD = '[data-testid="task-card"], [data-testid="job-card"]';
const BASE = 'product-ui--demo-workspace--pinned-usage--mocked';

function checkFixture(): void {
  const { cost, runs, calendar } = SNAPSHOT;
  expect(cost.projects.reduce((sum, p) => sum + (p.todayUsd ?? 0), 0)).toBeCloseTo(cost.todayUsd!, 6);
  expect(cost.projects.reduce((sum, p) => sum + (p.weekUsd ?? 0), 0)).toBeCloseTo(cost.weekUsd!, 6);
  expect(runs.some(r => r.includedInTotals && (r.provisionalCostUsd ?? 0) > 0)).toBe(true);
  expect(runs.some(r => !r.includedInTotals)).toBe(true);
  expect(calendar.dayStartUtc).toBe('2026-09-24T22:00:00Z');
  expect(calendar.dayEndUtc).toBe('2026-09-25T22:00:00Z');
  expect(new Intl.DateTimeFormat('en-GB', { timeZone: calendar.timeZone, hour: '2-digit', hourCycle: 'h23' })
    .format(new Date(calendar.dayStartUtc))).toBe('00');
  // Berlin spring DST day is 23 hours. The provider reset above stays its own instant.
  expect((Date.parse('2026-03-29T22:00:00Z') - Date.parse('2026-03-28T23:00:00Z')) / 3_600_000).toBe(23);
  expect(SNAPSHOT.clis[0].windows[0].resetAtUtc).toBe('2026-09-30T08:00:00Z');
}

async function selectDemo(page: Page): Promise<void> {
  const picker = page.getByTestId('studio-project-picker-trigger');
  await expect(picker).toBeVisible({ timeout: 30_000 });
  await picker.click();
  await page.getByTestId('studio-project-picker-panel').getByText('Demo App', { exact: true }).click();
  await expect(page.locator(CARD).first()).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('cockpit-header-usage-row').getByTestId('usage-cost-chip-value')).toHaveText('$12.48');
}

async function geometry(page: Page, width: number): Promise<void> {
  await page.evaluate(async () => {
    await document.fonts.ready;
    await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
  });
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth),
    { timeout: 5_000 }).toBeLessThanOrEqual(2);
  const result = await page.getByTestId('studio-titlebar').evaluate((host) => {
    const rect = host.getBoundingClientRect();
    const usage = host.querySelector<HTMLElement>('[data-testid="cockpit-header-usage-row"]')!;
    const buttons = [...host.querySelectorAll<HTMLElement>('.cockpit-header__usage button')]
      .filter(el => !el.closest('.cockpit-header__measure') && el.getClientRects().length);
    const values = [...usage.querySelectorAll<HTMLElement>('[data-testid$="weekly"], [data-testid$="session"], [data-testid="usage-cost-chip-value"]')]
      .filter(el => el.getClientRects().length);
    return {
      layout: host.getAttribute('data-layout'),
      viewportOverflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
      overflowElements: [...document.querySelectorAll<HTMLElement>('body *')]
        .filter(el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.right > window.innerWidth + 2 && r.left < window.innerWidth; })
        .slice(0, 12).map(el => `${el.tagName.toLowerCase()}.${el.className?.toString().slice(0, 55)}:${Math.round(el.getBoundingClientRect().right)}`),
      headerOverflow: host.scrollWidth - host.clientWidth,
      navHeight: host.querySelector<HTMLElement>('[data-testid="cockpit-header-nav-row"]')!.getBoundingClientRect().height,
      usageHeight: usage.getBoundingClientRect().height,
      headerHeight: rect.height,
      chipHeights: buttons.filter(el => el.closest('.cockpit-header__usage')).map(el => el.getBoundingClientRect().height),
      values: values.map(el => el.textContent?.trim()),
      problems: [
        ...buttons.filter(el => el.scrollWidth > el.clientWidth + 1).map(el => `clipped button: ${el.getAttribute('aria-label')}`),
        ...buttons.filter(el => { const r = el.getBoundingClientRect(); return r.left < rect.left - 1 || r.right > rect.right + 1; })
          .map(el => `outside header: ${el.getAttribute('aria-label')}`),
        ...values.filter(el => el.getClientRects().length !== 1 || el.scrollWidth > el.clientWidth + 1)
          .map(el => `clipped value: ${el.textContent}`),
      ],
    };
  });
  expect(result.headerOverflow).toBeLessThanOrEqual(1);
  expect(result.problems).toEqual([]);
  expect(Math.max(...result.chipHeights) - Math.min(...result.chipHeights), 'usage chip row height drift').toBeLessThanOrEqual(1);
  expect(result.values).toContain('15%');
  expect(result.values).toContain('$12.48');
  if (width < 768) {
    expect(result.layout).toBe('single');
    expect(result.values).toEqual(['15%', '$12.48']);
    expect(result.headerHeight).toBeGreaterThanOrEqual(48);
  } else {
    expect(result.layout).toBe('rows');
    expect(result.values).toContain('32%');
    expect(result.navHeight).toBeGreaterThanOrEqual(44);
    expect(result.usageHeight).toBeGreaterThanOrEqual(40);
  }
  // At zoom/boundary sizes only the header is required to avoid horizontal
  // overflow: the board is an independently scrollable lane surface.
  expect(result.viewportOverflow, `viewport overflow: ${result.overflowElements.join(', ')}`).toBeLessThanOrEqual(width < 768 ? 1 : 2);
}

async function shot(page: Page, name: string, headerOnly = false): Promise<void> {
  await page.evaluate(() => document.fonts.ready);
  const target = headerOnly ? page.getByTestId('studio-titlebar') : page;
  await target.screenshot({ path: path.join(OUT, `${name}--${BASE}.png`), animations: 'disabled', caret: 'hide' });
}

test('product after-state matrix and expanded usage evidence', async ({ page, devBackend }) => {
  expect(devBackend.baseUrl).toContain('5030');
  page.setDefaultNavigationTimeout(120_000);
  mkdirSync(OUT, { recursive: true });
  checkFixture();
  const pageErrors: string[] = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.clock.setFixedTime(FIXTURE_NOW);
  await page.addInitScript(() => localStorage.setItem('defaultCliType', 'codex'));
  let response = SNAPSHOT;
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }) }));
  await page.route('**/api/usage/cockpit**', route => {
    const workspaceId = new URL(route.request().url()).searchParams.get('workspaceId') || SNAPSHOT.workspaceId;
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...response, workspaceId }) });
  });
  const captures: string[] = [];
  const contrasts: Record<string, unknown> = {};

  await page.setViewportSize({ width: 1728, height: 850 });
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await dismissDevErrorDialog(page);
  await selectDemo(page);
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 850 });
    const sidebar = page.getByTestId('studio-sidebar');
    if (width < 768 && await sidebar.isVisible()) {
      await page.getByTestId('studio-ab-explorer').click();
      await expect(sidebar).toBeHidden();
    } else if (width >= 768 && !await sidebar.isVisible()) {
      await page.getByTestId('studio-ab-explorer').click();
      await expect(sidebar).toBeVisible();
    }
    for (const theme of THEMES) {
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      await geometry(page, width);
      await shot(page, `header-after-${width}-${theme}`, true);
      captures.push(`header-after-${width}-${theme}`);
      await shot(page, `board-after-${width}-${theme}`);
      captures.push(`board-after-${width}-${theme}`);
      if (width === 1728) {
        const chip = page.getByTestId('cockpit-header-usage-row').getByTestId('usage-cli-chip-codex');
        await chip.focus();
        const text = await sampleColours(page, '[data-testid="cockpit-header-usage-row"] [data-testid="usage-cli-chip-codex-weekly"]');
        const focus = await chip.evaluate(el => {
          const s = getComputedStyle(el);
          return { color: s.outlineColor, width: s.outlineWidth, background: getComputedStyle(el.parentElement!).backgroundColor };
        });
        const textRatio = contrastRatio(text.color, text.bg);
        const focusRatio = contrastRatio(focus.color, text.bg);
        expect(textRatio).toBeGreaterThanOrEqual(4.5);
        expect(focusRatio).toBeGreaterThanOrEqual(3);
        expect(parseFloat(focus.width)).toBeGreaterThanOrEqual(2);
        contrasts[theme] = { text: Number(textRatio.toFixed(2)), focus: Number(focusRatio.toFixed(2)) };
      }
    }
    const card = page.locator(CARD).filter({ hasText: 'DEMO-5' }).first();
    await expect(card).toBeVisible();
    await card.click();
    await expect(page.getByTestId('pane-protocol')).toBeVisible();
    for (const theme of THEMES) {
      await setTheme(page, theme);
      await geometry(page, width);
      await shot(page, `detail-after-${width}-${theme}`);
      captures.push(`detail-after-${width}-${theme}`);
    }
    if (width !== WIDTHS[WIDTHS.length - 1]) {
      await page.goBack({ waitUntil: 'domcontentloaded' });
      await expect(page.locator(CARD).first()).toBeVisible({ timeout: 30_000 });
    }
  }

  expect(captures).toHaveLength(18);
  await page.setViewportSize({ width: 1024, height: 850 });
  await setTheme(page, 'dark');
  const chip = page.getByTestId('cockpit-header-usage-row').getByTestId('usage-cli-chip-codex');
  await expect(chip).toHaveAttribute('aria-label', /weekly 15 percent used.*32 percent used/i);
  await chip.click();
  const panel = page.getByTestId('usage-detail-surface');
  await expect(panel).toBeVisible();
  await expect(panel.getByTestId('usage-detail-reset-local').first()).toBeVisible();
  await expect(panel.getByTestId('usage-detail-reset-utc').first()).toBeVisible();
  await shot(page, 'expanded-cli-1024-dark');
  await panel.getByTestId('usage-detail-close').click();
  await expect(chip).toBeFocused();
  const cost = page.getByTestId('cockpit-header-usage-row').getByTestId('usage-cost-chip');
  await cost.click();
  await expect(panel.getByTestId('usage-detail-projects')).toBeVisible();
  await expect(panel.getByTestId('usage-detail-live-runs')).toBeVisible();
  await shot(page, 'expanded-cost-1024-dark');
  await panel.getByTestId('usage-detail-close').click();
  await expect(cost).toBeFocused();

  await page.setViewportSize({ width: 390, height: 850 });
  await cost.click();
  await expect(panel).toHaveAttribute('data-mode', 'sheet');
  await expect(panel).toBeVisible();
  await expect(page.getByRole('dialog', { name: 'Cost and usage' })).toBeVisible();
  await expect(panel).toHaveJSProperty('open', true);
  expect(await panel.evaluate(el => el.matches(':modal'))).toBe(true);
  const sheetControls = panel.locator('a[href]:visible, button:not([disabled]):visible, input:not([disabled]):visible, select:visible, textarea:visible, [tabindex]:not([tabindex="-1"]):visible');
  expect(await sheetControls.count()).toBeGreaterThan(1);
  const firstControl = sheetControls.first();
  const lastControl = sheetControls.last();
  await lastControl.focus();
  await page.keyboard.press('Tab');
  await expect(firstControl, 'Tab wraps from the last sheet control to the first').toBeFocused();
  await page.keyboard.press('Shift+Tab');
  await expect(lastControl, 'Shift+Tab wraps from the first sheet control to the last').toBeFocused();
  await page.getByTestId('studio-project-picker-trigger').focus();
  await expect.poll(() => panel.evaluate(el => el.contains(document.activeElement)),
    { message: 'the modal sheet keeps background controls inert' }).toBe(true);
  await shot(page, 'expanded-cost-390-dark');
  await panel.getByTestId('usage-detail-close').click();
  await expect(cost).toBeFocused();

  // The focused slice specs exercise long labels and large numbers. This gate
  // checks the production shell at each layout boundary and at the 864 CSS-px
  // width produced by a 1728 px window at 200% browser zoom.
  for (const width of [320, 767, 768, 864, 1199, 1200, 1599, 1600]) {
    await page.setViewportSize({ width, height: 850 });
    await geometry(page, width);
  }
  await page.setViewportSize({ width: 1728, height: 850 });
  await page.evaluate(() => { document.documentElement.style.zoom = '200%'; });
  await expect.poll(() => page.getByTestId('studio-titlebar').getAttribute('data-tier')).toBe('tablet');
  await geometry(page, 864);
  await shot(page, 'header-zoom-200-dark', true);
  await page.evaluate(() => { document.documentElement.style.zoom = ''; });
  await page.setViewportSize({ width: 390, height: 850 });
  const touchTargets = await page.getByTestId('studio-titlebar').evaluate(host =>
    [...host.querySelectorAll<HTMLElement>('.cockpit-header__usage button, [data-testid="cockpit-header-more"]')]
      .filter(el => !el.closest('.cockpit-header__measure') && el.getClientRects().length)
      .map(el => ({ name: el.getAttribute('aria-label'), width: el.getBoundingClientRect().width, height: el.getBoundingClientRect().height })));
  for (const target of touchTargets) {
    expect(target.width, `${target.name} touch width`).toBeGreaterThanOrEqual(44);
    expect(target.height, `${target.name} touch height`).toBeGreaterThanOrEqual(44);
  }
  const readMotion = () => page.getByTestId('studio-titlebar').evaluate(host => {
    const chip = host.querySelector<HTMLElement>('[data-testid="usage-cost-chip"]')!;
    const more = host.querySelector<HTMLElement>('[data-testid="cockpit-header-more"]')!;
    return {
      requested: matchMedia('(prefers-reduced-motion: reduce)').matches,
      chipTransition: getComputedStyle(chip).transitionDuration,
      moreTransition: getComputedStyle(more).transitionDuration,
    };
  });
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  const defaultMotion = await readMotion();
  expect(defaultMotion.requested).toBe(false);
  expect(defaultMotion.chipTransition.split(',').some(duration => parseFloat(duration) > 0)).toBe(true);
  expect(defaultMotion.moreTransition.split(',').some(duration => parseFloat(duration) > 0)).toBe(true);
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const motion = await readMotion();
  expect(motion.requested).toBe(true);
  expect(motion.chipTransition.split(',').every(duration => parseFloat(duration) === 0)).toBe(true);
  expect(motion.moreTransition.split(',').every(duration => parseFloat(duration) === 0)).toBe(true);
  await page.getByTestId('cockpit-header-more').click();
  await expect(page.locator('[data-testid^="cockpit-header-more-menu-item-"]').first()).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('cockpit-header-more')).toBeFocused();

  response = { ...SNAPSHOT, cost: { ...SNAPSHOT.cost, coverage: {
    status: 'partial', observedAt: SNAPSHOT.cost.coverage.observedAt, ttlSeconds: 60,
    reason: 'Some model prices are unavailable',
  } } };
  await page.reload({ waitUntil: 'domcontentloaded' });
  const partialCost = page.getByTestId('cockpit-header-usage-row').getByTestId('usage-cost-chip');
  await expect(partialCost).toHaveAttribute('data-state', 'partial');
  await expect(partialCost).toHaveAttribute('aria-label', /partial/i);
  await shot(page, 'state-partial-390-dark');

  expect(pageErrors, 'page errors during capture').toEqual([]);
  writeFileSync(path.join(OUT, 'manifest.json'), JSON.stringify({
    provenance: 'Product Studio UI; isolated ADR-0056 seeded demo workspace; cockpit HTTP response pinned from usage-chips.fixtures.ts; no human sight review',
    browserTimeUtc: new Date(FIXTURE_NOW).toISOString(), widths: WIDTHS, themes: THEMES,
    geometryWidths: [320, 390, 767, 768, 864, 1024, 1199, 1200, 1599, 1600, 1728],
    zoomEvidence: '200% CSS root zoom over a 1728 px viewport; header reflows to tablet tier',
    captures, extra: ['expanded-cli-1024-dark', 'expanded-cost-1024-dark', 'expanded-cost-390-dark', 'header-zoom-200-dark', 'state-partial-390-dark'],
    contrastRatios: contrasts, pageErrors,
    accessibilityChecks: {
      phoneSheet: 'Native modal; forward and reverse Tab wrapped between first and last controls; background focus stayed in the sheet.',
      reducedMotion: { default: defaultMotion, reduced: motion },
      phoneTouchTargetsPx: touchTargets,
    },
    limitations: [
      'Provider quota and USD values are synthetic.',
      'Task status and server timestamps come from the running isolated backend; the demo seed is versioned but host startup may reconcile tasks.',
      'CSS root zoom exercises reflow but is not browser chrome zoom.',
      'Visual layout assertions do not replace human sight review.',
    ],
  }, null, 2) + '\n');
});
