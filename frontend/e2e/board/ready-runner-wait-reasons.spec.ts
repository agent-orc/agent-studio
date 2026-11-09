import { expect, test, type Locator, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3579 - the runner's own reasons reach the Ready lane.
 *
 * Card-level holds were visible (AGT-2818), but when the runner itself did not
 * pick (manual mode, every slot busy, quota defer) the Ready cards still read
 * "Waiting for runner slot · position N" and the reason sat only in the
 * free-text `lastPickReason` no frontend file read (operator walkthrough
 * AGT-3561, 9 November 2026).
 *
 * Backend-mocked on purpose: `RunnerPickupWaitReasonTests` pins how the runner
 * records the gate and the skips and how the live-status projection puts them
 * on `liveStatus.queue`; the payloads below are exactly that shape. What this
 * spec proves is that the lane header splits the waiting count by reason, that
 * the split adds up to the lane count (R3), and that each skipped card names
 * its reason in its live status line, in both themes.
 */
const PROJECT = 'Runner wait fixture';
const WATCH_PATH = '/fixtures/runner-wait';
const RESULTS = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), '..', 'results');
const NOW = '2026-11-09T09:00:00Z';

type Queue = { kind: 'runner'; position: number; reasonCode?: string; reason?: string };

function card(id: string, title: string, order: number, queue: Queue | null, extra: Record<string, unknown> = {}) {
  return {
    id,
    key: id.toUpperCase(),
    displayKey: id.toUpperCase(),
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state: '2-ready',
    order,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-11-09T08:00:00Z',
    lastActivity: '2026-11-09T08:30:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/2-ready/${id}`,
    ownerClientId: 'local-default',
    commits: [],
    tags: [],
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
    pickupHold: null,
    liveStatus: {
      attempt: 1,
      activeStep: null,
      nextSteps: [{ stepId: 'core-agent-run', displayName: 'Agent execution' }],
      queue,
      latestEventAt: '2026-11-09T08:30:00Z',
    },
    ...extra,
  };
}

interface Scenario {
  name: string;
  cards: ReturnType<typeof card>[];
  runner: Record<string, unknown>;
}

const MANUAL = 'Auto-pickup is off; start the card or switch the project to auto.';
const MANUAL_MODE: Scenario = {
  name: 'manual-mode',
  cards: [
    card('agt-4101', 'Split the scheduler config', 1, { kind: 'runner', position: 1, reasonCode: 'manual-mode', reason: MANUAL }),
    card('agt-4102', 'Retire the legacy lane keys', 2, { kind: 'runner', position: 2, reasonCode: 'manual-mode', reason: MANUAL }),
    card('agt-4103', 'Ship the release notes page', 3, { kind: 'runner', position: 3, reasonCode: 'manual-mode', reason: MANUAL }),
    card('agt-4104', 'Wire the follow-up migration', 4, null, {
      references: { dependsOn: ['AGT-4001'], relatedTo: [], blockedBy: [], supersedes: [] },
      pickupHold: {
        classification: 'satisfiable-soon',
        mechanism: 'dependency-gate',
        reason: 'Waits for AGT-4001 to complete.',
        sinceUtc: '2026-11-09T07:00:00Z',
        unsatisfiable: false,
        resolutions: [],
      },
    }),
  ],
  runner: {
    mode: 'manual',
    maxParallelism: 1,
    occupiedSlots: 0,
    lastPickReason: null,
    queuedJobIds: ['agt-4101', 'agt-4102', 'agt-4103'],
    pickupGate: { code: 'manual-mode', detail: MANUAL },
    pickupSkips: [],
  },
};

const SLOTS = 'All 2 runner slots are occupied.';
const FULL_SLOTS: Scenario = {
  name: 'slots-full',
  cards: [
    card('agt-4201', 'Cache the dependency graph', 1, { kind: 'runner', position: 1, reasonCode: 'slots-full', reason: SLOTS }),
    card('agt-4202', 'Trim the board payload', 2, { kind: 'runner', position: 2, reasonCode: 'slots-full', reason: SLOTS }),
    card('agt-4203', 'Recover the crashed import', 3, null, {
      pickupHold: {
        classification: 'satisfiable-soon',
        mechanism: 'crash-backoff',
        reason: 'The last run crashed; the next attempt waits out a cooldown.',
        sinceUtc: '2026-11-09T08:50:00Z',
        unsatisfiable: false,
        resolutions: [],
      },
    }),
  ],
  runner: {
    mode: 'auto-continuous',
    maxParallelism: 2,
    occupiedSlots: 2,
    lastPickReason: 'parallel-ok: slot 2 of 2',
    queuedJobIds: ['agt-4201', 'agt-4202'],
    pickupGate: { code: 'slots-full', detail: SLOTS },
    pickupSkips: [],
  },
};

const QUOTA = 'claude 5h window exhausted; next reset at 11:00.';
const QUOTA_DEFER: Scenario = {
  name: 'quota-defer',
  cards: [
    card('agt-4301', 'Rewrite the prompt catalogue', 1, { kind: 'runner', position: 1, reasonCode: 'quota-defer', reason: QUOTA }, { cliType: 'claude', agent: 'claude' }),
    card('agt-4302', 'Tighten the review rubric', 2, { kind: 'runner', position: 2, reasonCode: 'quota-defer', reason: QUOTA }, { cliType: 'claude', agent: 'claude' }),
    card('agt-4303', 'Next codex card in line', 3, { kind: 'runner', position: 3 }),
  ],
  runner: {
    mode: 'auto-continuous',
    maxParallelism: 1,
    occupiedSlots: 0,
    lastPickReason: `quota-defer: agt-4302: ${QUOTA}`,
    queuedJobIds: ['agt-4301', 'agt-4302', 'agt-4303'],
    pickupGate: null,
    pickupSkips: [
      { jobId: 'agt-4301', code: 'quota-defer', detail: QUOTA },
      { jobId: 'agt-4302', code: 'quota-defer', detail: QUOTA },
    ],
  },
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function openBoard(page: Page, scenario: Scenario): Promise<void> {
  await page.setViewportSize({ width: 1600, height: 960 });
  await page.clock.setFixedTime(new Date(NOW));
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0 }));
  await page.route(/\/api\/tasks(\?|$)/, route => json(route, scenario.cards));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: scenario.cards,
    progress: [], failedPickup: [], codeNotComplete: [], autoReview: [],
    review: [], humanReview: [], escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, route => json(route, {}));
  await page.route('**/api/cli/usage**', route => json(route, { at: NOW, sessions: [] }));
  await page.route('**/api/cli/quota**', route => json(route, { at: NOW, ttlSeconds: 600, snapshots: [] }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, {
    projects: {
      [PROJECT]: { projectName: PROJECT, activeJobId: null, activeExecution: null, ...scenario.runner },
    },
  }));
  await page.goto('/#/board');
  await page.addStyleTag({
    content: 'app-error-dialog, app-offline-banner, [data-testid="offline-banner"], [data-testid="error-dialog-overlay"] { display: none !important; }',
  });
  await dismissDevErrorDialog(page);
  await page.getByRole('tab', { name: 'All projects · Board' }).click();
  await expect(page.getByTestId('lane-2-ready')).toBeVisible({ timeout: 15_000 });
}

/** R3: the header split adds up to the number the lane header shows. */
async function expectSplitSumsToLaneCount(lane: Locator, expected: Record<string, number>): Promise<void> {
  const split = lane.getByTestId('ready-wait-split');
  await expect(split).toBeVisible();
  const laneCount = Number((await lane.getByTestId('lane-count-2-ready').locator('span').first().textContent())?.trim());
  const buckets = split.locator('[data-testid^="ready-wait-bucket-"]');
  const counts: Record<string, number> = {};
  for (const bucket of await buckets.all()) {
    const id = (await bucket.getAttribute('data-testid'))!.replace('ready-wait-bucket-', '');
    counts[id] = Number(await bucket.getAttribute('data-count'));
  }
  expect(counts).toEqual(expected);
  const sum = Object.values(counts).reduce((total, count) => total + count, 0);
  expect(sum).toBe(laneCount);
  await expect(split).toHaveAttribute('data-total', String(laneCount));
}

async function captureLane(page: Page, lane: Locator, name: string): Promise<void> {
  mkdirSync(RESULTS, { recursive: true });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await lane.screenshot({ path: join(RESULTS, `ready-runner-wait-${name}--${theme}--mocked.png`) });
  }
}

function cardFor(page: Page, title: string): Locator {
  return page.getByTestId('task-card').filter({ hasText: title });
}

test.describe('Ready cards name the runner reason they wait for', () => {
  test('manual mode: header splits by reason and each queued card says auto-pickup is off', async ({ page }) => {
    await openBoard(page, MANUAL_MODE);
    const lane = page.getByTestId('lane-2-ready');

    await expectSplitSumsToLaneCount(lane, { 'hold-dependency-gate': 1, 'runner-manual-mode': 3 });
    await expect(lane.getByTestId('ready-wait-bucket-runner-manual-mode')).toContainText('3 manual mode');

    const queued = cardFor(page, 'Retire the legacy lane keys');
    await expect(queued.getByTestId('task-live-status')).toHaveAttribute('data-wait-reason', 'manual-mode');
    await expect(queued.getByTestId('task-live-current')).toContainText('Not picked · auto-pickup is off · position 2');
    await expect(queued.getByTestId('task-live-status')).toContainText(MANUAL);
    // The held card keeps its own hold; the runner reason never overwrites it.
    const held = cardFor(page, 'Wire the follow-up migration');
    await expect(held.getByTestId('pickup-hold')).toHaveAttribute('data-mechanism', 'dependency-gate');
    await expect(held.getByTestId('task-live-status')).not.toHaveAttribute('data-wait-reason', /.+/);

    await captureLane(page, lane, MANUAL_MODE.name);
  });

  test('full slots: every queued card waits for a slot, the crashed card keeps its cooldown', async ({ page }) => {
    await openBoard(page, FULL_SLOTS);
    const lane = page.getByTestId('lane-2-ready');

    await expectSplitSumsToLaneCount(lane, { 'hold-crash-backoff': 1, 'runner-slots-full': 2 });

    const queued = cardFor(page, 'Cache the dependency graph');
    await expect(queued.getByTestId('task-live-status')).toHaveAttribute('data-wait-reason', 'slots-full');
    await expect(queued.getByTestId('task-live-current')).toContainText('Waiting for runner slot · slots full · position 1');
    await expect(queued.getByTestId('task-live-status')).toContainText(SLOTS);
    await expect(cardFor(page, 'Recover the crashed import').getByTestId('task-live-current'))
      .toContainText('Crash cooldown');

    await captureLane(page, lane, FULL_SLOTS.name);
  });

  test('quota defer: skipped cards name the quota, the next card stays plainly queued', async ({ page }) => {
    await openBoard(page, QUOTA_DEFER);
    const lane = page.getByTestId('lane-2-ready');

    await expectSplitSumsToLaneCount(lane, { 'runner-quota-defer': 2, 'queued-queued': 1 });

    const deferred = cardFor(page, 'Tighten the review rubric');
    await expect(deferred.getByTestId('task-live-status')).toHaveAttribute('data-wait-reason', 'quota-defer');
    await expect(deferred.getByTestId('task-live-current')).toContainText('Skipped · quota defer · position 2');
    await expect(deferred.getByTestId('task-live-status')).toContainText(QUOTA);
    await expect(cardFor(page, 'Next codex card in line').getByTestId('task-live-current'))
      .toContainText('Waiting for runner slot · position 3');

    await captureLane(page, lane, QUOTA_DEFER.name);
  });
});
