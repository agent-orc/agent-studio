import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-2818 - a card that cannot be picked says so where it claims to be queued.
 *
 * The two reported cards are reproduced as the board actually received them:
 *
 * - AGT-2373 waited in `2-ready` from 2026-08-11 behind a `releaseGate` edge
 *   pointing at AGT-2372, which is archived and was never released. The board
 *   said "waits for release: AGT-2372" and nothing else: not that the target is
 *   archived, not that no run will ever clear it, not how long, not what to do.
 * - AGT-2738 waited in `2-ready` from 2026-09-06 carrying a
 *   `remoteDispatchRejection` that no template in `frontend/src/app` read. The
 *   card looked queued and was silently skipped on every tick.
 *
 * Backend-mocked on purpose: the projection is covered by
 * `PickupHoldPolicyTests` / `PickupHoldEndpointsTests`, and what needs proving
 * here is that the sentences reach the operator's eyes in both themes.
 */
const PROJECT = 'Pickup hold fixture';
const WATCH_PATH = '/fixtures/pickup-hold';
const RESULTS = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), '..', 'results');

const ARCHIVED_GATE_CARD = {
  id: 'agt-2373',
  key: 'AGT-2373',
  displayKey: 'AGT-2373',
  taskKey: `${WATCH_PATH}::agt-2373`,
  title: 'Remove the duplicate CLI invocation paths',
  state: '2-ready',
  order: 1,
  agent: 'codex',
  cliType: 'codex',
  createdAt: '2026-08-11T09:00:00Z',
  lastActivity: '2026-08-11T09:00:00Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/2-ready/agt-2373`,
  ownerClientId: 'local-default',
  commits: [],
  tags: [],
  references: { dependsOn: ['AGT-2372'], relatedTo: [], blockedBy: [], supersedes: [] },
  waitsOn: {
    blocked: true,
    cycleDetected: false,
    unsatisfiableGate: true,
    items: [{
      key: 'AGT-2372',
      resolved: true,
      fulfilled: false,
      releaseGate: true,
      targetReleased: false,
      waitingForRelease: true,
      unsatisfiable: true,
      unsatisfiableReason:
        'AGT-2372 is archived and was never released, so no run is left that could open this gate.',
      targetJobId: 'agt-2372',
      targetTitle: 'Parity suite for both CAR execution paths',
      targetState: '7-archive',
      targetWatchPath: WATCH_PATH,
    }],
  },
  pickupHold: {
    mechanism: 'dependency-gate',
    reason: 'AGT-2372 is archived and was never released, so no run is left that could open this gate.',
    sinceUtc: '2026-08-11T09:00:00Z',
    heldForSeconds: 34 * 86400,
    unsatisfiable: true,
    resolutions: [
      {
        kind: 'release-target',
        label: 'Release AGT-2372',
        detail: 'Releasing states that the validation this gate stands for no longer has to happen. Only an operator may decide that.',
        targetKey: 'AGT-2372',
      },
      {
        kind: 'drop-release-gate',
        label: 'Drop the release gate on AGT-2372',
        detail: 'Remove the releaseGate edge through this card\'s references and re-plan the card.',
        targetKey: 'AGT-2372',
      },
    ],
  },
  liveStatus: {
    attempt: 1,
    activeStep: null,
    nextSteps: [{ stepId: 'core-agent-run', displayName: 'Agent execution' }],
    queue: null,
    latestEventAt: '2026-08-11T09:00:00Z',
  },
};

const REFUSED_DISPATCH_CARD = {
  ...ARCHIVED_GATE_CARD,
  id: 'agt-2738',
  key: 'AGT-2738',
  displayKey: 'AGT-2738',
  taskKey: `${WATCH_PATH}::agt-2738`,
  title: 'Installer: one executable for Windows and Linux',
  order: 2,
  createdAt: '2026-09-06T10:00:00Z',
  folderPath: `${WATCH_PATH}/2-ready/agt-2738`,
  references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
  waitsOn: null,
  executionLocation: {
    state: 'queued-remote',
    executionKind: 'none',
    connectionState: 'queued',
    leaseState: 'none',
    trustReason: 'Queued for a remote Runner.',
    lastRejection: {
      code: 'capability-mismatch',
      runnerId: 'agent-runner-01',
      runnerName: 'agent-runner-01',
      reason: "Required capability 'task-server:connectivity' is advertised as unavailable.",
      rejectedAtUtc: '2026-09-06T19:47:45Z',
    },
  },
  pickupHold: {
    mechanism: 'dispatch-rejection',
    reason: "Runner agent-runner-01 refused this card (capability-mismatch): "
      + "Required capability 'task-server:connectivity' is advertised as unavailable.",
    sinceUtc: '2026-09-06T19:47:45Z',
    heldForSeconds: 7 * 86400 + 3600,
    unsatisfiable: false,
    resolutions: [
      {
        kind: 'restore-runner-capability',
        label: 'Restore the runner capability',
        detail: 'Give agent-runner-01 what it reported missing, or route this project at a runner that has it.',
      },
      {
        kind: 'retry-dispatch',
        label: 'Re-offer the card',
        detail: 'The refusal stays visible until a later dispatch succeeds or an operator clears it.',
      },
    ],
  },
};

const PICKABLE_CARD = {
  ...REFUSED_DISPATCH_CARD,
  id: 'agt-9001',
  key: 'AGT-9001',
  displayKey: 'AGT-9001',
  taskKey: `${WATCH_PATH}::agt-9001`,
  title: 'A genuinely queued card',
  order: 3,
  folderPath: `${WATCH_PATH}/2-ready/agt-9001`,
  executionLocation: undefined,
  pickupHold: null,
  liveStatus: {
    attempt: 1,
    activeStep: null,
    nextSteps: [{ stepId: 'core-agent-run', displayName: 'Agent execution' }],
    queue: { kind: 'runner', position: 1 },
    latestEventAt: '2026-09-14T09:00:00Z',
  },
};

const CARDS = [ARCHIVED_GATE_CARD, REFUSED_DISPATCH_CARD, PICKABLE_CARD];

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

function detail(card: Record<string, unknown>): Record<string, unknown> {
  return {
    info: card,
    promptMarkdown: 'Remove the second CLI invocation path.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => json(route, []));
  for (const card of CARDS) {
    await page.route(new RegExp(`/api/tasks/${card.id}(\\?|$)`), route => json(route, detail(card)));
  }
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0 }));
  await page.route(/\/api\/tasks(\?|$)/, route => json(route, CARDS));
  await page.route('**/api/tasks/grouped**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: CARDS,
    progress: [], failedPickup: [], codeNotComplete: [], autoReview: [],
    review: [], humanReview: [], escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route(/\/api\/runner\/status(\?|$)/, route => json(route, {
    projects: {
      [PROJECT]: {
        projectName: PROJECT,
        mode: 'manual',
        activeJobId: null,
        activeExecution: null,
        queuedJobIds: ['agt-9001'],
      },
    },
  }));
}

async function openBoard(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1600, height: 960 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
  await page.addStyleTag({
    content: 'app-error-dialog, app-offline-banner, [data-testid="error-dialog-overlay"] { display: none !important; }',
  });
}

test.describe('pickup holds are visible where the card claims to be queued', () => {
  test('an archived release gate says it can never open, names its age and both ways out', async ({ page }) => {
    mkdirSync(RESULTS, { recursive: true });
    await openBoard(page);

    const card = page.getByTestId('task-card').filter({ hasText: 'duplicate CLI invocation paths' });
    const hold = card.getByTestId('pickup-hold');

    await expect(hold).toHaveAttribute('data-tone', 'blocked');
    await expect(hold).toHaveAttribute('data-mechanism', 'dependency-gate');
    await expect(card.getByTestId('pickup-hold-headline')).toContainText('cannot clear by itself');
    await expect(card.getByTestId('pickup-hold-reason')).toContainText('AGT-2372 is archived');
    await expect(card.getByTestId('pickup-hold-age')).toContainText('held for 34d');
    // The wait sentence and the configuration-error sentence are different.
    await expect(card.getByTestId('task-live-current')).toContainText('this gate can never open: AGT-2372');
    await expect(card.getByTestId('task-live-current')).not.toContainText('waits for release');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await card.screenshot({ path: join(RESULTS, `pickup-hold-archived-gate--${theme}--mocked.png`) });
    }
  });

  test('a refused dispatch names the runner, the code and when it was refused', async ({ page }) => {
    mkdirSync(RESULTS, { recursive: true });
    await openBoard(page);

    const card = page.getByTestId('task-card').filter({ hasText: 'Installer: one executable' });
    const hold = card.getByTestId('pickup-hold');

    await expect(hold).toHaveAttribute('data-tone', 'open');
    await expect(hold).toHaveAttribute('data-mechanism', 'dispatch-rejection');
    await expect(card.getByTestId('pickup-hold-mechanism')).toContainText('Dispatch refused');
    await expect(card.getByTestId('pickup-hold-age')).toContainText('held for 7d');
    await expect(card.getByTestId('task-live-current')).toContainText('the runner refused it');

    // The durable record itself, on the card, with the two facts it used to
    // withhold: which refusal this is, and when it happened.
    const rejection = card.getByTestId('remote-dispatch-rejection');
    await expect(rejection).toContainText('Runner agent-runner-01 rejected:');
    await expect(rejection).toContainText('task-server:connectivity');
    await expect(rejection).toHaveAttribute('data-rejection-code', 'capability-mismatch');
    await expect(card.getByTestId('remote-dispatch-rejection-code')).toContainText('capability-mismatch');
    await expect(card.getByTestId('remote-dispatch-rejection-at')).toContainText('refused');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await card.screenshot({ path: join(RESULTS, `pickup-hold-dispatch-rejection--${theme}--mocked.png`) });
    }
  });

  test('the opened card lists both ways out of the archived gate', async ({ page }) => {
    mkdirSync(RESULTS, { recursive: true });
    await openBoard(page);

    await page.getByTestId('task-card').filter({ hasText: 'duplicate CLI invocation paths' }).click();

    const waysOut = page.getByTestId('pickup-hold-ways-out');
    await expect(waysOut).toBeVisible({ timeout: 15_000 });
    await expect(waysOut).toContainText('Release AGT-2372');
    await expect(waysOut).toContainText('Drop the release gate on AGT-2372');
    // Offered, not taken: the block states the decision, it does not make it.
    await expect(waysOut).toContainText('Only an operator may decide that');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('pickup-hold').first().screenshot({
        path: join(RESULTS, `pickup-hold-ways-out--${theme}--mocked.png`),
      });
    }
  });

  test('a genuinely queued card carries no hold', async ({ page }) => {
    await openBoard(page);

    const card = page.getByTestId('task-card').filter({ hasText: 'A genuinely queued card' });

    await expect(card.getByTestId('pickup-hold')).toHaveCount(0);
    await expect(card.getByTestId('task-live-current')).toContainText('position 1');
  });
});
