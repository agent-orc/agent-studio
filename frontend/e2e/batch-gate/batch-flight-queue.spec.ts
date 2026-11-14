import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'fs';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

// AGT-3710/AGT-3712: formed batches wait for the project's one flight first
// in, first out. The batch panel lists the flight queue and names each formed
// batch's queue position. The fixture is three formed batches behind one flight.
test.use({ serviceWorkers: 'block', reducedMotion: 'reduce' });

const PROJECT = 'batch-flight-queue-fixture';
const WATCH_PATH = '/tmp/batch-flight-queue-fixture';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve('test-results', 'batch-flight-queue');
const WIDTHS = [1280, 390] as const;
const THEMES = ['light', 'dark'] as const;

const json = (route: Route, body: unknown) =>
  route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

function taskInfo(id: string, title: string) {
  return {
    id, key: id, taskKey: `${WATCH_PATH}::${id}`, title, state: '5-human-review', order: 1,
    agent: 'codex', cliType: 'codex', createdAt: '2026-11-10T03:00:00Z', lastActivity: '2026-11-10T05:11:00Z',
    watchPath: WATCH_PATH, projectName: PROJECT, folderPath: `${WATCH_PATH}/5-human-review/${id}`,
    commits: [], ownerClientId: 'local-default', tags: [],
  };
}

const formed = [
  { batchId: 'batch-31', queuedSinceUtc: '2026-11-10T03:47:00Z', taskKeys: ['AGT-11', 'AGT-12'] },
  { batchId: 'batch-32', queuedSinceUtc: '2026-11-10T04:40:00Z', taskKeys: ['AGT-13', 'AGT-14'] },
  { batchId: 'batch-33', queuedSinceUtc: '2026-11-10T04:50:00Z', taskKeys: ['AGT-15'] },
];
const cards = [...formed.flatMap(batch => batch.taskKeys), 'AGT-10']
  .map(key => taskInfo(key, `Fixture change ${key}`));
const member = (taskKey: string) => ({ taskKey, replay: { outcome: 'admitted' }, released: false });

const batchState = {
  plan: { decision: 'wait', reason: 'formed batches wait for the running flight', targetSize: 0, closeAtUtc: null,
    proposedMembers: [], upcoming: [], advisor: null },
  pending: [],
  batches: [
    ...[...formed].reverse().map((batch, index) => ({
      batchId: batch.batchId, parentBatchId: null, phase: 'formed', phaseReason: null,
      queuePosition: formed.length - index, baseSha: 'b'.repeat(40), candidateSha: null,
      members: batch.taskKeys.map(member), runs: [],
    })),
    { batchId: 'batch-30', parentBatchId: null, phase: 'running', phaseReason: null, queuePosition: null,
      baseSha: 'b'.repeat(40), candidateSha: 'c'.repeat(40), members: [member('AGT-10')],
      runs: [{ batchRunId: 'run-30', host: 'agent-runner-01', outcome: null, classification: null,
        startedAtUtc: '2026-11-10T05:05:00Z', finishedAtUtc: null }] },
  ],
  members: Object.fromEntries([
    ['AGT-10', { status: 'in-batch', batchId: 'batch-30', phase: 'running', position: 1, size: 1 }],
    ...formed.flatMap(batch => batch.taskKeys.map((taskKey, index) => [taskKey,
      { status: 'in-batch', batchId: batch.batchId, phase: 'formed', position: index + 1, size: batch.taskKeys.length }])),
  ]),
  flightQueue: formed.map((batch, index) => ({
    batchId: batch.batchId, position: index + 1, queuedSinceUtc: batch.queuedSinceUtc,
    size: batch.taskKeys.length, taskKeys: batch.taskKeys,
  })),
  isolationFindings: [],
};

async function installRoutes(page: Page): Promise<void> {
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(
    { v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__' })));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    codeNotComplete: [], review: [], autoReview: [], humanReview: cards, escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/projects/settings**', route => json(route, {}));
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, route => json(route, []));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-11-10T05:12:00Z', ttlSeconds: 600, snapshots: [] }));
  await page.route('**/api/runner/orchestrator-feed**', route => json(route, { entries: [], generatedAtUtc: '2026-11-10T05:12:00Z' }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/projects/*/batch-gate/state?history=10', route => json(route, batchState));
  await page.route('**/api/projects/*/batch-gate/failed-tests?windowDays=7', route => json(route, {
    project: PROJECT, windowDays: 7, pendingEvidenceRuns: 0, tests: [],
  }));
}

async function settle(page: Page): Promise<void> {
  await dismissDevErrorDialog(page);
  // This route-only fixture has no SignalR server; keep its offline banner out of the target surface.
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
}

test.beforeEach(() => { mkdirSync(RESULTS_DIR, { recursive: true }); });

test('batch panel lists three formed batches in flight queue order with their positions', async ({ page }) => {
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await settle(page);
  await page.locator('[data-testid="batch-lane-indicator"]').filter({ visible: true }).first().click();
  const panel = page.getByTestId('batch-panel');
  await expect(panel).toBeVisible();

  const queue = panel.getByTestId('batch-flight-queue');
  await expect(queue.getByRole('heading')).toHaveText('Flight queue (3)');
  const entries = queue.locator('li');
  await expect(entries).toHaveCount(3);
  for (const [index, batch] of formed.entries()) {
    const entry = queue.getByTestId(`batch-flight-queue-${batch.batchId}`);
    await expect(entry).toContainText(`Position ${index + 1}`);
    await expect(entry).toContainText(batch.taskKeys.join(', '));
    await expect(panel.locator(`#batch-${batch.batchId}`).getByTestId('batch-queue-position'))
      .toHaveText(`flight queue position ${index + 1} of 3`);
  }
  await expect(panel.locator('#batch-batch-30').getByTestId('batch-queue-position')).toHaveCount(0);

  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of THEMES) {
      await setTheme(page, theme);
      await queue.scrollIntoViewIfNeeded();
      await page.waitForTimeout(200);
      await page.screenshot({ path: path.join(RESULTS_DIR, `batch-flight-queue-panel-${width}-${theme}.png`) });
      await queue.screenshot({ path: path.join(RESULTS_DIR, `batch-flight-queue-list-${width}-${theme}.png`) });
      const first = panel.locator(`#batch-${formed[0].batchId}`);
      await first.scrollIntoViewIfNeeded();
      await page.waitForTimeout(200);
      await page.screenshot({ path: path.join(RESULTS_DIR, `batch-flight-queue-batches-${width}-${theme}.png`) });
    }
  }
});
