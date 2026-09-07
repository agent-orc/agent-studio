import { expect, test, type Page, type Route } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-2709: the operator can release a terminal task from the UI and see the
 * dependent unblock.
 *
 * The gate has two halves and only one of them looks like a problem on the
 * board: APP-1 says "waits for release: LIB-1", while LIB-1 sits in Delivered
 * looking finished. This spec drives the whole loop - find the blocked card,
 * open the target, release it, and prove the chip is gone - against a mocked
 * API whose `released` flag actually flips, so the assertion is about the
 * rendered truth rather than about the request having been sent.
 */
const PROJECT = 'Release gate fixture';
const WATCH_PATH = '/fixtures/release-gate';
const TARGET_ID = 'lib-acceptance';
const TARGET_KEY = 'LIB-1';
const DEPENDENT_ID = 'app-consumer';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR;

/** Server-side truth the mocked endpoints project, flipped by the PUT. */
const state = { released: false };

function baseTask(id: string, key: string, title: string, lane: string) {
  return {
    id,
    key,
    displayKey: key,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state: lane,
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    thinkingLevel: null,
    model: null,
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    createdAt: '2026-09-01T08:00:00Z',
    lastActivity: '2026-09-01T08:30:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/${lane}/${id}`,
    ownerClientId: 'local-default',
    tags: [],
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
  };
}

function target() {
  return { ...baseTask(TARGET_ID, TARGET_KEY, 'Library acceptance', '6-completed'), released: state.released };
}

function dependent() {
  return {
    ...baseTask(DEPENDENT_ID, 'APP-1', 'Consumer rollout', '2-ready'),
    references: {
      dependsOn: [{ key: TARGET_KEY, releaseGate: true }],
      relatedTo: [], blockedBy: [], supersedes: [],
    },
    waitsOn: {
      blocked: !state.released,
      cycleDetected: false,
      items: [{
        key: TARGET_KEY,
        resolved: true,
        fulfilled: state.released,
        releaseGate: true,
        targetReleased: state.released,
        waitingForRelease: !state.released,
        targetJobId: TARGET_ID,
        targetTitle: 'Library acceptance',
        targetState: '6-completed',
        targetWatchPath: WATCH_PATH,
      }],
    },
  };
}

/** The reverse index the target card reads to learn who it would unblock. */
const GATED_DEPENDENTS = [{
  sourceKey: 'APP-1',
  sourceJobId: DEPENDENT_ID,
  sourceTitle: 'Consumer rollout',
  sourceState: '2-ready',
  sourceWatchPath: WATCH_PATH,
  kind: 'dependsOn',
  releaseGate: true,
}];

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function stubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;

    if (request.method() === 'PUT' && /\/api\/tasks\/[^/]+\/release$/.test(pathname)) {
      state.released = (request.postDataJSON() as { released?: boolean })?.released === true;
      return json(route, { released: state.released });
    }
    if (pathname === '/api/auth/status') {
      return json(route, { profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    }
    if (pathname === '/api/watch-paths') {
      return json(route, [
        { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
      ]);
    }
    if (pathname === '/api/environment') {
      return json(route, {
        isDev: false,
        devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
      });
    }
    if (pathname === '/api/tasks/grouped') {
      return json(route, {
        backlog: [], preparation: [], orchestratorPrep: [], ready: [dependent()],
        progress: [], failedPickup: [], codeNotComplete: [], autoReview: [], review: [],
        humanReview: [], escalated: [], completed: [target()], archive: [],
      });
    }
    if (pathname === '/api/tasks/archive') return json(route, { items: [], total: 0 });
    if (pathname === '/api/tasks') return json(route, [dependent(), target()]);
    if (/\/api\/tasks\/[^/]+\/dependents$/.test(pathname)) {
      return json(route, pathname.includes(TARGET_ID) || pathname.includes(TARGET_KEY)
        ? GATED_DEPENDENTS
        : []);
    }
    if (pathname === `/api/tasks/${TARGET_ID}` || pathname === `/api/tasks/${TARGET_KEY}`) {
      return json(route, taskDetail(target()));
    }
    if (pathname === `/api/tasks/${DEPENDENT_ID}` || pathname === '/api/tasks/APP-1') {
      return json(route, taskDetail(dependent()));
    }
    if (pathname === '/api/runner/status') {
      return json(route, {
        projects: {
          [PROJECT]: {
            projectName: PROJECT, mode: 'manual', activeJobId: null,
            activeExecution: null, queuedJobIds: [],
          },
        },
      });
    }
    if (pathname === '/api/runner/global') return json(route, { mode: 'paused', activeProjects: [] });
    if (pathname === '/api/epics/completed/count') return json(route, { count: 0 });
    if (pathname.includes('/pipeline') || pathname.includes('/claude-session')) {
      return json(route, null);
    }
    if (pathname.includes('/session-events')) return json(route, { events: [], sessionChain: [] });
    if (pathname.includes('/screenshots')) return json(route, { screenshots: [] });
    return json(route, []);
  });
}

function taskDetail(info: unknown) {
  return {
    info,
    promptMarkdown: '# Release gate fixture',
    promptHistory: [],
    titleHistory: [],
    statusMarkdown: null,
    contextUsage: null,
    log: [],
    summaryState: null,
    reviewEvidence: [],
  };
}

async function openBoard(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await stubApi(page);
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
}

function dependentChip(page: Page) {
  return page.getByTestId('task-card').filter({ hasText: 'Consumer rollout' })
    .getByTestId('task-card-waiting-on');
}

test.describe('release gate operator action', () => {
  test.beforeEach(() => {
    state.released = false;
  });

  test('releasing the target from its card unblocks the dependent', async ({ page }) => {
    await openBoard(page);
    await expect(dependentChip(page)).toContainText('waits for release: LIB-1');

    // The target looks finished on the board; the release lives on its card.
    await page.getByTestId('task-card').filter({ hasText: 'Library acceptance' }).click();
    const releaseRow = page.getByTestId('references-row-release');
    await expect(releaseRow).toBeVisible({ timeout: 15_000 });
    await expect(releaseRow.getByTestId('release-gate-state')).toContainText('Release pending');
    await expect(releaseRow.getByTestId('release-gate-dependents')).toContainText('Unblocks APP-1');

    await releaseRow.getByTestId('release-gate-toggle').click();
    await expect(releaseRow.getByTestId('release-gate-state')).toContainText('Released');
    await expect(releaseRow.getByTestId('release-gate-toggle')).toContainText('Withdraw release');

    // Back to the board: the dependent no longer waits for a release.
    await openBoard(page);
    await expect(page.getByTestId('task-card').filter({ hasText: 'Consumer rollout' }))
      .toBeVisible();
    await expect(dependentChip(page)).toHaveCount(0);
  });

  test('the dependent card can release its target inline', async ({ page }) => {
    await openBoard(page);
    await page.getByTestId('task-card').filter({ hasText: 'Consumer rollout' }).click();

    const dependsOnRow = page.getByTestId('references-row-dependsOn');
    await expect(dependsOnRow).toBeVisible({ timeout: 15_000 });
    await dependsOnRow.getByTestId('release-gate-toggle').click();

    await expect.poll(() => state.released).toBe(true);
  });

  test('the board filter surfaces both sides of a pending gate', async ({ page }) => {
    await openBoard(page);
    await page.getByTestId('kanban-filter-waiting-for-release').click();

    await expect(page.getByTestId('task-card').filter({ hasText: 'Consumer rollout' }))
      .toBeVisible();
    await expect(page.getByTestId('task-card').filter({ hasText: 'Library acceptance' }))
      .toBeVisible();
    await expect(page.getByText('release:waiting')).toBeVisible();
  });

  for (const theme of ['light', 'dark'] as const) {
    test(`records the release affordance in ${theme} theme`, async ({ page }) => {
      await openBoard(page);
      await page.getByTestId('task-card').filter({ hasText: 'Library acceptance' }).click();
      await expect(page.getByTestId('references-row-release')).toBeVisible({ timeout: 15_000 });
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);

      if (RESULTS_DIR) {
        mkdirSync(RESULTS_DIR, { recursive: true });
        await page.screenshot({
          path: `${RESULTS_DIR}/release-gate-action-${theme}--mocked.png`,
          fullPage: false,
        });
      }
    });
  }
});
