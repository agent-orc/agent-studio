import { test, expect, Page } from '@playwright/test';
import { dismissDevErrorDialog } from '../helpers/theme';
import * as fs from 'fs';
import * as path from 'path';

/**
 * AGT-2721 Watcher contingent on Workspace CLI Management.
 *
 * The dedicated per-day and per-week budget sits next to the usage caps. Its
 * point is the backlog number: when the contingent is used up the Watcher keeps
 * detecting and counting and only stops spending, and an operator has to be able
 * to see that difference before deciding to raise the budget.
 *
 * Fully mocked, so the spec needs no backend and no Watcher sweep.
 */

// The production bundle registers an Angular service worker, which serves
// fetches itself and would bypass page routing. Blocking it keeps this
// fully-mocked spec deterministic on the dev and the stable target alike.
test.use({ serviceWorkers: 'block' });

function status(exhausted: boolean) {
  const limits = exhausted
    ? {
        modelCallsPerDay: 0, modelCallsPerWeek: 0,
        tokensPerDay: 0, tokensPerWeek: 0,
        proposalsPerDay: 0, proposalsPerWeek: 0,
        commentsPerDay: 0, commentsPerWeek: 0,
      }
    : {
        modelCallsPerDay: 20, modelCallsPerWeek: 80,
        tokensPerDay: 500000, tokensPerWeek: 2000000,
        proposalsPerDay: 5, proposalsPerWeek: 20,
        commentsPerDay: 20, commentsPerWeek: 80,
      };
  return {
    snapshot: {
      enabled: true,
      lastRunAtUtc: '2026-09-06T20:05:00Z',
      lastRunFailedAtUtc: null,
      sweeps: 12,
      signalsCollected: 64,
      findingsDetected: 8,
      openCases: 8,
      pendingProposals: exhausted ? 0 : 3,
      backlogCases: exhausted ? 8 : 1,
      proposalsCreatedLastRun: exhausted ? 0 : 3,
      commentsAppendedLastRun: 0,
      modelCallsLastRun: exhausted ? 0 : 3,
      resolvedLastRun: 0,
      lastRunElapsedMs: 180,
      lastError: null,
    },
    enabled: true,
    intervalSeconds: 300,
    analysisEnabled: true,
    analysisTier: 'sol-medium',
    detectorClasses: ['repetition', 'contradiction', 'silence', 'drift', 'hygiene'],
    sources: ['bus', 'integration', 'quota-probe'],
    contingent: {
      limits,
      usage: {
        modelCallsDay: exhausted ? 0 : 3,
        modelCallsWeek: exhausted ? 0 : 7,
        tokensDay: exhausted ? 0 : 42000,
        tokensWeek: exhausted ? 0 : 96000,
        proposalsDay: exhausted ? 0 : 3,
        proposalsWeek: exhausted ? 0 : 6,
        commentsDay: 0,
        commentsWeek: exhausted ? 0 : 1,
        costUsdDay: 0,
        costUsdWeek: 0,
        unpricedCallsDay: exhausted ? 0 : 3,
      },
      dayStartUtc: '2026-09-06T00:00:00Z',
      weekStartUtc: '2026-08-31T00:00:00Z',
      exhaustedDimensions: exhausted
        ? ['The Watcher contingent for model calls per day is exhausted (0/0).']
        : [],
      backlogCases: exhausted ? 8 : 1,
      proposalsBlocked: exhausted,
      modelCallsBlocked: exhausted,
      // A call without a catalogue price must never render as $0.00.
      costUsdDayDisplay: exhausted ? '0' : 'unknown',
    },
    stale: false,
    storeAvailable: true,
  };
}

async function mockStudio(page: Page, exhausted: boolean): Promise<void> {
  await page.route('**/update/status', route =>
    route.fulfill({ json: { phase: 'idle', isRunning: false, behindBy: 0 } })
  );
  await page.route('**/hubs/jobs/negotiate**', route =>
    route.fulfill({
      json: {
        connectionId: 'watcher-contingent-e2e',
        connectionToken: 'watcher-contingent-e2e',
        negotiateVersion: 1,
        availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
      },
    })
  );
  await page.routeWebSocket('**/hubs/jobs**', socket => {
    socket.onMessage(message => {
      if (message.toString().includes('"protocol":"json"')) socket.send('{}');
    });
  });
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    const json = (body: unknown) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

    if (url.pathname === '/api/watcher/status') return json(status(exhausted));
    if (url.pathname === '/api/auth/status') {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.pathname === '/api/cli/quota') return json({ snapshots: [], ttlSeconds: 600 });
    if (url.pathname === '/api/cli/quota/caps') return json({ defaultCapPct: 95, caps: {} });
    if (url.pathname === '/api/runner/orchestrator-feed') return json({ entries: [] });
    if (url.pathname === '/api/tasks/grouped') {
      return json({
        backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
        failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
        humanReview: [], escalated: [], completed: [], archive: [],
      });
    }
    if (url.pathname === '/api/tasks') return json([]);
    if (url.pathname === '/api/tasks/archive') return json({ items: [], total: 0 });
    if (url.pathname === '/api/epics/completed/count') return json({ count: 0 });
    if (url.pathname === '/api/orchestrator/sessions') return json({ sessions: [] });
    if (url.pathname === '/api/runner/pickup-gates') return json({ projects: {} });
    if (/\/api\/cli\/[^/]+\/models$/.test(url.pathname)) return json({ models: [], source: 'watcher-e2e' });
    if (url.pathname === '/api/watch-paths') return json([]);
    if (url.pathname === '/api/runner/status') return json({ projects: {} });
    if (url.pathname === '/api/projects' || url.pathname === '/api/workspaces') return json([]);
    if (url.pathname === '/api/epics') return json([]);
    if (url.pathname === '/api/crash-recovery/pending') return json({ pending: [] });
    if (url.pathname.startsWith('/api/v1/management/remote-hosts')) return json([]);
    if (url.pathname === '/api/tags') return json([]);
    if (url.pathname === '/api/clients' || url.pathname === '/api/clients/') return json([]);
    if (/^\/api\/bus\/[^/]+\/messages$/.test(url.pathname)) return json([]);
    return json({});
  });
}

async function openCliManagement(page: Page) {
  await page.setViewportSize({ width: 1440, height: 960 });
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await dismissDevErrorDialog(page);
  await page.getByTestId('status-bar-settings').click();
  await page.getByTestId('workspace-settings-rail-caps').click();
  const strip = page.getByTestId('watcher-contingent');
  await expect(strip).toBeVisible();
  return strip;
}

test.describe('Workspace CLI Management / Watcher contingent', () => {
  test('the contingent sits next to the usage caps with a row per budget dimension', async ({ page }) => {
    await mockStudio(page, false);

    const strip = await openCliManagement(page);

    await expect(page.getByTestId('cli-admin-watcher-contingent')).toBeVisible();
    await expect(strip.getByTestId('watcher-contingent-model-calls-day')).toContainText('3 / 20');
    await expect(strip.getByTestId('watcher-contingent-proposals-day')).toContainText('3 / 5');
    await expect(strip.getByTestId('watcher-contingent-tokens-week')).toContainText('2,000,000');
    await expect(strip.getByTestId('watcher-contingent-backlog')).toContainText('1 cases');
  });

  test('a model call without a catalogue price shows unknown, never a zero cost', async ({ page }) => {
    await mockStudio(page, false);

    const strip = await openCliManagement(page);

    await expect(strip.getByTestId('watcher-contingent-summary')).toContainText('spend today unknown');
    await expect(strip.getByTestId('watcher-contingent-summary')).not.toContainText('$0.00');
  });

  test('an exhausted contingent states that counting continues and shows the backlog', async ({ page }, testInfo) => {
    await mockStudio(page, true);

    const strip = await openCliManagement(page);

    const exhausted = strip.getByTestId('watcher-contingent-exhausted');
    await expect(exhausted).toContainText('keeps detecting and counting');
    await expect(exhausted).toContainText('8');

    const resultsDir = process.env['JOB_RESULTS_DIR'];
    if (resultsDir) {
      fs.mkdirSync(resultsDir, { recursive: true });
      await strip.screenshot({ path: path.join(resultsDir, 'watcher-contingent-exhausted.png') });
      testInfo.attach('watcher-contingent-exhausted', {
        path: path.join(resultsDir, 'watcher-contingent-exhausted.png'),
        contentType: 'image/png',
      });
    }
  });
});
