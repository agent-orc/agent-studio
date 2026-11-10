import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as nodePath from 'node:path';
import { auditModal } from '../helpers/audit-modal';

test.use({ serviceWorkers: 'block' });

const project = 'batch-fixture';
const path = '/tmp/batch-fixture';
const key = `${path}::AGT-2`;
const settings = {
  enabled: true, scope: 'code', planner: 'look-ahead', minimumSize: 2, maximumSize: 4,
  deadlineMinutes: 30, lookAheadMinutes: 15, urgentTags: ['urgent'], riskPolicy: 'balanced', calibration: 'empirical', stacking: 'off',
  overlap: 'separate', reviewTestLevel: 'work-package', gateTimeoutMinutes: 90, preCheckEnabled: true,
  advisor: { mode: 'enforce', cliType: 'codex', model: 'test-model', timeoutSeconds: 45 },
};
const state = {
  gateSlots: { busy: 2, total: 3 },
  preChecksRun: 3, preChecksRed: 1, preCheckMinutesSaved: 42,
  dailySummary: { phaseMinutes: {
    reviewToPreCheck: { p50: 8, p90: 12, cards: 4 },
    formationWait: { p50: 22, p90: 34, cards: 4 },
    batchRun: { p50: 10, p90: 20, cards: 4 },
    publish: { p50: 5, p90: 7, cards: 4 },
  }, diagnosticRerunShare: .25,
  preCheckYield: { runs: 3, red: 0, cardsKeptOut: 0, costMinutes: 158 } },
  plan: { decision: 'wait', reason: 'Advisor veto', targetSize: 2, closeAtUtc: '2026-10-11T14:05:00Z',
    proposedMembers: [key], upcoming: [], advisor: { mode: 'enforce', verdict: 'veto', reasons: ['Risk too high'], model: 'test-model', atUtc: '2026-10-11T13:00:00Z' } },
  pending: [{ taskKey: key, reviewAttemptId: 'review-1', resultSha: 'a'.repeat(40), waitMinutes: 12, risk: 'high',
    preCheck: { outcome: 'green', resultSha: 'a'.repeat(40), baseSha: 'b'.repeat(40), gateProfileDigest: 'profile', reason: 'passed', failedTests: [], completedAtUtc: '2026-10-11T13:00:00Z' } }],
  batches: [{ batchId: 'batch-7', phase: 'red', baseSha: 'b'.repeat(40), candidateSha: 'c'.repeat(40),
    members: [{ taskKey: 'AGT-2', replay: 'applied', released: false }],
    runs: [{ batchRunId: 'run-7', host: 'agent-runner-01', outcome: 'failed', classification: 'test-failure', startedAtUtc: '2026-10-11T13:00:00Z', finishedAtUtc: '2026-10-11T13:05:00Z' }] },
  { batchId: 'batch-8', parentBatchId: 'batch-7', phase: 'running', baseSha: 'b'.repeat(40), candidateSha: 'd'.repeat(40),
    formationReason: 'new batch: overlap separate from waiting batch batch-7, files src/shared.cs',
    exclusions: [{ taskKey: 'AGT-3', reason: 'overlapSeparate', detail: 'overlap separate: src/shared.cs' }],
    members: [{ taskKey: 'AGT-2', replay: 'applied', released: false }],
    runs: [{ batchRunId: 'run-8', host: 'agent-runner-02', startedAtUtc: '2026-10-11T13:06:00Z' }] }],
  members: { 'AGT-2': { status: 'in-batch', batchId: 'batch-8', phase: 'running', position: 1, size: 1 } },
};
const task = { id: 'AGT-2', key: 'AGT-2', taskKey: key, title: 'Batch fixture task', state: '4-auto-review', order: 1,
  agent: 'codex', cliType: 'codex', createdAt: '2026-10-11T12:00:00Z', watchPath: path, projectName: project,
  folderPath: `${path}/tasks/AGT-2`, lastActivity: '2026-10-11T13:00:00Z', commits: [], ownerClientId: 'local-default', tags: [] };
const grouped = { backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], codeNotComplete: [], review: [], autoReview: [task], humanReview: [], escalated: [], completed: [], archive: [] };
const json = (route: import('@playwright/test').Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
async function captureThemes(page: import('@playwright/test').Page, evidence: string | undefined, name: string): Promise<void> {
  if (!evidence) return;
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await page.screenshot({ path: nodePath.join(evidence, `${name}-${theme}.png`) });
  }
}

async function routeBoard(page: import('@playwright/test').Page, batchState: unknown,
  onSave: () => void = () => undefined, board: unknown = grouped): Promise<void> {
  await page.addInitScript(() => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({ v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__' })));
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null }));
  await page.route('**/api/v1/management/credential-inventory', route => json(route, { items: [] }));
  await page.route('**/api/v1/studio/board**', route => json(route, board));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0 }));
  await page.route('**/api/watch-paths**', route => json(route, [{ name: project, path, rootPath: path, repositoryPath: path }]));
  await page.route('**/api/v1/workspaces**', route => json(route, [{ id: 'workspace-1', displayName: 'Fixture', projects: [{
    id: project, displayName: project, shortCode: 'BAT', color: '#587a9b', sourceType: 'local-folder',
    workspaceId: 'workspace-1', storageLocation: path, rootPath: path, repositoryPath: path,
    repositoryUrl: null, urls: [], archived: false, sortOrder: 0,
  }] }]));
  const admission = { status: 'ok', message: null, overrideActive: false,
    codingAllowed: true, hostingAllowed: true, limited: false };
  await page.route('**/api/workspaces/*/quota/admission', route => json(route, {
    workspaceId: 'workspace-1', workspace: { ...admission, projectId: null, project: null },
    projects: [{ ...admission, projectId: project, project }],
  }));
  // The deferred workspace quota card on Project settings reads this snapshot.
  const meter = { modelTokens: 0, modelCostUsd: 0, computeCoreHours: 0, hostingSlots: 0 };
  await page.route(/\/api\/workspaces\/[^/]+(\/projects\/[^/]+)?\/quota$/, route => json(route, {
    workspaceId: 'workspace-1', projectId: null, weekStartUtc: '2026-10-05T00:00:00Z', weekEndUtc: '2026-10-12T00:00:00Z',
    limits: { modelTokensPerWeek: null, computeCoreHoursPerWeek: null, hostingSlots: null },
    usage: meter, remaining: meter, ...admission, meterComplete: true,
    biggestConsumers: [], biggestProjects: [], dailyTrend: [],
  }));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/usage/cockpit**', route => json(route, {
    snapshotVersion: 1, workspaceId: null, generatedAt: '2026-10-11T13:00:00Z', timeZone: 'Europe/Berlin', weekStart: 1,
    calendar: { timeZone: 'Europe/Berlin', weekStart: 1, dayStartUtc: '2026-10-11T00:00:00Z', dayEndUtc: '2026-10-12T00:00:00Z', weekStartUtc: '2026-10-05T00:00:00Z', weekEndUtc: '2026-10-12T00:00:00Z' },
    clis: [], cost: null, runs: [], slots: [], sources: {},
  }));
  await page.route('**/api/cli/quota**', route => json(route, { ttlSeconds: 600, snapshots: [] }));
  await page.route('**/api/cli/usage**', route => json(route, { at: '2026-10-11T13:00:00Z', sessions: [] }));
  await page.route('**/api/v1/management/retention/policy/**', route => json(route, { rules: [], revision: 1 }));
  await page.route('**/api/v1/management/retention/policy', route => json(route, { rules: [], revision: 1 }));
  await page.route('**/api/v1/management/credential-inventory', route => json(route, { items: [] }));
  await page.route('**/api/v1/studio/runner/status**', route => json(route, { projects: { [project]: { projectName: project, mode: 'manual', activeJobId: null, queuedJobIds: [] } } }));
  await page.route('**/api/projects/*/batch-gate/state?history=10', route => json(route, batchState));
  await page.route('**/api/projects/*/batch-gate/failed-tests?windowDays=7', route => json(route, {
    project, windowDays: 7, tests: [{ testName: 'Product.Tests.Unstable', batches: 4, runs: 5,
      distinctMemberSets: 4, firstFailureUtc: '2026-10-09T12:00:00Z', lastFailureUtc: '2026-10-11T13:00:00Z',
      latestErrorExcerpt: 'Error Message: timed out while opening card', flakyRerunPassed: true,
      flakySuspect: true, batchIds: ['batch-7', 'batch-5', 'batch-3', 'batch-1'],
      failures: [{ testName: 'Product.Tests.Unstable', batchId: 'batch-7', runId: 'run-7',
        members: ['AGT-2'], atUtc: '2026-10-11T13:00:00Z',
        errorExcerpt: 'Error Message: timed out while opening card', flakyRerunPassed: true }] }],
  }));
  await page.route('**/api/projects/*/snapshot**', route => json(route, {
    settings: { autoCommit: false, crashRecoveryEnabled: false, autoPushStrategy: 'off', runnerMode: 'manual' },
    runnerStatus: null, orchestratorLogTail: [], orchestratorSession: null, paths: null,
    reviewDecisionsPending: [], runnerPendingDecisions: [], queueHealth: null,
  }));
  await page.route('**/api/projects/*/workbenches**', route => json(route, { items: [] }));
  await page.route('**/api/projects/*/execution', route => json(route, { valid: false, issues: [], source: 'repository' }));
  await page.route('**/api/projects/*/batch-gate', route => {
    if (route.request().method() === 'PUT') { onSave(); expect(route.request().headers()['x-client-id']).toBeTruthy(); return json(route, { settings: route.request().postDataJSON(), limits: { hardMaximumSize: 8 } }); }
    return json(route, { settings, limits: { hardMaximumSize: 8 } });
  });
}

for (const width of [1280, 390]) {
  test(`batch panel contains keyboard focus and restores it on Escape at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 800 });
    await routeBoard(page, state);
    await page.goto('/?includeFixtures=true');
    await page.getByRole('tab', { name: 'All projects · Board' }).click();
    if (width === 390) await page.getByRole('combobox', { name: 'Board lane' })
      .selectOption({ label: 'Post Processing' });
    const opener = page.getByTestId('batch-lane-indicator').first();
    await opener.click();
    const dialog = page.getByTestId('batch-panel');
    await expect(dialog).toBeVisible();
    await auditModal(page, '[data-testid="batch-panel"]', `batch-panel-${width}`);
    if (process.env.JOB_RESULTS_DIR) {
      await page.screenshot({ path: nodePath.join(process.env.JOB_RESULTS_DIR, `batch-panel-keyboard-${width}.png`) });
    }
    await expect(dialog.getByRole('button', { name: 'Close batch panel' })).toBeFocused();
    await expect.poll(() => page.evaluate(() => {
      const host = document.querySelector('[data-testid="batch-panel"]')?.closest('app-batch-panel');
      return !!host && [...document.body.children].filter(child => child !== host).every(child => (child as HTMLElement).inert);
    })).toBe(true);
    for (let index = 0; index < 12; index++) {
      await page.keyboard.press('Tab');
      await expect.poll(() => page.evaluate(() =>
        !!document.querySelector('[data-testid="batch-panel"]')?.contains(document.activeElement))).toBe(true);
    }
    for (let index = 0; index < 12; index++) {
      await page.keyboard.press('Shift+Tab');
      await expect.poll(() => page.evaluate(() =>
        !!document.querySelector('[data-testid="batch-panel"]')?.contains(document.activeElement))).toBe(true);
    }
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    await expect(opener).toBeFocused();
  });
}

test('board badge, batch panel and project settings save use batch contract', async ({ page }) => {
  const evidence = process.env.JOB_RESULTS_DIR;
  if (evidence) fs.mkdirSync(evidence, { recursive: true });
  let saved = false;
  await routeBoard(page, { ...state, pushWait: {
    reason: 'Approved integration push awaits the worker or restart recovery.',
    jobId: 'AGT-2', approvedSha: 'd'.repeat(40),
    sinceUtc: '2026-10-11T13:00:00Z', ageAlert: true, ageMinutes: 4.5,
  } }, () => { saved = true; });
  await page.goto('/?includeFixtures=true');
  await page.getByRole('tab', { name: 'All projects · Board' }).click();
  // This route-only fixture has no SignalR server; keep its offline banner out of the target surface.
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  const card = page.getByTestId('task-card').filter({ hasText: 'Batch fixture task' });
  await expect(card.getByTestId('batch-member-badge')).toContainText('Batch batch-8');
  await expect(card.getByTestId('task-card-integration-push-wait'))
    .toContainText('Awaiting approved origin push · 4.5 min');
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  if (evidence) {
    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
      await card.screenshot({ path: nodePath.join(evidence, `integration-push-card-${theme}.png`) });
    }
  }
  await page.getByTestId('batch-lane-indicator').first().click();
  await expect.poll(async () => (await page.getByTestId('batch-panel').boundingBox())?.x ?? 0).toBeGreaterThan(700);
  await expect(page.getByTestId('batch-panel')).toContainText('Advisor veto');
  await expect(page.getByTestId('batch-failed-tests')).toContainText('Flaky suspect');
  await expect(page.getByTestId('batch-integration-push-wait'))
    .toContainText('Approved integration push awaits the worker or restart recovery.');
  await expect(page.getByTestId('batch-gate-slot-usage')).toHaveText('Gate slots: 2/3 busy');
  await expect(page.getByTestId('batch-pre-check-counters')).toContainText('3 run · 1 red · 42 estimated gate minutes saved');
  await expect(page.getByTestId('batch-phase-metrics')).toContainText('22.0 min');
  await expect(page.getByTestId('batch-panel')).toContainText('pre-check green');
  await expect(page.getByTestId('batch-panel')).toContainText('from batch-7');
  await expect(page.getByTestId('batch-panel')).toContainText('test-failure');
  await expect(page.getByTestId('batch-panel')).toContainText('AGT-3 · overlap separate: src/shared.cs');
  await expect(page.getByTestId('batch-panel')).toContainText('new batch: overlap separate from waiting batch batch-7, files src/shared.cs');
  await captureThemes(page, evidence, 'batch-panel');
  await page.setViewportSize({ width: 390, height: 900 });
  await captureThemes(page, evidence, 'batch-panel-390');
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.locator('.batch-panel__body').evaluate(element => { element.scrollTop = element.scrollHeight; });
  await captureThemes(page, evidence, 'batch-panel-history');
  await page.getByRole('button', { name: 'Close batch panel' }).click();
  await expect(page.getByTestId('batch-panel')).toHaveCount(0);
  await expect(page.getByTestId('batch-lane-indicator').first()).toBeFocused();
  await page.goto('/#/projects/batch-fixture/settings');
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  await expect(page.getByTestId('batch-settings')).toBeVisible();
  await expect(page.locator('[name="preCheckEnabled"]')).toBeChecked();
  await page.getByTestId('project-settings-jump-batch').click();
  await expect(page.getByTestId('batch-settings')).toBeFocused();
  await expect(page.getByTestId('batch-settings-calibration')).toHaveValue('empirical');
  await expect(page.getByTestId('batch-settings-stacking')).toHaveValue('off');
  await page.getByTestId('batch-settings-enabled').uncheck();
  await page.getByTestId('batch-settings-scope').selectOption('documentation');
  await page.getByTestId('batch-settings-enabled').check();
  await expect(page.getByTestId('batch-settings-scope')).toHaveValue('code');
  await expect(page.getByTestId('batch-settings-scope-defaulted')).toContainText('Scope set to Code.');
  await page.getByTestId('batch-settings-stacking').selectOption('1');
  if (evidence) {
    await page.getByTestId('batch-settings-stacking').scrollIntoViewIfNeeded();
    await page.getByTestId('batch-settings').screenshot({ path: nodePath.join(evidence, 'batch-settings-calibration-stacking.png') });
  }
  await page.getByTestId('batch-settings-save').click();
  await expect.poll(() => saved).toBe(true);
  await expect(page.getByTestId('batch-settings')).toContainText('Saved');
  await expect(page.getByTestId('batch-settings-stacking')).toHaveValue('1');
  await expect(page.getByTestId('batch-pre-check-yield')).toContainText('Pre-check yield is 0 today.');
  await expect(page.getByTestId('batch-pre-check-yield')).toContainText('158.0 gate minutes');
  await page.locator('#batch-settings-title').scrollIntoViewIfNeeded();
  await captureThemes(page, evidence, 'batch-settings');
  await page.setViewportSize({ width: 390, height: 900 });
  await page.getByTestId('studio-ab-explorer').click();
  await page.getByTestId('batch-phase-metrics').scrollIntoViewIfNeeded();
  await captureThemes(page, evidence, 'batch-settings-390');
  await page.getByTestId('batch-pre-check-yield').scrollIntoViewIfNeeded();
  await captureThemes(page, evidence, 'batch-settings-yield-390');
});

test('failed-test ranking stays readable at wide and mobile widths in both themes', async ({ page }) => {
  await routeBoard(page, state);
  await page.route('**/api/projects/*/batch-gate/state?history=500', route => json(route, {
    ...state, batches: [...state.batches, {
      batchId: 'batch-1', phase: 'red', baseSha: 'b'.repeat(40), candidateSha: 'c'.repeat(40),
      members: [{ taskKey: 'AGT-4', replay: 'applied', released: false }], runs: [],
    }],
  }));
  await page.goto('/?includeFixtures=true');
  await page.getByRole('tab', { name: 'All projects · Board' }).click();
  await page.getByTestId('batch-lane-indicator').first().click();
  await expect(page.getByTestId('batch-failed-tests')).toContainText('Product.Tests.Unstable');
  const evidence = process.env.JOB_RESULTS_DIR;
  if (!evidence) return;
  fs.mkdirSync(evidence, { recursive: true });
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 });
    await expect(page.getByTestId('batch-panel')).toBeVisible();
    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
      await page.screenshot({
        path: nodePath.join(evidence, `failed-tests-${width}-${theme}.png`),
      });
    }
  }
  await page.getByRole('link', { name: 'batch-1' }).click();
  await expect(page.locator('#batch-batch-1')).toBeVisible();
  await page.getByRole('button', { name: 'Create fix card for Product.Tests.Unstable' }).click();
  await expect(page.getByTestId('create-title')).toHaveValue('Fix flaky test: Product.Tests.Unstable');
  await expect(page.getByTestId('create-prompt')).toHaveValue(/Batches: batch-7, batch-5, batch-3, batch-1/);
  await expect(page.getByTestId('create-task-type-bug')).toHaveAttribute('aria-pressed', 'true');
});

test('ejected batch member badge names why it went to the per-task gate', async ({ page }) => {
  const evidence = process.env.JOB_RESULTS_DIR;
  if (evidence) fs.mkdirSync(evidence, { recursive: true });
  const detail = 'Immutable result ref does not resolve to the expected SHA.';
  await routeBoard(page, {
    ...state,
    members: { 'AGT-2': { status: 'fallback', batchId: 'batch-8', phase: 'running', position: 1, size: 1, reason: 'unattributable', reasonDetail: detail } },
  });
  await page.goto('/?includeFixtures=true');
  await page.getByRole('tab', { name: 'All projects · Board' }).click();
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  const card = page.getByTestId('task-card').filter({ hasText: 'Batch fixture task' });
  const badge = card.getByTestId('batch-member-badge');
  await expect(badge).toHaveText('Per-task fallback · unattributable history');
  await expect(badge).toHaveAttribute('data-reason', 'unattributable');
  await expect(badge).toHaveAttribute('title', detail);
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  if (!evidence) return;
  for (const theme of ['light', 'dark'] as const) {
    await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
    await card.screenshot({ path: nodePath.join(evidence, `batch-member-ejected-${theme}.png`) });
  }
});

test('review concern fix wait is visible on the card and batch gate', async ({ page }) => {
  const evidence = process.env.JOB_RESULTS_DIR;
  if (evidence) fs.mkdirSync(evidence, { recursive: true });
  await routeBoard(page, {
    ...state,
    pending: [],
    reviewFixWaits: [{ taskKey: 'AGT-2', reviewAttemptId: 'review-7', round: 1,
      maximum: 1, aspects: ['code-quality'] }],
    members: { 'AGT-2': { status: 'review-fix', reason: 'actionable review concern',
      reasonDetail: 'Review review-7 found a behavior gap in code-quality. Fix round 1 of 1 must finish before batch admission.' } },
  }, () => undefined, { ...grouped, ready: [{ ...task, state: '2-ready' }], autoReview: [] });
  await page.goto('/?includeFixtures=true');
  await page.getByRole('tab', { name: 'All projects · Board' }).click();
  const card = page.getByTestId('task-card').filter({ hasText: 'Batch fixture task' });
  await expect(card.getByTestId('batch-member-badge')).toContainText('Review fix before batch');
  if (evidence) {
    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
      await card.screenshot({ path: nodePath.join(evidence, `batch-review-fix-card-${theme}.png`) });
    }
  }
  await page.getByTestId('batch-lane-indicator').first().click();
  await expect(page.getByTestId('batch-review-fix-waits')).toContainText('AGT-2 · concern fix round 1 of 1 · code-quality');
  await captureThemes(page, evidence, 'batch-review-fix-wait');
});

test('red, abandoned and running batches show truthful status and evidence at desktop and mobile widths', async ({ page }) => {
  const batchState = {
    ...state,
    gateSlots: { busy: 1, total: 1 },
    plan: { ...state.plan, decision: 'idle', reason: 'no eligible cards', proposedMembers: [] },
    pending: [],
    batches: [
      { ...state.batches[0], phase: 'red', phaseReason: 'Suite failed; isolate the culprit',
        phaseHistory: [{ phase: 'running', atUtc: '2026-10-11T13:00:00Z' },
          { phase: 'red', reason: 'Suite failed; isolate the culprit', atUtc: '2026-10-11T13:05:00Z' }],
        runs: [{ batchRunId: 'run-7', host: 'agent-runner-01', startedAtUtc: '2026-10-11T13:00:00Z', evidenceAvailable: true }] },
      { ...state.batches[1], batchId: 'batch-abandoned', parentBatchId: null, phase: 'abandoned',
        members: [{ taskKey: 'AGT-2', replay: null, released: false }],
        runs: [{ batchRunId: 'run-abandoned', host: 'agent-runner-01', startedAtUtc: '2026-10-11T13:01:00Z', evidenceAvailable: true }] },
      { ...state.batches[1], batchId: 'batch-running', parentBatchId: null, phase: 'running',
        runs: [{ batchRunId: 'run-running', host: 'agent-runner-02', startedAtUtc: '2026-10-11T13:06:00Z', evidenceAvailable: true }] },
    ],
    members: { 'AGT-2': { status: 'in-batch', batchId: 'batch-7', phase: 'red', position: 1, size: 2 } },
  };
  await routeBoard(page, batchState);
  await page.goto('/?includeFixtures=true');
  await page.getByRole('tab', { name: 'All projects · Board' }).click();
  const card = page.getByTestId('task-card').filter({ hasText: 'Batch fixture task' });
  await expect(card.getByTestId('batch-member-badge')).toContainText('in a red batch: Suite failed; isolate the culprit');
  await expect(page.getByTestId('batch-lane-indicator').first()).toContainText('1 running');
  const evidence = process.env.JOB_RESULTS_DIR;
  if (evidence) {
    fs.mkdirSync(evidence, { recursive: true });
    await page.setViewportSize({ width: 1280, height: 900 });
    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
      await card.screenshot({ path: nodePath.join(evidence, `batch-red-member-1280-${theme}.png`) });
    }
  }
  await page.getByTestId('batch-lane-indicator').first().click();
  const panel = page.getByTestId('batch-panel');
  await expect(panel.locator('#batch-batch-7')).toContainText('interrupted');
  await expect(panel.locator('#batch-batch-abandoned')).toContainText('interrupted');
  await expect(panel.locator('#batch-batch-running')).toContainText('running');
  await expect(panel.getByTestId('batch-red-next')).toContainText('waits for a gate slot');
  await expect(panel.getByTestId('batch-close-now')).toBeDisabled();
  await expect(panel.getByTestId('batch-close-unavailable')).toHaveText('No eligible cards to close.');
  await expect(panel.locator('#batch-batch-7').getByRole('link', { name: /Batch fixture task/ })).toHaveAttribute('href', '#/tasks/AGT-2');
  await expect(panel.locator('#batch-batch-7').getByRole('link', { name: 'Evidence' }))
    .toHaveAttribute('href', /run-evidence\?batchId=batch-7&runId=run-7/);
  await expect(panel.locator('#batch-batch-7').getByRole('link', { name: 'Run log' }))
    .toHaveAttribute('href', /run-log\?batchId=batch-7&runId=run-7/);
  await expect(panel.locator('#batch-batch-7').getByText('Phase history')).toBeVisible();
  await expect(panel.locator('#batch-batch-7').getByText('Oct 11, 2026', { exact: false }).first()).toBeHidden();
  await panel.locator('#batch-batch-7 summary').click();
  await expect(panel.locator('#batch-batch-7').getByText('Oct 11, 2026', { exact: false }).first()).toBeVisible();
  if (!evidence) return;
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of ['light', 'dark'] as const) {
      await page.evaluate(value => { document.documentElement.dataset['studioTheme'] = value; }, theme);
      for (const id of ['batch-7', 'batch-abandoned', 'batch-running']) {
        await panel.locator(`#batch-${id}`).screenshot({ path: nodePath.join(evidence, `batch-status-${id}-${width}-${theme}.png`) });
      }
    }
  }
});
