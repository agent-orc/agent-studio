import { test, expect, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { contrastRatio } from '../helpers/contrast';
import { setTheme, dismissDevErrorDialog, sampleColours } from '../helpers/theme';

/**
 * Workspace token timeline view (`#/workspace/tokens`). One central
 * timeline of orchestrator token spend across every watched project,
 * stacked per project. The page mounts the
 * `<app-workspace-token-timeline>` component inside an overlay; the
 * usage-hover-panel header carries a deep link.
 *
 * This spec stubs `/api/workspace/tokens/timeline` so the assertions do
 * not depend on whatever the live workspace happens to have done in the
 * last 24 hours; CI runs see real cells, hover popovers, and a populated
 * legend regardless of the underlying state.
 *
 * Asserts:
 * - The page renders when navigated via the `#/workspace/tokens` hash.
 * - The window toggle (1h / 6h / 24h / 7d) re-issues the timeline call
 *   with the right params and re-renders the chart.
 * - The project legend toggles a project off and back on.
 * - Hovering a chart segment reveals the cell-detail popover.
 */

const SCREENSHOT_DIR = process.env.OVERLAY_SHOT_DIR ?? 'test-results';

interface FakeCell {
  project: string;
  bucketStart: string;
  bucketEnd: string;
  calls: number;
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  total: number;
  dollars: number | null;
  allModelsPriced: boolean;
  agentTokens: number;
  supportingTokens: number;
  orchestratorTokens: number;
}

interface FakeProject {
  project: string;
  calls: number;
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  total: number;
  dollars: number | null;
  allModelsPriced: boolean;
  peakBucketStart: string;
  peakBucketTotal: number;
  lastActivity: string;
  agentTokens: number;
  supportingTokens: number;
  orchestratorTokens: number;
}

function buildFakeTimeline(windowHours: number, bucketMinutes: number) {
  const now = Date.now();
  const bucketMs = bucketMinutes * 60 * 1000;
  const windowEnd = Math.floor(now / bucketMs) * bucketMs;
  const windowStart = windowEnd - windowHours * 60 * 60 * 1000;
  const bucketCount = Math.round((windowEnd - windowStart) / bucketMs);

  const projects = ['alpha', 'bravo', 'charlie'];
  const cells: FakeCell[] = [];
  const totals: Record<string, FakeProject> = {};

  for (const p of projects) {
    totals[p] = {
      project: p,
      calls: 0,
      input: 0,
      output: 0,
      cacheRead: 0,
      cacheWrite: 0,
      total: 0,
      dollars: 0,
      allModelsPriced: true,
      peakBucketStart: new Date(windowEnd).toISOString(),
      peakBucketTotal: 0,
      lastActivity: new Date(windowEnd).toISOString(),
      agentTokens: 0,
      supportingTokens: 0,
      orchestratorTokens: 0,
    };
  }

  // Spread cells across roughly half the buckets, so the chart has
  // visible bars to hover but is not visually noisy.
  for (let i = 0; i < bucketCount; i++) {
    const bucketStart = new Date(windowStart + i * bucketMs).toISOString();
    const bucketEnd = new Date(windowStart + (i + 1) * bucketMs).toISOString();
    if (i % 2 === 0) {
      for (const p of projects) {
        const seed = (i + 1) * (projects.indexOf(p) + 1);
        const input = 1000 * seed;
        const output = 100 * seed;
        const total = input + output;
        // Split each cell into the three categories so the composition bar,
        // legend chips, and table columns have real data to reconcile.
        // Agent runs dominate (70%), supporting 10%, orchestrator 20%.
        const agentTokens = Math.round(total * 0.7);
        const supportingTokens = Math.round(total * 0.1);
        const orchestratorTokens = total - agentTokens - supportingTokens;
        cells.push({
          project: p,
          bucketStart,
          bucketEnd,
          calls: 1,
          input,
          output,
          cacheRead: 0,
          cacheWrite: 0,
          total,
          dollars: 0.001 * seed,
          allModelsPriced: true,
          agentTokens,
          supportingTokens,
          orchestratorTokens,
        });
        totals[p].calls += 1;
        totals[p].input += input;
        totals[p].output += output;
        totals[p].total += total;
        totals[p].agentTokens += agentTokens;
        totals[p].supportingTokens += supportingTokens;
        totals[p].orchestratorTokens += orchestratorTokens;
        totals[p].dollars = (totals[p].dollars ?? 0) + 0.001 * seed;
        if (total > totals[p].peakBucketTotal) {
          totals[p].peakBucketTotal = total;
          totals[p].peakBucketStart = bucketStart;
        }
        totals[p].lastActivity = bucketEnd;
      }
    }
  }

  return {
    windowStart: new Date(windowStart).toISOString(),
    windowEnd: new Date(windowEnd).toISOString(),
    windowHours,
    bucketMinutes,
    bucketCount,
    cells,
    projects: Object.values(totals).sort((a, b) => b.total - a.total),
    betterCandidateUsage: [{
      project: 'bravo',
      weekStart: '2026-09-07',
      weekEnd: '2026-09-14',
      calls: 4,
      tokens: 128_400,
      costUsd: 2.18,
      allModelsPriced: true,
    }],
    fetchedAt: new Date().toISOString(),
    disclaimer: 'Theoretical API cost based on Anthropic\'s published rates. Your CLI subscription is billed separately.',
  };
}

async function stubTimeline(page: Page) {
  await page.route('**/api/workspace/tokens/timeline*', async (route) => {
    const url = new URL(route.request().url());
    const windowHours = Number.parseInt(url.searchParams.get('windowHours') ?? '24', 10);
    const bucketMinutes = Number.parseInt(url.searchParams.get('bucketMinutes') ?? '60', 10);
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(buildFakeTimeline(windowHours, bucketMinutes)),
    });
  });
}

/**
 * Stub the noisy background endpoints the app polls on boot so the
 * spec runs even when only the dev frontend is up. The point of the
 * spec is the timeline view, not the rest of the shell.
 */
async function stubBackgroundApis(page: Page) {
  const empty = (body: unknown) => async (route: import('@playwright/test').Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/auth/status', empty({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }));
  await page.route('**/api/tasks', empty([]));
  await page.route('**/api/tasks/grouped', empty({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] }));
  await page.route('**/api/watch-paths', empty([]));
  await page.route('**/api/runner/status', empty({ projects: {} }));
  await page.route('**/api/runner/token-summary-aggregate*', empty({
    projects: 0, orchestratorEntries: 0, orchestratorLlmCalls: 0,
    totalInputTokens: 0, totalOutputTokens: 0, totalCacheReadTokens: 0, totalCacheCreationTokens: 0,
    estimatedApiCostUsd: 0, allModelsPriced: false, byModel: [], byProject: [],
    fetchedAt: new Date().toISOString(), disclaimer: 'stubbed',
  }));
  await page.route('**/api/cli/quota', empty({ ttlMs: 600_000, snapshots: [] }));
  await page.route('**/api/cli/usage', empty({ entries: [] }));
  await page.route('**/api/dev-tools/flags', empty({ updateStableEnabled: false, deleteE2EJobsEnabled: false }));
  await page.route('**/api/clients', empty([]));
}

test.describe('Workspace token timeline', () => {
  test.beforeEach(async ({ page }) => {
    mkdirSync(SCREENSHOT_DIR, { recursive: true });
    await page.setViewportSize({ width: 1600, height: 900 });
    await stubBackgroundApis(page);
    await stubTimeline(page);
    await page.goto('http://localhost:4010/#/workspace/tokens');
    await page.waitForLoadState('domcontentloaded');
  });

  test('opens the timeline, toggles window + project, hover popover renders', async ({ page }) => {
    const overlay = page.getByTestId('workspace-tokens-overlay');
    await expect(overlay).toBeVisible({ timeout: 5_000 });

    const view = page.getByTestId('workspace-token-timeline');
    await expect(view).toBeVisible();
    await expect(page.getByTestId('wtt-chart')).toBeVisible();
    await dismissDevErrorDialog(page);

    // The default 24 h window should render cells from the stubbed payload.
    await expect(page.getByTestId('wtt-win-24h')).toHaveClass(/wtt__win-btn--active/);
    const segments = page.getByTestId(/^wtt-seg-/);
    await expect(segments.first()).toBeVisible({ timeout: 3_000 });
    const initialBars = await page.getByTestId(/^wtt-bar-/).count();
    expect(initialBars).toBeGreaterThan(0);

    // Window toggle: flip to 6 h. The button must light up active and
    // the chart must re-render against the new bucket size.
    await page.getByTestId('wtt-win-6h').click();
    await expect(page.getByTestId('wtt-win-6h')).toHaveClass(/wtt__win-btn--active/);
    await expect(page.getByTestId('wtt-win-24h')).not.toHaveClass(/wtt__win-btn--active/);
    await expect(page.getByTestId(/^wtt-seg-/).first()).toBeVisible({ timeout: 3_000 });

    // Back to 24h for the rest of the assertions and screenshot.
    await page.getByTestId('wtt-win-24h').click();
    await expect(page.getByTestId('wtt-win-24h')).toHaveClass(/wtt__win-btn--active/);

    // Project legend toggle: click the 'alpha' chip off, all alpha
    // segments disappear; click again, they come back.
    const alphaChip = page.getByTestId('wtt-legend-alpha');
    await expect(alphaChip).toBeVisible();
    const alphaSegmentsBefore = await page.getByTestId('wtt-seg-alpha').count();
    expect(alphaSegmentsBefore).toBeGreaterThan(0);

    await alphaChip.click();
    await expect(alphaChip).toHaveClass(/wtt__chip--off/);
    await expect(page.getByTestId('wtt-seg-alpha')).toHaveCount(0);

    await alphaChip.click();
    await expect(alphaChip).not.toHaveClass(/wtt__chip--off/);
    await expect(page.getByTestId('wtt-seg-alpha').first()).toBeVisible();

    // Hover a segment - the popover with the cell's full record renders.
    const seg = page.getByTestId('wtt-seg-bravo').first();
    await seg.hover();
    const popover = page.getByTestId('wtt-popover');
    await expect(popover).toBeVisible({ timeout: 2_000 });
    await expect(popover).toContainText('bravo');
    await expect(popover).toContainText('input');
    await expect(popover).toContainText('output');

    // Category composition strip: the three category chips + total are
    // present and the orchestrator share is broken out separately.
    await expect(page.getByTestId('wtt-composition')).toBeVisible();
    await expect(page.getByTestId('wtt-cat-agent')).toContainText('Agent');
    await expect(page.getByTestId('wtt-cat-supporting')).toContainText('Supporting');
    await expect(page.getByTestId('wtt-cat-orchestrator')).toContainText('Orchestrator');
    await expect(page.getByTestId('wtt-cat-total')).toContainText('Total');

    // Per-project summary table is populated, with the Agent / Supporting /
    // Orchestrator columns and a folded Total footer row (Summen-invariante).
    const table = page.getByTestId('wtt-table');
    await expect(table).toBeVisible();
    await expect(table).toContainText('alpha');
    await expect(table).toContainText('bravo');
    await expect(table).toContainText('charlie');
    await expect(table).toContainText('Orchestrator');
    await expect(page.getByTestId('wtt-table-total')).toBeVisible();
    await expect(page.getByTestId('wtt-table-total')).toContainText('Total');

    const candidateReport = page.getByTestId('better-candidate-usage-report');
    await expect(candidateReport).toBeVisible();
    await expect(candidateReport).toContainText('bravo');
    await expect(candidateReport).toContainText('128K tokens');
    await expect(candidateReport).toContainText('$2.18');

    // Capture the visible state for the task report.
    await page.screenshot({
      path: `${SCREENSHOT_DIR}/workspace-token-timeline-24h--mocked.png`,
      fullPage: false,
    });
    await view.screenshot({
      path: `${SCREENSHOT_DIR}/workspace-token-timeline-24h-closeup--mocked.png`,
    });
  });

  // The CLI-usage timeline overlay used fixed dark-theme colours and washed
  // out on the light theme. After the Tier-2 token conversion its chrome must
  // clear WCAG AA on BOTH themes; the SVG chart's flip is shown by the shots.
  for (const theme of ['dark', 'light'] as const) {
    test(`timeline chrome stays legible (${theme} theme)`, async ({ page }) => {
      await setTheme(page, theme);
      const view = page.getByTestId('workspace-token-timeline');
      await expect(view).toBeVisible({ timeout: 5_000 });
      await expect(page.getByTestId('wtt-chart')).toBeVisible();
      await dismissDevErrorDialog(page);

      const samples: { what: string; selector: string }[] = [
        { what: 'title', selector: '.wtt__title' },
        { what: 'subtitle', selector: '.wtt__sub' },
        { what: 'active window button', selector: '.wtt__win-btn--active' },
        { what: 'legend chip', selector: '.wtt__chip' },
        { what: 'table cell', selector: '.wtt__tab td' },
      ];
      for (const { what, selector } of samples) {
        const { color, bg } = await sampleColours(page, selector);
        const ratio = contrastRatio(color, bg);
        expect(
          ratio,
          `${what} contrast ${ratio.toFixed(2)} (${color} on ${bg}) [${theme}]`,
        ).toBeGreaterThanOrEqual(4.5);
      }

      await view.screenshot({ path: join(SCREENSHOT_DIR, `workspace-token-timeline-${theme}.png`) });
    });
  }
});
