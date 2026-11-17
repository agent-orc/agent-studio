import { test, expect, type Page } from '../fixtures/dev-backend';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';
import { fixtureDetail, fixtureResponse } from '../../src/app/features/token-analysis/testing/token-analysis.fixtures';

/**
 * AGT-3267 card-centric token analysis. The first journey reads the real
 * backend endpoint; the others pin the analysis response so the screenshots
 * and assertions are deterministic.
 */
test.use({ reducedMotion: 'reduce', trace: 'off', video: 'off' });

const RESULT_DIR = process.env.JOB_RESULTS_DIR
  ? join(process.env.JOB_RESULTS_DIR, 'screenshots')
  : join('test-results', 'token-analysis');
const ROUTE = '/#/workspace/settings/tokens/analysis';

async function open(page: Page, hash = ROUTE): Promise<void> {
  await page.addInitScript(() => { localStorage.setItem('atp.flag.vsCodeLayout', '0'); });
  await page.goto(hash);
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('token-analysis-view')).toBeVisible({ timeout: 30_000 });
}

async function pin(page: Page, analysis = fixtureResponse()): Promise<void> {
  await page.route('**/api/workspace/tokens/analysis**', route => route.fulfill({ json: analysis }));
  await page.route('**/api/projects/*/token-usage/analysis/cards/**', route => route.fulfill({ json: fixtureDetail() }));
}

/**
 * The settings overlay scrolls inside fixed-height ancestors, which clips an
 * element screenshot. Lift those limits so one capture shows the whole view.
 */
async function shoot(page: Page, testId: string, file: string): Promise<void> {
  await page.getByTestId(testId).evaluate(view => {
    for (let el = view.parentElement; el && el !== document.body; el = el.parentElement) {
      el.style.overflow = 'visible';
      el.style.maxHeight = 'none';
      el.style.height = 'auto';
    }
  });
  await page.getByTestId(testId).screenshot({ path: join(RESULT_DIR, file) });
}

async function noHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.getByTestId('token-analysis-view')
    .evaluate(view => view.scrollWidth - view.clientWidth);
  expect(overflow).toBeLessThanOrEqual(2);
}

test('the analysis page reads the real backend endpoint', async ({ page }) => {
  const response = page.waitForResponse(r => r.url().includes('/api/workspace/tokens/analysis'));
  await open(page);
  expect((await response).status()).toBe(200);
  await expect(page.getByTestId('token-usage-nav-analysis')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('token-analysis-stats').or(page.getByTestId('token-analysis-empty')).first()).toBeVisible();
});

test('mixed reasoning level shows priced subtotal and an unpriced model keeps its diagnostic link', async ({ page }) => {
  mkdirSync(RESULT_DIR, { recursive: true });
  const analysis = fixtureResponse();
  const unpriced = analysis.cards[0].slices[1];
  unpriced.model = 'demo-unpriced-model';
  unpriced.level = 'high';
  unpriced.costUsd = 0;
  unpriced.priced = false;
  await pin(page, analysis);
  await page.setViewportSize({ width: 1280, height: 2600 });
  await open(page);

  const high = page.getByTestId('token-analysis-distribution-level').getByRole('row', { name: /^high\b/ });
  await expect(high.getByRole('cell').last()).toContainText('$0.600');
  await expect(high.getByRole('link', { name: 'Incomplete price data; view unpriced models' }))
    .toHaveAttribute('href', '#/workspace/settings/tokens');
  const model = page.getByTestId('token-analysis-distribution-model')
    .getByRole('row', { name: /^demo-unpriced-model\b/ });
  await expect(model.getByRole('link', { name: 'no price data' }))
    .toHaveAttribute('href', '#/workspace/settings/tokens');

  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await shoot(page, 'token-analysis-view', `token-analysis-distribution-mixed-${theme}.png`);
  }
});

test('operator filters cards, opens a drill-down, and the URL restores both', async ({ page }) => {
  mkdirSync(RESULT_DIR, { recursive: true });
  await pin(page);
  await page.setViewportSize({ width: 1280, height: 2600 });
  await open(page);

  await expect(page.getByTestId('token-analysis-total')).toHaveText('2.6K');
  await expect(page.getByTestId('token-analysis-table-total')).toHaveText('2.6K');
  await expect(page.getByTestId('token-analysis-time-chart').locator('[data-incomplete="true"]')).toHaveCount(1);
  await expect(page.getByTestId('token-analysis-quota-table')).toContainText('100K');
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await shoot(page, 'token-analysis-view', `token-analysis-1280-${theme}.png`);
  }

  await page.getByTestId('token-analysis-filter-kind').selectOption('review-aspect');
  await expect(page.getByTestId('token-analysis-total')).toHaveText('400');
  expect(decodeURIComponent(page.url())).toContain('kind=review-aspect');

  await page.getByTestId('token-analysis-row-AGT-1').getByRole('button').click();
  await expect(page.getByTestId('card-token-drilldown-events').locator('li')).toHaveCount(2);
  await expect(page.getByTestId('card-token-drilldown-summary')).toContainText('3 rounds');
  await shoot(page, 'card-token-drilldown', 'token-analysis-drilldown-1280-light.png');

  await page.reload();
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('token-analysis-view')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('token-analysis-filter-kind')).toHaveValue('review-aspect');
  await expect(page.getByTestId('card-token-drilldown-events').locator('li')).toHaveCount(2);

  await page.getByTestId('card-token-drilldown-close').click();
  await expect(page.getByTestId('card-token-drilldown')).toHaveCount(0);
  await page.getByTestId('token-analysis-filter-reset').click();
  await expect(page.getByTestId('token-analysis-total')).toHaveText('2.6K');
  expect(page.url()).not.toContain('ta=');
});

test('the analysis stays legible at 390 px in both themes', async ({ page }) => {
  mkdirSync(RESULT_DIR, { recursive: true });
  await pin(page);
  await page.setViewportSize({ width: 390, height: 5200 });
  await open(page, `${ROUTE}&ta=${encodeURIComponent('card=Studio%2Fjob-1')}`);
  await expect(page.getByTestId('card-token-drilldown-events').locator('li')).toHaveCount(2);
  await noHorizontalOverflow(page);
  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await shoot(page, 'token-analysis-view', `token-analysis-390-${theme}.png`);
  }
});

/** AGT-3805: the cost-first ranking and a table that fits the 1280 px settings page. */
test('at 1280 px the cards rank by cost and card, rounds, tokens and cost fit without horizontal scroll', async ({ page }) => {
  mkdirSync(RESULT_DIR, { recursive: true });
  const analysis = fixtureResponse();
  const long = 'A long card title that names the whole change so the column would grow past the panel edge';
  analysis.cards = analysis.cards.map(c => ({ ...c, title: long }));
  // Most tokens, least cost: cache reads.
  analysis.cards.push({ ...analysis.cards[1], jobId: 'job-3', key: 'AGT-3', inFlight: false, totalTokens: 90_000,
    costUsd: 0.02, slices: [{ ...analysis.cards[1].slices[0], input: 0, output: 0, cacheRead: 90_000, costUsd: 0.02 }] });
  const detail = fixtureDetail();
  detail.events = detail.events.map(e => ({ ...e, label: 'aspect-review-architecture-and-boundaries', host: 'agent-runner-01' }));
  await page.route('**/api/workspace/tokens/analysis**', route => route.fulfill({ json: analysis }));
  await page.route('**/api/projects/*/token-usage/analysis/cards/**', route => route.fulfill({ json: detail }));
  await page.setViewportSize({ width: 1280, height: 2600 });
  await open(page, `${ROUTE}&ta=${encodeURIComponent('card=Studio%2Fjob-1')}`);

  await expect(page.getByTestId('token-analysis-kind-bars-measure')).toContainText('by theoretical cost');
  await expect(page.getByTestId('token-analysis-kind-cost')).toContainText('Coding run');
  const table = page.getByTestId('token-analysis-card-table');
  await expect(table.locator('tbody tr').first()).toHaveAttribute('data-testid', 'token-analysis-row-AGT-1');

  const scroll = table.locator('.tt__scroll');
  const fit = await scroll.evaluate(el => el.scrollWidth - el.clientWidth);
  expect(fit).toBeLessThanOrEqual(1);
  const box = (await scroll.boundingBox())!;
  for (const name of ['Card', 'Rounds', 'Tokens', 'Cost']) {
    const header = (await table.getByRole('columnheader', { name, exact: false }).first().boundingBox())!;
    expect(header.x + header.width, `${name} column inside the panel`).toBeLessThanOrEqual(box.x + box.width + 1);
  }

  // The event text keeps a readable line instead of one word per line.
  const what = page.getByTestId('card-token-drilldown-event-what').first();
  expect((await what.boundingBox())!.width).toBeGreaterThan(260);
  await setTheme(page, 'dark');
  await shoot(page, 'token-analysis-view', 'token-analysis-cost-1280-dark.png');

  await page.getByTestId('token-analysis-sort-tokens').click();
  await expect(table.locator('tbody tr').first()).toHaveAttribute('data-testid', 'token-analysis-row-AGT-3');
  await expect(page.getByTestId('token-analysis-kind-bars-measure')).toContainText('by tokens');
  expect(decodeURIComponent(page.url())).toContain('sort=tokens');
  await setTheme(page, 'light');
  await shoot(page, 'token-analysis-card-table', 'token-analysis-table-tokens-1280-light.png');
});

test('empty and error states explain what to do', async ({ page }) => {
  mkdirSync(RESULT_DIR, { recursive: true });
  let fail = true;
  await page.route('**/api/workspace/tokens/analysis**', route => fail
    ? route.fulfill({ status: 500, body: 'boom' })
    : route.fulfill({ json: { ...fixtureResponse(), cards: [], remainders: [], quota: [] } }));
  await page.setViewportSize({ width: 1280, height: 800 });
  await open(page);

  await expect(page.getByTestId('token-analysis-error')).toContainText('could not be loaded');
  await shoot(page, 'token-analysis-error', 'token-analysis-error.png');
  fail = false;
  await page.getByTestId('token-analysis-retry').click();
  await expect(page.getByTestId('token-analysis-empty')).toContainText('No token usage in this range');
  await shoot(page, 'token-analysis-view', 'token-analysis-empty.png');
});
