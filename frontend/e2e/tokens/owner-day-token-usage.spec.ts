import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import type { TokenAnalysisResponse } from '../../src/app/features/token-analysis/models/token-analysis.model';
import { setTheme } from '../helpers/theme';

// The demo profile boots the real backend with --owner-day. No routes are mocked.
test('owner-day token usage reconciles across workspace, projects, and visible cards', async ({ page, request }) => {
  const get = async (project?: string): Promise<TokenAnalysisResponse> => {
    const query = project ? `&project=${encodeURIComponent(project)}` : '';
    const response = await request.get(`/api/workspace/tokens/analysis?days=7${query}`);
    expect(response.ok()).toBeTruthy();
    return response.json() as Promise<TokenAnalysisResponse>;
  };
  const workspace = await get();
  const app = await get('Demo App');
  const platform = await get('Demo Platform');
  const tokens = (data: TokenAnalysisResponse) => data.cards.reduce((sum, card) => sum + card.totalTokens, 0)
    + data.remainders.reduce((sum, row) => sum + row.totalTokens, 0);
  const cost = (data: TokenAnalysisResponse) => data.cards.reduce((sum, card) => sum + card.costUsd, 0)
    + data.remainders.reduce((sum, row) => sum + row.costUsd, 0);
  expect(app.cards.length).toBeGreaterThan(0);
  expect(platform.cards.length).toBeGreaterThan(0);
  expect(tokens(app)).toBeGreaterThan(0);
  expect(tokens(platform)).toBeGreaterThan(0);
  expect(tokens(workspace)).toBe(tokens(app) + tokens(platform));
  expect(cost(workspace)).toBeCloseTo(cost(app) + cost(platform), 6);
  for (const [name, analysis] of [['Demo App', app], ['Demo Platform', platform]] as const) {
    const response = await request.get(`/api/projects/${encodeURIComponent(name)}/token-usage/summary`);
    expect(response.ok()).toBeTruthy();
    const summary = await response.json() as { last24hTotalTokens: number; last24hCalls: number };
    expect(summary.last24hTotalTokens).toBe(tokens(analysis));
    expect(summary.last24hCalls).toBeGreaterThan(0);
  }
  for (const card of workspace.cards) {
    expect(card.totalTokens).toBe(card.slices.reduce((sum, slice) =>
      sum + slice.input + slice.output + slice.cacheRead + slice.cacheWrite, 0));
    expect(card.costUsd).toBeCloseTo(card.slices.reduce((sum, slice) => sum + slice.costUsd, 0), 6);
    expect(card.costComplete).toBe(card.slices.every((slice) => slice.priced));
  }
  const unpriced = workspace.cards.flatMap((card) => card.slices.filter((slice) => !slice.priced));
  expect(unpriced.length).toBeGreaterThan(0);
  expect(unpriced.some((slice) => slice.model === 'demo-unpriced-model')).toBeTruthy();

  await page.goto('/#/workspace/settings/tokens/analysis');
  await expect(page.getByTestId('token-analysis-view')).toBeVisible();
  await expect(page.getByTestId('token-analysis-empty')).toHaveCount(0);
  const showAll = page.getByTestId('token-analysis-table-more');
  if (await showAll.isVisible()) await showAll.click();
  await expect(page.getByTestId('token-analysis-row-DEMO-6')).toBeVisible();
  await expect(page.getByTestId('token-analysis-row-DEMO-24')).toContainText('*');
  await expect(page.getByTestId('token-analysis-unpriced')).toContainText('demo-unpriced-model');
  await expect(page.getByTestId('token-analysis-distribution-model')).toContainText('no price data');
  await expect(page.getByTestId('token-analysis-cost')).toContainText('*');
  await expect(page.getByTestId('token-analysis-table-total')).not.toHaveText('0');
  await page.getByTestId('token-analysis-unpriced').getByRole('link', { name: 'View unpriced models' }).click();
  const diagnostic = page.getByTestId('unpriced-models');
  await expect(diagnostic).toBeVisible();
  await diagnostic.getByRole('button', { name: 'Show unpriced models' }).click();
  await expect(diagnostic).toContainText('demo-unpriced-model');
  await expect(diagnostic).toContainText('Current workspace week:');
  await expect(diagnostic.locator('table')).toContainText('Unpriced calls');
  await expect(diagnostic.locator('table')).toContainText('Unpriced tokens');
  const results = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'owner-day-token-usage');
  mkdirSync(results, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(results, `workspace--${theme}.png`), fullPage: false });
  }
});
