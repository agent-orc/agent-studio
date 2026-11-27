import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Card flow fixture';
const WATCH = '/fixtures/card-flow';
const NOW = '2026-11-26T09:00:00Z';
const RESULTS = process.env.JOB_RESULTS_DIR ?? '/tmp/agt4054-screenshots';
const make = (key: string, state: string, extra = {}) => ({
  id: key.toLowerCase(), key, displayKey: key, taskKey: `${WATCH}::${key.toLowerCase()}`,
  title: `Delivery for ${key}`, state, projectName: PROJECT, watchPath: WATCH,
  folderPath: `${WATCH}/${state}/${key.toLowerCase()}`, agent: 'codex', cliType: 'codex',
  createdAt: NOW, lastActivity: NOW, enteredLaneAt: NOW, commits: [], tags: [], ...extra,
});
const ready = [
  make('AGT-4101', '2-ready', { waitingReason: 'waits for AGT-4011 (escalated)',
    waitsOn: { blocked: true, items: [{ key: 'AGT-4011', resolved: true, fulfilled: false }] },
    liveStatus: { attempt: 1, nextSteps: [], queue: { kind: 'runner', position: 4 } } }),
  make('AGT-4102', '2-ready', { waitingReason: 'waits for a free slot' }),
  make('AGT-4103', '2-ready', { waitingReason: 'quota' }),
  make('AGT-4104', '2-ready', { waitingReason: `dispatch rule: ${'recorded rejection with a long explanation '.repeat(12)}` }),
  make('AGT-4105', '2-ready', { liveStatus: { attempt: 1, nextSteps: [], queue: { kind: 'runner', position: 1 } } }),
];
const dependencyFallbacks = [
  make('AGT-4106', '2-ready', {
    waitsOn: { blocked: true, items: [{ key: 'AGT-4011', resolved: true, fulfilled: false }] },
    liveStatus: { attempt: 1, nextSteps: [], queue: { kind: 'runner', position: 4 } },
  }),
  make('AGT-4107', '2-ready', {
    waitsOn: { blocked: true, cycleDetected: true, items: [{ key: 'AGT-4011', resolved: true, fulfilled: false }] },
    liveStatus: { attempt: 1, nextSteps: [], queue: { kind: 'runner', position: 4 } },
  }),
  make('AGT-4108', '2-ready', {
    waitsOn: { blocked: true, items: [{ key: 'AGT-4011', resolved: true, fulfilled: false, waitingForRelease: true }] },
    liveStatus: { attempt: 1, nextSteps: [], queue: { kind: 'runner', position: 4 } },
  }),
  make('AGT-4109', '2-ready', {
    waitingReason: 'waits for AGT-4011 (escalated)',
    waitsOn: { blocked: true, items: [{ key: 'AGT-4011', resolved: true, fulfilled: false }] },
  }),
];
ready.push(...dependencyFallbacks);
const escalated = [make('AGT-4011', '5e-escalated', {
  transitiveWaiters: { count: 3, keys: ['AGT-4101', 'AGT-4110', 'AGT-4111'] },
}), make('AGT-4012', '5e-escalated')];
const humanReview = [make('AGT-4013', '5-human-review', {
  transitiveWaiters: { count: 1, keys: ['AGT-4112'] },
})];
const cards = [...ready, ...escalated, ...humanReview];
const json = (route: Route, body: unknown) => route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });

async function openBoard(page: Page) {
  await page.clock.setFixedTime(new Date(NOW));
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
  })));
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route(/\/api\/v1\/projects(\?|$)/, route => json(route, [{ id: PROJECT, displayName: PROJECT, shortCode: 'AGT', storageLocation: WATCH, archived: false }]));
  await page.route('**/api/v1/management/credential-inventory**', route => json(route, { generatedAt: NOW, items: [] }));
  await page.route('**/api/tasks/*/runs**', route => json(route, {
    runCount: 0, firstStartedAt: null, lastActivityAt: null, hasActiveRun: false,
    runs: [], promptEntries: [], refinements: [], runnerEvents: [],
  }));
  await page.route('**/api/tasks/*/session-events**', route => json(route, { events: [], sessionChain: [] }));
  await page.route('**/api/tasks/*/plan**', route => json(route, {
    hasPlan: false, source: null, snapshotCount: 0, activeItemId: null, items: [], unassignedSubActions: [],
  }));
  await page.route('**/api/tasks/*/pipeline**', route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null, executions: [], config: {}, cost: null,
  }));
  await page.route('**/code-review/list**', route => json(route, { entries: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0 }));
  await page.route(/\/api\/tasks(\?|$)/, route => json(route, cards));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready, progress: [], failedPickup: [],
    codeNotComplete: [], autoReview: [], review: [], humanReview, escalated, completed: [], archive: [],
  }));
  await page.route(/\/api\/v1\/projects\/[^/]+\/tasks\/agt-[0-9]+(?:\?|$)/, route => {
    const id = /\/tasks\/(agt-[0-9]+)/.exec(route.request().url())![1];
    return json(route, { info: cards.find(card => card.id === id), promptMarkdown: 'Fixture brief.',
      statusMarkdown: '', log: [], promptHistory: [], titleHistory: [], reviewEvidence: [], summaryState: null });
  });
  await page.route(/\/api\/tasks\/agt-[0-9]+\/(core|details\/[^?]+)(?:\?|$)/i, route => {
    const match = /\/tasks\/(agt-[0-9]+)\/(core|details\/([^?]+))/i.exec(route.request().url())!;
    const card = cards.find(item => item.id === match[1].toLowerCase())!;
    if (match[2] === 'core') return json(route, {
      ...card, projectId: PROJECT, kind: 'task', taskType: 'chore', lane: card.state, mode: 'coding',
      state: 'ready', order: 1, released: false, coreVersion: '1', runtimeVersion: '1',
      pins: { modelExplicit: false, thinkingLevelExplicit: false, allowWebAccess: false, noBranchExpected: false },
      actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
      blocking: { dependencyBlocked: false, dependencyState: 'ready', dependencies: [], dependsOn: [], blockedBy: [] },
      runtime: { location: 'none', leaseState: 'none' },
      statusSummary: { state: 'missing', originalBytes: 0 },
      prompt: { state: 'ready', text: 'Fixture brief.', originalBytes: 14 },
      timeline: { state: 'missing', events: [], originalBytes: 0 },
    });
    const resource = match[3];
    const data = resource === 'overview' ? card : resource === 'documents'
      ? { name: new URL(route.request().url()).searchParams.get('name'), markdown: 'Fixture brief.', summaryState: null }
      : resource === 'review' ? { reviewProjection: null, evidence: [] } : {};
    return json(route, { id: card.id, taskKey: card.taskKey, projectId: PROJECT, attemptId: null,
      coreVersion: '1', resource, version: '1', computedAt: NOW, state: 'ready', data, reason: null });
  });
  await page.route('**/api/watch-paths**', route => json(route, [{ name: PROJECT, path: WATCH, rootPath: WATCH, repositoryPath: WATCH }]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, route => json(route, {}));
  await page.route('**/api/cli/usage**', route => json(route, { at: NOW, sessions: [] }));
  await page.route('**/api/cli/quota**', route => json(route, { at: NOW, snapshots: [] }));
  await page.route(/\/api\/usage\/cockpit(\?|$)/, route => json(route, { workspaceId: 'fixture', clis: [], cost: null }));
  await page.route('**/api/v1/studio/runner/status**', route => json(route, { projects: {} }));
  await page.goto('/#/board');
  await expect(page.getByTestId('studio-board')).toBeVisible();
}

for (const width of [1600, 390]) {
  test(`server waiting reasons and held chips keep the key visible at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 960 });
    await openBoard(page);
    const lane = page.getByTestId('lane-2-ready');
    await lane.scrollIntoViewIfNeeded();
    await expect(lane.getByTestId('card-waiting-reason')).toHaveCount(5);
    const free = lane.getByTestId('task-card').filter({ hasText: 'AGT-4105' });
    await expect(free.getByTestId('card-waiting-reason')).toHaveCount(0);
    await expect(free.getByTestId('task-live-status')).toHaveCount(0);
    for (const key of ['AGT-4101', 'AGT-4109']) {
      const dependency = lane.getByTestId('task-card').filter({ hasText: key });
      await expect(dependency.getByTestId('card-waiting-reason')).toHaveText('waits for AGT-4011 (escalated)');
      await expect(dependency.getByTestId('task-live-status')).toHaveCount(0);
      await expect(dependency.getByTestId('task-card-waiting-on')).toHaveCount(0);
      await expect(dependency).not.toContainText('runner slot');
    }
    for (const [key, headline] of [
      ['AGT-4106', 'waits for completion: AGT-4011'],
      ['AGT-4107', 'Dependency gate blocked · cycle: AGT-4011'],
      ['AGT-4108', 'waits for release: AGT-4011'],
    ]) {
      const dependency = lane.getByTestId('task-card').filter({ hasText: key });
      await expect(dependency.getByTestId('task-live-current')).toContainText(headline);
      await expect(dependency.getByTestId('card-waiting-reason')).toHaveCount(0);
      await expect(dependency.getByTestId('task-card-waiting-on')).toHaveCount(0);
      await expect(dependency).not.toContainText('runner slot');
    }
    const baselineWidth = (await free.boundingBox())!.width;
    for (const card of await lane.getByTestId('task-card').all()) {
      await expect(card.getByTestId('task-card-key')).toBeVisible();
      expect(await card.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
      const key = await card.getByTestId('task-card-key').boundingBox();
      const box = await card.boundingBox();
      expect(box!.width).toBeLessThanOrEqual(baselineWidth + 1);
      expect(key!.x).toBeGreaterThanOrEqual(box!.x);
      expect(key!.x + key!.width).toBeLessThanOrEqual(box!.x + box!.width + 1);
    }
    mkdirSync(RESULTS, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await lane.screenshot({ path: join(RESULTS, `card-waits-${width}-${theme}--mocked.png`) });
    }
    if (width === 390) await page.getByRole('combobox', { name: 'Board lane', exact: true }).selectOption('5e-escalated');
    const held = page.getByTestId('lane-5e-escalated').getByTestId('task-card').first();
    await held.scrollIntoViewIfNeeded();
    const chip = held.getByTestId('task-card-decision-dam');
    await expect(chip).toHaveText('holds 3 cards');
    await chip.hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('AGT-4101, AGT-4110, AGT-4111');
    await held.screenshot({ path: join(RESULTS, `card-holds-${width}--mocked.png`) });
    await held.getByTestId('task-card-title').click();
    const title = page.getByTestId('overview-title-block');
    const detail = title.getByTestId('detail-held-cards');
    await expect(detail).toContainText('holds 3 cards:');
    await detail.getByRole('link', { name: 'AGT-4101', exact: true }).click();
    await expect(page.getByTestId('overview-title-block').getByTestId('detail-waiting-links')).toContainText('AGT-4011');
    await expect(page.getByTestId('overview-title-block').getByTestId('card-waiting-reason'))
      .toHaveText('waits for AGT-4011 (escalated)');
    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
    await page.screenshot({ path: join(RESULTS, `card-detail-${width}--mocked.png`) });
  });
}
