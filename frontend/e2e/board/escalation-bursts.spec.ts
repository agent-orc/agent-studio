import { expect, test, type Locator, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3585 (walkthrough AGT-3561, finding F10): a burst of automatic
 * escalations reads as groups per cause, not one row per card. Replays the
 * walkthrough shape - 30 Demo App cards escalated for missing delivery and 15
 * Demo Platform cards for a stall intervention within seconds - on the
 * Escalated lane and in Activity across projects. Group counts add up to the
 * visible members (R3), and each group's bulk action lists the cards before it
 * moves them. Mocked API; screenshots in both themes go to JOB_RESULTS_DIR.
 */

const resultsDir = process.env['JOB_RESULTS_DIR'] ?? path.join(process.cwd(), 'test-results');
const PROJECTS = [
  { name: 'Demo App', code: 'DA', path: '/fixtures/demo-app', id: 'PROJ-DA' },
  { name: 'Demo Platform', code: 'DP', path: '/fixtures/demo-platform', id: 'PROJ-DP' },
];
const STALL = 'Card had no active Auto Review owner beyond the configured threshold.';
const MISSING = 'missing-delivery: no completed delivery or review step to recover.';

interface Card { id: string; key: string; project: typeof PROJECTS[number]; cause: string; second: number }

const cards: Card[] = [
  ...Array.from({ length: 30 }, (_, i) => ({
    id: `demo-${i + 1}-feature`, key: `DEMO-${i + 1}`, project: PROJECTS[0], cause: 'missing-delivery', second: i % 4,
  })),
  ...Array.from({ length: 15 }, (_, i) => ({
    id: `plat-${i + 1}-feature`, key: `PLAT-${i + 1}`, project: PROJECTS[1], cause: 'stall-intervention', second: 1 + (i % 3),
  })),
];

const at = (second: number) => `2026-11-09T09:23:0${second}Z`;

function boardCard(card: Card, state: string) {
  return {
    id: card.id, key: card.key, displayKey: card.key,
    taskKey: `${card.project.path}::${card.id}`, jobKey: `${card.project.path}::${card.id}`,
    title: `${card.key} feature work`, state, order: 0,
    projectName: card.project.name, watchPath: card.project.path,
    folderPath: `${card.project.path}/${state}/${card.id}`,
    createdAt: '2026-08-09T08:00:00Z', lastActivity: at(card.second),
    agent: 'codex', cliType: 'codex', sessionName: null, model: null, useOwnSession: null,
    lastUsage: null, execution: null, commit: null, commits: [], ownerClientId: 'local-default',
    tags: [], pendingIntent: null, autoLoop: null, summaryState: null,
    parkedBlocker: state !== '5e-escalated' ? null : {
      blockerType: card.cause, parkedAt: at(card.second), reason: `[${card.cause}] escalated`,
      lastEvaluatedAt: null, detail: '', lane: state, decision: null, needsInputFile: null,
      evaluationAgeSeconds: null, evaluationStale: true, requiresDecisionCard: false,
    },
  };
}

function feedEntry(card: Card) {
  const stall = card.cause === 'stall-intervention';
  return {
    ts: at(card.second), project: card.project.name, watchPath: card.project.path, jobId: card.id,
    kind: 'alert', topic: stall ? 'stall-intervention' : 'acceptance-audit',
    summary: stall
      ? `AI Operator escalated auto-review:${card.key}: ${STALL}`
      : `Acceptance audit escalated ${card.key}: ${MISSING}`,
    reasoning: 'There is no completed delivery or review step to recover.',
  };
}

interface Mock { requeued: Set<string>; batches: { items: { jobId: string; targetState: string }[] }[] }

async function installRoutes(page: Page): Promise<Mock> {
  const mock: Mock = { requeued: new Set(), batches: [] };
  const grouped = () => ({
    backlog: [], preparation: [], orchestratorPrep: [], failedPickup: [], codeNotComplete: [],
    progress: [], autoReview: [], review: [], humanReview: [], completed: [], archive: [],
    ready: cards.filter(card => mock.requeued.has(card.id)).map(card => boardCard(card, '2-ready')),
    escalated: cards.filter(card => !mock.requeued.has(card.id)).map(card => boardCard(card, '5e-escalated')),
  });
  const projects = PROJECTS.map((project, sortOrder) => ({
    id: project.id, displayName: project.name, shortCode: project.code, workspaceId: 'WS-DEMO',
    color: null, cliDefault: null, modelDefault: null, sortOrder, storageLocation: project.path,
    archived: false, createdAt: '2026-08-09T08:00:00Z', urls: [],
  }));
  // Bulk moves need a connected hub: the offline guard blocks mutations without one.
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill({ json: {
    connectionId: 'agt-3585', connectionToken: 'agt-3585', negotiateVersion: 1,
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  } }));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (message.toString().includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    const json = (body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    const p = url.pathname;
    if (p === '/api/v1/studio/auth/status' || p === '/api/auth/status') {
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (p === '/api/tasks/batch-move' && route.request().method() === 'POST') {
      const body = route.request().postDataJSON() as Mock['batches'][number];
      mock.batches.push(body);
      body.items.forEach(item => mock.requeued.add(item.jobId));
      return json(batch(body.items));
    }
    if (p.startsWith('/api/tasks/batch-move/')) return json(batch(mock.batches.at(-1)?.items ?? []));
    if (p === '/api/v1/studio/board') return json(grouped());
    if (p === '/api/tasks/archive') return json({ items: [], total: 0, offset: 0, limit: 50 });
    if (p === '/api/tasks') return json(Object.values(grouped()).flat());
    if (p === '/api/runner/orchestrator-feed') return json({ entries: cards.map(feedEntry) });
    if (p === '/api/watch-paths') {
      return json(PROJECTS.map(project => ({ name: project.name, path: project.path, rootPath: project.path, repositoryPath: project.path })));
    }
    if (p === '/api/v1/projects') return json(projects);
    if (p === '/api/v1/workspaces' || p === '/api/workspaces') {
      return json([{ id: 'WS-DEMO', displayName: 'Workspace', sortOrder: 0, isDefault: true, color: null,
        createdAt: '2026-08-09T08:00:00Z', projects }]);
    }
    if (p === '/api/environment') return json({ isDev: false, devTools: {} });
    if (/^\/api\/workspaces\/[^/]+\/quota\/admission$/.test(p)) {
      return json({ workspaceId: 'WS-DEMO', projects: [], workspace: { projectId: null, project: null, status: 'ok',
        message: null, overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false } });
    }
    if (p === '/api/usage/cockpit') return json({ workspaceId: 'WS-DEMO', clis: [], cost: null });
    if (p === '/api/v1/management/credential-inventory') return json({ items: [] });
    if (p === '/api/cli/quota') return json({ at: '2026-11-09T09:30:00Z', ttlSeconds: 600, snapshots: [] });
    if (p === '/api/v1/studio/runner/status') return json({ projects: {} });
    return json([]);
  });
  return mock;
}

function batch(items: { jobId: string }[]) {
  return {
    id: 'batch-agt-3585', status: 'completed', total: items.length, completed: items.length,
    succeeded: items.length, failed: 0, message: null,
    results: items.map((item, index) => ({ index, jobId: item.jobId, status: 'moved', message: null, durationMs: 1 })),
    metrics: {}, createdAt: '2026-11-09T09:30:00Z', startedAt: '2026-11-09T09:30:00Z', finishedAt: '2026-11-09T09:30:01Z',
  };
}

async function boot(page: Page): Promise<Mock> {
  fs.mkdirSync(resultsDir, { recursive: true });
  await page.addInitScript(() => {
    localStorage.setItem('atp.orchestrator-feed.alerts-seen-at', '2026-01-01T00:00:00Z');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
    }));
  });
  const mock = await installRoutes(page);
  // Theme switches below must not be captured mid colour transition.
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.setViewportSize({ width: 1440, height: 960 });
  await page.goto('/');
  await expect(page.getByTestId('lane-count-5e-escalated')).toContainText('45', { timeout: 30_000 });
  return mock;
}

/** Scrolls the lane until the group head for `cause` renders (the lane is windowed above 40 cards). */
async function groupHead(page: Page, cause: string): Promise<Locator> {
  const lane = page.getByTestId('lane-scroll-5e-escalated');
  const head = lane.locator(`[data-testid="lane-group-head"][data-cause="${cause}"]`);
  for (let step = 0; step < 40 && !(await head.isVisible()); step++) {
    await lane.evaluate(element => {
      const scroller = element.closest<HTMLElement>('.lane-group__lanes') ?? element;
      scroller.scrollTop += 400;
    });
  }
  await head.scrollIntoViewIfNeeded();
  await expect(head).toBeVisible();
  return head;
}

const count = async (head: Locator) => Number(await head.getByTestId('lane-group-count').textContent());

test('Escalated lane groups the burst by cause; counts add up and a bulk requeue lists its cards first', async ({ page }) => {
  const mock = await boot(page);

  // The lane is windowed above 40 cards, so each head is read while it is rendered.
  const missing = await groupHead(page, 'missing-delivery');
  await expect(missing.getByTestId('lane-group-label')).toHaveText('Missing Delivery');
  const missingCount = await count(missing);
  const stall = await groupHead(page, 'stall-intervention');
  await expect(stall.getByTestId('lane-group-label')).toHaveText('Stall Intervention');
  const stallCount = await count(stall);
  expect([missingCount, stallCount]).toEqual([30, 15]);
  expect(missingCount + stallCount).toBe(45);
  await expect(page.getByTestId('escalation-rest')).toHaveCount(0);
  await expect(stall.getByTestId('bulk-action-reissue-escalated')).toHaveText('Continue (reissue)');
  await expect(stall.getByTestId('bulk-action-manual-resolve-escalated')).toHaveText('Resolve manually');

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await (await groupHead(page, 'stall-intervention')).scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(resultsDir, `agt-3585--escalated-lane-groups--${theme}--mocked.png`) });
    await page.getByTestId('lane-scroll-5e-escalated').evaluate(element => {
      (element.closest<HTMLElement>('.lane-group__lanes') ?? element).scrollTop = 0;
    });
    await expect(page.locator('[data-testid="lane-group-head"][data-cause="missing-delivery"]')).toBeVisible();
    await page.screenshot({ path: path.join(resultsDir, `agt-3585--escalated-lane-top--${theme}--mocked.png`) });
  }
  await groupHead(page, 'stall-intervention');
  await setTheme(page, 'light');

  await stall.getByTestId('bulk-action-reissue-escalated').click();
  const dialog = page.getByTestId('confirm-dialog');
  await expect(dialog).toContainText('Continue (reissue) for 15 cards?');
  await expect(page.getByTestId('confirm-dialog-item')).toHaveCount(15);
  await expect(page.getByTestId('confirm-dialog-item').first()).toContainText('PLAT-');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: path.join(resultsDir, `agt-3585--bulk-requeue-confirm--${theme}--mocked.png`) });
  }
  await setTheme(page, 'light');
  await page.getByTestId('confirm-dialog-cancel').click();
  await expect(dialog).toHaveCount(0);
  expect(mock.batches).toHaveLength(0);

  await stall.getByTestId('bulk-action-reissue-escalated').click();
  await page.getByTestId('confirm-dialog-confirm').click();
  await expect.poll(() => mock.batches.length).toBe(1);
  expect(mock.batches[0].items).toHaveLength(15);
  expect(new Set(mock.batches[0].items.map(item => item.targetState))).toEqual(new Set(['2-ready']));
  expect(mock.batches[0].items.every(item => item.jobId.startsWith('plat-'))).toBe(true);
  await expect(page.getByTestId('lane-count-5e-escalated')).toContainText('30');
});

test('Activity across projects folds the burst into one row per cause with its cards and bulk actions', async ({ page }) => {
  await boot(page);
  await page.getByTestId('studio-ab-activity').click();
  await expect(page).toHaveURL(/#\/feed$/);

  const bursts = page.getByTestId('orchestrator-feed-burst');
  await expect(bursts).toHaveCount(2);
  const counts = await bursts.evaluateAll(rows => rows.map(row => Number((row as HTMLElement).dataset['count'])));
  expect(counts.sort((a, b) => a - b)).toEqual([15, 30]);
  await expect(page.getByTestId('feed-kind-alert')).toContainText('45');
  expect(counts.reduce((sum, value) => sum + value, 0)).toBe(45);
  await expect(page.getByTestId('orchestrator-feed-entry')).toHaveCount(0);

  const stall = bursts.filter({ hasText: 'auto-review' });
  await expect(stall.getByTestId('orchestrator-feed-burst-count')).toHaveText('15 cards');
  await expect(stall.getByTestId('orchestrator-feed-burst-label')).toHaveText(`AI Operator escalated auto-review: ${STALL}`);
  await stall.click();
  await expect(stall).toHaveAttribute('aria-expanded', 'true');
  await expect(page.getByTestId('orchestrator-feed-burst-members').getByTestId('orchestrator-feed-entry')).toHaveCount(15);

  const detail = page.getByTestId('orchestrator-burst-detail');
  await expect(detail.getByTestId('orchestrator-burst-card-count')).toHaveText('15');
  await expect(detail.getByTestId('orchestrator-burst-card')).toHaveCount(15);
  await expect(detail.getByTestId('orchestrator-burst-card-lane').first()).toHaveText('Escalated');
  await expect(detail.getByTestId('bulk-action-reissue-escalated')).toBeVisible();
  await expect(detail.getByTestId('bulk-action-manual-resolve-escalated')).toBeVisible();

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: path.join(resultsDir, `agt-3585--activity-burst--${theme}--mocked.png`) });
  }

  await stall.click();
  await expect(stall).toHaveAttribute('aria-expanded', 'false');
  await expect(page.getByTestId('orchestrator-feed-burst-members')).toHaveCount(0);
});
