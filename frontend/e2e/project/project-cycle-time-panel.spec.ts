import { expect, test, type Page, type Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * Project Deck "Cycle Time" rail. Fully mocked (only the frontend is needed):
 * the cycle-time endpoint answers with a deterministic payload, so the spec
 * proves the calm surface, the window selector round trip, the highlighted
 * gate and integration rows, the composition bar in lane order, sorting in
 * the drill-down, and both themes. Screenshots are labelled `--mocked`.
 */

const PROJECT_ID = 'PROJ-910';
const PROJECT_NAME = 'Cycle Demo';
const RESULTS_DIR = process.env.PROJECT_CYCLE_TIME_RESULTS_DIR
  ?? process.env.JOB_RESULTS_DIR
  ?? path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-cycle-time-panel');

const project = {
  id: PROJECT_ID,
  displayName: PROJECT_NAME,
  shortCode: 'CYC',
  workspaceId: 'WS-CYC',
  storageLocation: '/mock/tasks/cycle-demo',
  rootPath: '/mock/repos/cycle-demo',
  repositoryPath: '/mock/repos/cycle-demo',
  sortOrder: 0,
  archived: false,
  urls: [],
};

function aggregate(
  stage: string,
  label: string,
  kind: 'stage' | 'rollup' | 'count',
  count: number,
  p50: number | null,
  highlighted = false,
) {
  return {
    stage, label, kind, unit: kind === 'count' ? 'count' : 'seconds', highlighted, count,
    p50, p90: p50 === null ? null : p50 * 2.5, max: p50 === null ? null : p50 * 4, mean: p50, total: (p50 ?? 0) * count,
  };
}

function row(key: string, completedAt: string, lead: number, gate: number, integration: number, outcome: string | null) {
  const queue = 600;
  const coding = 3600;
  const human = lead - queue - coding - gate - integration - 300;
  return {
    taskId: key.toLowerCase(),
    taskKey: key,
    title: `${key} mocked delivery`,
    terminalState: '7-archive',
    watchPath: project.storageLocation,
    createdAt: '2026-08-18T08:00:00Z',
    firstClaimedAt: '2026-08-18T08:10:00Z',
    completedAt,
    completionSource: 'ledger',
    stages: {
      preparation: 0, queueWait: queue, coding, reviewWait: 300, testGate: gate, reviewOther: 0,
      integration, humanReview: human, unattributed: 0,
    },
    reviewRunSeconds: gate + integration,
    leadTimeSeconds: lead,
    cycleTimeSeconds: lead - queue,
    codingRuns: 1,
    reviewRounds: 1,
    bounceRounds: 0,
    integrationAttempts: outcome ? 1 : 0,
    integrationOutcome: outcome,
    integrationStage: outcome ? 'pre-human-review' : null,
    dataGaps: [],
    backwardTransitions: gate > 2000 ? 2 : 0,
    transitions: null,
  };
}

function transitionSummary(n: number) {
  return {
    totalTransitions: 4 * n + 2,
    backwardTransitions: 2,
    tasksWithBackwardTransitions: 1,
    lanes: ['2-ready', '3-progress', '4-auto-review', '5-human-review', '6-completed'],
    cells: [
      { from: '2-ready', to: '3-progress', count: n + 2, direction: 'forward' },
      { from: '3-progress', to: '4-auto-review', count: n + 2, direction: 'forward' },
      { from: '4-auto-review', to: '2-ready', count: 2, direction: 'backward' },
      { from: '4-auto-review', to: '5-human-review', count: n, direction: 'forward' },
      { from: '5-human-review', to: '6-completed', count: n, direction: 'forward' },
    ],
    laneDwell: [
      { lane: '2-ready', stays: n + 2, p50Seconds: 600, p90Seconds: 1200, maxSeconds: 1800, totalSeconds: 6000 },
      { lane: '3-progress', stays: n + 2, p50Seconds: 3600, p90Seconds: 5400, maxSeconds: 7200, totalSeconds: 20_000 },
      { lane: '4-auto-review', stays: n + 2, p50Seconds: 900, p90Seconds: 2400, maxSeconds: 3000, totalSeconds: 8000 },
      { lane: '5-human-review', stays: n, p50Seconds: 30_000, p90Seconds: 40_000, maxSeconds: 50_000, totalSeconds: 120_000 },
    ],
    bounceCauses: [
      { cause: 'gate-failure', label: 'Build/test gate failed', count: 2, tasks: 1, reworkKnown: 2, reworkP50Seconds: 2400, reworkP90Seconds: 3000, reworkTotalSeconds: 5400, details: [{ outcome: 'build-test-gate-fail', count: 2 }] },
    ],
    topLoops: [
      { taskId: 'cyc-2', taskKey: 'CYC-2', title: 'CYC-2 mocked delivery', watchPath: project.storageLocation, backwardTransitions: 2, leadTimeSeconds: 52_000, causes: [{ outcome: 'gate-failure', count: 2 }] },
    ],
  };
}

function taskTransitions() {
  const t = (at: string, from: string, to: string, direction: string, cause: string, dwell: number, detail: string | null = null, rework: number | null = null) => ({
    at, from, to, direction, dwellSeconds: dwell, actor: direction === 'backward' ? 'system' : 'remote-runner:r',
    actorKind: direction === 'backward' ? 'system' : 'runner', cause, causeDetail: detail, attemptId: null, reworkSeconds: rework,
  });
  return [
    t('2026-08-18T08:10:00Z', '2-ready', '3-progress', 'forward', 'claimed', 600),
    t('2026-08-18T09:10:00Z', '3-progress', '4-auto-review', 'forward', 'delivered', 3600),
    t('2026-08-18T09:25:00Z', '4-auto-review', '2-ready', 'backward', 'gate-failure', 900, 'build-test-gate-fail', 2400),
    t('2026-08-18T09:26:00Z', '2-ready', '3-progress', 'forward', 'claimed', 60),
    t('2026-08-18T10:05:00Z', '3-progress', '4-auto-review', 'forward', 'delivered', 2340),
    t('2026-08-18T10:55:00Z', '4-auto-review', '2-ready', 'backward', 'gate-failure', 3000, 'build-test-gate-fail', 3000),
    t('2026-08-18T10:56:00Z', '2-ready', '3-progress', 'forward', 'claimed', 60),
    t('2026-08-18T11:45:00Z', '3-progress', '4-auto-review', 'forward', 'delivered', 2940),
    t('2026-08-18T12:00:00Z', '4-auto-review', '5-human-review', 'forward', 'review-verdict', 900, 'Merged'),
    t('2026-08-21T10:00:00Z', '5-human-review', '6-completed', 'forward', 'accepted', 252_000),
  ];
}

function payload(window: string) {
  const tasks = window === '7d'
    ? [
      row('CYC-3', '2026-08-22T10:00:00Z', 40_000, 1200, 120, 'Merged'),
      row('CYC-2', '2026-08-21T10:00:00Z', 52_000, 2400, 0, null),
      row('CYC-1', '2026-08-20T10:00:00Z', 30_000, 600, 90, 'Merged'),
    ]
    : [
      row('CYC-3', '2026-08-22T10:00:00Z', 40_000, 1200, 120, 'Merged'),
      row('CYC-2', '2026-08-21T10:00:00Z', 52_000, 2400, 0, null),
      row('CYC-1', '2026-08-20T10:00:00Z', 30_000, 600, 90, 'Merged'),
      row('CYC-0', '2026-08-01T10:00:00Z', 90_000, 3000, 300, 'Conflict'),
    ];
  const n = tasks.length;
  return {
    project: PROJECT_NAME,
    projectId: PROJECT_ID,
    shortCode: 'CYC',
    window,
    capturedAt: '2026-08-23T12:00:00Z',
    since: window === 'all' ? null : '2026-08-16T12:00:00Z',
    coverage: {
      tasksInProject: n + 3, tasksTerminal: n + 1, tasksInWindow: n,
      excludedNoCompletionTimestamp: 1, excludedInFlight: 2, excludedEpics: 0,
      tasksWithoutLedger: 0, tasksWithLaneEntryCompletion: 0,
    },
    aggregates: [
      aggregate('preparation', 'Preparation', 'stage', 0, null),
      aggregate('queueWait', 'Queue wait', 'stage', n, 600),
      aggregate('coding', 'Coding run', 'stage', n, 3600),
      aggregate('reviewWait', 'Post-processing wait', 'stage', n, 300),
      aggregate('testGate', 'Build/test gate', 'stage', n, 1200, true),
      aggregate('reviewOther', 'Review aspects and decision', 'stage', 0, null),
      aggregate('integration', 'Integration', 'stage', n - 1, 120, true),
      aggregate('humanReview', 'Human review', 'stage', n, 30_000),
      aggregate('unattributed', 'Unattributed', 'stage', 0, null),
      aggregate('reviewRun', 'Review run', 'rollup', n, 1320),
      aggregate('leadTime', 'Lead time', 'rollup', n, 40_000),
      aggregate('cycleTime', 'Cycle time', 'rollup', n, 39_400),
      aggregate('codingRuns', 'Coding runs', 'count', n, 1),
      aggregate('reviewRounds', 'Review rounds', 'count', n, 1),
      aggregate('bounceRounds', 'Bounce rounds', 'count', n, 0),
      aggregate('integrationAttempts', 'Integration attempts', 'count', n, 1),
    ],
    integrationOutcomes: [{ outcome: 'Merged', count: 2 }, { outcome: 'none', count: 1 }],
    transitions: transitionSummary(n),
    tasks,
  };
}

async function json(route: Route, body: unknown): Promise<void> {
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/v1/workspaces**', route => json(route, [{
    id: 'WS-CYC', displayName: 'Cycle Workspace', sortOrder: 0, isDefault: true, projects: [project],
  }]));
  await page.route('**/api/watch-paths**', route => json(route, [{
    name: PROJECT_NAME, path: project.storageLocation, rootPath: project.rootPath,
  }]));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
    escalated: [], review: [], completed: [], archive: [],
  }));
  await page.route(/\/api\/v1\/studio\/runner\/status(?:\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-23T12:00:00Z', ttlSeconds: 600, snapshots: [],
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-08-23T12:00:00Z', sessions: [],
  }));
  await page.route('**/api/cli/maintenance-model', route => json(route, {
    cliType: 'claude', model: 'claude-sonnet-5', thinkingLevel: null,
  }));
  await page.route(/\/api\/projects\/[^/]+\/workbenches(?:\?|$)/, route => json(route, {
    projectName: PROJECT_NAME, items: [], count: 0, historyCount: 0,
  }));
  await page.route(/\/api\/projects\/[^/]+\/cycle-time(?:\?|$)/, route => {
    const url = new URL(route.request().url());
    return json(route, payload(url.searchParams.get('window') ?? '7d'));
  });
  await page.route(/\/api\/projects\/[^/]+\/cycle-time\/tasks\/[^/?]+(?:\?|$)/, route => {
    const key = decodeURIComponent(route.request().url().split('/cycle-time/tasks/')[1].split('?')[0]);
    const task = payload('all').tasks.find(t => t.taskKey === key);
    if (!task) return route.fulfill({ status: 404, contentType: 'application/json', body: JSON.stringify({ error: 'unknown' }) });
    return json(route, { project: PROJECT_NAME, capturedAt: '2026-08-23T12:00:00Z', task: { ...task, transitions: taskTransitions() } });
  });
}

test.beforeEach(async ({ page }) => {
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  await installRoutes(page);
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.removeItem('atp.studio.tabs.v1');
  });
});

test('cycle time rail renders aggregates, composition, drill-down, and both themes', async ({ page }, testInfo) => {
  await page.goto(`/#/projects/${PROJECT_ID}/cycle-time`);
  const panel = page.getByTestId('project-cycle-time-panel');
  await expect(panel).toBeVisible({ timeout: 20_000 });
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('project-shell-rail-cycle-time')).toHaveAttribute('aria-current', 'page');

  await expect(page.getByTestId('cycle-time-window-7d')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('cycle-time-summary')).toContainText('3 tasks completed');

  const segments = page.locator('[data-testid="cycle-time-bar"] .cyc__segment');
  await expect(segments).toHaveCount(6);
  expect(await segments.evaluateAll(els => els.map(e => e.getAttribute('data-stage')))).toEqual([
    'queueWait', 'coding', 'reviewWait', 'testGate', 'integration', 'humanReview',
  ]);

  const gateRow = page.locator('[data-testid="cycle-time-stages"] tr[data-stage="testGate"]');
  await expect(gateRow).toContainText('Build/test gate');
  await expect(gateRow).toContainText('20m');
  await expect(page.locator('[data-testid="cycle-time-stages"] tr[data-stage="integration"]')).toContainText('2m');
  await expect(page.locator('[data-testid="cycle-time-stages"] tr[data-stage="leadTime"]')).toContainText('11.1h');
  await expect(page.getByTestId('cycle-time-outcomes')).toContainText('Merged 2');

  const keys = page.locator('[data-testid="cycle-time-tasks"] > tbody > tr[data-task-key]:not(.cyc__expansion)');
  expect(await keys.evaluateAll(els => els.map(e => e.getAttribute('data-task-key')))).toEqual(['CYC-3', 'CYC-2', 'CYC-1']);
  await page.getByTestId('cycle-time-sort-testGate').click();
  expect(await keys.evaluateAll(els => els.map(e => e.getAttribute('data-task-key')))).toEqual(['CYC-2', 'CYC-3', 'CYC-1']);
  await page.getByTestId('cycle-time-sort-testGate').click();
  expect(await keys.evaluateAll(els => els.map(e => e.getAttribute('data-task-key')))).toEqual(['CYC-1', 'CYC-3', 'CYC-2']);

  // Lane transitions: bounce causes, matrix with a backward cell, dwell, loops, and the per-task history.
  await expect(page.getByTestId('cycle-time-transitions-summary')).toContainText('2 backward');
  await expect(page.locator('[data-testid="cycle-time-bounce-causes"] tr[data-cause="gate-failure"]')).toContainText('40m');
  const backwardCell = page.locator('[data-testid="cycle-time-matrix"] tr[data-from="4-auto-review"] td[data-to="2-ready"]');
  await expect(backwardCell).toHaveClass(/ctt__cell--backward/);
  await expect(backwardCell).toContainText('2');
  await expect(page.locator('[data-testid="cycle-time-matrix"] tr[data-from="2-ready"] td[data-to="3-progress"]')).toHaveAttribute('data-count', '5');
  await expect(page.locator('[data-testid="cycle-time-top-loops"] tr[data-task-key="CYC-2"]')).toContainText('gate-failure 2');

  await page.getByTestId('cycle-time-expand-CYC-2').click();
  const history = page.getByTestId('cycle-time-task-transitions-CYC-2');
  await expect(history).toBeVisible();
  await expect(history.locator('tbody tr')).toHaveCount(10);
  await expect(history.locator('tbody tr[data-direction="backward"]')).toHaveCount(2);
  await expect(history.locator('tbody tr').nth(2)).toContainText('build-test-gate-fail');

  await setTheme(page, 'light');
  const light = path.join(RESULTS_DIR, 'project-cycle-time--7d--light--mocked.png');
  await panel.screenshot({ path: light });
  await testInfo.attach('project-cycle-time--7d--light', { path: light, contentType: 'image/png' });
  const transitionsLight = path.join(RESULTS_DIR, 'project-cycle-time--transitions--light--mocked.png');
  await page.getByTestId('cycle-time-transitions').screenshot({ path: transitionsLight });
  await testInfo.attach('project-cycle-time--transitions--light', { path: transitionsLight, contentType: 'image/png' });

  await page.getByTestId('cycle-time-window-all').click();
  await expect(page.getByTestId('cycle-time-window-all')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('cycle-time-summary')).toContainText('4 tasks completed');
  await expect(page.getByTestId('cycle-time-summary')).toContainText('All time');
  await expect(keys).toHaveCount(4);

  await setTheme(page, 'dark');
  const dark = path.join(RESULTS_DIR, 'project-cycle-time--all--dark--mocked.png');
  await panel.screenshot({ path: dark });
  await testInfo.attach('project-cycle-time--all--dark', { path: dark, contentType: 'image/png' });
  const transitionsDark = path.join(RESULTS_DIR, 'project-cycle-time--transitions--dark--mocked.png');
  await page.getByTestId('cycle-time-transitions').screenshot({ path: transitionsDark });
  await testInfo.attach('project-cycle-time--transitions--dark', { path: transitionsDark, contentType: 'image/png' });
});
