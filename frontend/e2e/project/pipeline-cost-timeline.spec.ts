import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

/**
 * Project-level pipeline cost-by-step-kind trend (this task, deliverable 3:
 * "a project-level view shows aggregate tokens + cost per step kind with a
 * time trend"). The Token Usage rail grows a "Pipeline cost by step kind"
 * section: a per-kind legend (cost + tokens + total) and a stacked per-day
 * bar trend folded from every task's pipeline-execution.json, priced through
 * the single TokenPricing table.
 *
 * The new GET /token-usage/pipeline-cost endpoint may not be live on every
 * long-running backend, and a real project's day-to-day data is noisy, so
 * this spec mocks the four token-usage reads with deterministic fixtures.
 * That keeps the assertion + screenshot stable while still exercising the
 * real compiled Angular template + SCSS in a browser (which is what catches
 * a binding or class-name typo the prod-only tsc pass cannot see). All reads,
 * no writes - safe to run against the shared dev stack.
 */

interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR || process.env.PROJECT_SHELL_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return path.join(fromEnv, 'pipeline-cost');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pipeline-cost');
})();

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

const DAYS = ['2026-05-28', '2026-05-29', '2026-05-30', '2026-05-31', '2026-06-01', '2026-06-02'];

function fakePipelineCost(project: string) {
  const core = [60000, 90000, 0, 120000, 80000, 140000];
  const aspect = [12000, 18000, 0, 22000, 16000, 30000];
  const tool = [0, 4000, 0, 6000, 3000, 8000];
  const priceCore = 0.000003;   // ~Sonnet-ish blended, illustrative
  const priceAspect = 0.000001; // ~Haiku-ish
  const priceTool = 0.0000005;
  const cell = (tokens: number, price: number) => ({ day: '', totalTokens: tokens, costUsd: +(tokens * price).toFixed(6) });
  const series = (kind: string, arr: number[], price: number) => {
    const cells = arr.map((t, i) => ({ ...cell(t, price), day: DAYS[i] }));
    const totalTokens = arr.reduce((a, b) => a + b, 0);
    const totalCostUsd = +cells.reduce((a, c) => a + c.costUsd, 0).toFixed(6);
    return { kind, totalTokens, totalCostUsd, anyModelUnknown: false, cells };
  };
  const kinds = [
    series('core', core, priceCore),
    series('aspect', aspect, priceAspect),
    series('tool', tool, priceTool),
  ];
  const totalTokens = kinds.reduce((a, k) => a + k.totalTokens, 0);
  const totalCostUsd = +kinds.reduce((a, k) => a + k.totalCostUsd, 0).toFixed(6);
  return {
    project,
    days: DAYS,
    windowDays: 30,
    kinds,
    totalTokens,
    totalCostUsd,
    decidingCostUsd: kinds.filter(kind => kind.kind !== 'core').reduce((sum, kind) => sum + kind.totalCostUsd, 0),
    coreCostUsd: kinds[0].totalCostUsd,
    decidingUnpricedRuns: 0,
    coreUnpricedRuns: 0,
    steps: [
      { stepId: 'core-agent-run', kind: 'core', totalTokens: kinds[0].totalTokens, totalCostUsd: kinds[0].totalCostUsd, anyModelUnknown: false, occurrences: 5 },
      { stepId: 'post-orchestrator-decision', kind: 'orchestrator', totalTokens: 5000, totalCostUsd: 0.03, anyModelUnknown: false, occurrences: 7 },
    ],
    anyModelUnknown: false,
    taskCount: 5,
    hasData: true,
    fetchedAt: '2026-06-02T00:00:00Z',
  };
}

function fakeSummary(project: string) {
  return {
    project,
    hasData: true,
    lifetimeTotalTokens: 540000,
    lifetimeJobTokens: 410000,
    lifetimeSupportingTokens: 60000,
    lifetimeOrchestratorTokens: 70000,
    lifetimeCalls: 42,
    last24hTotalTokens: 178000,
    last24hJobTokens: 140000,
    last24hSupportingTokens: 8000,
    last24hOrchestratorTokens: 30000,
    last24hCalls: 11,
    firstActivity: '2026-05-28T08:00:00Z',
    lastActivity: '2026-06-02T09:00:00Z',
    fetchedAt: '2026-06-02T00:00:00Z',
    disclaimer: 'Cost is theoretical (per-model price table); CLI subscriptions make the real bill zero.',
  };
}

function fakeHeatmap(project: string) {
  return {
    project,
    hasData: true,
    days: DAYS,
    jobs: [
      {
        jobId: 'task-alpha', title: 'Alpha pipeline task', state: '6-completed', category: 'job', total: 220000,
        calls: 6, cells: DAYS.map((d, i) => ({ day: d, total: [20000, 40000, 0, 60000, 40000, 60000][i] })),
      },
      {
        jobId: 'task-beta', title: 'Beta refactor', state: '5-human-review', category: 'job', total: 120000,
        calls: 4, cells: DAYS.map((d, i) => ({ day: d, total: [10000, 20000, 0, 30000, 20000, 40000][i] })),
      },
    ],
  };
}

function fakeExpensive(project: string) {
  return {
    project,
    jobs: [
      { jobId: 'task-alpha', title: 'Alpha pipeline task', category: 'job', totalTokens: 220000, calls: 6 },
      { jobId: 'task-beta', title: 'Beta refactor', category: 'job', totalTokens: 120000, calls: 4 },
    ],
  };
}

let projectName = '';
let projectSlug = '';

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  const preferred = paths.find(p => /playwright/i.test(p.name)) ?? paths[0];
  expect(preferred, 'needs at least one watched project').toBeTruthy();
  projectName = preferred.name;
  projectSlug = slugFor(projectName);
});

test('token usage: pipeline cost-by-step-kind section renders legend + stacked trend', async ({ page }) => {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/token-usage/pipeline-cost*', r => r.fulfill(json(fakePipelineCost(projectName))));
  await page.route('**/token-usage/summary', r => r.fulfill(json(fakeSummary(projectName))));
  await page.route('**/token-usage/heatmap*', r => r.fulfill(json(fakeHeatmap(projectName))));
  await page.route('**/token-usage/expensive*', r => r.fulfill(json(fakeExpensive(projectName))));

  await page.goto(`/#/projects/${projectSlug}/token-usage`);

  const panel = page.getByTestId('project-token-usage-panel');
  await expect(panel).toBeVisible({ timeout: 10_000 });

  const section = page.getByTestId('token-usage-pipeline-cost');
  await expect(section).toBeVisible();
  // Data path, not the empty hint.
  await expect(page.getByTestId('pipeline-cost-empty')).toHaveCount(0);
  await expect(page.getByTestId('pipeline-decision-vs-core')).toContainText('Deciding');
  await expect(page.getByTestId('pipeline-step-occurrences')).toContainText('post-orchestrator-decision · 7 runs');

  const legend = page.getByTestId('pipeline-cost-legend');
  await expect(legend).toBeVisible();
  await expect(page.getByTestId('pipeline-cost-legend-core')).toBeVisible();
  await expect(page.getByTestId('pipeline-cost-legend-aspect')).toBeVisible();
  await expect(page.getByTestId('pipeline-cost-legend-tool')).toBeVisible();
  await expect(page.getByTestId('pipeline-cost-total')).toBeVisible();

  // One stacked column per day in the window.
  const cols = page.getByTestId('pipeline-cost-bars').getByRole('listitem');
  await expect(cols).toHaveCount(DAYS.length);
  // The busiest day carries multiple stacked segments.
  await expect(cols.last().getByTestId('pipeline-cost-segment')).toHaveCount(3);

  await section.scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-pipeline-cost-trend.png'), fullPage: true });

  // A tighter shot of just the new section for the task write-up.
  await section.screenshot({ path: path.join(SCREENSHOT_DIR, '02-pipeline-cost-section.png') });
});
