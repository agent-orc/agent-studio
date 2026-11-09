import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3580 - ways out and remedies are typed actions, not HTTP or shell lines.
 *
 * The operator walkthrough (AGT-3561) found the pickup-hold way out for a
 * local execution placement rendered as `PUT /api/projects/.../execution-runner
 * {...}` and the readiness remedies for repository visibility and the
 * integration branch rendered the same way, so the operator fell back to
 * scripts. Here every remedy is prose plus a control: a runner picker that
 * calls the existing execution-runner route, a link that opens the owning
 * project setting, and shell commands only as a copyable detail.
 *
 * Backend-mocked: the typed actions are pinned by PickupHoldPolicyTests and
 * ServerReadinessPolicyTests; this proves they reach the operator as controls.
 */
const PROJECT = 'Demo';
const WATCH_PATH = '/fixtures/typed-remedies';
const RESULTS = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), '..', 'results');
const HTTP_VERB = /^\s*(GET|PUT|POST|PATCH|DELETE|HEAD|OPTIONS)\b/;

const PLACEMENT_CARD = {
  id: 'agt-9100',
  key: 'AGT-9100',
  displayKey: 'AGT-9100',
  taskKey: `${WATCH_PATH}::agt-9100`,
  title: 'Coding card held at local placement',
  state: '2-ready',
  order: 1,
  agent: 'codex',
  cliType: 'codex',
  mode: 'coding',
  createdAt: '2026-11-09T08:00:00Z',
  lastActivity: '2026-11-09T08:00:00Z',
  enteredLaneAt: '2026-11-09T08:00:00Z',
  watchPath: WATCH_PATH,
  projectName: PROJECT,
  folderPath: `${WATCH_PATH}/2-ready/agt-9100`,
  ownerClientId: 'local-default',
  commits: [],
  tags: [],
  references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
  waitsOn: null,
  pickupHold: {
    classification: 'unsatisfiable',
    mechanism: 'execution-placement',
    reason: `Project ${PROJECT} is placed at local execution, but local agent execution is disabled on this orchestrator.`,
    sinceUtc: '2026-11-09T08:00:00Z',
    unsatisfiable: true,
    resolutions: [{
      kind: 'route-to-remote-runner',
      label: 'Route to a remote runner',
      detail: `Pick the remote runner that executes ${PROJECT}; the project's execution placement setting holds the same choice.`,
      targetKey: null,
      action: { kind: 'api', target: 'set-execution-runner', project: PROJECT, taskKey: null },
    }],
  },
  liveStatus: { attempt: 1, activeStep: null, nextSteps: [], queue: null, latestEventAt: '2026-11-09T08:00:00Z' },
};

const RUNNER = {
  id: 'agent-runner-01',
  displayName: 'agent-runner-01',
  emoji: null,
  colour: null,
  kind: 'agent-instance',
  registeredAt: '2026-11-01T08:00:00Z',
  lastSeenAt: '2026-11-09T08:59:30Z',
  tokenBudgetMonthly: null,
  notes: null,
  runnerGitStatus: 'ok',
  runnerGitDetail: null,
  runnerActiveSlots: 0,
  runnerAvailableSlots: 2,
};

const line = (id: string, title: string, scope: string, subject: string | null, state: string, meaning: string,
  remedy: string | null = null, command: string | null = null, action: Record<string, string> | null = null) =>
  ({ id, title, scope, subject, state, meaning, remedy, command, action });

/** The lines a freshly moved server reports, in the server's AGT-3580 wording. */
const CHECKS = [
  line('git-identity', 'Git identity of the service user', 'server', null, 'fail',
    'Git has no user.name and no user.email for the service user.',
    'Set the Git identity of the service user in the API container, from the stack directory.',
    'docker compose exec api git config --global user.name "Agent Studio" && docker compose exec api git config --global user.email "studio@agent-studio.invalid"'),
  line('execution-host', 'Execution host enrolled and seen', 'host', 'agent-runner-02', 'fail',
    'Enrolled, but not seen for 5 h: it claims no cards.',
    'On agent-runner-02, check that the runner service runs and its RUNNER_SERVER_URL points at this server, then restart it.',
    null, { kind: 'route', target: 'execution-hosts' }),
  line('repository-visibility', 'Repository visibility known', 'project', PROJECT, 'warn',
    'Not recorded. Remote Review then treats the repository as public and clones it without credentials, which fails for a private repository.',
    "Record it as private or public in the project's repository visibility setting.",
    null, { kind: 'setting', target: 'repository-visibility', project: PROJECT }),
  line('integration-branch', 'Integration branch set', 'project', PROJECT, 'fail',
    'The integration branch develop (set for the project) has no origin/develop in this checkout, so every integration fails.',
    'Fetch the branch into the checkout; if the branch has another name, set it in the project setting.',
    'git -C /data/projects/demo fetch origin develop', { kind: 'setting', target: 'integration-branch', project: PROJECT }),
  line('integration-lane', 'Integration lane present', 'project', PROJECT, 'pass', 'refs/agent-studio/integration/main is present.'),
];

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

async function installRoutes(page: Page, executionPuts: unknown[]): Promise<void> {
  await page.route('**/api/**', route => json(route, []));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${PLACEMENT_CARD.id}(\\?|$)`), route => json(route, {
    info: PLACEMENT_CARD, promptMarkdown: 'Implement the change.', statusMarkdown: '', log: [], promptHistory: [],
    contextUsage: null, reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  }));
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
  for (const card of [PLACEMENT_CARD]) {
    await page.route(new RegExp(`/api/tasks/${card.id}/core(\\?|$)`), route => json(route, core(card)));
    await routeDetailResources(page, card);
  }
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0 }));
  await page.route(/\/api\/tasks(\?|$)/, route => json(route, [PLACEMENT_CARD]));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [PLACEMENT_CARD],
    progress: [], failedPickup: [], codeNotComplete: [], autoReview: [],
    review: [], humanReview: [], escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/environment/flow', route => json(route, {
    capturedAt: '2026-11-09T09:00:00Z',
    today: { created: 1, delivered: 0, reviewed: 0, integrated: 0, deployed: null, completed: 0, readyToIntegrationMedianMinutes: null },
    lanes: [], queues: [], waitingReasons: [],
  }));
  await page.route('**/api/clients', route => json(route, [RUNNER]));
  await page.route(/\/api\/(?:v1\/studio\/)?runner\/status(\?|$)/, route => json(route, {
    projects: { [PROJECT]: { projectName: PROJECT, mode: 'auto', activeJobId: null, activeExecution: null, queuedJobIds: [] } },
  }));
  await page.route(/\/api\/tasks\/[^/?]+\/pipeline(\?|$)/, route => json(route, {
    pipeline: { id: 'standard', displayName: 'Standard task pipeline', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null,
  }));
  await page.route(/\/api\/tasks\/[^/?]+\/(session-events|runs)(\?|$)/, route =>
    json(route, route.request().url().includes('/runs') ? { runs: [] } : { events: [], sessionChain: [] }));
  await page.route('**/api/projects/settings', route => json(route, {
    [PROJECT]: { pickupMode: 'auto', executionLocation: 'local', localCodingAdmitted: false, repositoryVisibility: null, integrationBranch: 'develop' },
  }));
  await page.route('**/api/system/readiness', route => json(route, {
    computedAt: '2026-11-09T09:00:00+00:00', state: 'fail', failing: 3, attention: 1, checks: CHECKS,
  }));
  await page.route(`**/api/projects/${PROJECT}/execution-runner`, async route => {
    executionPuts.push({ method: route.request().method(), body: route.request().postDataJSON() });
    await json(route, { executionLocation: 'agent-runner-01', pickupMode: 'auto' });
  });
}

async function openStudio(page: Page, executionPuts: unknown[] = []): Promise<void> {
  await page.setViewportSize({ width: 1600, height: 960 });
  await page.clock.setFixedTime(new Date('2026-11-09T09:00:00Z'));
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1, tabs: [{ kind: 'board', projectName: '__all__' }], activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page, executionPuts);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
  await page.addStyleTag({
    content: 'app-error-dialog, app-offline-banner, [data-testid="error-dialog-overlay"] { display: none !important; }',
  });
}

/** Every visible remedy sentence and way-out text, as the operator reads it. */
async function renderedRemedies(page: Page): Promise<string[]> {
  return page.locator([
    '[data-testid="readiness-remedy"]',
    '[data-testid="readiness-remedy-action"]',
    '[data-resolution-kind] [data-testid="pickup-hold-resolution-label"]',
    '[data-resolution-kind] [data-testid="pickup-hold-resolution-detail"]',
    '[data-resolution-kind] button',
  ].join(', ')).allInnerTexts();
}

test.describe('remedies are typed actions, not HTTP or shell lines', () => {
  test('a local placement hold offers a runner picker that calls the execution-runner route', async ({ page }) => {
    mkdirSync(RESULTS, { recursive: true });
    const puts: { method: string; body: unknown }[] = [];
    await openStudio(page, puts);

    await page.getByTestId('task-card').filter({ hasText: 'held at local placement' }).click();
    const way = page.locator('[data-resolution-kind="route-to-remote-runner"]');
    await expect(way).toBeVisible({ timeout: 15_000 });
    await expect(way).not.toContainText('/api/');
    const route = way.getByTestId('pickup-hold-decision-route-to-remote-runner');
    await expect(route).toBeDisabled();
    await expect(way.getByTestId('pickup-hold-open-setting')).toBeVisible();

    const remedies = await renderedRemedies(page);
    expect(remedies.length).toBeGreaterThan(0);
    for (const text of remedies) expect(text, text).not.toMatch(HTTP_VERB);

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('pickup-hold').first().screenshot({
        path: join(RESULTS, `typed-remedies-runner-picker--${theme}.png`),
      });
    }
    await way.getByTestId('pickup-hold-runner-select').selectOption('agent-runner-01');
    await expect(route).toBeEnabled();
    await route.click();

    await expect.poll(() => puts.length).toBe(1);
    expect(puts[0]).toEqual({ method: 'PUT', body: { executionLocation: 'agent-runner-01' } });
  });

  test('readiness remedies are prose, commands are copyable details, and settings open in place', async ({ page }) => {
    mkdirSync(RESULTS, { recursive: true });
    await openStudio(page);
    await setTheme(page, 'light');
    await page.getByTestId('studio-titlebar-environment').click();
    const panel = page.getByTestId('server-readiness');
    await expect(panel.locator('[data-testid="readiness-remedy"]')).toHaveCount(4);

    const remedies = await renderedRemedies(page);
    for (const text of remedies) expect(text, text).not.toMatch(HTTP_VERB);
    await expect(panel).not.toContainText('/api/');
    // The shell command is a copyable detail, never the remedy itself.
    const identity = panel.getByTestId('readiness-server:git-identity:');
    await expect(identity.getByTestId('readiness-remedy')).not.toContainText('docker compose');
    await expect(identity.getByTestId('readiness-command-text')).toContainText('git config --global user.name');
    await expect(identity.getByTestId('readiness-command-copy')).toBeVisible();
    await expect(panel.getByTestId('readiness-host:execution-host:agent-runner-02').getByTestId('readiness-remedy-action'))
      .toHaveText('Open Execution Hosts →');
    await expect(panel.getByTestId('readiness-project:integration-branch:Demo').getByTestId('readiness-remedy-action'))
      .toHaveText('Open integration branch →');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await panel.screenshot({ path: join(RESULTS, `typed-remedies-readiness--${theme}.png`) });
    }
    await setTheme(page, 'light');

    await panel.getByTestId('readiness-project:repository-visibility:Demo').getByTestId('readiness-remedy-action').click();
    await expect(page.getByTestId('environment-flow')).toHaveCount(0);
    const visibility = page.locator('[data-setting-key="repository-visibility"]');
    await expect(visibility).toBeVisible({ timeout: 15_000 });
    await expect(visibility).toHaveAttribute('aria-current', 'location');
    await expect(page.getByTestId('project-repository-visibility-unrecorded')).toBeVisible();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('project-repository-card').screenshot({
        path: join(RESULTS, `typed-remedies-repository-setting--${theme}.png`),
      });
    }
  });
});
