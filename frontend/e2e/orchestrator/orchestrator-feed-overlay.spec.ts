import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme, dismissDevErrorDialog, sampleColours, type Theme } from '../helpers/theme';
import { contrastRatio } from '../helpers/contrast';

/**
 * Orchestrator-feed overlay regression (ASS-693 ticket): the overlay that
 * shows the global orchestrator "brain" plus the per-project feed had a
 * broken column layout, dark-only hex colours that washed out on the light
 * shell, no visible separation between the global and project scopes, and
 * no deep-link anchor (a reload/bookmark could not reproduce the open feed).
 *
 * This spec locks the four fixes against a fully mocked backend so it is
 * deterministic and needs no live data:
 *   1. Deep-link: navigating to `#/project/<slug>/feed` reproduces the open
 *      overlay once watch-paths resolve the slug.
 *   2. Scope separation: both the "Global scope" badge and the "Project
 *      scope" chip render and read their distinct labels.
 *   3. Layout: at a wide viewport the three feed panes (filters / stream /
 *      detail) all lay out side-by-side with non-zero width.
 *   4. Contrast: the feed's readable text clears WCAG AA on BOTH the dark
 *      (Mocha) and light shells — the regression that motivated the ticket.
 * Light + dark screenshots are dropped into the job results/ folder.
 */

const PROJECT = 'Runbook';

const SESSION = {
  sessionId: 'orch-sess-1',
  model: 'claude-opus-4-8',
  bootedAt: '2026-06-04T08:00:00Z',
  bootPromptPreview: 'You are the global orchestrator across all watched projects.',
  bootReplyPreview: 'Understood. Monitoring every watched project for stalls and drift.',
  cumulativeInputTokens: 128_400,
  cumulativeOutputTokens: 22_310,
  cumulativeCacheReadTokens: 510_000,
  cumulativeCacheCreationTokens: 64_000,
  calls: 37,
  lastUsedAt: '2026-06-04T09:42:00Z',
  lastError: null,
};

const LOG_ENTRIES = [
  {
    ts: new Date(Date.now() - 55 * 60_000).toISOString(),
    kind: 'observation',
    topic: 'board/scan',
    summary: 'Scanned 7 lanes; 2 tasks idle in review for >2h.',
    reasoning: null,
    jobId: null,
    tokenUsage: { model: 'claude-haiku-4-5', thinkingLevel: 'medium', inputTokens: 1200, outputTokens: 340, cacheReadTokens: 8000, cacheCreationTokens: 0 },
  },
  {
    ts: new Date(Date.now() - 42 * 60_000).toISOString(),
    kind: 'decision',
    topic: 'budget/switch',
    summary: 'Switch auth-refactor from Opus to Sonnet to preserve the five-hour window.',
    reasoning: 'The diff touches session-token storage which legal flagged; auto-accept policy excludes compliance-sensitive paths.',
    jobId: 'task-auth-refactor-42',
    watchPath: 'C:/Projects/Runbook',
    tokenUsage: { model: 'claude-opus-4-8', thinkingLevel: 'high', inputTokens: 9400, outputTokens: 1820, cacheReadTokens: 41000, cacheCreationTokens: 2200 },
  },
  {
    ts: new Date(Date.now() - 31 * 60_000).toISOString(),
    kind: 'action',
    topic: 'followup/queue',
    summary: 'Queued a Steer follow-up asking the agent to add an integration test before re-review.',
    reasoning: null,
    jobId: 'task-auth-refactor-42',
    tokenUsage: { model: 'claude-opus-4-8', inputTokens: 5100, outputTokens: 640, cacheReadTokens: 22000, cacheCreationTokens: 0 },
  },
  {
    ts: new Date(Date.now() - 20 * 60_000).toISOString(),
    kind: 'intervention',
    topic: 'watchdog/kill',
    summary: 'Killed a runaway run that exceeded the per-task token budget twice in a row.',
    reasoning: 'Two consecutive budget overruns with no commit; watchdog policy kills to protect the quota.',
    jobId: 'task-flaky-99',
    tokenUsage: null,
  },
];

const GLOBAL_LOG_ENTRIES = LOG_ENTRIES.map((entry, index) => ({
  ...entry,
  project: index === 3 ? 'Agent Task Processor' : PROJECT,
}));

const EMPTY_GROUPED = {
  archive: [], autoReview: [], backlog: [], codeNotComplete: [], completed: [],
  failedPickup: [], humanReview: [], orchestratorPrep: [], preparation: [], progress: [], ready: [], review: [],
};

async function mockBackend(page: Page): Promise<void> {
  await page.route('**/update/status', route => route.fulfill({
    json: { phase: 'idle', isRunning: false, behindBy: 0 },
  }));
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill({
    json: {
      connectionId: 'orchestrator-feed-overlay-e2e',
      connectionToken: 'orchestrator-feed-overlay-e2e',
      negotiateVersion: 1,
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
    },
  }));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (message.toString().includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  // Broad fallback FIRST so it has the lowest precedence; specific routes
  // registered afterwards win. Keeps a backend-less `ng serve` from spraying
  // proxy ECONNREFUSED errors that would pollute the screenshot. Anything
  // that looks like a collection gets `[]`; everything else gets `{}`.
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    const list = /\/(watch-paths|tasks|clients|workspaces|tags|models)\b/.test(url);
    return route.fulfill({ status: 200, contentType: 'application/json', body: list ? '[]' : '{}' });
  });

  // The board's `laneGroups` computed iterates each lane, so the grouped
  // endpoint must return the full lane-keyed shape (empty arrays) or the
  // app throws `jobs is not iterable` and the dev error dialog buries the UI.
  await page.route('**/api/v1/studio/board', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(EMPTY_GROUPED) }),
  );
  await page.route('**/api/tasks/archive**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], total: 0 }) }),
  );
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }),
  );
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }),
  );
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      ttlSeconds: 600,
      snapshots: [{ cliType: 'claude', fetchedAt: new Date().toISOString(), plan: 'Max', source: 'probe', error: null, windows: [
        { label: '5h', usedPct: 68, used: null, limit: null, unit: null, resetAt: new Date(Date.now() + 90 * 60_000).toISOString(), resetLabel: 'in 1h 30m' },
        { label: '7d', usedPct: 42, used: null, limit: null, unit: null, resetAt: new Date(Date.now() + 4 * 86400_000).toISOString(), resetLabel: 'in 4 days' },
      ] }],
    }) }),
  );

  await page.route('**/api/runner/token-summary-aggregate**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      projects: 2, orchestratorEntries: 4, orchestratorLlmCalls: 3,
      totalInputTokens: 15700, totalOutputTokens: 2800, totalCacheReadTokens: 71000, totalCacheCreationTokens: 2200,
      estimatedApiCostUsd: 1.24, allModelsPriced: true, byProject: [], fetchedAt: new Date().toISOString(), disclaimer: 'Estimated list pricing.',
      byModel: [{ model: 'claude-opus-4-8', calls: 2, inputTokens: 14500, outputTokens: 2460, cacheReadTokens: 63000, cacheCreationTokens: 2200, estimatedApiCostUsd: 1.1, modelPriced: true }],
    }) }),
  );
  await page.route('**/api/workspace/tokens/timeline**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      windowStart: new Date(Date.now() - 86400_000).toISOString(), windowEnd: new Date().toISOString(), windowHours: 24,
      bucketMinutes: 60, bucketCount: 24, projects: [], fetchedAt: new Date().toISOString(), disclaimer: 'Captured usage.',
      cells: [{ project: PROJECT, bucketStart: new Date(Date.now() - 40 * 60_000).toISOString(), bucketEnd: new Date().toISOString(), calls: 3, input: 15700, output: 2800, cacheRead: 71000, cacheWrite: 2200, total: 91700, dollars: 1.24, allModelsPriced: true, agentTokens: 0, supportingTokens: 0, orchestratorTokens: 91700 }],
    }) }),
  );

  await page.route('**/api/watch-paths', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: PROJECT, path: 'C:/Projects/Runbook', rootPath: 'C:/Projects/Runbook' },
        { name: 'Agent Task Processor', path: 'C:/Projects/agent-taskboard', rootPath: 'C:/Projects/agent-taskboard' },
      ]),
    }),
  );
  await page.route('**/api/v1/workspaces', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/v1/projects', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/bus/*/messages**', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/v1/management/remote-hosts', route =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );

  // StatusBar.runningCount does `Object.values(status.projects)`, so the
  // runner-status snapshot must carry a `projects` object.
  await page.route('**/api/v1/studio/runner/status', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }),
  );

  await page.route('**/api/runner/global/orchestrator-session', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ project: 'global', session: SESSION }) }),
  );

  await page.route('**/api/runner/*/orchestrator-log', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ project: PROJECT, entries: LOG_ENTRIES }) }),
  );

  await page.route('**/api/runner/orchestrator-feed', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ entries: GLOBAL_LOG_ENTRIES }) }),
  );

  await page.route('**/api/runner/*/token-summary', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project: PROJECT,
        orchestratorEntries: LOG_ENTRIES.length,
        orchestratorLlmCalls: 4,
        totalInputTokens: 20_800,
        totalOutputTokens: 3_640,
        totalCacheReadTokens: 71_000,
        totalCacheCreationTokens: 4_400,
        estimatedApiCostUsd: 0.42,
        allModelsPriced: true,
        byModel: [],
        disclaimer: 'Estimated from list prices.',
      }),
    }),
  );
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR
  ? join(process.env.JOB_RESULTS_DIR, 'orch-feed-overlay')
  : join(process.cwd(), 'test-results', 'orch-feed-overlay');

test('workspace orchestrator feed: Activity opens it, filters, layout + contrast hold on both themes', async ({ page }) => {
  mkdirSync(RESULTS_DIR, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 960 });
  await mockBackend(page);

  // Open the workspace Activity surface from its primary navigation entry.
  await page.goto('/');
  await dismissDevErrorDialog(page);
  await page.getByTestId('studio-ab-activity').click();

  const feed = page.getByTestId('orchestrator-feed');
  await expect(feed).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
  await expect(feed).toBeVisible();

  // Requirement #3: workspace and global-session scopes are explicit.
  const projectScope = page.getByTestId('orchestrator-feed-scope');
  const globalScope = page.getByTestId('global-orchestrator-scope');
  await expect(projectScope).toBeVisible();
  await expect(projectScope).toHaveText(/workspace scope/i);
  await expect(globalScope).toBeVisible();
  await expect(globalScope).toHaveText(/scope all projects/i);

  // Feed actually rendered the mocked entries.
  const entries = page.getByTestId('orchestrator-feed-entry');
  await expect(entries.first()).toBeVisible();
  // All activity is the entry default, in one workspace-wide stream.
  expect(await entries.count()).toBe(4);
  await page.getByTestId('orchestrator-feed-filters').getByRole('button', { name: 'Runbook', exact: true }).click();
  await expect(entries).toHaveCount(3);
  await page.getByTestId('feed-project-all').click();
  await page.getByTestId('feed-kind-all').click();
  await expect(entries).toHaveCount(LOG_ENTRIES.length);

  // Requirement #1 — three-pane layout lays out side by side (non-zero,
  // left-to-right) at a wide viewport, not collapsed/overlapping.
  const [filters, stream, detail] = await Promise.all([
    page.locator('.orch-feed__filters').boundingBox(),
    page.getByTestId('orchestrator-feed-stream').boundingBox(),
    page.getByTestId('orchestrator-feed-detail').boundingBox(),
  ]);
  if (!filters || !stream || !detail) throw new Error('feed panes missing bounding boxes');
  for (const box of [filters, stream, detail]) expect(box.width).toBeGreaterThan(40);
  expect(stream.x).toBeGreaterThan(filters.x + filters.width - 4);
  expect(detail.x).toBeGreaterThan(stream.x + stream.width - 4);

  // Requirement #2 — contrast on BOTH themes. Sample the readable text the
  // ticket called out and assert it clears WCAG AA, then drop a screenshot.
  const themes: Theme[] = ['dark', 'light'];
  for (const theme of themes) {
    await setTheme(page, theme);
    await dismissDevErrorDialog(page);
    await page.waitForTimeout(150);

    // (selector, nth, minRatio). Body text -> 4.5; bold uppercase pills are
    // small but decorative scope markers -> 3.0 (still comfortably legible).
    const probes: [string, number, number][] = [
      ['.orch-feed__title', 0, 4.5],
      ['.orch-feed__sub', 0, 4.5],
      ['.orch-feed__refresh', 0, 4.5],
      ['[data-testid="orchestrator-entry-summary"]', 0, 4.5],
      ['[data-testid="orchestrator-feed-scope"]', 0, 3.0],
      ['[data-testid="global-orchestrator-scope"]', 0, 3.0],
      ['[data-testid="global-orchestrator-status"]', 0, 4.5],
    ];
    for (const [selector, nth, min] of probes) {
      const { color, bg } = await sampleColours(page, selector, nth);
      const ratio = contrastRatio(color, bg);
      expect(ratio, `${theme} · ${selector} (${color} on ${bg})`).toBeGreaterThanOrEqual(min);
    }

    await feed.screenshot({
      path: join(RESULTS_DIR, `orch-feed-${theme}--mocked.png`),
    });
  }

  await page.getByTestId('orchestrator-view-load').click();
  const load = page.getByTestId('load-distribution');
  await expect(load).toBeVisible();
  await expect(page.getByTestId('load-quota-windows')).toContainText('68% used');
  await expect(page.getByTestId('load-model-efficiency')).toContainText('claude-opus-4-8');
  await expect(page.getByTestId('load-decision-stream')).toContainText('Switch auth-refactor');
  await page.getByTestId('load-period-1h').click();
  await expect(page.getByTestId('load-period-1h')).toHaveAttribute('aria-pressed', 'true');
  await load.getByRole('button', { name: 'switch', exact: true }).click();
  await expect(page.getByTestId('load-decision-stream').locator('.load__decision')).toHaveCount(1);

  for (const theme of themes) {
    await setTheme(page, theme);
    await page.waitForTimeout(100);
    await feed.screenshot({
      path: join(RESULTS_DIR, `load-distribution-${theme}--mocked.png`),
    });
  }
});
