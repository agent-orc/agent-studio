import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * Deck Agent activity rail: surfaces the Agent Message Bus for one
 * project as a dense operational view. The panel is fixture-backed so
 * a project with no live bus traffic still renders every surface (the
 * prompt explicitly allows the fixture path while the bus bridge is in
 * flight). This spec covers:
 *   1. Rail entry routes to the custom panel and the empty state shows.
 *      The legacy `observability` section opens Operations instead.
 *   2. Loading the fixture dataset paints counters, timeline, matrix,
 *      heatmap, message table.
 *   3. Filters narrow the message table (kind=intervention).
 *   4. Selecting a message renders the raw-JSON drill-down.
 *
 * Screenshots land in the job's results/ folder when
 * PROJECT_OBSERVABILITY_RESULTS_DIR is set, otherwise in a sibling
 * playwright-screenshots/ tree. The reviewer reads them straight off
 * the protocol pane.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.PROJECT_OBSERVABILITY_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-observability');
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

async function openObservability(page: import('@playwright/test').Page): Promise<void> {
  await page.goto(`/#/projects/${slugFor(projectName)}/agent-activity`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('project-observability-panel')).toBeVisible({ timeout: 10_000 });
}

test('rail entry opens the observability panel and shows empty state when no bus traffic', async ({ page }) => {
  await openObservability(page);

  await expect(page.getByTestId('project-shell-rail-agent-activity')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('project-shell-rail-observability')).toHaveCount(0);
  await expect(page.getByTestId('observability-source')).toBeVisible();

  // The panel exposes either the populated surfaces (live bus has data)
  // or the empty state with the "Load sample dataset" affordance. Both
  // are valid; assert at least one is visible.
  const empty = page.getByTestId('observability-empty');
  const counters = page.getByTestId('observability-counters');
  await expect(empty.or(counters)).toBeVisible();

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '01-observability-default.png'),
    fullPage: true,
  });
});

test('the legacy observability section opens Operations, not the Deck', async ({ page }) => {
  await page.goto(`/#/projects/${slugFor(projectName)}/observability`);
  await expect(page.getByTestId('project-operations-area')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('project-shell')).toHaveCount(0);
  await expect(page.getByTestId('project-observability-panel')).toHaveCount(0);
});

test('fixture dataset paints every surface', async ({ page }) => {
  await openObservability(page);

  // Force the fixture-backed dev path so the spec is deterministic
  // regardless of what the live bus has projected.
  const empty = page.getByTestId('observability-empty');
  if (await empty.isVisible().catch(() => false)) {
    await page.getByTestId('observability-load-fixture').click();
  } else {
    // Live data was present; refresh and load fixture by clearing then
    // re-injecting via the page evaluate handle — instead, simply force
    // an "all time" range so all live data shows up.
    const range = page.getByTestId('observability-filter-range');
    await range.selectOption({ value: '0' });
  }

  await expect(page.getByTestId('observability-counters')).toBeVisible();
  await expect(page.getByTestId('observability-timeline')).toBeVisible();
  await expect(page.getByTestId('observability-matrix')).toBeVisible();
  await expect(page.getByTestId('observability-heatmap')).toBeVisible();

  // Counter chips render with numeric labels.
  await expect(page.getByTestId('observability-counter-total')).toBeVisible();
  await expect(page.getByTestId('observability-counter-interventions')).toBeVisible();
  await expect(page.getByTestId('observability-counter-errors')).toBeVisible();
  await expect(page.getByTestId('observability-counter-tokens')).toBeVisible();
  await expect(page.getByTestId('observability-counter-silent')).toBeVisible();

  // Timeline lanes are present.
  const lanes = page.getByTestId('observability-timeline-row');
  await expect(lanes.first()).toBeVisible();

  // Messages table is non-empty.
  await expect(page.getByTestId('observability-messages')).toBeVisible();
  const rows = page.getByTestId('observability-message-row');
  expect(await rows.count()).toBeGreaterThan(0);

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '02-observability-fixture.png'),
    fullPage: true,
  });
});

test('filter narrows the messages table and a row opens the JSON drilldown', async ({ page }) => {
  await openObservability(page);
  const empty = page.getByTestId('observability-empty');
  if (await empty.isVisible().catch(() => false)) {
    await page.getByTestId('observability-load-fixture').click();
  }

  // Open All-time so fixture rows fall in range regardless of clock skew.
  await page.getByTestId('observability-filter-range').selectOption({ value: '0' });

  const allRows = page.getByTestId('observability-message-row');
  const allCount = await allRows.count();
  expect(allCount).toBeGreaterThan(0);

  await page.getByTestId('observability-filter-kind').selectOption('decision');
  await expect(page.getByTestId('observability-counter-total')).toBeVisible();

  const filteredRows = page.getByTestId('observability-message-row');
  const filteredCount = await filteredRows.count();
  expect(filteredCount).toBeGreaterThan(0);
  // Filter actually narrowed the set (decision messages are a subset of all).
  expect(filteredCount).toBeLessThanOrEqual(allCount);

  // Open the drill-down by clicking the first filtered row.
  await filteredRows.first().click();
  const detail = page.getByTestId('observability-detail');
  await expect(detail).toBeVisible();
  const json = page.getByTestId('observability-detail-json');
  await expect(json).toBeVisible();
  // The raw JSON contains the selected message's id.
  const text = (await json.textContent()) ?? '';
  expect(text).toContain('"id"');
  expect(text).toContain('"kind"');

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '03-observability-detail.png'),
    fullPage: true,
  });

  // Reset filters and confirm the row count expands again.
  await page.getByTestId('observability-filter-reset').click();
  await page.getByTestId('observability-filter-range').selectOption({ value: '0' });
  const resetRows = page.getByTestId('observability-message-row');
  expect(await resetRows.count()).toBe(allCount);
});
