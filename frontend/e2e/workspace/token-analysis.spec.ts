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
  // Clip by bounding box: an element screenshot waits for "stable", which a
  // freshly lifted 2600 px view can miss on a cold start.
  const box = await page.getByTestId(testId).boundingBox();
  if (!box) throw new Error(`${testId} has no layout box to capture`);
  await page.screenshot({ path: join(RESULT_DIR, file), fullPage: true, clip: box, animations: 'disabled' });
}

async function noHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.getByTestId('token-analysis-view')
    .evaluate(view => view.scrollWidth - view.clientWidth);
  expect(overflow).toBeLessThanOrEqual(2);
}

test('the analysis page reads the real backend endpoint', async ({ page }) => {
  // The first fold after a cold backend start can exceed the 10 s action timeout.
  const response = page.waitForResponse(r => r.url().includes('/api/workspace/tokens/analysis'), { timeout: 45_000 });
  await open(page);
  expect((await response).status()).toBe(200);
  await expect(page.getByTestId('token-usage-nav-analysis')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('token-analysis-stats').or(page.getByTestId('token-analysis-empty')).first()).toBeVisible();
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
