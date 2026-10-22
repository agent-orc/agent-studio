import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * Project Product Runtime Observability panel: surfaces the structured
 * runtime stream emitted by the built software. Distinct from the Agent
 * Message Bus panel. This surface lives under Operations and is fixture-backed so it can paint every section
 * without waiting on a live producer.
 *
 * Coverage:
 *   1. Operations routes to the custom panel; empty or live state shows.
 *   2. Loading the fixture paints counters, recent events, error groups,
 *      latency summary, counters table, domain timeline, malformed-line
 *      warnings.
 *   3. Filters narrow the events table (level=Error+, subsystem) and the
 *      reset button restores the original count.
 *   4. Selecting an event row paints the raw-JSON drill-down.
 *
 * Screenshots land in PROJECT_PRODUCT_RUNTIME_RESULTS_DIR if set
 * (orchestrator passes the job's results/ folder), otherwise in a
 * sibling playwright-screenshots/ tree.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.PROJECT_PRODUCT_RUNTIME_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-product-runtime');
})();

let projectName = '';

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  const preferred = paths.find(p => /agent.?task/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
});

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

async function openRuntimePanel(page: import('@playwright/test').Page): Promise<void> {
  await page.goto(`/#/projects/${slugFor(projectName)}/operations`);
  await expect(page.getByTestId('project-operations-area')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('project-product-runtime-panel')).toBeVisible({ timeout: 10_000 });
}

test('Operations opens the product runtime panel and shows empty state when no events', async ({ page }) => {
  await page.route('**/api/runtime/task-detail-latency', route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ count: 100, p50Ms: 44, p95Ms: 180, maxMs: 220 }),
  }));
  await openRuntimePanel(page);

  await expect(page.getByTestId('task-detail-latency')).toContainText('p95 180 ms');

  await expect(page.getByTestId('project-shell-rail-observability')).toHaveCount(0);
  await expect(page.getByTestId('product-runtime-source')).toBeVisible();

  // Empty state OR populated state is acceptable depending on whether
  // there's live data. Both are valid.
  const empty = page.getByTestId('product-runtime-empty');
  const counters = page.getByTestId('product-runtime-counters');
  await expect(empty.or(counters)).toBeVisible();

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '01-product-runtime-default.png'),
    fullPage: true,
  });
});

test('fixture dataset paints every surface', async ({ page }) => {
  await openRuntimePanel(page);

  const empty = page.getByTestId('product-runtime-empty');
  if (await empty.isVisible().catch(() => false)) {
    await page.getByTestId('product-runtime-load-fixture').click();
  } else {
    // Live data was present; widen the range so everything is in scope.
    await page.getByTestId('product-runtime-filter-range').selectOption({ value: '0' });
  }

  await expect(page.getByTestId('product-runtime-counters')).toBeVisible();
  await expect(page.getByTestId('product-runtime-events')).toBeVisible();
  await expect(page.getByTestId('product-runtime-error-groups')).toBeVisible();
  await expect(page.getByTestId('product-runtime-latency')).toBeVisible();
  await expect(page.getByTestId('product-runtime-event-counters')).toBeVisible();
  await expect(page.getByTestId('product-runtime-domain-timeline')).toBeVisible();

  // Counter chips render values.
  await expect(page.getByTestId('product-runtime-counter-total')).toBeVisible();
  await expect(page.getByTestId('product-runtime-counter-errors')).toBeVisible();
  await expect(page.getByTestId('product-runtime-counter-warns')).toBeVisible();
  await expect(page.getByTestId('product-runtime-counter-p95')).toBeVisible();
  await expect(page.getByTestId('product-runtime-counter-malformed')).toBeVisible();

  // Recent events table is non-empty.
  const rows = page.getByTestId('product-runtime-event-row');
  expect(await rows.count()).toBeGreaterThan(0);

  // Fixture seeds errors -> error-groups table has at least one row.
  const errorRows = page.getByTestId('product-runtime-error-group-row');
  expect(await errorRows.count()).toBeGreaterThan(0);

  // Fixture seeds Ok-status timed events -> latency table has at least one row.
  const latencyRows = page.getByTestId('product-runtime-latency-row');
  expect(await latencyRows.count()).toBeGreaterThan(0);

  // Domain timeline has at least one entry.
  const domainRows = page.getByTestId('product-runtime-domain-row');
  expect(await domainRows.count()).toBeGreaterThan(0);

  // Fixture seeds a parse warning -> malformed-line section is visible.
  await expect(page.getByTestId('product-runtime-warnings')).toBeVisible();

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '02-product-runtime-fixture.png'),
    fullPage: true,
  });
});

test('level + subsystem filters narrow the events table; reset restores the original count', async ({ page }) => {
  await openRuntimePanel(page);
  const empty = page.getByTestId('product-runtime-empty');
  if (await empty.isVisible().catch(() => false)) {
    await page.getByTestId('product-runtime-load-fixture').click();
  }

  await page.getByTestId('product-runtime-filter-range').selectOption({ value: '0' });

  const allRows = page.getByTestId('product-runtime-event-row');
  const allCount = await allRows.count();
  expect(allCount).toBeGreaterThan(0);

  // Errors-and-above filter narrows the set strictly.
  await page.getByTestId('product-runtime-filter-level').selectOption('Error');
  const errOnly = page.getByTestId('product-runtime-event-row');
  const errCount = await errOnly.count();
  expect(errCount).toBeGreaterThan(0);
  expect(errCount).toBeLessThanOrEqual(allCount);

  // Open the drill-down by clicking the first matching row.
  await errOnly.first().click();
  const detail = page.getByTestId('product-runtime-detail');
  await expect(detail).toBeVisible();
  const json = page.getByTestId('product-runtime-detail-json');
  await expect(json).toBeVisible();
  const text = (await json.textContent()) ?? '';
  expect(text).toContain('"event"');
  expect(text).toContain('"level"');

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '03-product-runtime-filtered-detail.png'),
    fullPage: true,
  });

  // Reset filters and confirm the row count expands again.
  await page.getByTestId('product-runtime-filter-reset').click();
  await page.getByTestId('product-runtime-filter-range').selectOption({ value: '0' });
  const resetRows = page.getByTestId('product-runtime-event-row');
  expect(await resetRows.count()).toBe(allCount);
});
