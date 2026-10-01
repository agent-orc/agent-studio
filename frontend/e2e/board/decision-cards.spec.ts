import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'fs';
import * as path from 'path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-2795 decision cards, frontend (Dossier docs/operations/decision-cards).
 *
 * Fully mocked (`--mocked` screenshots): the board shows the Decision kind
 * badge with the decider, the Explorer project header counts open decisions,
 * the decision detail opens on the question and options and records a choice
 * through `POST /api/tasks/{id}/decision`, a dependant says "blocked by" with
 * a link and its move control explains the block, and the wiki pulse inbox
 * lists the pending decision next to the Dossier lifecycle.
 */

const PROJECT = 'Decision fixture';
const SLUG = 'decision-fixture';
const WATCH_PATH = '/fixtures/decision';
const DECISION_ID = 'decide-lock-file';
const DEPENDANT_ID = 'stable-release-tag';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve('test-results', 'decision-cards');

const DESKTOP = { width: 1440, height: 900 };
const PHONE = { width: 390, height: 844 };

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function pendingDecision() {
  return {
    question: 'Ship the Stable release with the lock file back, or with an identity that needs no lock file?',
    options: [
      {
        id: 'a', label: 'Lock file back',
        consequences: 'Stable installs are byte-reproducible; the release preflight passes unchanged.',
        effort: 'Small: restore the file and its CI check.', risk: 'Low',
      },
      {
        id: 'b', label: 'Identity without lock file',
        consequences: 'The release contract moves to a content hash; the preflight must be rewritten.',
        effort: 'Medium: new identity step plus preflight change.', risk: 'Medium: first release under a new contract.',
      },
    ],
    recommendedOptionId: 'a',
    recommendationReason: 'Reproducible installs are the point of the Stable contract; option A keeps it with the least change.',
    decider: 'operator',
    dueDate: '2026-09-16T09:00:00Z',
    dependants: ['AGT-2793'],
    status: 'pending',
    history: [],
  };
}

function card(id: string, over: Record<string, unknown>) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title: id,
    state: '1-preparation',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5.6-sol',
    createdAt: '2026-09-13T09:00:00.000Z',
    lastActivity: '2026-09-13T09:30:00.000Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/1-preparation/${id}`,
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
    ...over,
  };
}

interface Fixture {
  decision: Record<string, unknown>;
  decisionState: string;
  decideBodies: unknown[];
}

function decisionCard(fx: Fixture) {
  return card(DECISION_ID, {
    key: 'AGT-2792',
    title: 'Stable release contract: lock file or not',
    kind: 'decision',
    state: fx.decisionState,
    decision: fx.decision,
  });
}

function dependantCard(fx: Fixture) {
  const open = fx.decision['status'] !== 'decided';
  return card(DEPENDANT_ID, {
    key: 'AGT-2793',
    title: 'Tag the first Stable release',
    references: { dependsOn: ['AGT-2792'], relatedTo: [], blockedBy: [], supersedes: [] },
    blockedBy: open ? ['AGT-2792'] : [],
    waitsOn: {
      blocked: open,
      cycleDetected: false,
      items: [{
        key: 'AGT-2792', resolved: true, fulfilled: !open, pendingDecision: open,
        targetJobId: DECISION_ID, targetTitle: 'Stable release contract: lock file or not',
        targetState: fx.decisionState, targetWatchPath: WATCH_PATH,
      }],
    },
  });
}

function readyCard() {
  return card('docs-refresh', { key: 'AGT-2801', title: 'Refresh the release runbook', state: '2-ready' });
}

function grouped(fx: Fixture) {
  const decision = decisionCard(fx);
  return {
    backlog: [], preparation: [dependantCard(fx), ...(fx.decisionState === '1-preparation' ? [decision] : [])],
    orchestratorPrep: [], ready: [readyCard()], progress: [], failedPickup: [], codeNotComplete: [],
    review: [], autoReview: [], humanReview: [], escalated: [],
    completed: fx.decisionState === '6-completed' ? [decision] : [], archive: [],
  };
}

function detail(info: unknown) {
  return {
    info,
    promptMarkdown: '# Decision\n\nChoose how the Stable release identifies its dependencies.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: null,
  };
}

const LIFECYCLE = {
  available: true,
  reason: null,
  count: 1,
  items: [{
    relPath: 'operations/decision-cards/index.html', title: 'Decision cards', pageKind: 'workbench',
    state: 'decided', editedBy: 'Operator', editedAtUtc: '2026-09-25T07:52:00Z', history: [],
    workbenchId: null, valid: true, error: null,
  }],
};

async function installRoutes(page: Page): Promise<Fixture> {
  const fx: Fixture = { decision: pendingDecision(), decisionState: '1-preparation', decideBodies: [] };

  // No backend runs: answer the Studio hub handshake so the shell stays online.
  await page.route('**/update/status', route => json(route, { phase: 'idle', isRunning: false, behindBy: 0 }));
  await page.route('**/hubs/v1/studio/negotiate**', route => json(route, {
    connectionId: 'decision-cards-e2e',
    connectionToken: 'decision-cards-e2e',
    negotiateVersion: 1,
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  }));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (message.toString().includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: false, user: null,
  }));
  await page.route('**/api/crash-recovery/pending', route => json(route, { pending: [] }));
  await page.route('**/api/v1/studio/board**', route => json(route, grouped(fx)));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/projects/settings**', route => json(route, {}));
  await page.route(/\/api\/v1\/workspaces(\?|$)/, route => json(route, [{
    id: 'ws-default', displayName: 'Default', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-09-01T00:00:00Z',
    projects: [{
      sourceType: 'local', id: 'PROJ-0099', displayName: PROJECT, shortCode: 'AGT', workspaceId: 'ws-default',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: WATCH_PATH,
      repositoryPath: WATCH_PATH, rootPath: WATCH_PATH, repositoryUrl: null, urls: [], archived: false,
    }],
  }]));
  await page.route(/\/api\/tasks\/archive(\?|$)/, route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route(/\/api\/clients\/[^/]+\/telemetry/, route => json(route, { points: [], findings: [], window: '14d' }));
  await page.route(/\/api\/projects\/[^/]+\/workbenches(\?|$)/, route => json(route, {
    projectName: PROJECT, includesHistory: false, count: 0, items: [],
  }));
  await page.route('**/api/clients', route => json(route, [
    { id: 'local-default', displayName: 'Local', kind: 'agent-instance' },
  ]));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-09-13T09:30:00.000Z', ttlSeconds: 600, snapshots: [],
  }));
  await page.route('**/api/runner/orchestrator-feed**', route => json(route, {
    entries: [], generatedAtUtc: '2026-09-13T09:30:00.000Z',
  }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));

  for (const id of [DECISION_ID, DEPENDANT_ID]) {
    await page.route(new RegExp(`/api/tasks/${id}/output(\\?|$)`), route => json(route, []));
    await page.route(new RegExp(`/api/tasks/${id}/runs(\\?|$)`), route => json(route, { runs: [] }));
    await page.route(new RegExp(`/api/tasks/${id}/session-events(\\?|$)`), route => json(route, {
      events: [], sessionChain: [],
    }));
    await page.route(new RegExp(`/api/tasks/${id}/pipeline(\\?|$)`), route => json(route, {
      pipeline: { id: 'p', displayName: 'Pipeline', version: 1, pre: [], core: [], post: [], allSteps: [] },
      execution: null, cost: null, config: {},
    }));
  }
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${DECISION_ID}(\\?|$)`), route => json(route, detail(decisionCard(fx))));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${DEPENDANT_ID}(\\?|$)`), route => json(route, detail(dependantCard(fx))));
  await page.route(new RegExp(`/api/tasks/${DECISION_ID}/decision(\\?|$)`), async route => {
    const body = route.request().postDataJSON() as { optionId: string; rationale: string };
    fx.decideBodies.push(body);
    fx.decision = {
      ...fx.decision,
      status: 'decided',
      chosenOptionId: body.optionId,
      rationale: body.rationale,
      decidedBy: 'operator',
      decidedAt: '2026-09-25T07:52:00Z',
      recordPath: 'operations/decisions/AGT-2792.md',
      history: [{ status: 'decided', optionId: body.optionId, rationale: body.rationale, actor: 'operator', at: '2026-09-25T07:52:00Z' }],
    };
    fx.decisionState = '6-completed';
    await json(route, { decision: fx.decision, targetState: '6-completed' });
  });

  await page.route(/\/api\/projects\/[^/]+\/wiki\/pulse/, route => json(route, {
    projectName: PROJECT, baseDir: '/repo/docs', exists: true, generatedAtUtc: '2026-09-25T08:00:00Z',
    feed: { available: true, reason: null, items: [] },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: { available: false, reason: 'Drift not computed in this fixture.', overallGrade: null, areas: [], counts: { fresh: 0, aging: 0, stale: 0, graded: 0 } },
    warnings: { available: true, reason: null, count: 0, items: [] },
    activity: { available: true, reason: null, runs: [] },
    lifecycle: LIFECYCLE,
    workbenches: { projectName: PROJECT, includesHistory: false, count: 0, items: [] },
  }));
  await page.route(/\/api\/projects\/[^/]+\/style-guides/, route => json(route, {
    projectKey: 'PROJ-0099', projectDisplayName: PROJECT, technologies: [], guides: [], warnings: [],
    snapshotId: 'fixture', capturedAtUtc: '2026-09-25T08:00:00Z', refreshAfterUtc: '2026-09-25T08:05:00Z',
  }));
  await page.route(/\/api\/projects\/[^/]+\/wiki\/tree/, route => json(route, {
    exists: true, root: [{ type: 'md', name: 'README.md', title: 'Readme', relPath: 'README.md', children: [] }],
  }));
  return fx;
}

/**
 * Phone framing: the shell reserves a fixed Explorer track while the rail is
 * open, which squeezes the content to a sliver at 390 px. Close the rail for
 * the evidence shot (screenshot framing only, as in done-decide-escalated-card).
 */
async function toPhone(page: Page): Promise<void> {
  await page.setViewportSize(PHONE);
  const explorer = page.getByTestId('studio-ab-explorer');
  if (await explorer.evaluate(el => el.classList.contains('studio-ab__btn--active')).catch(() => false)) {
    await explorer.click();
  }
  await page.waitForTimeout(300);
}

async function shot(page: Page, name: string, target?: import('@playwright/test').Locator): Promise<void> {
  const file = path.join(RESULTS_DIR, `${name}--mocked.png`);
  if (target) await target.screenshot({ path: file });
  else await page.screenshot({ path: file, fullPage: false });
}

test.describe('Decision cards (AGT-2795)', () => {
  test.beforeAll(() => mkdirSync(RESULTS_DIR, { recursive: true }));
  test.afterEach(async ({ page }) => {
    await page.unrouteAll({ behavior: 'ignoreErrors' });
  });

  test('board shows the Decision badge with the decider, blocked-by links, and the header count', async ({ page }) => {
    await page.setViewportSize(DESKTOP);
    await installRoutes(page);
    await page.goto('/');
    await dismissDevErrorDialog(page);

    const decision = page.getByTestId('task-card-decision-badge');
    await expect(decision).toHaveText('Decision');
    await expect(page.getByTestId('task-card-decider')).toHaveText('Decider: Operator');
    await expect(page.getByTestId('task-card-blocked-by')).toHaveText('blocked by AGT-2792');
    const decisionsCounter = page.getByTestId(`studio-explorer-project-board-count-decisions-${PROJECT}`);
    if (!(await decisionsCounter.isVisible())) await page.getByTestId(`studio-explorer-project-${PROJECT}`).click();
    await expect(decisionsCounter).toHaveText('1');
    // The dependant's generic waits-on chip does not repeat the decision edge.
    await expect(page.getByTestId('task-card-waiting-on')).toHaveCount(0);

    await page.mouse.move(DESKTOP.width - 10, DESKTOP.height / 2);
    const decisionCardEl = page.getByTestId('task-card').filter({ has: page.getByTestId('task-card-decision-badge') });
    const dependantCardEl = page.getByTestId('task-card').filter({ has: page.getByTestId('task-card-blocked-by') });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await shot(page, `decision-board-desktop-${theme}`);
      await shot(page, `decision-board-card-desktop-${theme}`, decisionCardEl);
      await shot(page, `decision-board-dependant-card-desktop-${theme}`, dependantCardEl);
      await shot(page, `decision-header-count-desktop-${theme}`, page.getByTestId(`studio-explorer-project-board-${PROJECT}`));
    }

    await toPhone(page);
    await decisionCardEl.scrollIntoViewIfNeeded();
    await shot(page, 'decision-board-phone-dark');
    await shot(page, 'decision-board-card-phone-dark', decisionCardEl);
    await shot(page, 'decision-board-dependant-card-phone-dark', dependantCardEl);
    await page.setViewportSize(DESKTOP);

    await page.getByTestId('task-card-blocked-by').click();
    await expect(page.getByTestId('decision-card-panel')).toBeVisible();
  });

  test('decision detail opens on the question and options and records one choice with a rationale', async ({ page }) => {
    await page.setViewportSize(DESKTOP);
    const fx = await installRoutes(page);
    await page.goto(`/?job=${DECISION_ID}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissDevErrorDialog(page);

    const panel = page.getByTestId('decision-card-panel');
    await expect(panel).toBeVisible();
    await expect(page.getByTestId('decision-card-question')).toContainText('lock file back');
    await expect(page.getByTestId('decision-card-option')).toHaveCount(2);
    await expect(page.getByTestId('decision-card-recommended')).toHaveCount(1);
    await expect(page.getByTestId('decision-card-option').first()).toContainText('byte-reproducible');
    await expect(page.getByTestId('decision-card-choose')).toHaveCount(2);
    await expect(page.getByTestId('decision-card-due')).toHaveAttribute('data-overdue', 'true');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await shot(page, `decision-detail-pending-desktop-${theme}`, panel);
    }
    await toPhone(page);
    await shot(page, 'decision-detail-pending-phone-dark', panel);
    await page.setViewportSize(DESKTOP);

    await page.getByTestId('decision-card-rationale-input').fill('Reproducible installs are the Stable contract.');
    await page.getByTestId('decision-card-choose').first().click();

    await expect.poll(() => fx.decideBodies).toEqual([
      { optionId: 'a', rationale: 'Reproducible installs are the Stable contract.' },
    ]);
    const receipt = page.getByTestId('decision-card-receipt');
    await expect(receipt).toContainText('Decided: a');
    await expect(receipt).toContainText('Lock file back');
    await expect(page.getByTestId('decision-card-rationale')).toContainText('Reproducible installs are the Stable contract.');
    await expect(page.getByTestId('decision-card-decided-by')).toHaveText('Operator');
    await expect(page.getByTestId('decision-card-decided-at')).toHaveText('2026-09-25 07:52Z');
    await expect(page.getByTestId('decision-card-record-link')).toHaveAttribute(
      'href', `#/projects/${SLUG}/wiki?page=operations%2Fdecisions%2FAGT-2792.md`);
    await expect(page.getByTestId('decision-card-choose')).toHaveCount(0);
    await expect(page.getByTestId('decision-card-reopen')).toBeVisible();

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await shot(page, `decision-detail-decided-desktop-${theme}`, panel);
    }
    await toPhone(page);
    await shot(page, 'decision-detail-decided-phone-dark', panel);
  });

  test('a dependant shows "blocked by" with a link and its move control explains the block', async ({ page }) => {
    await page.setViewportSize(DESKTOP);
    await installRoutes(page);
    await page.goto(`/?job=${DEPENDANT_ID}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await dismissDevErrorDialog(page);

    const notice = page.getByTestId('decision-blocked-notice');
    await expect(notice).toContainText('Blocked by');
    await expect(notice).toContainText('cannot be claimed or moved to Ready or In Progress');
    await expect(page.getByTestId('decision-blocked-link')).toHaveText('AGT-2792');

    const promote = page.getByTestId('studio-triage-action-promote-ready');
    await expect(promote).toBeDisabled();

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await shot(page, `decision-dependant-desktop-${theme}`);
    }
    await toPhone(page);
    await shot(page, 'decision-dependant-phone-dark');
    await page.setViewportSize(DESKTOP);

    await page.getByTestId('decision-blocked-link').click();
    await expect(page.getByTestId('decision-card-panel')).toBeVisible();
  });

  test('wiki pulse inbox lists the pending decision card next to the Dossier lifecycle', async ({ page }) => {
    await page.setViewportSize(DESKTOP);
    await installRoutes(page);
    await page.goto(`/#/projects/${SLUG}/wiki`);
    await dismissDevErrorDialog(page);

    const inbox = page.getByTestId('project-wiki-pulse-lifecycle');
    await expect(inbox).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('project-wiki-lifecycle-group-decision-cards')).toContainText('Decisions waiting');
    const row = page.getByTestId('project-wiki-decision-card-AGT-2792');
    await expect(row).toContainText('decider Operator');
    await expect(row).toContainText('blocks AGT-2793');
    await expect(page.getByTestId('project-wiki-lifecycle-group-decided')).toContainText('Decision cards');

    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await shot(page, `decision-inbox-desktop-${theme}`, inbox);
    }
    await toPhone(page);
    await shot(page, 'decision-inbox-phone-dark', inbox);
    await page.setViewportSize(DESKTOP);

    await row.click();
    await expect(page.getByTestId('decision-card-panel')).toBeVisible();
  });
});
