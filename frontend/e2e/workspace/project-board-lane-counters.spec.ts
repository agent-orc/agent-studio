import { test, expect, type Page, type Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

const PROJECT = 'Lane Counter Project';
const WATCH_PATH = 'C:/fixtures/lane-counter-project';
const evidenceCaptureState = process.env['TASK_EVIDENCE_CAPTURE_STATE'] === 'before' ? 'before' : 'after';

interface TaskFixture {
  id: string;
  taskKey: string;
  title: string;
  state: string;
  order: number;
  agent: string;
  cliType: string;
  createdAt: string;
  watchPath: string;
  projectName: string;
  folderPath: string;
  lastActivity: string;
  sessionName: null;
  model: null;
  useOwnSession: null;
  lastUsage: null;
  execution: null;
  commit: null;
  ownerClientId: string;
  tags: string[];
  testEvidence?: {
    runId: string | null;
    runCommit: string | null;
    runState: string | null;
    runResult: string | null;
    matchQuality: string;
    direction: string;
    distance: number | null;
    diffContained: boolean;
    evidenceState: string;
    awaitingEvidence: boolean;
    summary: string;
    sources?: Array<{
      kind: string;
      id: string;
      commit: string;
      result: string;
      observedAt: string;
      summary: string;
    }>;
  };
}

function task(id: string, state: string, order: number, testEvidence?: TaskFixture['testEvidence']): TaskFixture {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title: id,
    state,
    order,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-06-09T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${id}`,
    lastActivity: '2026-06-09T08:00:00Z',
    sessionName: null,
    model: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    ownerClientId: 'local-default',
    tags: [],
    testEvidence,
  };
}

const UNASSIGNED_EVIDENCE: NonNullable<TaskFixture['testEvidence']> = {
  runId: null,
  runCommit: null,
  runState: null,
  runResult: null,
  matchQuality: 'none',
  direction: 'none',
  distance: null,
  diffContained: false,
  evidenceState: 'unassigned',
  awaitingEvidence: false,
  summary: 'No test evidence assigned: card has no commit',
};

const READY = [
  task('ready-one', '2-ready', 1, {
    runId: 'TR-perfect', runCommit: 'a'.repeat(40), runState: 'completed', runResult: 'passed',
    matchQuality: 'perfect', direction: 'exact', distance: 0, diffContained: true,
    evidenceState: 'proven', awaitingEvidence: false, summary: 'Perfect match',
  }),
  task('ready-two', '2-ready', 2, {
    runId: 'TR-later', runCommit: 'b'.repeat(40), runState: 'completed', runResult: 'passed',
    matchQuality: 'contains-diff', direction: 'after', distance: 10, diffContained: true,
    evidenceState: 'proven', awaitingEvidence: false, summary: '10 commit(s) after, diff included',
  }),
  task('ready-three', '2-ready', 3, UNASSIGNED_EVIDENCE),
];
const PROGRESS = [
  task('progress-one', '3-progress', 1, UNASSIGNED_EVIDENCE),
  task('progress-two', '3-progress', 2),
];
const HUMAN_REVIEW = [
  task('review-one', '5-human-review', 1, UNASSIGNED_EVIDENCE),
  task('review-two', '5-human-review', 2),
  task('review-three', '5-human-review', 3),
  task('review-four', '5-human-review', 4),
  task('review-five', '5-human-review', 5),
];

const GROUPED = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: READY,
  progress: PROGRESS,
  failedPickup: [],
  codeNotComplete: [],
  review: [],
  autoReview: [],
  humanReview: HUMAN_REVIEW,
  escalated: [],
  completed: [],
  archive: [task('archived', '7-archive', 1)],
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installRoutes(page: Page, grouped: () => typeof GROUPED = () => GROUPED): Promise<void> {
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    if (url.includes('/api/auth/status')) {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (url.includes('/api/tasks/grouped') || url.includes('/api/tasks/grouped')) {
      return json(route, grouped());
    }
    if (url.includes('/api/tasks/archive')) {
      return json(route, { items: [], total: 0, offset: 0, limit: 50 });
    }
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) {
      const snapshot = grouped();
      return json(route, Object.values(snapshot).flat());
    }
    if (url.includes('/api/watch-paths')) {
      return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    }
    if (new URL(url).pathname === '/api/projects') {
      return json(route, [{
        id: 'PROJ-LANE',
        displayName: PROJECT,
        shortCode: 'LANE',
        workspaceId: 'ws-lane',
        color: null,
        cliDefault: null,
        modelDefault: null,
        sortOrder: 0,
        storageLocation: WATCH_PATH,
        archived: false,
        createdAt: '2026-06-09T08:00:00Z',
      }]);
    }
    if (url.includes('/api/workspaces')) {
      return json(route, [{
        id: 'ws-lane',
        displayName: 'Lane Workspace',
        sortOrder: 0,
        isDefault: true,
        color: null,
        createdAt: '2026-06-09T08:00:00Z',
        projects: [{
          id: 'PROJ-LANE',
          displayName: PROJECT,
          shortCode: 'LANE',
          workspaceId: 'ws-lane',
          color: null,
          cliDefault: null,
          modelDefault: null,
          sortOrder: 0,
          storageLocation: WATCH_PATH,
          archived: false,
          createdAt: '2026-06-09T08:00:00Z',
        }],
      }]);
    }
    if (url.includes('/api/runner/status')) {
      return json(route, {
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      });
    }
    if (url.includes('/api/runner/token-summary-aggregate')) return json(route, {});
    if (url.includes('/api/tags')) return json(route, []);
    if (url.includes('/api/clients')) return json(route, []);
    if (url.includes('/api/cli/usage')) return json(route, { at: '2026-06-09T08:00:00Z', sessions: [] });
    if (url.includes('/api/cli/quota')) return json(route, { at: '2026-06-09T08:00:00Z', ttlSeconds: 600, snapshots: [] });
    if (url.includes('/api/environment')) {
      return json(route, { isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } });
    }
    if (url.includes('/api/agent-rules')) return json(route, []);
    return json(route, []);
  });
}

async function boot(page: Page, grouped: () => typeof GROUPED = () => GROUPED): Promise<void> {
  await page.addInitScript((project) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: project }],
      activeKey: `board:${project}`,
    }));
    localStorage.setItem('atp.studio.explorer.expanded', JSON.stringify([project]));
    localStorage.removeItem('atp.studio.explorerSections');
  }, PROJECT);
  await installRoutes(page, grouped);
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 15_000 });
}

test('Explorer Project Board row shows subtle live lane counters', async ({ page }) => {
  await boot(page);

  const board = page.getByTestId(`studio-explorer-project-board-${PROJECT}`);
  await expect(board).toBeVisible();
  await expect(board).toHaveAttribute('aria-label', 'Board, 3 ready, 2 in progress, 5 human review');
  await expect(page.getByTestId(`studio-explorer-project-board-counts-${PROJECT}`))
    .toHaveAttribute('aria-label', '3 ready, 2 in progress, 5 human review');
  await expect(page.getByTestId(`studio-explorer-project-board-count-ready-${PROJECT}`)).toHaveText('3');
  await expect(page.getByTestId(`studio-explorer-project-board-count-progress-${PROJECT}`)).toHaveText('2');
  await expect(page.getByTestId(`studio-explorer-project-board-count-human-review-${PROJECT}`)).toHaveText('5');

  const styles = await page.getByTestId(`studio-explorer-project-board-count-ready-${PROJECT}`).evaluate((el) => {
    const computed = getComputedStyle(el);
    return {
      color: computed.color,
      background: computed.backgroundColor,
      border: computed.borderColor,
    };
  });

  expect(styles.color).not.toBe(styles.background);
  expect(styles.border).not.toBe(styles.background);

  const screenshotPath = test.info().outputPath('project-board-lane-counters.png');
  await board.screenshot({ path: screenshotPath });
  await test.info().attach('project-board-lane-counters', {
    path: screenshotPath,
    contentType: 'image/png',
  });
});

test('project context menu is scoped to the project row', async ({ page }) => {
  await boot(page);

  const projectRow = page.getByTestId(`studio-explorer-project-${PROJECT}`);
  const boardRow = page.getByTestId(`studio-explorer-project-board-${PROJECT}`);
  const menu = page.getByTestId('studio-explorer-proj-ctx-panel');

  // The dev-only NG0919 dialog may overlay the shell under ng serve. A forced
  // browser click still dispatches the real right-click event to the row.
  await projectRow.click({ button: 'right', force: true });
  await expect(menu).toBeVisible();
  await expect(page.getByTestId('studio-explorer-proj-ctx-item-rename')).toBeVisible();
  await expect(page.getByTestId('studio-explorer-proj-ctx-item-delete')).toBeVisible();

  await page.getByTestId('app-menu-backdrop').dispatchEvent('mousedown');
  await expect(menu).not.toBeVisible();

  await boardRow.click({ button: 'right', force: true });
  await expect(menu).not.toBeVisible();
});

test('each lane counter explains its lane via the canonical appTooltip', async ({ page }) => {
  await boot(page);

  await expect(page.getByTestId(`studio-explorer-project-board-counts-${PROJECT}`)).toBeVisible();

  const tip = page.getByTestId('cac-tooltip');

  // Grey counter = Ready (2-ready).
  await page.getByTestId(`studio-explorer-project-board-count-ready-${PROJECT}`).hover();
  await expect(tip).toBeVisible({ timeout: 5_000 });
  await expect(tip).toHaveText(/Ready.*queued for a coding agent/);

  // Orange counter = In Progress (3-progress).
  await page.getByTestId(`studio-explorer-project-board-count-progress-${PROJECT}`).hover();
  await expect(tip).toBeVisible({ timeout: 5_000 });
  await expect(tip).toHaveText(/In Progress.*actively running/);

  // Review-hue counter = Human Review (5-human-review).
  await page.getByTestId(`studio-explorer-project-board-count-human-review-${PROJECT}`).hover();
  await expect(tip).toBeVisible({ timeout: 5_000 });
  await expect(tip).toHaveText(/Human Review.*waiting for your review/);

  const tipShot = test.info().outputPath('project-board-lane-counter-tooltip.png');
  await tip.screenshot({ path: tipShot });
  await test.info().attach('project-board-lane-counter-tooltip', {
    path: tipShot,
    contentType: 'image/png',
  });
});

test('missing test evidence is lane-aware while recorded evidence remains visible', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1300 });
  await boot(page);

  const evidence = page.getByTestId('task-card-test-evidence');
  await expect(evidence).toHaveCount(evidenceCaptureState === 'before' ? 5 : 3);
  await expect(evidence.filter({ hasText: 'Perfect match' })).toHaveAttribute('data-match-quality', 'perfect');
  await expect(evidence.filter({ hasText: '10 commit(s) after, diff included' })).toHaveAttribute('data-match-quality', 'contains-diff');
  const readyWithoutDelivery = page.getByTestId('task-card').filter({ hasText: 'ready-three' });
  const progressWithoutDelivery = page.getByTestId('task-card').filter({ hasText: 'progress-one' });
  const reviewWithoutDelivery = page.getByTestId('task-card').filter({ hasText: 'review-one' });
  if (evidenceCaptureState === 'before') {
    await expect(readyWithoutDelivery.getByTestId('task-card-test-evidence')).toBeVisible();
    await expect(progressWithoutDelivery.getByTestId('task-card-test-evidence')).toBeVisible();
  } else {
    await expect(readyWithoutDelivery.getByTestId('task-card-test-evidence')).toHaveCount(0);
    await expect(progressWithoutDelivery.getByTestId('task-card-test-evidence')).toHaveCount(0);
  }
  const reviewEvidence = reviewWithoutDelivery.getByTestId('task-card-test-evidence');
  await expect(reviewEvidence).toHaveAttribute('data-evidence-state', 'unassigned');
  await expect(reviewEvidence).toContainText('No SHA-linked project run');

  const screenshotName = `task-test-evidence-visibility--${evidenceCaptureState}--mocked.png`;
  const boardShot = test.info().outputPath(screenshotName);
  await page.getByTestId('studio-board').screenshot({ path: boardShot });
  await test.info().attach(screenshotName, { path: boardShot, contentType: 'image/png' });
});

test('archived-card evidence incident renders honest before and SHA-linked after states', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1300 });
  let linked = false;
  const examples = [
    ['AGT-2416', '3aa5ad85', 'review_05aa90204763466abc2627c9be2eedc8'],
    ['AGT-2399', '67d3039c', 'review_b916bab377404c1f9457f6cf075c58f1'],
    ['AGT-2426', 'd1649ce9', 'review_8017590a9dd34619b1480e0fdbb5938e'],
  ] as const;
  const grouped = (): typeof GROUPED => ({
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    review: [],
    autoReview: [],
    humanReview: examples.map(([id, sha, reviewId], index) => task(
      id,
      '5-human-review',
      index + 1,
      linked
        ? {
            runId: null,
            runCommit: null,
            runState: null,
            runResult: null,
            matchQuality: 'perfect',
            direction: 'exact',
            distance: 0,
            diffContained: true,
            evidenceState: 'proven',
            awaitingEvidence: false,
            summary: `Review build-tests Pass at ${sha}`,
            sources: [{
              kind: 'review-build-tests',
              id: reviewId,
              commit: sha,
              result: 'passed',
              observedAt: '2026-07-29T20:41:22Z',
              summary: `Review build-tests Pass at ${sha}`,
              reason: 'verify-1 and verify-2 passed.',
              reportRef: `remote-review-grade-${reviewId}.md`,
            }],
          }
        : {
            runId: null,
            runCommit: null,
            runState: null,
            runResult: null,
            matchQuality: 'none',
            direction: 'none',
            distance: null,
            diffContained: false,
            evidenceState: 'unassigned',
            awaitingEvidence: true,
            summary: 'Evidence pending: No test run assigned',
          },
    )),
    escalated: [],
    completed: [],
    archive: [],
  });

  await boot(page, grouped);
  const evidence = page.getByTestId('task-card-test-evidence');
  await expect(evidence).toHaveCount(3);
  await expect(evidence).toContainText([
    'Evidence pending: No test run assigned',
    'Evidence pending: No test run assigned',
    'Evidence pending: No test run assigned',
  ]);
  const reviewLane = page.getByTestId('lane-5-human-review');
  const before = test.info().outputPath('evidence-pending-before.png');
  await reviewLane.screenshot({ path: before });
  await test.info().attach('evidence-pending-before', { path: before, contentType: 'image/png' });

  linked = true;
  await page.reload();
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 15_000 });
  await expect(evidence).toHaveCount(3);
  for (const [, sha] of examples) {
    await expect(evidence.filter({ hasText: `Review build-tests Pass at ${sha}` }))
      .toHaveAttribute('data-evidence-state', 'proven');
  }
  await expect(evidence.filter({ hasText: 'Evidence pending' })).toHaveCount(0);
  const after = test.info().outputPath('evidence-linked-after.png');
  await reviewLane.screenshot({ path: after });
  await test.info().attach('evidence-linked-after', { path: after, contentType: 'image/png' });
});

test('build gate not-applicable is neutral while a true skip stays red', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  let corrected = false;
  const gateEvidence = (
    state: 'not-applicable' | 'not-proven',
    summary: string,
  ): NonNullable<TaskFixture['testEvidence']> => ({
    runId: null,
    runCommit: null,
    runState: null,
    runResult: null,
    matchQuality: 'perfect',
    direction: 'exact',
    distance: 0,
    diffContained: true,
    evidenceState: state,
    awaitingEvidence: false,
    summary,
    sources: [{
      kind: 'build-test-gate',
      id: state === 'not-applicable' ? 'gate-aow-9' : 'gate-aow-10',
      commit: 'a1b2c3d4',
      result: state,
      observedAt: '2026-08-08T10:00:00Z',
      summary,
      reason: state === 'not-proven' ? 'The build command did not run.' : 'No build/test commands are defined.',
      reportRef: 'post-steps/build-test-gate-1.log',
    }],
  });
  const grouped = (): typeof GROUPED => ({
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
    humanReview: [
      task('AOW-9', '5-human-review', 1, corrected
        ? gateEvidence('not-applicable', 'No build/test defined')
        : gateEvidence('not-proven', 'Build/test gate skipped at a1b2c3d4')),
      task('AOW-10', '5-human-review', 2,
        gateEvidence('not-proven', 'Build/test gate skipped at e5f6a7b8')),
    ],
    escalated: [], completed: [], archive: [],
  });
  const resultsDir = process.env['JOB_RESULTS_DIR']
    ?? test.info().outputDir;
  fs.mkdirSync(resultsDir, { recursive: true });

  await boot(page, grouped);
  const reviewLane = page.getByTestId('lane-5-human-review');
  const aow9 = page.getByTestId('task-card').filter({ hasText: 'AOW-9' })
    .getByTestId('task-card-test-evidence');
  const aow10 = page.getByTestId('task-card').filter({ hasText: 'AOW-10' })
    .getByTestId('task-card-test-evidence');
  await expect(aow9).toHaveAttribute('data-evidence-state', 'not-proven');
  await reviewLane.screenshot({
    path: path.join(resultsDir, 'agt-2518--build-test-gate-skip-classes--before--mocked.png'),
  });

  corrected = true;
  await page.reload();
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 15_000 });
  await expect(aow9).toHaveAttribute('data-evidence-state', 'not-applicable');
  await expect(aow9).toContainText('No build/test defined');
  await expect(aow10).toHaveAttribute('data-evidence-state', 'not-proven');
  await expect(aow10).toContainText('Build/test gate skipped at e5f6a7b8');

  const [neutral, skipped] = await Promise.all([
    aow9.evaluate(element => ({
      color: getComputedStyle(element).color,
      background: getComputedStyle(element).backgroundColor,
    })),
    aow10.evaluate(element => ({
      color: getComputedStyle(element).color,
      background: getComputedStyle(element).backgroundColor,
    })),
  ]);
  expect(neutral.color).not.toBe(skipped.color);
  expect(neutral.background).not.toBe(skipped.background);
  await reviewLane.screenshot({
    path: path.join(resultsDir, 'agt-2518--build-test-gate-skip-classes--after--mocked.png'),
  });
});
