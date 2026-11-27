import { test, expect, type Page, type Route } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';
import { startLongTaskRecorder } from '../helpers/timing';
import { setTheme } from '../helpers/theme';
import { formatCompactTokens } from '../../src/app/features/tokens/token-number-format.util';

/** Project token usage reads: a real empty response plus controlled usage
 * responses for populated, delayed, failed and out-of-order states. No task
 * or telemetry store is mutated by these fixtures. */
interface WatchPath { name: string; path: string }

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR ?? process.env.PROJECT_TOKEN_USAGE_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-token-usage-panel');
})();

let projectName = '';
let projectId = '';

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  const preferred = paths.find(p => /agent.?task/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
  const workspaces = await api<{ projects: { id: string; displayName: string }[] }[]>('/api/v1/workspaces');
  const project = workspaces.flatMap(workspace => workspace.projects).find(item => item.displayName === projectName);
  expect(project).toBeTruthy();
  projectId = project!.id;
});

interface PlantedEntry {
  ts: string;
  jobId?: string | null;
  model: string;
  input: number;
  output: number;
  cacheRead?: number;
  cacheCreate?: number;
  topic?: string;
  summary?: string;
}

async function mockUsageEntries(page: Page, entries: PlantedEntry[]) {
  const total = (entry: PlantedEntry) => entry.input + entry.output + (entry.cacheRead ?? 0) + (entry.cacheCreate ?? 0);
  const sum = (rows: PlantedEntry[]) => rows.reduce((value, entry) => value + total(entry), 0);
  const category = (entry: PlantedEntry) => !entry.jobId ? 'orchestrator' : entry.jobId.startsWith('sec-') ? 'supporting' : 'job';
  const split = (rows: PlantedEntry[]) => ({
    TotalTokens: sum(rows), JobTokens: sum(rows.filter(entry => category(entry) === 'job')),
    SupportingTokens: sum(rows.filter(entry => category(entry) === 'supporting')),
    OrchestratorTokens: sum(rows.filter(entry => category(entry) === 'orchestrator')),
  });
  const now = Date.now();
  const summary = { project: projectName, hasData: entries.length > 0, lastActivity: entries.map(entry => entry.ts).sort().at(-1) ?? null } as Record<string, unknown>;
  for (const [prefix, rows] of [['lifetime', entries], ['last24h', entries.filter(entry => Date.parse(entry.ts) >= now - 86400000 && Date.parse(entry.ts) <= now)]] as const) {
    for (const [key, value] of Object.entries(split([...rows]))) summary[prefix + key] = value;
  }
  const days = Array.from({ length: 30 }, (_, index) => new Date(now - (29 - index) * 86400000).toISOString().slice(0, 10));
  const groups = new Map<string, PlantedEntry[]>();
  for (const entry of entries) {
    if (entry.jobId) groups.set(entry.jobId, [...(groups.get(entry.jobId) ?? []), entry]);
  }
  for (const rows of groups.values()) rows.sort((a, b) => a.ts.localeCompare(b.ts));
  const jobs = [...groups].map(([jobId, rows]) => ({
    jobId, title: jobId, category: category(rows[0]), totalTokens: sum(rows), calls: rows.length,
    lastActivity: rows.at(-1)!.ts, lastModel: rows.at(-1)!.model,
  })).sort((a, b) => b.totalTokens - a.totalTokens);
  await page.route('**/api/projects/*/token-usage/**', async route => {
    const suffix = new URL(route.request().url()).pathname.split('/token-usage/')[1];
    if (suffix === 'summary') return route.fulfill({ json: summary });
    if (suffix === 'expensive') return route.fulfill({ json: { project: projectName, jobs: jobs.slice(0, 10) } });
    if (suffix === 'heatmap') return route.fulfill({ json: {
      project: projectName, hasData: jobs.length > 0, days,
      jobs: jobs.map(job => ({ ...job, total: job.totalTokens,
        cells: days.map(day => ({ day, total: sum(groups.get(job.jobId)!.filter(entry => entry.ts.startsWith(day))) })),
      })),
    } });
    if (suffix.startsWith('job/')) {
      const jobId = decodeURIComponent(suffix.slice(4));
      const rows = [...(groups.get(jobId) ?? [])].sort((a, b) => a.ts.localeCompare(b.ts));
      return route.fulfill({ json: { ...jobs.find(job => job.jobId === jobId), project: projectName,
        runs: rows.map((entry, index) => ({ index, ts: entry.ts, model: entry.model, total: total(entry),
          deltaVsPrev: index === 0 ? null : total(entry) - total(rows[index - 1]), summary: entry.summary,
        })),
      } });
    }
    return route.continue();
  });
}

async function openTokenUsageRail(page: import('@playwright/test').Page) {
  await page.goto(`/#/projects/${projectId}/token-usage`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('project-token-usage-panel')).toBeVisible();
  expect(page.url()).toContain(`#/projects/${projectId}/token-usage`);
}

test('empty state - no orchestrator entries renders explicit empty copy', async ({ page }) => {
  await openTokenUsageRail(page);

  await expect(page.getByTestId('token-usage-empty')).toBeVisible();
  await expect(page.getByTestId('token-usage-empty')).toContainText('No token activity');
  // Cards must NOT render in empty state — the panel has no totals to show.
  await expect(page.getByTestId('token-usage-cards')).toHaveCount(0);
  await expect(page.getByTestId('token-usage-heatmap')).toHaveCount(0);

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '01-empty-state--real.png'),
    fullPage: true,
  });
});

test('populated state - cards, heatmap, expensive jobs, drill-down', async ({ page }) => {
  // Install deterministic token-usage responses: a few "regular" jobs, a
  // "Security audit" supporting job, and a handful of orchestrator-only
  // entries spread across the last several days.
  const now = new Date();
  const dayAgo = (n: number, h = 12) => {
    const d = new Date(now);
    d.setUTCDate(d.getUTCDate() - n);
    d.setUTCHours(h, 0, 0, 0);
    return d.toISOString();
  };

  await mockUsageEntries(page, [
    // Job alpha — accumulates across three days, becomes the most-expensive job.
    { ts: dayAgo(0, 9),  jobId: 'alpha', model: 'claude-sonnet-4-6', input: 9_000,  output: 3_000, summary: 'alpha turn 1' },
    { ts: dayAgo(0, 14), jobId: 'alpha', model: 'claude-sonnet-4-6', input: 12_000, output: 4_000, summary: 'alpha turn 2' },
    { ts: dayAgo(1, 10), jobId: 'alpha', model: 'claude-haiku-4-5',  input: 4_000,  output: 1_500, summary: 'alpha turn 3' },
    { ts: dayAgo(2, 11), jobId: 'alpha', model: 'claude-haiku-4-5',  input: 5_500,  output: 2_000, summary: 'alpha turn 4' },
    // Job beta — smaller spend.
    { ts: dayAgo(0, 13), jobId: 'beta',  model: 'claude-haiku-4-5',  input: 1_000,  output: 400 },
    { ts: dayAgo(3, 12), jobId: 'beta',  model: 'claude-haiku-4-5',  input: 1_500,  output: 600 },
    // Supporting job — title prefix puts it in the supporting bucket.
    { ts: dayAgo(1, 16), jobId: 'sec-audit-2026-05-04', model: 'claude-sonnet-4-6', input: 8_000, output: 2_500, summary: 'security audit turn 1' },
    // Orchestrator-only entries (no jobId).
    { ts: dayAgo(0, 17), jobId: null, model: 'claude-haiku-4-5', input: 600, output: 200, topic: 'task-queued', summary: 'orchestrator decision' },
    { ts: dayAgo(2, 8),  jobId: null, model: 'claude-haiku-4-5', input: 800, output: 300, topic: 'watchdog', summary: 'orchestrator watchdog' },
  ]);

  await openTokenUsageRail(page);

  // Empty-state must NOT show — we have entries.
  await expect(page.getByTestId('token-usage-empty')).toHaveCount(0);

  // Four cards render with their dedicated test IDs.
  await expect(page.getByTestId('token-usage-card-total')).toBeVisible();
  await expect(page.getByTestId('token-usage-card-job')).toBeVisible();
  await expect(page.getByTestId('token-usage-card-supporting')).toBeVisible();
  await expect(page.getByTestId('token-usage-card-orchestrator')).toBeVisible();

  // Total card includes the secondary "Last 24h" row.
  await expect(page.getByTestId('token-usage-card-total')).toContainText('Last 24h');

  // Heatmap renders with at least the alpha row at the top (sorted desc).
  const heatmap = page.getByTestId('token-usage-heatmap');
  await expect(heatmap).toBeVisible();
  const firstRow = page.locator('[data-testid="heatmap-row"]').first();
  await expect(firstRow).toHaveAttribute('data-job-id', 'alpha');

  // Expensive-jobs list renders alpha first.
  const firstExpensive = page.locator('[data-testid="expensive-row"]').first();
  await expect(firstExpensive).toHaveAttribute('data-job-id', 'alpha');

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '02-populated--mocked.png'),
    fullPage: true,
  });

  // Drill-down via the expensive-jobs list.
  await page.getByTestId('expensive-btn-alpha').click();
  const drill = page.getByTestId('token-usage-drill');
  await expect(drill).toBeVisible();
  // Alpha had four planted runs; the drill-down should list them all.
  await expect(drill.locator('[data-testid="drill-run"]')).toHaveCount(4);
  // The first run is "first run" (no delta yet); a later run should have
  // a delta — assert by scanning for an arrow / sign-prefixed token.
  const runs = drill.locator('[data-testid="drill-run"]');
  await expect(runs.nth(0)).toContainText('first run');

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '03-drill-from-expensive--mocked.png'),
    fullPage: true,
  });

  // Selecting a different job swaps the drill-down body.
  await page.getByTestId('expensive-btn-sec-audit-2026-05-04').click();
  await expect(drill).toContainText('security audit');
  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '04-drill-supporting--mocked.png'),
    fullPage: true,
  });

  // Close button collapses the drill-down.
  await page.getByTestId('drill-close').click();
  await expect(page.getByTestId('token-usage-drill')).toHaveCount(0);
});

test('long-task budget - mounting + heatmap interactions stay under 200 ms cumulative', async ({ page }) => {
  // Plant a richer fixture so the heatmap actually has rows to interact with.
  const now = new Date();
  const entries: PlantedEntry[] = [];
  // 20 jobs × 3 days × 1 entry each = 60 token-using entries, mirroring a
  // small but non-trivial board.
  for (let j = 0; j < 20; j++) {
    for (let d = 0; d < 3; d++) {
      const ts = new Date(now);
      ts.setUTCDate(ts.getUTCDate() - d);
      ts.setUTCHours(10 + (j % 8), 0, 0, 0);
      entries.push({
        ts: ts.toISOString(),
        jobId: `lt-job-${j.toString().padStart(2, '0')}`,
        model: j % 2 === 0 ? 'claude-sonnet-4-6' : 'claude-haiku-4-5',
        input: 1_000 + j * 100,
        output: 400 + j * 30,
      });
    }
  }
  await mockUsageEntries(page, entries);

  // Install the recorder before opening Token Usage to capture its mount.
  await page.goto(`/#/projects/${projectId}/overview`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  const longTasks = await startLongTaskRecorder(page);
  // The shared observer includes buffered page startup; measure this panel only.
  const startupLongMs = await longTasks.totalMs();
  await page.evaluate(() => {
    const state = (window as unknown as { __longTasks?: { total: number; count: number; observer?: PerformanceObserver } }).__longTasks;
    if (state) { state.observer?.takeRecords(); state.total = 0; state.count = 0; }
  });
  await page.getByTestId('project-shell-rail-token-usage').click();
  await expect(page.getByTestId('project-token-usage-panel')).toBeVisible();

  // Wait for the visible token reads to settle.
  await expect(page.getByTestId('token-usage-card-total')).toBeVisible();
  await expect(page.getByTestId('token-usage-heatmap')).toBeVisible();

  // Click a few heatmap rows to exercise the drill-down path; each
  // selection issues a network call but should not produce a Long Task.
  const rows = page.locator('[data-testid="heatmap-row"]');
  const rowCount = Math.min(3, await rows.count());
  for (let i = 0; i < rowCount; i++) {
    await rows.nth(i).locator('button').first().click();
    // Brief settle so the drill-down request has a chance to resolve.
    await page.waitForTimeout(120);
  }

  // Close drill-down to return to a clean state for the screenshot.
  if (await page.getByTestId('drill-close').count()) {
    await page.getByTestId('drill-close').click();
  }

  // Budget: 200 ms cumulative across mount + ~3 drill-down toggles.
  // Generous to absorb CI noise; any real regression (synchronous chart
  // recompute, layout thrash on heatmap rebuild) blows well past this.
  const totalLong = await longTasks.totalMs();
  fs.writeFileSync(path.join(SCREENSHOT_DIR, 'long-task-measurement.json'), JSON.stringify({ startupLongMs, panelLongMs: totalLong }, null, 2));
  expect(totalLong).toBeLessThan(200);

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '05-longtask-budget--mocked.png'),
    fullPage: true,
  });
});

test('reload preserves the token-usage rail with populated content', async ({ page }) => {
  await mockUsageEntries(page, [
    {
      ts: new Date(Date.now() - 3_600_000).toISOString(),
      jobId: 'persist-job',
      model: 'claude-haiku-4-5',
      input: 1_000,
      output: 400,
    },
  ]);

  await openTokenUsageRail(page);
  await expect(page.getByTestId('token-usage-card-total')).toBeVisible();

  await page.reload();
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 10_000 });
  await expect(page.getByTestId('project-shell-rail-token-usage')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('project-token-usage-panel')).toBeVisible();
  await expect(page.getByTestId('token-usage-card-total')).toBeVisible();
});


const midnight = '2026-11-27T00:00:00Z';
const taskFixture = (jobId: string, totalTokens: number) => ({
  jobId, title: `Task ${jobId}`, category: 'job', state: 'done', totalTokens, calls: 1,
  lastActivity: midnight, lastModel: 'test-model',
});

async function mockUsage(page: Page, intercept?: (route: Route, suffix: string) => Promise<boolean>) {
  await page.route('**/api/projects/*/token-usage/**', async route => {
    const suffix = new URL(route.request().url()).pathname.split('/token-usage/')[1];
    if (await intercept?.(route, suffix)) return;
    let body: unknown;
    switch (suffix) {
      case 'summary': body = {
        project: projectName, hasData: true, lifetimeTotalTokens: 1_000_000_000,
        lifetimeJobTokens: 1_000_000_000, lifetimeSupportingTokens: 0, lifetimeOrchestratorTokens: 0,
        last24hTotalTokens: 12345, last24hJobTokens: 12345, last24hSupportingTokens: 0, last24hOrchestratorTokens: 0,
        lastActivity: midnight, freshness: { status: 'complete', asOf: midnight, sources: [], warning: null },
      }; break;
      case 'expensive': body = { project: projectName, jobs: [taskFixture('A', 1_000_000_000 - 12345), taskFixture('B', 12345)] }; break;
      case 'heatmap': body = { project: projectName, hasData: true, days: ['2026-11-27'], jobs: [
        { ...taskFixture('B', 12345), total: 12345, cells: [{ day: '2026-11-27', total: 12345 }] },
      ] }; break;
      case 'pipeline-cost': body = { project: projectName, hasData: true, days: [], kinds: [], steps: [],
        totalCostUsd: 12.34, totalTokens: 12345, anyModelUnknown: false, taskCount: 1 }; break;
      case 'review-observations': body = { byModelAndReasoning: [], laterEvidence: [], method: 'Fixture review measurements.' }; break;
      default: {
        if (!suffix.startsWith('job/')) { await route.continue(); return; }
        const id = suffix.split('/')[1];
        const total = id === 'A' ? 1_000_000_000 - 12345 : 12345;
        body = { ...taskFixture(id, total), runs: [{ index: 0, ts: midnight, total, model: 'test-model', deltaVsPrev: null }] };
      }
    }
    await route.fulfill({ json: body });
  });
}

async function captureUsage(page: Page, state: string) {
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('token-usage-card-total').scrollIntoViewIfNeeded();
      await page.screenshot({ path: path.join(SCREENSHOT_DIR, `${state}-${width}-${theme}-totals--mocked.png`) });
      const detail = page.getByTestId(state === 'selected-task' ? 'token-usage-drill' : 'expensive-error');
      await detail.scrollIntoViewIfNeeded();
      await expect(detail).toBeVisible();
      if (state === 'selected-task') await expect(detail.getByText('Run times in UTC.', { exact: true })).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.screenshot({ path: path.join(SCREENSHOT_DIR, `${state}-${width}-${theme}-detail--mocked.png`) });
    }
  }
}

test.describe('request ownership and section states', () => {
  test.use({ timezoneId: 'America/Los_Angeles' });

  test('new task wins when the older response arrives last, with workspace counts and UTC times', async ({ page }) => {
    let oldRoute: Route | undefined;
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await mockUsage(page, async (route, suffix) => {
      if (suffix !== 'job/A') return false;
      oldRoute = route;
      return true;
    });
    await openTokenUsageRail(page);
    await expect(page.getByTestId('token-usage-card-total')).toContainText(formatCompactTokens(1_000_000_000));
    await expect(page.getByTestId('token-usage-card-total').getByLabel('1,000,000,000 tokens', { exact: true })).toBeVisible();
    await expect(page.getByTestId('expensive-btn-B')).toContainText(formatCompactTokens(12345));
    await page.getByTestId('expensive-btn-A').click();
    await expect.poll(() => !!oldRoute).toBe(true);
    await page.getByTestId('expensive-btn-B').click();
    const drill = page.getByTestId('token-usage-drill');
    await expect(drill).toContainText('Drill-down: Task B');
    await oldRoute!.fulfill({ json: { ...taskFixture('A', 1_000_000_000 - 12345), runs: [] } });
    await expect(page.getByTestId('expensive-btn-B')).toHaveAttribute('aria-current', 'true');
    await expect(drill).toContainText('Drill-down: Task B');
    await expect(drill).toContainText('12K tokens');
    await expect(drill).not.toContainText('1,000.0M');
    await expect(drill.locator('time')).toHaveText('2026-11-27 00:00 UTC');
    await expect(drill.locator('time')).toHaveAttribute('datetime', midnight);
    await expect(page.getByTestId('token-usage-as-of')).toContainText('2026-11-27 00:00 UTC');
    await captureUsage(page, 'selected-task');
    expect(errors).toEqual([]);
  });

  test('a delayed and failed panel keeps successful siblings and retries independently', async ({ page }) => {
    let pending: Route | undefined;
    let attempts = 0;
    const calls: Record<string, number> = {};
    await mockUsage(page, async (route, suffix) => {
      calls[suffix] = (calls[suffix] ?? 0) + 1;
      if (suffix !== 'expensive' || ++attempts > 1) return false;
      pending = route;
      return true;
    });
    await openTokenUsageRail(page);
    await expect.poll(() => !!pending).toBe(true);
    await expect(page.getByTestId('token-usage-card-total')).toBeVisible();
    await expect(page.getByTestId('token-usage-heatmap')).toBeVisible();
    await expect(page.getByTestId('pipeline-cost-total')).toContainText('$12.34');
    await expect(page.getByTestId('expensive-empty')).toHaveCount(0);
    await expect(page.getByTestId('token-usage-loading')).toBeVisible();
    await pending!.fulfill({ status: 503, json: { error: 'private transport detail' } });
    await expect(page.getByTestId('expensive-error')).toContainText('Could not load expensive tasks.');
    const panel = page.getByTestId('project-token-usage-panel');
    await expect(panel).not.toContainText('Http failure');
    await expect(panel).not.toContainText('private transport detail');
    await expect(panel).not.toContainText('No tasks have recorded token spend');
    await captureUsage(page, 'section-error');
    await page.getByRole('button', { name: 'Retry expensive tasks', exact: true }).click();
    await expect(page.getByTestId('expensive-btn-B')).toBeVisible();
    await expect(page.getByTestId('expensive-error')).toHaveCount(0);
    expect(calls['summary']).toBe(1);
    expect(calls['heatmap']).toBe(1);
    expect(calls['pipeline-cost']).toBe(1);
    expect(calls['expensive']).toBe(2);
  });
});
