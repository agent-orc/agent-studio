import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'fs';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

// AGT-3577: after a red batch the UI names the member the bisection proved
// red, the failing tests of the run that proved it, and its evidence. The
// fixture is a four-member red batch (batch-14) bisected to AGT-2.
test.use({ serviceWorkers: 'block', reducedMotion: 'reduce' });

const PROJECT = 'batch-isolation-fixture';
const WATCH_PATH = '/tmp/batch-isolation-fixture';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve('test-results', 'batch-isolation');
const WIDTHS = [1280, 390] as const;
const THEMES = ['light', 'dark'] as const;

const json = (route: Route, body: unknown) =>
  route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

function taskInfo(id: string, state: string, title: string) {
  return {
    id, key: id, taskKey: `${WATCH_PATH}::${id}`, title, state, order: 1,
    agent: 'codex', cliType: 'codex', createdAt: '2026-11-09T08:00:00Z', lastActivity: '2026-11-09T09:52:00Z',
    watchPath: WATCH_PATH, projectName: PROJECT, folderPath: `${WATCH_PATH}/${state}/${id}`,
    commits: [], ownerClientId: 'local-default', tags: [],
  };
}

const culprit = taskInfo('AGT-2', '5e-escalated', 'Checkout totals include shipping');
const survivors = [
  taskInfo('AGT-1', '5-human-review', 'Cart badge counts items'),
  taskInfo('AGT-3', '5-human-review', 'Docs: checkout glossary'),
  taskInfo('AGT-4', '5-human-review', 'Refresh payment icons'),
];

const run = (id: string, outcome: string, start: string, end: string) => ({ batchRunId: id, host: 'agent-runner-01',
  outcome, classification: outcome === 'fail' ? 'DeterministicSuiteRed' : null, startedAtUtc: start, finishedAtUtc: end });
const member = (taskKey: string, released = false) => ({ taskKey, replay: { outcome: 'admitted' }, released });
const evidence = (batchId: string) => `/var/lib/agentstudio/batch-gates/${batchId}/gate-evidence.jsonl`;
const proven = (taskKey: string, status: string, batchId: string, runId: string, at: string, extra: object = {}) => ({
  project: PROJECT, parentBatchId: 'batch-14', taskKey, runAttempt: 'run-1', resultSha: 'a'.repeat(40),
  baseSha: 'b'.repeat(40), status, evidencePath: evidence(batchId), recordedAtUtc: at,
  provingBatchId: batchId, provingRunId: runId, failures: [], evidenceAvailable: false, ...extra,
});
const failingTests = [
  { testName: 'Checkout.Tests.TotalsIncludeShipping',
    errorExcerpt: 'Error Message:\n Assert.Equal() Failure: Values differ\n Expected: 12.50\n Actual:   10.00' },
  { testName: 'Checkout.Tests.EmptyCartHasNoTotal', errorExcerpt: '' },
];

const batchState = {
  plan: { decision: 'idle', reason: 'no eligible cards', targetSize: 0, closeAtUtc: null,
    proposedMembers: [], upcoming: [], advisor: null },
  pending: [],
  batches: [
    { batchId: 'batch-18', parentBatchId: 'batch-14', phase: 'published', baseSha: 'b'.repeat(40), candidateSha: 'e'.repeat(40),
      members: [member('AGT-1', true)], runs: [run('run-18', 'pass', '2026-11-09T09:40:00Z', '2026-11-09T09:52:00Z')],
      publication: { verifiedRemoteSha: 'e'.repeat(40) } },
    { batchId: 'batch-17', parentBatchId: 'batch-14', phase: 'red', baseSha: 'b'.repeat(40), candidateSha: 'f'.repeat(40),
      members: [member('AGT-2')], runs: [run('run-17', 'fail', '2026-11-09T09:40:00Z', '2026-11-09T09:51:00Z')] },
    { batchId: 'batch-16', parentBatchId: 'batch-14', phase: 'published', baseSha: 'b'.repeat(40), candidateSha: 'd'.repeat(40),
      members: [member('AGT-3', true), member('AGT-4', true)], runs: [run('run-16', 'pass', '2026-11-09T09:20:00Z', '2026-11-09T09:33:00Z')],
      publication: { verifiedRemoteSha: 'd'.repeat(40) } },
    { batchId: 'batch-15', parentBatchId: 'batch-14', phase: 'red', baseSha: 'b'.repeat(40), candidateSha: 'a'.repeat(40),
      members: [member('AGT-1'), member('AGT-2')], runs: [run('run-15', 'fail', '2026-11-09T09:20:00Z', '2026-11-09T09:34:00Z')] },
    { batchId: 'batch-14', parentBatchId: null, phase: 'red', phaseReason: 'DeterministicSuiteRed',
      baseSha: 'b'.repeat(40), candidateSha: 'c'.repeat(40),
      members: [member('AGT-1'), member('AGT-2'), member('AGT-3'), member('AGT-4')],
      runs: [run('run-14', 'fail', '2026-11-09T09:00:00Z', '2026-11-09T09:15:00Z')] },
  ],
  members: {
    'AGT-1': { status: 'published', batchId: 'batch-18', phase: 'published', position: 1, size: 1 },
    'AGT-2': { status: 'in-batch', batchId: 'batch-17', phase: 'red', position: 1, size: 1 },
    'AGT-3': { status: 'published', batchId: 'batch-16', phase: 'published', position: 1, size: 2 },
    'AGT-4': { status: 'published', batchId: 'batch-16', phase: 'published', position: 2, size: 2 },
  },
  isolationFindings: [
    proven('AGT-3', 'proven-green', 'batch-16', 'run-16', '2026-11-09T09:33:00Z'),
    proven('AGT-4', 'proven-green', 'batch-16', 'run-16', '2026-11-09T09:33:00Z'),
    proven('AGT-2', 'proven-red', 'batch-17', 'run-17', '2026-11-09T09:51:00Z',
      { failures: failingTests, evidenceAvailable: true }),
    proven('AGT-1', 'proven-green', 'batch-18', 'run-18', '2026-11-09T09:52:00Z'),
  ],
};

const failedTests = {
  project: PROJECT, windowDays: 7, pendingEvidenceRuns: 0,
  tests: failingTests.map(test => ({
    testName: test.testName, batches: 3, runs: 3, distinctMemberSets: 3,
    firstFailureUtc: '2026-11-09T09:15:00Z', lastFailureUtc: '2026-11-09T09:51:00Z',
    latestErrorExcerpt: test.errorExcerpt, flakyRerunPassed: false, flakySuspect: false,
    batchIds: ['batch-17', 'batch-15', 'batch-14'],
    failures: [
      { testName: test.testName, batchId: 'batch-17', runId: 'run-17', members: ['AGT-2'],
        atUtc: '2026-11-09T09:51:00Z', errorExcerpt: test.errorExcerpt, flakyRerunPassed: false },
      { testName: test.testName, batchId: 'batch-15', runId: 'run-15', members: ['AGT-1', 'AGT-2'],
        atUtc: '2026-11-09T09:34:00Z', errorExcerpt: test.errorExcerpt, flakyRerunPassed: false },
      { testName: test.testName, batchId: 'batch-14', runId: 'run-14', members: ['AGT-1', 'AGT-2', 'AGT-3', 'AGT-4'],
        atUtc: '2026-11-09T09:15:00Z', errorExcerpt: test.errorExcerpt, flakyRerunPassed: false },
    ],
  })),
};

async function installRoutes(page: Page): Promise<void> {
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify(
    { v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__' })));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    codeNotComplete: [], review: [], autoReview: [], humanReview: survivors, escalated: [culprit], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/projects/settings**', route => json(route, {}));
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, route => json(route, []));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-11-09T10:00:00Z', ttlSeconds: 600, snapshots: [] }));
  await page.route('**/api/runner/orchestrator-feed**', route => json(route, { entries: [], generatedAtUtc: '2026-11-09T10:00:00Z' }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/projects/*/batch-gate/state?history=10', route => json(route, batchState));
  await page.route('**/api/projects/*/batch-gate/failed-tests?windowDays=7', route => json(route, failedTests));
  // The evidence link opens a new tab, so this route sits on the browser context.
  await page.context().route('**/api/projects/*/batch-gate/isolation-evidence**', route => route.fulfill({
    status: 200, contentType: 'text/plain; charset=utf-8',
    body: '{"gateRunId":"run-17","output":"Failed Checkout.Tests.TotalsIncludeShipping"}\n',
  }));
  for (const card of [culprit, ...survivors]) {
    await page.route(new RegExp(`/api/tasks/${card.id}/runs(\\?|$)`), route => json(route, { runs: [] }));
    await page.route(new RegExp(`/api/tasks/${card.id}/session-events(\\?|$)`), route => json(route, { events: [], sessionChain: [] }));
    await page.route(new RegExp(`/api/tasks/${card.id}/pipeline(\\?|$)`), route => json(route, {
      pipeline: { id: 'task', displayName: 'Task pipeline', version: 1, pre: [], core: [], post: [], allSteps: [] },
      execution: null, cost: null, config: {},
    }));
    await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${card.id}(\\?|$)`), route => json(route, {
      info: card, promptMarkdown: `# ${card.title}`, statusMarkdown: null, log: [], promptHistory: [],
      contextUsage: null, reviewEvidence: [], summaryState: null,
    }));
  }
}

async function settle(page: Page): Promise<void> {
  await dismissDevErrorDialog(page);
  // This route-only fixture has no SignalR server; keep its offline banner out of the target surface.
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
}

test.beforeEach(() => { mkdirSync(RESULTS_DIR, { recursive: true }); });

test('lane indicator and batch panel name the proven-red member, its failing tests and its evidence', async ({ page }) => {
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await settle(page);
  const indicator = page.locator('[data-testid="batch-lane-indicator"][data-batch-alert="red"]').first();
  await expect(indicator).toHaveAttribute('data-batch-id', 'batch-14');
  await expect(indicator.getByTestId('batch-lane-alert')).toHaveText('Batch batch-14 red');
  await expect(indicator.getByTestId('batch-lane-alert')).toHaveAttribute('data-tone', 'danger');
  await expect(indicator).toContainText('AGT-2 proven red');
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    await indicator.scrollIntoViewIfNeeded();
    for (const theme of THEMES) {
      await setTheme(page, theme);
      await page.waitForTimeout(200);
      await indicator.screenshot({ path: path.join(RESULTS_DIR, `batch-isolation-lane-${width}-${theme}.png`) });
    }
  }

  await page.setViewportSize({ width: 1280, height: 900 });
  await indicator.click();
  const panel = page.getByTestId('batch-panel');
  await expect(panel).toBeVisible();
  const isolation = panel.locator('#batch-batch-14').getByTestId('batch-isolation');
  const rows = isolation.getByTestId('batch-isolation-member');
  await expect(rows).toHaveCount(4);
  await expect(rows.getByTestId('batch-isolation-status')).toHaveText(['Proven green', 'Proven red', 'Proven green', 'Proven green']);
  const red = isolation.locator('[data-testid="batch-isolation-member"][data-task-key="AGT-2"]');
  await expect(red.getByTestId('batch-proven-red-test')).toHaveText(failingTests.map(test => test.testName));
  await expect(red.getByTestId('batch-proven-red-excerpt')).toContainText('Expected: 12.50');
  await expect(red.getByTestId('batch-proven-red-evidence-path')).toHaveText(evidence('batch-17'));
  await expect(red).toContainText('Proved by batch batch-17, run run-17');
  const link = red.getByTestId('batch-proven-red-evidence');
  await expect(link).toHaveAttribute('href',
    `/api/projects/${PROJECT}/batch-gate/isolation-evidence?parentBatchId=batch-14&taskKey=AGT-2`);
  const failed = panel.getByTestId('batch-failed-tests');
  await expect(failed.getByTestId('batch-failure-runs').first()).toContainText('batch-17 · run run-17 · members AGT-2 · proved AGT-2 red');
  await expect(failed.getByTestId('batch-failure-runs').first()).toContainText('members AGT-1, AGT-2, AGT-3, AGT-4');
  // A deterministic failure is no flaky suspect and still offers the fix card.
  await expect(failed).not.toContainText('Flaky suspect');
  await expect(panel.getByRole('button', { name: 'Create fix card for Checkout.Tests.TotalsIncludeShipping' })).toBeVisible();

  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of THEMES) {
      await setTheme(page, theme);
      await failed.scrollIntoViewIfNeeded();
      await page.waitForTimeout(200);
      await page.screenshot({ path: path.join(RESULTS_DIR, `batch-isolation-failed-tests-${width}-${theme}.png`) });
      await isolation.scrollIntoViewIfNeeded();
      await page.waitForTimeout(200);
      await page.screenshot({ path: path.join(RESULTS_DIR, `batch-isolation-panel-${width}-${theme}.png`) });
      await red.screenshot({ path: path.join(RESULTS_DIR, `batch-isolation-culprit-row-${width}-${theme}.png`) });
    }
  }

  await page.setViewportSize({ width: 1280, height: 900 });
  await panel.getByRole('button', { name: 'Create fix card for Checkout.Tests.TotalsIncludeShipping' }).click();
  await expect(page.getByTestId('create-title')).toHaveValue('Fix failing test: Checkout.Tests.TotalsIncludeShipping');
  await expect(page.getByTestId('create-prompt')).toHaveValue(/Batch isolation proved AGT-2 red in batch batch-17, run run-17\./);
});

test('the culprit card says it was proven red in batch-14 and lists the failing tests', async ({ page }) => {
  await installRoutes(page);
  await page.goto(`/?job=${encodeURIComponent(culprit.id)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await settle(page);
  const section = page.getByTestId('task-batch-verification');
  await expect(section).toBeVisible();
  const provenRed = section.getByTestId('task-batch-proven-red');
  await expect(provenRed.getByTestId('task-batch-proven-red-status')).toHaveText('Proven red in batch-14');
  await expect(provenRed.getByTestId('batch-proven-red-test')).toHaveText(failingTests.map(test => test.testName));
  await expect(provenRed.getByTestId('batch-proven-red-evidence')).toBeVisible();
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of THEMES) {
      await setTheme(page, theme);
      await section.scrollIntoViewIfNeeded();
      await page.waitForTimeout(200);
      await section.screenshot({ path: path.join(RESULTS_DIR, `batch-isolation-card-${width}-${theme}.png`) });
    }
  }

  const evidenceTab = page.waitForEvent('popup');
  await provenRed.getByTestId('batch-proven-red-evidence').click();
  const opened = await evidenceTab;
  await expect(opened.locator('body')).toContainText('Failed Checkout.Tests.TotalsIncludeShipping');
  await opened.close();
});

test('a survivor card shows no proven-red notice', async ({ page }) => {
  await installRoutes(page);
  await page.goto(`/?job=AGT-1&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await settle(page);
  await expect(page.getByTestId('task-batch-verification')).toBeVisible();
  await expect(page.getByTestId('task-batch-proven-red')).toHaveCount(0);
});
