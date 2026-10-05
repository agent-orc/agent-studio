import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT = 'Queue dependency fixture';
const WATCH_PATH = '/fixtures/queue-dependency';
const RESULTS = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), '..', 'results');

interface QueueFixtureState {
  gateFulfilled: boolean;
}

function task(
  id: string,
  title: string,
  order: number,
  position: number | null,
  waitsOn: Record<string, unknown> | null = null,
): Record<string, unknown> {
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
    createdAt: `2026-08-09T08:0${order}:00Z`,
    lastActivity: '2026-08-09T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/2-ready/${id}`,
    ownerClientId: 'local-default',
    commits: [],
    tags: [],
    references: {
      dependsOn: waitsOn ? ['AGT-2534'] : [],
      relatedTo: [],
      blockedBy: [],
      supersedes: [],
    },
    waitsOn,
    liveStatus: {
      attempt: 1,
      activeStep: null,
      nextSteps: [
        { stepId: 'core-agent-run', displayName: 'Agent execution' },
        { stepId: 'aspect-requirement-fit', displayName: 'Requirement fit' },
      ],
      queue: position === null ? null : { kind: 'runner', position },
      latestEventAt: '2026-08-09T08:00:00Z',
    },
  };
}

function tasks(state: QueueFixtureState): Record<string, unknown>[] {
  const dependency = {
    key: 'AGT-2534',
    resolved: true,
    fulfilled: state.gateFulfilled,
    releaseGate: false,
    targetReleased: false,
    waitingForRelease: false,
    targetJobId: 'agt-2534',
    targetTitle: 'Required predecessor',
    targetState: state.gateFulfilled ? '6-completed' : '5-human-review',
    targetWatchPath: '/fixtures/dependency',
  };

  return [
    task('agt-eligible-1', 'First eligible card', 1, 1),
    task('agt-eligible-2', 'Second eligible card', 2, 2),
    // The stale position reproduces the operator evidence. waitsOn must win in
    // CURRENT even if an older payload briefly carries both facts.
    task('agt-2538', 'Dependency-gated card', 3, state.gateFulfilled ? 3 : 4, {
      blocked: !state.gateFulfilled,
      cycleDetected: false,
      items: [dependency],
    }),
  ];
}

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function installRoutes(page: Page, state: QueueFixtureState): Promise<void> {
  const now = new Date().toISOString();
  const freshUntil = new Date(Date.now() + 5 * 60_000).toISOString();
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0 }));
  await page.route(/\/api\/tasks(\?|$)/, route => json(route, tasks(state)));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: tasks(state),
    progress: [], failedPickup: [], codeNotComplete: [], autoReview: [],
    review: [], humanReview: [], escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/v1/management/remote-hosts', route => json(route, [{
    runnerId: 'queue-runner',
    name: 'queue-runner',
    hostId: 'queue-host',
    instanceId: 'coding',
    runnerVersion: '1.2.0',
    protocolVersion: 2,
    status: 'active',
    registeredAt: now,
    lastSeenAt: now,
    hostAdmission: { hostId: 'queue-host', admissionState: 'open' },
    capabilities: [{
      key: 'provider-auth:codex',
      category: 'provider-auth',
      advertisedStatus: 'ready',
      healthState: 'healthy',
      reason: null,
      advertisedAt: now,
      freshUntil,
      isFresh: true,
      firstFailureAt: null,
      lastFailureAt: null,
      cooldownUntil: null,
      canaryClaimId: null,
      consecutiveFailures: 0,
      version: null,
      identity: 'codex',
      detail: 'Authenticated.',
      affectedClaims: [],
      recoveryHistory: [],
    }],
    telemetry: null,
  }]));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, {
    projects: {
      [PROJECT]: {
        projectName: PROJECT,
        mode: 'manual',
        activeJobId: null,
        activeExecution: null,
        queuedJobIds: state.gateFulfilled
          ? ['agt-eligible-1', 'agt-eligible-2', 'agt-2538']
          : ['agt-eligible-1', 'agt-eligible-2'],
      },
    },
  }));
}

async function hideDevOverlays(page: Page): Promise<void> {
  await dismissDevErrorDialog(page);
  await page.addStyleTag({
    content: 'app-error-dialog, app-offline-banner, [data-testid="error-dialog-overlay"] { display: none !important; }',
  });
}

async function openBoard(page: Page, state: QueueFixtureState): Promise<void> {
  await page.setViewportSize({ width: 1600, height: 960 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page, state);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await hideDevOverlays(page);
}

test.describe('runner queue dependency precedence', () => {
  test('shows one gate wait, excludes gated positions, then flips to the canonical slot position', async ({ page }) => {
    const state: QueueFixtureState = { gateFulfilled: false };
    mkdirSync(RESULTS, { recursive: true });
    await openBoard(page, state);

    let gatedCard = page.getByTestId('task-card').filter({ hasText: 'Dependency-gated card' });
    const firstCard = page.getByTestId('task-card').filter({ hasText: 'First eligible card' });
    const secondCard = page.getByTestId('task-card').filter({ hasText: 'Second eligible card' });

    await expect(gatedCard.getByTestId('task-live-current')).toContainText('waits for completion: AGT-2534');
    await expect(gatedCard.getByTestId('task-live-current')).not.toContainText('runner slot');
    await expect(gatedCard.getByTestId('task-live-current')).not.toContainText('position 4');
    await expect(gatedCard.getByTestId('task-card-waiting-on')).toHaveCount(0);
    await expect(gatedCard.getByTestId('task-live-next')).toContainText('Agent execution → Requirement fit');
    await expect(firstCard.getByTestId('task-live-current')).toContainText('position 1');
    await expect(secondCard.getByTestId('task-live-current')).toContainText('position 2');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await gatedCard.screenshot({
        path: join(RESULTS, `runner-queue-dependency-gate--${theme}--mocked.png`),
      });
    }

    state.gateFulfilled = true;
    await page.reload({ waitUntil: 'domcontentloaded' });
    await hideDevOverlays(page);
    gatedCard = page.getByTestId('task-card').filter({ hasText: 'Dependency-gated card' });

    await expect(gatedCard.getByTestId('task-live-current'))
      .toContainText('Waiting for runner slot · position 3');
    await expect(gatedCard.getByTestId('task-live-current')).not.toContainText('waits for completion');
    await expect(gatedCard.getByTestId('task-card-waiting-on')).toHaveCount(0);
    await expect(gatedCard.getByTestId('task-live-next')).toContainText('Agent execution → Requirement fit');
    await setTheme(page, 'light');
    await gatedCard.screenshot({
      path: join(RESULTS, 'runner-queue-dependency-fulfilled--light--mocked.png'),
    });
  });
});
