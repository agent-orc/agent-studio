import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Live pipeline';
const WATCH_PATH = '/fixtures/live-pipeline';
const RESULTS = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), '..', 'results');
const NOW = '2026-07-24T20:00:40Z';

function task(
  id: string,
  title: string,
  state: '3-progress' | '4-auto-review',
  liveStatus: Record<string, unknown>,
  lastActivity: string,
): Record<string, unknown> {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    key: id.toUpperCase(),
    title,
    state,
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-07-24T19:30:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/${state}/${id}`,
    lastActivity,
    sessionName: `session-${id}`,
    model: 'gpt-5.4-mini',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    tags: [],
    references: {
      dependsOn: [],
      relatedTo: [],
      blockedBy: [],
      supersedes: [],
    },
    liveStatus,
  };
}

const ACTIVE = task(
  'agt-live',
  'Live review chain',
  '4-auto-review',
  {
    attempt: 4,
    activeStep: {
      stepId: 'aspect-tests-and-evidence',
      displayName: 'Tests and evidence',
      kind: 'aspect',
      startedAt: '2026-07-24T20:00:00Z',
      model: 'gpt-5.4-mini',
      cliType: 'codex',
    },
    nextSteps: [
      { stepId: 'grade', displayName: 'Grade' },
      { stepId: 'gate', displayName: 'Gate' },
      { stepId: 'merge', displayName: 'Merge' },
    ],
    queue: null,
    latestEventAt: '2026-07-24T20:00:00Z',
  },
  '2026-07-24T20:00:00Z',
);

Object.assign(ACTIVE, {
  executionLocation: {
    state: 'remote-running',
    executionKind: 'remote',
    runnerId: 'agent-runner-01',
    clientId: 'agent-runner-01',
    hostDisplayName: 'agent-runner-01',
    startedAt: '2026-07-24T20:00:00Z',
    lastHeartbeat: '2026-07-24T20:00:35Z',
    lastActivityAt: '2026-07-24T20:00:35Z',
    processId: 654,
    sessionId: 'session-agt-live',
    branch: 'task/agt-live',
    worktreePath: `${WATCH_PATH}/agt-live`,
    connectionState: 'connected',
    leaseState: 'active',
    trustReason: 'The task server holds the fenced run lease.',
    historical: false,
  },
});

const QUEUED = task(
  'agt-queued',
  'Waiting review slot',
  '4-auto-review',
  {
    attempt: 2,
    activeStep: null,
    nextSteps: [
      { stepId: 'aspect-requirement-fit', displayName: 'Requirement fit' },
      { stepId: 'grade', displayName: 'Grade' },
    ],
    queue: { kind: 'review', position: 3 },
    latestEventAt: '2026-07-24T19:59:40Z',
  },
  '2026-07-24T19:59:40Z',
);

const DEGRADED = task(
  'agt-result-degraded',
  'Reviewable concept with degraded Result',
  '4-auto-review',
  {},
  '2026-07-24T19:58:40Z',
);

Object.assign(DEGRADED, {
  summaryState: {
    status: 'degraded',
    startedAt: '2026-07-24T19:58:00Z',
    finishedAt: '2026-07-24T19:58:40Z',
    errorMessage: 'Summary service unavailable.',
    bytesWritten: null,
    attempt: 3,
    maxAttempts: 3,
  },
});

const PRE_STEP = task(
  'agt-pre-step',
  'Live preparation step',
  '3-progress',
  {
    attempt: 1,
    activeStep: {
      stepId: 'pre-worktree-create',
      displayName: 'Create worktree',
      kind: 'pre',
      startedAt: '2026-07-24T20:00:30Z',
    },
    nextSteps: [{ stepId: 'core', displayName: 'Agent execution' }],
    queue: null,
    latestEventAt: '2026-07-24T20:00:30Z',
  },
  '2026-07-24T20:00:30Z',
);

Object.assign(PRE_STEP, {
  runActivity: { kind: 'failed-idle', attempt: 1, lastError: 'stale failure' },
  execution: {
    jobId: 'agt-pre-step',
    taskKey: `${WATCH_PATH}::agt-pre-step`,
    processId: 0,
    startedAt: '2026-07-24T19:59:30Z',
    status: 'failed',
    exitCode: 1,
    durationSeconds: 1,
    model: 'gpt-5.4-mini',
    runOutcome: 'failed',
  },
});

const STALLED = task(
  'agt-stalled',
  'Possible pipeline hang',
  '3-progress',
  {
    attempt: 7,
    activeStep: null,
    nextSteps: [
      { stepId: 'core', displayName: 'Agent execution' },
      { stepId: 'aspect-tests-and-evidence', displayName: 'Tests and evidence' },
    ],
    queue: null,
    latestEventAt: '2026-07-24T19:48:40Z',
  },
  '2026-07-24T19:48:40Z',
);

const TASKS = [ACTIVE, QUEUED, DEGRADED, PRE_STEP, STALLED];

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = route.request().url();
    const path = new URL(url).pathname;
    const detailMatch = path.match(/^\/api\/v1\/projects\/[^/]+\/tasks\/([^/]+)$/);

    if (url.includes('/api/v1/studio/auth/status')) {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true });
    }
    if (url.includes('/api/tasks/archive')) return json(route, { items: [], total: 0 });
    if (route.request().method() === 'GET' && detailMatch && !['archive', 'grouped'].includes(detailMatch[1])) {
      const requestedId = decodeURIComponent(detailMatch[1]).toLowerCase();
      const info = TASKS.find(item => String(item['id']).toLowerCase() === requestedId);
      return json(route, {
        info,
        promptMarkdown: null,
        promptHistory: [],
        titleHistory: [],
        statusMarkdown: null,
        contextUsage: null,
        log: [],
        summaryState: null,
        reviewEvidence: [],
      });
    }
    if (url.includes('/api/v1/studio/board')) {
      return json(route, {
        backlog: [],
        preparation: [],
        orchestratorPrep: [],
        ready: [],
        progress: [PRE_STEP, STALLED],
        failedPickup: [],
        codeNotComplete: [],
        review: [],
        autoReview: [ACTIVE, QUEUED, DEGRADED],
        humanReview: [],
        escalated: [],
        completed: [],
        archive: [],
      });
    }
    if (/\/api\/(?:tasks|jobs)(\?|$)/.test(url)) return json(route, TASKS);
    if (url.includes('/api/watch-paths')) {
      return json(route, [{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH }]);
    }
    if (url.includes('/api/clients')) {
      return json(route, [{ id: 'local-default', displayName: 'Local', kind: 'agent-instance' }]);
    }
    if (url.includes('/api/v1/studio/runner/status')) return json(route, { projects: {} });
    if (url.includes('/api/environment')) return json(route, { isDev: false, devTools: {} });
    if (url.includes('/api/cli/')) return json(route, { snapshots: [], sessions: [] });
    return json(route, []);
  });
}

async function prepareBoard(page: Page, freezeTime = true): Promise<void> {
  if (freezeTime) await page.clock.install({ time: new Date(NOW) });
  await page.addInitScript(() => {
    if (localStorage.getItem('atp.studio.tabs.v1')) return;
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await page.addStyleTag({
    content: '.dialog__overlay, [data-testid="offline-banner"] { display: none !important; }',
  });
  await dismissDevErrorDialog(page);
}

test.describe('task live status', () => {
  test.use({ viewport: { width: 1600, height: 960 } });

  test('shows current work, the next chain and honest idle states on cards', async ({ page }) => {
    test.setTimeout(120_000);
    mkdirSync(RESULTS, { recursive: true });
    await prepareBoard(page);

    const activeCard = page.locator('[data-testid="task-card"]', { hasText: 'Live review chain' });
    const queuedCard = page.locator('[data-testid="task-card"]', { hasText: 'Waiting review slot' });
    const degradedCard = page.locator('[data-testid="task-card"]', { hasText: 'Reviewable concept with degraded Result' });
    const preStepCard = page.locator('[data-testid="task-card"]', { hasText: 'Live preparation step' });
    const stalledCard = page.locator('[data-testid="task-card"]', { hasText: 'Possible pipeline hang' });

    await expect(activeCard.getByTestId('task-live-current')).toContainText('Review aspect · Tests and evidence');
    await expect(activeCard.getByTestId('task-live-status'))
      .toContainText(/running (?:\d+s|\d+m\d{2}s|\d+h\d{2}m)/);
    await expect(activeCard.getByTestId('task-live-status')).toContainText('agent-runner-01');
    await expect(activeCard.getByTestId('task-live-status')).toContainText('gpt-5.4-mini');
    await expect(activeCard.getByTestId('task-live-status')).toContainText('via Codex');
    await expect(activeCard.getByTestId('task-live-next')).toContainText('Grade → Gate → Merge');
    await expect(queuedCard.getByTestId('task-live-current')).toContainText('Waiting for review slot · position 3');
    await expect(degradedCard.getByTestId('task-card-review')).toHaveText(/result degraded/);
    await degradedCard.getByTestId('task-card-review').hover();
    await expect(page.getByTestId('cac-tooltip')).toContainText('3/3 summary attempts');
    await expect(page.getByTestId('cac-tooltip')).toContainText('core run remains reviewable');
    await page.mouse.move(1590, 950);
    await expect(page.getByTestId('cac-tooltip')).toBeHidden();
    await expect(preStepCard).toHaveAttribute('data-running', 'true');
    await expect(preStepCard.getByTestId('task-live-current')).toContainText('Create worktree');
    await expect(preStepCard).not.toContainText('No active run');
    await expect(preStepCard).not.toContainText('Failed');
    await expect(preStepCard).not.toContainText('Stalled');
    await expect(stalledCard.getByTestId('task-live-current'))
      .toContainText(/No activity for 12m\d{2}s · possible hang/);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.getByTestId('lane-4-auto-review')).toBeVisible();
      await page.screenshot({
        path: join(RESULTS, `task-live-status-card--mocked--${theme}.png`),
        fullPage: true,
      });
    }
  });

  test('renders and captures the shared detail presentation', async ({ page }) => {
    test.setTimeout(120_000);
    mkdirSync(RESULTS, { recursive: true });
    await prepareBoard(page);
    const activeCard = page.locator('[data-testid="task-card"]', { hasText: 'Live review chain' });
    await expect(activeCard).toBeVisible({ timeout: 60_000 });
    const detailHost = activeCard.locator('app-task-live-status');

    // The detail and card use the same Angular component. Switch this mounted
    // instance through Angular's dev-mode signal input so the screenshot covers
    // the real `variant="detail"` template and styling without duplicating a
    // visual-only fixture component.
    await detailHost.evaluate((host) => {
      const debug = (window as unknown as {
        ng?: {
          getComponent(element: Element): Record<string, unknown> | null;
          applyChanges(component: object): void;
        };
      }).ng;
      const component = debug?.getComponent(host) as { variant?: (() => string) & Record<PropertyKey, unknown> } | null;
      const variant = component?.variant;
      const node = variant && Object.getOwnPropertySymbols(variant)
        .map(symbol => variant[symbol])
        .find((value): value is { applyValueToInputSignal(node: object, value: string): void } =>
          typeof (value as { applyValueToInputSignal?: unknown })?.applyValueToInputSignal === 'function');
      if (!component || !node) throw new Error('Angular detail-variant input is unavailable');
      node.applyValueToInputSignal(node, 'detail');
      debug!.applyChanges(component);
    });

    const detail = detailHost.getByTestId('task-live-status');
    await expect(detail).toHaveAttribute('data-attempt', '4', { timeout: 60_000 });
    await expect(detail).toHaveClass(/task-live--detail/);
    await expect(detail).toContainText('Review aspect · Tests and evidence');
    await expect(detail).toContainText('started');
    await expect(detail).toContainText('running');
    await expect(detail).toContainText('agent-runner-01');
    await expect(detail).toContainText('Grade → Gate → Merge');

    await page.addStyleTag({
      content: `
        [data-testid="task-live-detail-showcase"] {
          padding: var(--studio-spacing-5);
          background: var(--studio-bg-editor);
          width: 1080px;
        }
        [data-testid="task-live-detail-showcase"] app-task-live-status {
          display: block;
          width: 100%;
        }
      `,
    });
    await detailHost.evaluate((host) => {
      const showcase = document.createElement('section');
      showcase.dataset['testid'] = 'task-live-detail-showcase';
      document.body.appendChild(showcase);
      showcase.appendChild(host);
    });
    const showcase = page.getByTestId('task-live-detail-showcase');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await showcase.screenshot({
        path: join(RESULTS, `task-live-status-detail--mocked--${theme}.png`),
      });
    }
  });
});
