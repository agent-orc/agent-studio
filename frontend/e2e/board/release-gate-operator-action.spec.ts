import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-2709 — the operator affordance for the release gate.
 *
 * A `references.dependsOn` edge with `releaseGate: true` is only fulfilled once
 * the target is terminal AND carries its explicit `released` flag; nothing in
 * the lifecycle sets that flag. Before this task the flag had no UI at all, so
 * a card could sit in "waits for release" forever while the operator hunted for
 * a release screen that did not exist (AGT-2373 waiting on the archived
 * AGT-2372, 2026-09-06).
 *
 * Proven here against mocked API routes (no backend needed):
 *  1. the dependent card releases its target inline and then loses its
 *     "waits for release" chip on the board;
 *  2. the terminal target itself offers the release and names the dependents it
 *     unblocks;
 *  3. the board filter surfaces both sides of a pending release at once.
 */

const PROJECT = 'Release gate fixture';
const WATCH_PATH = 'C:/fixtures/release-gate';
const DEPENDENT_ID = 'app-1';
const TARGET_ID = 'lib-1';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve('test-results', 'release-gate-operator-action');

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

/** The dependent: terminal target, release still missing while `released` is false. */
function dependent(released: boolean) {
  return {
    id: DEPENDENT_ID,
    key: 'APP-1',
    displayKey: 'APP-1',
    taskKey: `${WATCH_PATH}::${DEPENDENT_ID}`,
    title: 'Consumer waiting on the library',
    state: '2-ready',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-09-01T08:00:00Z',
    lastActivity: '2026-09-01T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/2-ready/${DEPENDENT_ID}`,
    ownerClientId: 'local-default',
    commits: [],
    tags: [],
    references: {
      dependsOn: [{ key: 'LIB-1', releaseGate: true }],
      relatedTo: [], blockedBy: [], supersedes: [], workbenches: [],
    },
    waitsOn: {
      blocked: !released,
      cycleDetected: false,
      items: [{
        key: 'LIB-1',
        resolved: true,
        fulfilled: released,
        releaseGate: true,
        targetReleased: released,
        waitingForRelease: !released,
        targetJobId: TARGET_ID,
        targetTitle: 'Library acceptance',
        targetState: '7-archive',
        targetWatchPath: WATCH_PATH,
      }],
    },
  };
}

/** The target: archived, and the only thing between the dependent and pickup. */
function target(released: boolean) {
  return {
    id: TARGET_ID,
    key: 'LIB-1',
    displayKey: 'LIB-1',
    taskKey: `${WATCH_PATH}::${TARGET_ID}`,
    title: 'Library acceptance',
    state: '7-archive',
    released,
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-08-20T08:00:00Z',
    lastActivity: '2026-08-30T08:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/7-archive/${TARGET_ID}`,
    ownerClientId: 'local-default',
    commits: [],
    tags: [],
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [], workbenches: [] },
  };
}

const GATED_LINK = {
  sourceKey: 'APP-1',
  sourceJobId: DEPENDENT_ID,
  sourceTitle: 'Consumer waiting on the library',
  sourceState: '2-ready',
  sourceWatchPath: WATCH_PATH,
  kind: 'dependsOn',
  releaseGate: true,
};

/** An unrelated Ready card, so the "waiting for release" filter has to narrow. */
function bystander() {
  return { ...dependent(true), id: 'app-2', key: 'APP-2', displayKey: 'APP-2', taskKey: `${WATCH_PATH}::app-2`, title: 'Unrelated work', references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [], workbenches: [] }, waitsOn: null };
}

function detail(info: unknown) {
  return {
    info,
    promptMarkdown: '# Prompt',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: null,
  };
}

interface Harness {
  /** Bodies of every PUT /release the UI issued, in order. */
  releaseWrites: { jobId: string; released: boolean }[];
}

async function installRoutes(page: Page): Promise<Harness> {
  // Server-side truth for the fixture; the release write flips it so the
  // follow-up reads show the unblocked projection.
  let released = false;
  const releaseWrites: { jobId: string; released: boolean }[] = [];

  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0 }));
  await page.route(/\/api\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  // Object-shaped reads the shell dereferences eagerly. The `[]` catch-all
  // above would make them throw and raise the dev error overlay, which then
  // intercepts every click in this spec.
  await page.route('**/api/projects/settings**', route => json(route, {}));
  await page.route('**/api/projects/*/workbenches**', route => json(route, {
    projectName: PROJECT, includesHistory: true, count: 0, items: [],
  }));
  await page.route('**/api/workspaces**', route => json(route, []));
  await page.route('**/api/clients', route => json(route, [
    { id: 'local-default', displayName: 'Local', kind: 'agent-instance' },
  ]));
  await page.route('**/api/clients/local-default/defaults**', route => json(route, {}));
  await page.route('**/api/clients/local-default/telemetry**', route => json(route, {
    points: [], findings: [], window: '14d',
  }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-09-01T08:00:00Z', ttlSeconds: 600, snapshots: [],
  }));
  await page.route('**/api/runner/orchestrator-feed**', route => json(route, {
    entries: [], generatedAtUtc: '2026-09-01T08:00:00Z',
  }));
  for (const id of [DEPENDENT_ID, TARGET_ID]) {
    await page.route(new RegExp(`/api/tasks/${id}/runs(\\?|$)`), route => json(route, { runs: [] }));
    await page.route(new RegExp(`/api/tasks/${id}/session-events(\\?|$)`),
      route => json(route, { events: [], sessionChain: [] }));
    await page.route(new RegExp(`/api/tasks/${id}/pipeline(\\?|$)`), route => json(route, {
      pipeline: { id: 'p', displayName: 'Pipeline', version: 1, pre: [], core: [], post: [], allSteps: [] },
      execution: null, cost: null, config: {},
    }));
  }
  await page.route(/\/api\/tasks(\?|$)/, route => json(route, [dependent(released), bystander()]));
  await page.route('**/api/tasks/grouped**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [],
    ready: [dependent(released), bystander()],
    progress: [], failedPickup: [], codeNotComplete: [], autoReview: [], review: [],
    humanReview: [], escalated: [], completed: [], archive: [],
  }));

  // Reverse index: the target's release-gated dependent.
  await page.route(new RegExp(`/api/tasks/${TARGET_ID}/dependents(\\?|$)`),
    route => json(route, [GATED_LINK]));
  await page.route(new RegExp(`/api/tasks/${DEPENDENT_ID}/dependents(\\?|$)`),
    route => json(route, []));

  await page.route(new RegExp(`/api/tasks/${DEPENDENT_ID}(\\?|$)`),
    route => json(route, detail(dependent(released))));
  await page.route(new RegExp(`/api/tasks/${TARGET_ID}(\\?|$)`),
    route => json(route, detail(target(released))));

  await page.route(/\/api\/tasks\/[^/]+\/release(\?|$)/, async route => {
    const jobId = /\/api\/tasks\/([^/]+)\/release/.exec(route.request().url())?.[1] ?? '';
    const body = route.request().postDataJSON() as { released?: boolean };
    released = body?.released === true;
    releaseWrites.push({ jobId, released });
    await json(route, { released });
  });

  return { releaseWrites };
}

async function openBoard(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
}

async function openDetail(page: Page, jobId: string): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(
    `/?job=${encodeURIComponent(jobId)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    { waitUntil: 'domcontentloaded' },
  );
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('references-section')).toBeVisible({ timeout: 15_000 });
}

test.describe('release gate operator affordance', () => {
  test('the dependent releases its target inline and stops waiting', async ({ page }) => {
    const harness = await installRoutes(page);
    await openDetail(page, DEPENDENT_ID);

    const waiting = page.getByTestId('release-gate-waiting-LIB-1');
    await expect(waiting).toBeVisible();
    await expect(waiting).toContainText('LIB-1');

    mkdirSync(RESULTS_DIR, { recursive: true });
    await page.getByTestId('references-section').screenshot({
      path: `${RESULTS_DIR}/release-gate-dependent-inline--mocked.png`,
    });

    await page.getByTestId('release-gate-release-LIB-1').click();

    // One write, addressed at the TARGET rather than the card in front of us.
    await expect.poll(() => harness.releaseWrites).toEqual([
      { jobId: TARGET_ID, released: true },
    ]);
    // The re-fetched projection no longer reports a pending release.
    await expect(page.getByTestId('release-gate-waiting')).toHaveCount(0);

    // And the board card stops announcing that it waits for a release; the
    // dependency chip flips to the fulfilled reading instead.
    await openBoard(page);
    const card = page.getByTestId('task-card').filter({ hasText: 'Consumer waiting on the library' });
    await expect(card).toBeVisible();
    await expect(card.getByTestId('task-card-waiting-on')).not.toContainText('waits for release');
  });

  test('the terminal target offers the release and names the dependents it unblocks', async ({ page }) => {
    const harness = await installRoutes(page);
    await openDetail(page, TARGET_ID);

    const section = page.getByTestId('release-gate-target');
    await expect(section).toBeVisible();
    await expect(page.getByTestId('release-gate-state')).toHaveText('Release pending');
    await expect(page.getByTestId('release-gate-dependent-APP-1')).toContainText('APP-1');

    await page.getByTestId('release-gate-toggle').click();

    await expect.poll(() => harness.releaseWrites).toEqual([
      { jobId: TARGET_ID, released: true },
    ]);
    // Reversible: the same control now offers to withdraw the release.
    await expect(page.getByTestId('release-gate-state')).toHaveText('Released');
    await expect(page.getByTestId('release-gate-toggle')).toHaveText('Withdraw release');
  });

  test('the board filter surfaces tasks waiting for release without opening cards', async ({ page }) => {
    await installRoutes(page);
    await openBoard(page);

    const cards = page.getByTestId('task-card');
    await expect(cards).toHaveCount(2);

    await page.getByTestId('studio-ab-filters').click();
    const toggle = page.getByTestId('kanban-filter-waiting-for-release');
    await expect(toggle).toBeVisible();
    await toggle.locator('input[type="checkbox"]').check();

    await expect(cards).toHaveCount(1);
    await expect(cards.first()).toContainText('Consumer waiting on the library');
    await expect(page.getByTestId('board-active-filter-chip')).toContainText('Waiting for release');
    // Shareable: the facet round-trips through the filter hash.
    await expect.poll(() => page.url()).toContain('release%3Awaiting');

    mkdirSync(RESULTS_DIR, { recursive: true });
    await page.screenshot({ path: `${RESULTS_DIR}/release-gate-board-filter--mocked.png` });
  });

  for (const theme of ['light', 'dark'] as const) {
    test(`records the release affordance in ${theme} theme`, async ({ page }) => {
      await installRoutes(page);
      await openDetail(page, TARGET_ID);
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      await expect(page.getByTestId('release-gate-toggle')).toBeVisible();

      mkdirSync(RESULTS_DIR, { recursive: true });
      await page.getByTestId('references-section').screenshot({
        path: `${RESULTS_DIR}/release-gate-action-${theme}--mocked.png`,
      });
    });
  }
});
