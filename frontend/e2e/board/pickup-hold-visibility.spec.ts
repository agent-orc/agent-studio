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
/**
 * Browser clock: 34 days after the archived gate started holding, 7 days and
 * 13 hours after the refused dispatch. The card derives "held for" from
 * `sinceUtc` against this clock; the board payload carries no age counter.
 */
const NOW = '2026-09-14T09:00:00Z';

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
    classification: 'unsatisfiable',
    mechanism: 'dependency-gate',
    reason: 'AGT-2372 is archived and was never released, so no run is left that could open this gate.',
    sinceUtc: '2026-08-11T09:00:00Z',
    unsatisfiable: true,
    resolutions: [
      {
        kind: 'drop-dependency',
        label: 'Drop the dependency',
        detail: 'Remove this waits-on edge. The platform never takes this decision automatically.',
        targetKey: 'AGT-2372',
        action: { kind: 'api', target: 'edit-waits-on', project: PROJECT },
      },
      {
        kind: 'repoint-dependency',
        label: 'Point to a successor card',
        detail: 'Replace the edge with the stable key of the card that now owns the prerequisite.',
        targetKey: 'AGT-2372',
        action: { kind: 'api', target: 'edit-waits-on', project: PROJECT },
      },
      {
        kind: 'archive-waiting-card',
        label: 'Archive this waiting card',
        detail: 'Close the waiting card without changing the prerequisite.',
        targetKey: 'AGT-2372',
        action: { kind: 'api', target: 'archive-task', project: PROJECT },
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
    unsatisfiable: false,
    resolutions: [
      {
        kind: 'restore-runner-capability',
        label: 'Restore the runner capability',
        detail: 'Give agent-runner-01 what it reported missing, or route this project at a runner that has it.',
        action: { kind: 'route', target: 'execution-hosts', project: PROJECT },
      },
      {
        kind: 'retry-dispatch',
        label: 'Re-offer the card',
        detail: 'Clears the recorded refusal so the next dispatch tick offers the card again.',
        action: { kind: 'api', target: 'clear-dispatch-rejection', project: PROJECT },
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

/** Minimal bounded task core (AGT-2953): the detail opens from `/core`, the hold from the board's TaskInfo. */
function core(card: { id: string; key: string; taskKey: string; title: string; folderPath: string }) {
  return {
    state: 'ready', projectId: PROJECT, projectName: PROJECT,
    id: card.id, taskKey: card.taskKey, key: card.key,
    watchPath: WATCH_PATH, folderPath: card.folderPath,
    title: card.title, kind: 'task', taskType: 'feature',
    lane: '2-ready', archiveState: null, enteredLaneAt: '2026-08-11T09:00:00Z',
    order: 1, mode: 'coding', released: false, pendingIntent: false, coreVersion: '1',
    pins: { model: null, modelExplicit: false, thinkingLevel: null, thinkingLevelExplicit: false,
      cliType: 'codex', contextMode: null, useOwnSession: null, allowWebAccess: false, noBranchExpected: false },
    actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
    blocking: { blockerType: null, blockerCondition: null, blockerStatus: null, blockerDescription: null,
      needsInput: null, dependencyBlocked: false, dependencyState: 'ready', dependencies: [] },
    runtime: { attemptId: null, runnerId: null, runnerName: null, hostname: null,
      executionStatus: null, location: 'none', heartbeatAt: null, leaseState: 'none', leaseId: null },
    runtimeVersion: 'v1',
    statusSummary: { state: 'ready', text: '', originalBytes: 0, hash: 'status', cursor: null },
    prompt: { state: 'ready', text: 'Implement the change.', originalBytes: 21, hash: 'prompt', cursor: null, continuationUrl: null },
    timeline: { state: 'ready', events: [], cursor: null, continuationUrl: null },
  };
}

/** One `/details/<name>` resource envelope for the core-first detail. */
function resource(card: { id: string; taskKey: string }, name: string, data: unknown) {
  return { id: card.id, taskKey: card.taskKey, projectId: PROJECT, attemptId: null, coreVersion: '1',
    resource: name, version: 'v1', computedAt: '2026-08-11T09:00:00Z', state: 'ready', data, reason: null };
}

async function routeDetailResources(page: Page, card: { id: string; taskKey: string }): Promise<void> {
  await page.route(`**/api/tasks/${card.id}/details/documents**`, route => json(route, resource(card, 'documents', {
    name: new URL(route.request().url()).searchParams.get('name'), markdown: 'Implement the change.', summaryState: null,
  })));
  await page.route(`**/api/tasks/${card.id}/details/usage**`, route => json(route, resource(card, 'usage', null)));
  await page.route(`**/api/tasks/${card.id}/details/review**`, route =>
    json(route, resource(card, 'review', { reviewProjection: null, evidence: null })));
  await page.route(`**/api/tasks/${card.id}/details/git**`, route => json(route, resource(card, 'git', {
    mergeSignal: null, integration: null, publishSignal: null, testEvidence: null, commit: null, commits: [], provenance: null,
  })));
  await page.route(`**/api/tasks/${card.id}/details/overview**`, route => json(route, resource(card, 'overview', {
    decision: null, planningSpawn: null, conceptDossier: null, olderBriefDelivery: null, relatedWikiPages: [],
    creationSource: 'operator', completionClaim: null, parkedBlocker: null, needsInput: null,
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] }, waitsOn: null, acceptanceAttemptGap: null,
  })));
}

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

async function installRoutes(page: Page, cards: readonly Record<string, unknown>[] = CARDS): Promise<void> {
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/projects', route => json(route, [{
    id: 'PROJ-001', displayName: PROJECT, shortCode: 'PH', workspaceId: 'ws-default', archived: false,
  }]));
  await page.route('**/api/v1/workspaces**', route => json(route, [{
    id: 'ws-default', displayName: 'Default', sortOrder: 0, isDefault: true,
    projects: [{ id: 'PROJ-001', displayName: PROJECT, shortCode: 'PH',
      workspaceId: 'ws-default', archived: false }],
  }]));
  for (const card of cards) {
    await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${card['id']}(\\?|$)`), route => json(route, detail(card)));
    await page.route(`**/api/tasks/${card['id']}/core**`, route => json(route, {
      state: 'ready', projectId: 'PROJ-001', projectName: PROJECT,
      id: card['id'], taskKey: card['taskKey'], watchPath: WATCH_PATH,
      folderPath: card['folderPath'], key: card['key'], title: card['title'],
      kind: 'task', taskType: 'chore', lane: '2-ready',
      enteredLaneAt: card['createdAt'], order: card['order'], mode: 'coding',
      released: false, pendingIntent: false,
      pins: { modelExplicit: false, thinkingLevelExplicit: false,
        allowWebAccess: false, noBranchExpected: false },
      actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
      blocking: { dependencyBlocked: false, dependencyState: 'ready',
        dependencies: [], dependsOn: [], blockedBy: [] },
      runtime: { location: 'none', leaseState: 'none' }, runtimeVersion: 'r0',
      statusSummary: { state: 'missing', originalBytes: 0 },
      prompt: { state: 'ready', text: 'A coding task.', originalBytes: 14 },
      timeline: { state: 'missing', events: [], originalBytes: 0 }, coreVersion: '1',
    }));
    await page.route(`**/api/tasks/${card['id']}/details/*`, route => {
      const url = new URL(route.request().url());
      const resource = url.pathname.split('/').at(-1);
      const data = resource === 'documents'
        ? { name: url.searchParams.get('name'), markdown: 'A coding task.', summaryState: null }
        : resource === 'usage'
          ? { tokenSummary: null, lastUsage: null, contextUsage: null }
          : resource === 'overview' ? {} : { evidence: [], reviewProjection: null };
      return json(route, {
        id: card['id'], taskKey: card['taskKey'], projectId: url.searchParams.get('project'),
        attemptId: null, coreVersion: '1', resource, version: '1',
        computedAt: null, state: 'ready', data, reason: null,
      });
    });
  }
  // A registered project keeps FirstRunLaunchService from opening the first-run checklist over the board.
  const registered = {
    id: PROJECT, displayName: PROJECT, shortCode: 'DEM', workspaceId: 'ws-demo',
    storageLocation: WATCH_PATH, sortOrder: 0, archived: false, createdAt: '2026-08-01T09:00:00Z',
  };
  await page.route(/\/api\/v1\/projects(\?|$)/, route => json(route, [registered]));
  await page.route('**/api/v1/workspaces**', route => json(route, [{
    id: 'ws-demo', displayName: 'Demo Workspace', sortOrder: 0, isDefault: true,
    createdAt: '2026-08-01T09:00:00Z', projects: [registered],
  }]));
  for (const card of CARDS) {
    await page.route(new RegExp(`/api/tasks/${card.id}/core(\\?|$)`), route => json(route, core(card)));
    await routeDetailResources(page, card);
  }
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0 }));
  await page.route(/\/api\/tasks(\?|$)/, route => json(route, cards));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: cards,
    progress: [], failedPickup: [], codeNotComplete: [], autoReview: [],
    review: [], humanReview: [], escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, {
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

async function openBoard(page: Page, cards: readonly Record<string, unknown>[] = CARDS): Promise<void> {
  await page.setViewportSize({ width: 1600, height: 960 });
  await page.clock.setFixedTime(new Date(NOW));
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page, cards);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
  await page.addStyleTag({
    content: 'app-error-dialog, app-offline-banner, [data-testid="error-dialog-overlay"] { display: none !important; }',
  });
}

test.describe('pickup holds are visible where the card claims to be queued', () => {
  test('an archived release gate says it can never open and names its age', async ({ page }) => {
    mkdirSync(RESULTS, { recursive: true });
    await openBoard(page);

    const card = page.getByTestId('task-card').filter({ hasText: 'duplicate CLI invocation paths' });
    const hold = card.getByTestId('pickup-hold');

    await expect(hold).toHaveAttribute('data-tone', 'blocked');
    await expect(hold).toHaveAttribute('data-mechanism', 'dependency-gate');
    await expect(card.getByTestId('pickup-hold-headline')).toContainText('cannot clear by itself');
    await expect(card.getByTestId('pickup-hold-reason')).toContainText('AGT-2372 is archived');
    await expect(card.getByTestId('pickup-hold-age')).toContainText('held for 34d');
    await expect(card.getByTestId('pickup-hold-classification')).toContainText('Unsatisfiable');
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

  test('the opened card offers all three explicit decisions for the archived gate', async ({ page }) => {
    mkdirSync(RESULTS, { recursive: true });
    await openBoard(page);

    await page.getByTestId('task-card').filter({ hasText: 'duplicate CLI invocation paths' }).click();

    const waysOut = page.getByTestId('pickup-hold-ways-out');
    await expect(waysOut).toBeVisible({ timeout: 15_000 });
    await expect(waysOut.getByRole('button', { name: 'Drop the dependency' })).toBeVisible();
    await expect(waysOut.getByRole('button', { name: 'Point to a successor card' })).toBeVisible();
    await expect(waysOut.getByRole('button', { name: 'Archive this waiting card' })).toBeVisible();

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('pickup-hold').first().screenshot({
        path: join(RESULTS, `pickup-hold-ways-out--${theme}--mocked.png`),
      });
    }

    const editRequest = page.waitForRequest(request => request.method() === 'PUT'
      && request.url().includes('/api/tasks/agt-2373/waits-on'));
    await waysOut.getByRole('button', { name: 'Drop the dependency' }).click();
    const body = (await editRequest).postDataJSON() as { remove: string[]; reason: string };
    expect(body.remove).toEqual(['AGT-2372']);
    expect(body.reason).toContain('Operator dropped unsatisfiable dependency AGT-2372');
  });

  test('a local coding hold opens the execution location in Project Settings', async ({ page }) => {
    mkdirSync(RESULTS, { recursive: true });
    const held = {
      ...PICKABLE_CARD,
      id: 'agt-local-placement', key: 'AGT-9002', displayKey: 'AGT-9002',
      taskKey: `${WATCH_PATH}::agt-local-placement`,
      title: 'First coding card on a local checkout',
      folderPath: `${WATCH_PATH}/2-ready/agt-local-placement`,
      pickupHold: {
        classification: 'unsatisfiable', mechanism: 'execution-placement',
        reason: 'Local agent execution is disabled on this orchestrator.',
        sinceUtc: '2026-09-14T08:00:00Z', unsatisfiable: true,
        resolutions: [{ kind: 'route-to-remote-runner', label: 'Choose an execution runner',
          detail: 'Open Project Settings and choose an available runner under Execution location.',
          action: { kind: 'setting', target: 'execution-runner', project: PROJECT } }],
      },
    };
    await openBoard(page, [held]);
    await page.getByTestId('task-card').filter({ hasText: held.title }).click();
    const button = page.getByTestId('pickup-hold-link-route-to-remote-runner');
    await expect(button).toBeVisible();
    await expect(button).toContainText('Open Project Settings');
    await expect(page.getByTestId('pickup-hold-ways-out')).not.toContainText('PUT /api/');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('pickup-hold').first().screenshot({
        path: join(RESULTS, `pickup-hold-execution-placement--${theme}--mocked.png`),
      });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByTestId('pickup-hold').first().screenshot({
      path: join(RESULTS, 'pickup-hold-execution-placement--390px--mocked.png'),
    });
    await page.setViewportSize({ width: 1600, height: 960 });
    await button.click();
    await expect(page).toHaveURL(/#\/projects\/PICKUP%20HOLD%20FIXTURE\/settings/);
    await expect(page.getByTestId('project-settings-panel')).toBeVisible();
    await expect(page.getByTestId('project-execution-card')).toBeVisible();
    await expect(page.locator('[data-setting-key="execution-runner"]')).toHaveAttribute('aria-current', 'location');
    await expect(page.getByTestId('project-execution-host-select')).toBeFocused();
  });

  test('a genuinely queued card carries no hold', async ({ page }) => {
    await openBoard(page);

    const card = page.getByTestId('task-card').filter({ hasText: 'A genuinely queued card' });

    await expect(card.getByTestId('pickup-hold')).toHaveCount(0);
    await expect(card.getByTestId('task-live-current')).toContainText('position 1');
  });
});
