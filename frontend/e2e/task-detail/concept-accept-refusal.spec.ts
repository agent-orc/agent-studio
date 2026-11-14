import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { test, expect, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3704 (AGT-3649 finding F11): a concept card in Human review showed
 * "Checking git status" and then "Await integration" because the Git resource
 * answered integration null, and a refused Accept or lane pick said nothing.
 *
 * Fully mocked (no backend), on the AGT-3096 core-first harness. The card
 * opens through `#/tasks/<key>` with the Git pane closed (the default layout).
 */

const WATCH_PATH = 'C:/fixtures/concept-accept';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';
const DELIVERY_SHA = 'abcdef0123456789abcdef0123456789abcdef01';

interface Fixture {
  id: string;
  ref: string;
  mode: 'concept' | 'coding';
  /** What the board card carries: null is a projection that has not caught up. */
  boardIntegration: Record<string, unknown> | null;
  /** What the Git resource answers. */
  gitIntegration: Record<string, unknown> | null;
}

const CONCEPT: Fixture = {
  id: 'agt-3649-concept', ref: 'AGT-3649', mode: 'concept',
  boardIntegration: null,
  gitIntegration: { status: 'not-applicable', integrationBranch: 'develop', sha: null, deliveryRef: null,
    detail: 'The delivery contract expects no repository change.' },
};

const PENDING: Fixture = {
  id: 'agt-3650-pending', ref: 'AGT-3650', mode: 'coding',
  boardIntegration: { status: 'pending', integrationBranch: 'develop', sha: null,
    deliveryRef: 'runner/agent-runner-01/AGT-3650', detail: 'Not in develop yet.' },
  gitIntegration: { status: 'pending', integrationBranch: 'develop', sha: null,
    deliveryRef: 'runner/agent-runner-01/AGT-3650', detail: 'Not in develop yet.' },
};

const INTEGRATED: Fixture = {
  id: 'agt-3651-integrated', ref: 'AGT-3651', mode: 'coding',
  boardIntegration: { status: 'integrated', integrationBranch: 'develop', sha: DELIVERY_SHA,
    deliveryRef: 'runner/agent-runner-01/AGT-3651', detail: null },
  gitIntegration: { status: 'integrated', integrationBranch: 'develop', sha: DELIVERY_SHA,
    deliveryRef: 'runner/agent-runner-01/AGT-3651', detail: null },
};

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function key(f: Fixture): string { return `${WATCH_PATH}::${f.id}`; }

function task(f: Fixture) {
  const coded = f.mode === 'coding';
  return {
    id: f.id, taskKey: key(f), key: f.ref, displayKey: f.ref,
    title: coded ? 'Delivery fixture' : 'Concept Dossier fixture', state: '5-human-review',
    kind: 'task', mode: f.mode, taskType: coded ? 'feature' : 'concept',
    agent: 'codex', cliType: 'codex', model: 'gpt-5.2-codex', order: 1,
    createdAt: '2026-11-09T08:00:00Z', lastActivity: '2026-11-09T10:00:00Z',
    watchPath: WATCH_PATH, projectName: 'fixture', folderPath: `${WATCH_PATH}/${f.id}`,
    sessionName: null, useOwnSession: null, lastUsage: null, execution: null,
    commit: coded ? { sha: DELIVERY_SHA, shortSha: 'abcdef0', message: 'feat: delivery',
      filesChanged: 1, files: ['src/delivery.ts'], at: '2026-11-09T09:00:00Z' } : null,
    commits: [],
    integration: f.boardIntegration,
    requiresIntegration: coded ? null : false,
    estimatedTokens: 0,
  };
}

function core(f: Fixture) {
  return {
    state: 'ready', projectId: 'fixture', projectName: 'fixture',
    id: f.id, taskKey: key(f), key: f.ref, watchPath: WATCH_PATH, folderPath: `${WATCH_PATH}/${f.id}`,
    title: task(f).title, kind: 'task', taskType: task(f).taskType,
    lane: '5-human-review', archiveState: null, enteredLaneAt: '2026-11-09T10:00:00Z',
    order: 1, mode: f.mode, released: false, pendingIntent: false, coreVersion: '3',
    pins: { model: 'gpt-5.2-codex', modelExplicit: true, thinkingLevel: null,
      thinkingLevelExplicit: false, cliType: 'codex', contextMode: null,
      useOwnSession: null, allowWebAccess: false, noBranchExpected: f.mode === 'concept' },
    actions: { canEdit: true, canMove: true, canDelete: true, canContinue: true },
    blocking: { blockerType: null, blockerCondition: null, blockerStatus: null,
      blockerDescription: null, needsInput: null, dependencyBlocked: false,
      dependencyState: 'ready', dependencies: [] },
    runtime: { attemptId: null, runnerId: null, runnerName: null, hostname: null,
      executionStatus: null, location: 'none', heartbeatAt: null, leaseState: 'none', leaseId: null },
    runtimeVersion: 'v1',
    statusSummary: { state: 'ready', text: 'Delivered.', originalBytes: 10, hash: 'status', cursor: null },
    prompt: { state: 'ready', text: '# Fixture\n\nHuman review.', originalBytes: 25,
      hash: 'prompt', cursor: null, continuationUrl: null },
    timeline: { state: 'ready', events: [], cursor: null, continuationUrl: null },
  };
}

function resource(f: Fixture, name: string, data: unknown) {
  return { id: f.id, taskKey: key(f), projectId: 'fixture', attemptId: null,
    coreVersion: '3', resource: name, version: 'v1', computedAt: '2026-11-09T10:00:00Z',
    state: 'ready', data, reason: null };
}

interface Traffic { gitReads: number; moves: Record<string, unknown>[] }

async function mockApplication(page: Page, f: Fixture, move: (route: Route) => Promise<void>): Promise<Traffic> {
  const traffic: Traffic = { gitReads: 0, moves: [] };
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/hubs/v1/studio/negotiate**', route => json(route, {
    connectionId: 'mock', connectionToken: 'mock', negotiateVersion: 1,
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
  }));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (String(message).includes('"protocol"')) socket.send('{}\x1e');
    });
  });
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { id: 'fixture', name: 'fixture', shortCode: 'AGT', path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, route => json(route, [{
    id: 'workspace', displayName: 'Workspace', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-11-09T08:00:00Z',
    projects: [{
      sourceType: 'local-folder', id: 'fixture', displayName: 'fixture', shortCode: 'AGT',
      workspaceId: 'workspace', color: null, cliDefault: 'codex', modelDefault: null,
      sortOrder: 0, storageLocation: WATCH_PATH, repositoryPath: WATCH_PATH,
      rootPath: WATCH_PATH, repositoryUrl: null, urls: [], archived: false,
      createdAt: '2026-11-09T08:00:00Z',
    }],
  }]));
  await page.route('**/api/workspaces/*/quota/admission**', route => json(route, {
    workspaceId: 'workspace',
    workspace: { projectId: null, project: null, status: 'ok', message: null,
      overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false },
    projects: [],
  }));
  await page.route('**/api/v1/management/credential-inventory', route =>
    json(route, { generatedAt: '2026-11-09T10:00:00Z', items: [] }));
  await page.route(`**/api/tasks/${f.id}/artifacts**`, route => json(route, { files: [] }));
  await page.route(`**/api/tasks/${f.id}/screenshots**`, route => json(route, { screenshots: [] }));
  await page.route(`**/api/tasks/${f.id}/code-review/list**`, route => json(route, { entries: [] }));
  await page.route(`**/api/tasks/${f.id}/quality-studio**`, route => json(route, { state: 'not-registered',
    repositoryId: null, currentHead: null, rounds: [], landedSha: null, landedBaseSha: null,
    landedPatchEqualsReviewedPatch: null }));
  await page.route('**/api/cli/usage**', route => json(route, { items: [] }));
  await page.route('**/api/usage/cockpit**', route => json(route, {}, 503));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-11-09T10:00:00Z', snapshots: [] }));
  await page.route('**/api/projects/*/workbenches**', route => json(route, { items: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/tasks', route => json(route, [task(f)]));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [task(f)],
    escalated: [], completed: [], archive: [],
  }));
  await page.route(`**/api/tasks/${f.id}/runs**`, route => json(route, {
    runCount: 0, firstStartedAt: null, lastActivityAt: null,
    hasActiveRun: false, runs: [], promptEntries: [], refinements: [], runnerEvents: [],
  }));
  await page.route(`**/api/tasks/${f.id}/session-events**`, route => json(route, { events: [], sessionChain: [] }));
  await page.route(`**/api/tasks/${f.id}/plan**`, route => json(route, { hasPlan: false,
    source: null, snapshotCount: 0, activeItemId: null, items: [], unassignedSubActions: [] }));
  await page.route(`**/api/tasks/${f.id}/pipeline**`, route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null, executions: [], config: {}, cost: null,
  }));
  await page.route(`**/api/tasks/${f.id}/provenance**`, route => json(route, {
    branch: null, base: null, transitions: [], merge: null, landedState: 'on-branch-only',
    ladder: null, commits: [],
  }));
  await page.route(new RegExp(`/api/tasks/(?:${f.ref}|${f.id})/core(\\?|$)`), route => json(route, core(f)));
  // Human Review also reads full detail for the authoritative integration verdict.
  await page.route(new RegExp(`/api/tasks/${f.id}(\\?|$)`), route => json(route, {
    info: { ...task(f), integration: f.gitIntegration },
  }));
  await page.route(`**/api/tasks/${f.id}/details/documents**`, route => {
    const name = new URL(route.request().url()).searchParams.get('name');
    return json(route, resource(f, 'documents', { name,
      markdown: name === 'prompt' ? '# Fixture\n\nHuman review.' : 'Delivered.', summaryState: null }));
  });
  await page.route(`**/api/tasks/${f.id}/details/usage**`, route => json(route, { error: 'usage offline' }, 503));
  await page.route(`**/api/tasks/${f.id}/details/review**`, route =>
    json(route, resource(f, 'review', { reviewProjection: null, evidence: null })));
  await page.route(`**/api/tasks/${f.id}/details/overview**`, route => json(route, resource(f, 'overview', {
    decision: null, planningSpawn: null, conceptDossier: null, olderBriefDelivery: null,
    relatedWikiPages: [], creationSource: 'operator', completionClaim: null,
    parkedBlocker: null, needsInput: null,
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] }, waitsOn: null,
    acceptanceAttemptGap: null,
  })));
  await page.route(`**/api/tasks/${f.id}/details/git**`, route => {
    traffic.gitReads++;
    const card = task(f);
    return json(route, resource(f, 'git', { mergeSignal: null, integration: f.gitIntegration,
      publishSignal: null, testEvidence: null, commit: card.commit, commits: card.commits, provenance: null }));
  });
  await page.route(new RegExp(`/tasks/${f.id}/move(\\?|$)`), route => {
    traffic.moves.push(route.request().postDataJSON() as Record<string, unknown>);
    return move(route);
  });
  return traffic;
}

/** Switch theme and finish the token transitions so a screenshot shows the settled state. */
async function theme(page: Page, value: 'dark' | 'light'): Promise<void> {
  await setTheme(page, value);
  await dismissDevErrorDialog(page);
  await page.evaluate(() => document.getAnimations()
    .filter(animation => animation instanceof CSSTransition)
    .forEach(animation => animation.finish()));
}

async function openCoreFirst(page: Page, f: Fixture): Promise<void> {
  await page.goto(`/#/tasks/${f.ref}`, { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('studio-task')).toBeVisible({ timeout: 30_000 });
}

/** Toolbar crop: the triage cluster with the refusal and Accept, plus the open form. */
async function screenshot(page: Page, fileName: string, extra: string[] = []): Promise<void> {
  if (!RESULTS_DIR) return;
  mkdirSync(RESULTS_DIR, { recursive: true });
  const boxes = [];
  for (const testId of ['studio-pane-toggles', 'studio-triage-panel', ...extra]) {
    const box = await page.getByTestId(testId).boundingBox();
    if (box) boxes.push(box);
  }
  const pad = 6;
  const x = Math.max(0, Math.min(...boxes.map(b => b.x)) - pad);
  const y = Math.max(0, Math.min(...boxes.map(b => b.y)) - pad);
  const viewport = page.viewportSize()!;
  await page.screenshot({ path: path.join(RESULTS_DIR, fileName), clip: { x, y,
    width: Math.min(viewport.width - x, Math.max(...boxes.map(b => b.x + b.width)) - x + pad),
    height: Math.max(...boxes.map(b => b.y + b.height)) - y + pad } });
}

test.describe('Concept and held Accept in Human review (AGT-3704)', () => {
  test.use({ serviceWorkers: 'block' });

  test('a concept card shows an enabled Accept with the Git pane closed', async ({ page }) => {
    const traffic = await mockApplication(page, CONCEPT, route => json(route, {}));
    await openCoreFirst(page, CONCEPT);

    await expect(page.getByTestId('studio-pane-toggle-git')).toHaveAttribute('aria-pressed', 'false');
    const accept = page.getByTestId('studio-triage-action-mark-done');
    await expect(accept).toHaveText(/Accept/, { timeout: 15_000 });
    await expect(accept).toBeEnabled({ timeout: 15_000 });
    await expect(page.getByTestId('acceptance-refusal')).toHaveCount(0);
    expect(traffic.gitReads).toBeGreaterThan(0);
    for (const value of ['dark', 'light'] as const) {
      await theme(page, value);
      await screenshot(page, `concept-accept-enabled-${value}.png`);
    }

    await accept.click();
    await expect.poll(() => traffic.moves.length).toBe(1);
    expect(traffic.moves[0]).toMatchObject({ targetState: '6-completed' });
    expect(traffic.moves[0]['operatorOverride']).toBeUndefined();
  });

  test('a held Accept names the reason next to it and accepts with a written owner override', async ({ page }) => {
    const traffic = await mockApplication(page, PENDING, route => json(route, {}));
    await openCoreFirst(page, PENDING);

    const accept = page.getByTestId('studio-triage-action-mark-done');
    await expect(accept).toBeDisabled({ timeout: 15_000 });
    const refusal = page.getByTestId('acceptance-refusal');
    await expect(refusal).toContainText('The delivery is not in develop yet.', { timeout: 15_000 });
    const action = page.getByTestId('acceptance-refusal-action');
    await expect(action).toHaveText('Accept with override…');
    // The refusal sits directly before the Accept button in the toolbar.
    const [refusalBox, acceptBox] = [await refusal.boundingBox(), await accept.boundingBox()];
    expect(refusalBox!.x + refusalBox!.width).toBeLessThanOrEqual(acceptBox!.x + 1);
    expect(Math.abs(refusalBox!.y + refusalBox!.height / 2 - (acceptBox!.y + acceptBox!.height / 2))).toBeLessThan(6);
    // The toolbar never clips its primary action (header guide, 1024 px rule).
    await expect(accept).toBeInViewport({ ratio: 1 });
    for (const value of ['dark', 'light'] as const) {
      await theme(page, value);
      await screenshot(page, `held-accept-refusal-${value}.png`);
    }

    await action.click();
    const submit = page.getByTestId('acceptance-refusal-submit');
    await expect(submit).toBeDisabled();
    await page.getByTestId('acceptance-refusal-override-reason')
      .fill('Reviewed by the owner; the batch gate is down and the change is docs only.');
    await expect(submit).toBeEnabled();
    for (const value of ['dark', 'light'] as const) {
      await theme(page, value);
      await screenshot(page, `held-accept-override-form-${value}.png`, ['acceptance-refusal-form']);
    }
    await submit.click();

    await expect.poll(() => traffic.moves.length).toBe(1);
    expect(traffic.moves[0]).toMatchObject({
      targetState: '6-completed', operatorOverride: true,
      reason: 'Reviewed by the owner; the batch gate is down and the change is docs only.',
    });
  });

  test('a server refusal of Accept is shown next to the button, never a silent refresh', async ({ page }) => {
    const refusal = 'Acceptance refused: the current delivery is not contained in origin/develop.';
    const traffic = await mockApplication(page, INTEGRATED, route =>
      json(route, { error: refusal, code: 'integration-dead-end' }, 409));
    await openCoreFirst(page, INTEGRATED);

    const accept = page.getByTestId('studio-triage-action-mark-done');
    await expect(accept).toBeEnabled({ timeout: 15_000 });
    await expect(page.getByTestId('acceptance-refusal')).toHaveCount(0);
    await accept.click();

    await expect.poll(() => traffic.moves.length).toBe(1);
    await expect(page.getByTestId('acceptance-refusal')).toContainText(refusal, { timeout: 15_000 });
    await expect(page.getByTestId('acceptance-refusal')).toHaveAttribute('data-refusal-source', 'server');
    await expect(page.getByTestId('acceptance-refusal-action')).toHaveText('Accept with override…');
    await expect(page.getByTestId('notification-message').filter({ hasText: refusal })).toBeVisible();
    await expect(accept).toBeInViewport({ ratio: 1 });
    await theme(page, 'dark');
    await screenshot(page, 'server-refused-accept-dark.png');
    if (RESULTS_DIR) await page.screenshot({ path: path.join(RESULTS_DIR, 'server-refused-accept-full-dark.png') });
  });

  test('picking an empty lane in the lane list says it pages and how to move the task', async ({ page }) => {
    const traffic = await mockApplication(page, CONCEPT, route => json(route, {}));
    await openCoreFirst(page, CONCEPT);
    await expect(page.getByTestId('studio-triage-action-mark-done')).toBeEnabled({ timeout: 15_000 });

    await page.getByTestId('studio-lane-select').selectOption('6-completed');
    await expect(page.getByTestId('notification-message')
      .filter({ hasText: 'This list pages through lanes; it does not move this task.' })).toBeVisible();
    expect(traffic.moves).toEqual([]);
  });

  test('390 px: the held Accept keeps its reason icon and the one action reachable', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await mockApplication(page, PENDING, route => json(route, {}));
    await openCoreFirst(page, PENDING);
    const action = page.getByTestId('acceptance-refusal-action');
    await expect(action).toBeVisible({ timeout: 15_000 });
    for (const value of ['dark', 'light'] as const) {
      await theme(page, value);
      if (RESULTS_DIR) {
        mkdirSync(RESULTS_DIR, { recursive: true });
        await page.screenshot({ path: path.join(RESULTS_DIR, `held-accept-390-${value}.png`) });
      }
    }
    await action.click();
    await expect(page.getByTestId('acceptance-refusal-form')).toBeInViewport();
    if (RESULTS_DIR) await page.screenshot({ path: path.join(RESULTS_DIR, 'held-accept-390-form-light.png') });
  });
});
