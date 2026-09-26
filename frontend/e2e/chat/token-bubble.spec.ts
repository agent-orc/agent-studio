import { test, expect, Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';

/**
 * Job-card token bubble.
 *
 * Verifies the user-visible contract introduced when the KB size on every
 * card was replaced with a token-spend bubble:
 *   - cards without recorded token activity render no bubble;
 *   - cards with token activity render a colour-tiered bubble showing the
 *     compact total ("2.4k", "850k", "3.1M");
 *   - hovering the bubble reveals a popover with the full breakdown
 *     (input / output / cacheRead / cacheWrite / total / model / last
 *     update) plus a "View workspace timeline" link.
 *
 * The card data is shaped via a `page.route` intercept on
 * `/api/tasks/grouped` so the spec doesn't depend on the watch path
 * carrying real orchestrator activity.
 */

const SHOTS = process.env.JOB_RESULTS_DIR
  ? `${process.env.JOB_RESULTS_DIR}/token-popover-model`
  : 'screenshots/token-bubble';

interface JobInfoStub {
  id: string;
  jobKey: string;
  title: string;
  state: string;
  order: number;
  agent: string;
  createdAt: string;
  watchPath: string;
  projectName: string;
  folderPath: string;
  lastActivity: string;
  sessionName: null;
  model: string | null;
  cliType: string | null;
  useOwnSession: null;
  lastUsage: null;
  execution: null;
  commit: null;
  ownerClientId: string;
  tokenSummary: null | {
    calls: number;
    inputTokens: number;
    outputTokens: number;
    cacheReadTokens: number;
    cacheCreationTokens: number;
    totalTokens: number;
    estimatedApiCostUsd?: number;
    allModelsPriced?: boolean;
    hasModelMismatch?: boolean;
    lastModel: string | null;
    lastUpdate: string | null;
    entries: {
      ts: string;
      model: string | null;
      inputTokens: number;
      outputTokens: number;
      cacheReadTokens: number;
      cacheCreationTokens: number;
      estimatedApiCostUsd?: number;
      modelPriced?: boolean;
      pinnedModel?: string | null;
      modelMismatch?: boolean;
    }[];
  };
}

function jobStub(over: Partial<JobInfoStub>): JobInfoStub {
  const id = over.id ?? 'stub-job';
  return {
    id,
    jobKey: `stub::${id}`,
    title: over.title ?? id,
    state: over.state ?? '2-ready',
    order: over.order ?? 1,
    agent: 'copilot',
    createdAt: '2026-05-05T08:00:00Z',
    watchPath: 'C:/stub',
    projectName: 'stub-project',
    folderPath: 'C:/stub/' + id,
    lastActivity: '2026-05-05T08:00:00Z',
    sessionName: null,
    model: 'claude-sonnet-4-6',
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    ownerClientId: 'local-default',
    tokenSummary: null,
    ...over
  };
}

/**
 * Dismiss the global error dialog if it is mounted. Some startup API
 * calls return shapes the catch-all stub can't perfectly mimic; the app
 * surfaces this as a non-fatal toast that intercepts pointer events. We
 * close it so it can't block the hover test.
 */
async function dismissErrorDialogIfPresent(page: Page): Promise<void> {
  const close = page.getByTestId('error-dialog-close').last();
  if (await close.isVisible().catch(() => false)) {
    await close.click({ force: true }).catch(() => { /* best-effort */ });
  }
}

async function stubGroupedJobs(page: Page, jobs: JobInfoStub[]): Promise<void> {
  // Single route handler that dispatches by URL. Avoids order-of-registration
  // pitfalls (Playwright matches routes in reverse insertion order, so
  // overlapping globs are easy to get wrong). Returns a shape each service
  // can safely consume so the error-dialog overlay never appears and
  // doesn't block pointer events on the bubble.
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const p = url.pathname;
    if (p === '/api/auth/status') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: false, user: null }),
      });
    }
    if (p === '/api/tasks/archive') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: [], total: 0, offset: 0, limit: 50, hasMore: false }),
      });
    }
    if (p === '/api/crash-recovery/pending') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ pending: [] }),
      });
    }
    if (p === '/api/orchestrator/sessions') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ sessions: [] }),
      });
    }
    if (/^\/api\/cli\/[^/]+\/models$/.test(p)) {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ models: [], source: 'fixture', fetchedAt: '2026-05-05T08:00:00Z' }),
      });
    }
    if (p === '/api/clients/local-default/defaults') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ clientId: 'local-default', defaultCliType: null, defaultModel: null }),
      });
    }
    if (p === '/api/environment') {
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
      });
    }
    if (p === '/api/projects/settings') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    }
    if (p === '/api/tasks/grouped') {
      const body = {
        backlog: [],
        preparation: jobs.filter((j) => j.state === '1-preparation'),
        orchestratorPrep: [],
        ready: jobs.filter((j) => j.state === '2-ready'),
        progress: jobs.filter((j) => j.state === '3-progress'),
        failedPickup: [],
        review: jobs.filter((j) => j.state === '4-review'),
        autoReview: [],
        humanReview: [],
        completed: jobs.filter((j) => j.state === '5-completed'),
        archive: jobs.filter((j) => j.state === '6-archive')
      };
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    }
    if (p === '/api/tasks' || p === '/api/tasks/') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(jobs) });
    }
    if (/^\/api\/clients\/[^/]+\/telemetry$/.test(p)) {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ points: [] }) });
    }
    if (p.startsWith('/api/clients')) {
      const list = [{
        id: 'local-default', displayName: 'Local Default', emoji: '🤖', colour: '#64748b', kind: 'human',
        registeredAt: '2026-01-01T00:00:00Z', lastSeenAt: null, tokenBudgetMonthly: null, notes: null
      }];
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(list) });
    }
    if (p === '/api/watch-paths') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
        { name: 'stub-project', path: 'C:/stub', rootPath: 'C:/stub' }
      ]) });
    }
    if (p === '/api/cli/quota') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] }) });
    }
    if (p === '/api/cli/usage') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: new Date().toISOString(), sections: [] }) });
    }
    if (p === '/api/v1/management/remote-hosts') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
    }
    if (p === '/api/v1/management/links'
        || p === '/api/v1/management/provider-refusals'
        || p === '/api/tags'
        || p === '/api/workspaces'
        || p === '/api/projects'
        || p === '/api/git/summary') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
    }
    if (p === '/api/cli/model-migrations') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ version: 'fixture', wikiPath: '', migrations: [] }) });
    }
    if (p === '/api/pipeline/accepted-integration-alert') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ active: false, stalledTaskCount: 0, thresholdMinutes: 30, oldestAcceptedAt: null, observedAt: new Date().toISOString(), items: [] }) });
    }
    if (p === '/api/auto-review/status') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ lastTickAt: null, accept: 0, reissue: 0, escalate: 0, aspectsRun: 0, currentJob: null, currentProject: null, activeJobs: [] }) });
    }
    if (p.startsWith('/api/runner')) {
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) });
    }
    // Catch-all: empty array works for list endpoints, empty object for
    // single-record. Use null to cover both shapes safely; consumers
    // should fall back to defaults when the response is empty.
    return route.fulfill({ status: 200, contentType: 'application/json', body: 'null' });
  });
}

test.describe('Token bubble on job cards', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1,
        tabs: [{ kind: 'board', projectName: '__all__' }],
        activeKey: 'board:__all__',
      }));
    });
  });

  test('cards without token activity render no bubble', async ({ page }) => {
    const quietJob = jobStub({ id: 'quiet-card', title: 'Quiet card no tokens', tokenSummary: null });
    await stubGroupedJobs(page, [quietJob]);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const card = page.locator('[data-testid="task-card"]', { hasText: 'Quiet card no tokens' });
    await expect(card).toBeVisible();
    await expect(card.locator('[data-testid="task-card-token-bubble"]')).toHaveCount(0);
  });

  test('card with tokens shows a bubble; hover reveals the popover', async ({ page }) => {
    const noisyJob = jobStub({
      id: 'noisy-card',
      title: 'Noisy card with tokens',
      tokenSummary: {
        calls: 3,
        inputTokens: 120_000,
        outputTokens: 18_000,
        cacheReadTokens: 250_000,
        cacheCreationTokens: 12_000,
        totalTokens: 400_000,
        estimatedApiCostUsd: 1.25,
        allModelsPriced: true,
        hasModelMismatch: true,
        lastModel: 'GPT-5 Codex',
        lastUpdate: '2026-05-05T08:30:00Z',
        entries: [
          // Each entry is priced at its own timestamp (estimatedApiCostUsd),
          // not today's rate — the popover must show these dated per-run
          // costs, not just one combined estimate.
          { ts: '2026-05-05T08:00:00Z', model: 'GPT-5 Codex', inputTokens: 50_000, outputTokens: 6_000, cacheReadTokens: 100_000, cacheCreationTokens: 4_000, estimatedApiCostUsd: 0.6, modelPriced: true },
          { ts: '2026-05-05T08:15:00Z', model: 'Claude Haiku 4.5', pinnedModel: 'claude-opus-5-5', modelMismatch: true, inputTokens: 40_000, outputTokens: 6_000, cacheReadTokens: 80_000, cacheCreationTokens: 4_000, estimatedApiCostUsd: 0.15, modelPriced: true },
          { ts: '2026-05-05T08:30:00Z', model: 'GPT-5 Codex', inputTokens: 30_000, outputTokens: 6_000, cacheReadTokens: 70_000, cacheCreationTokens: 4_000, estimatedApiCostUsd: 0.5, modelPriced: true }
        ]
      }
    });
    await stubGroupedJobs(page, [noisyJob]);
    await page.route('**/api/tasks/noisy-card/pipeline**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          pipeline: { id: 'default', displayName: 'Default', version: 1, pre: [], core: [], post: [] },
          execution: null,
          cost: {
            steps: [
              { stepId: 'core', kind: 'core', model: 'GPT-5 Codex', modelKnown: true, inputTokens: 80_000, outputTokens: 12_000, cacheReadTokens: 180_000, cacheCreationTokens: 8_000, totalTokens: 280_000, inputCostUsd: 0.8, outputCostUsd: 0.15, cacheReadCostUsd: 0.1, cacheCreationCostUsd: 0.05, costUsd: 1.1 },
              { stepId: 'code-quality', kind: 'aspect', model: 'Claude Haiku 4.5', modelKnown: true, inputTokens: 40_000, outputTokens: 6_000, cacheReadTokens: 70_000, cacheCreationTokens: 4_000, totalTokens: 120_000, inputCostUsd: 0.1, outputCostUsd: 0.03, cacheReadCostUsd: 0.02, cacheCreationCostUsd: 0, costUsd: 0.15 },
            ],
            totalInputTokens: 120_000, totalOutputTokens: 18_000, totalCacheReadTokens: 250_000, totalCacheCreationTokens: 12_000, totalTokens: 400_000,
            totalInputCostUsd: 0.9, totalOutputCostUsd: 0.18, totalCacheReadCostUsd: 0.12, totalCacheCreationCostUsd: 0.05, totalCostUsd: 1.25,
            anyModelUnknown: false,
          },
          config: {},
        }),
      }));

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const card = page.locator('[data-testid="task-card"]', { hasText: 'Noisy card with tokens' });
    await expect(card).toBeVisible();
    await dismissErrorDialogIfPresent(page);

    const bubble = card.locator('[data-testid="task-card-token-bubble"]');
    await expect(bubble).toBeVisible();
    // 400k total -> blue tier (50k <= total < 500k).
    await expect(bubble).toHaveAttribute('data-token-tier', 'blue');
    // Compact label.
    await expect(bubble).toHaveText('400k');

    // Focusing the bubble reveals the popover (focusin on the wrap drives
    // TokenPopoverDirective). Equivalent to hover for the user-visible
    // contract — the popover is keyboard reachable as well — and dodges
    // pointer-events races with any overlay that might appear before the
    // cards land.
    await bubble.focus();
    const popover = page.locator('[data-testid="task-card-token-popover"]');
    await expect(popover).toBeVisible();
    await expect(popover.getByTestId('token-row-input')).toContainText('120k');
    await expect(popover.getByTestId('token-row-output')).toContainText('18k');
    await expect(popover.getByTestId('token-row-cache-read')).toContainText('250k');
    await expect(popover.getByTestId('token-row-cache-write')).toContainText('12k');
    await expect(popover.getByTestId('token-row-total')).toContainText('400k');
    await expect(popover.getByTestId('token-row-model')).toContainText('GPT-5 Codex');
    await expect(popover.getByTestId('token-model-mismatch')).toHaveText('Mismatch');

    // Calm layout: the estimate caveat is a single quiet footnote line with
    // the honest total, not a paragraph. The full disclaimer text (incl.
    // "historical list prices") lives in the tooltip, not inline. Focus
    // (not hover) for the same pointer-events-race reason as the bubble above.
    const footnote = popover.getByTestId('token-cost-tooltip');
    await expect(footnote).toContainText('Total (est.): $1.25');
    await expect(footnote).not.toContainText('historical list prices');
    await footnote.focus();
    const tooltip = page.getByTestId('cac-tooltip');
    await expect(tooltip).toContainText('Estimated - historical list prices');

    // Per-run dated costs: each run is priced at its own timestamp.
    const runs = popover.getByTestId('token-usage-runs');
    await expect(runs).toContainText('Claude Haiku 4.5');
    await expect(runs.getByTestId('token-run-model-mismatch')).toContainText('pinned claude-opus-5-5');
    await expect(runs).toContainText('$0.15');
    await expect(runs).toContainText('$0.60');

    // Breakdown by type (lazy-fetched from the job's pipeline endpoint):
    // the core coding run and the aspect review pass show up as separate rows.
    const byType = popover.getByTestId('token-usage-by-type');
    await expect(byType).toContainText('Core agent work');
    await expect(byType).toContainText('Aspect');

    await expect(popover.getByTestId('token-popover-timeline-link')).toBeVisible();

    // Anti-clipping contract: the directive lifts the panel into the
    // shared body overlay portal, so the card's overflow/content-visibility
    // containment and lane scroll container cannot cut it off.
    const popBox = await popover.boundingBox();
    const vp = page.viewportSize()!;
    expect(popBox).not.toBeNull();
    expect(popBox!.x).toBeGreaterThanOrEqual(0);
    expect(popBox!.y).toBeGreaterThanOrEqual(0);
    expect(popBox!.x + popBox!.width).toBeLessThanOrEqual(vp.width + 1);
    expect(popBox!.y + popBox!.height).toBeLessThanOrEqual(vp.height + 1);

    // Screenshot evidence: bubble + overlay popover in the viewport. A
    // background poll unrelated to tokens can pop the shared error dialog
    // between assertions above and here; clear it so the evidence shot
    // shows the popover, not an incidental toast.
    await dismissErrorDialogIfPresent(page);
    await bubble.focus();
    await expect(popover).toBeVisible();
    mkdirSync(SHOTS, { recursive: true });
    await page.screenshot({ path: `${SHOTS}/card-with-bubble-and-popover.png`, fullPage: false });
  });

  test('tier escalates with spend', async ({ page }) => {
    const small = jobStub({
      id: 'small-spend',
      title: 'Small spend card',
      order: 1,
      tokenSummary: {
        calls: 1,
        inputTokens: 1_000,
        outputTokens: 500,
        cacheReadTokens: 0,
        cacheCreationTokens: 0,
        totalTokens: 1_500,
        lastModel: 'claude-haiku-4-5',
        lastUpdate: '2026-05-05T08:00:00Z',
        entries: [
          { ts: '2026-05-05T08:00:00Z', model: 'claude-haiku-4-5', inputTokens: 1_000, outputTokens: 500, cacheReadTokens: 0, cacheCreationTokens: 0 }
        ]
      }
    });
    const huge = jobStub({
      id: 'huge-spend',
      title: 'Huge spend card',
      order: 2,
      tokenSummary: {
        calls: 1,
        inputTokens: 3_000_000,
        outputTokens: 200_000,
        cacheReadTokens: 3_000_000,
        cacheCreationTokens: 100_000,
        totalTokens: 6_300_000,
        lastModel: 'claude-opus-4-7',
        lastUpdate: '2026-05-05T08:00:00Z',
        entries: [
          { ts: '2026-05-05T08:00:00Z', model: 'claude-opus-4-7', inputTokens: 3_000_000, outputTokens: 200_000, cacheReadTokens: 3_000_000, cacheCreationTokens: 100_000 }
        ]
      }
    });
    await stubGroupedJobs(page, [small, huge]);

    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const smallBubble = page.locator('[data-testid="task-card"]', { hasText: 'Small spend card' })
      .locator('[data-testid="task-card-token-bubble"]');
    await expect(smallBubble).toHaveAttribute('data-token-tier', 'neutral');
    await expect(smallBubble).toHaveText('1.5k');

    const hugeBubble = page.locator('[data-testid="task-card"]', { hasText: 'Huge spend card' })
      .locator('[data-testid="task-card-token-bubble"]');
    await expect(hugeBubble).toHaveAttribute('data-token-tier', 'peach');
    await expect(hugeBubble).toHaveText('6.3M');
  });
});
