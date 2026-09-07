import { test, expect, type Page } from '@playwright/test';

/**
 * AGT-2726 - the board's git-state stamp.
 *
 * Git-derived card enrichment (merge signal, integration status, publish
 * signal) is produced by a background index now, so `/api/tasks/grouped`
 * answers from the last completed snapshot and says when that was. This spec
 * pins the two user-visible halves of that contract:
 *
 * 1. the stamp renders quietly in the status bar and names its age;
 * 2. a stale snapshot keeps rendering its cards and says it is refreshing -
 *    it never blanks the board and never blocks on git - and the next push-
 *    driven refetch replaces it with the fresh stamp.
 */

const PROJECT = 'fixture-git-state-stamp';
const WATCH_PATH = 'C:/fixtures/git-state-stamp-repo';

function task(id: string, state: string, title: string, order: number) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state,
    order,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-09-07T09:30:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/${state}/${id}`,
    lastActivity: '2026-09-07T09:55:00Z',
    sessionName: null,
    model: 'gpt-5',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
  };
}

function groupedPayload(gitStateAt: string | null, gitStateStale: boolean) {
  return {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    review: [],
    autoReview: [task('git-state-card', '4-auto-review', 'Git state stamp fixture', 1)],
    humanReview: [],
    escalated: [],
    completed: [],
    archive: [],
    gitStateAt,
    gitStateStale,
  };
}

/**
 * Serves `/api/tasks/grouped` from a mutable holder so the test can flip the
 * backend's answer between polls, which is how a real index run becomes
 * visible to the board.
 */
async function installRoutes(page: Page, state: { body: unknown }) {
  await page.route('**/api/tasks/grouped**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(state.body),
  }));

  await page.route('**/api/watch-paths**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
  }));
  await page.route(/\/api\/runner\/status(\?|$)/, (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      projects: {
        [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
      },
    }),
  }));
  await page.route('**/api/environment**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
  }));
  // Everything else the shell polls: an empty, well-formed answer keeps the
  // board rendering so the assertions are about the stamp, not about fixtures.
  await page.route('**/api/**', (route) => route
    .fulfill({ status: 200, contentType: 'application/json', body: '[]' })
    .catch(() => undefined));
}

async function gotoBoard(page: Page, state: { body: unknown }): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
  await installRoutes(page, state);
  await page.goto('/?includeFixtures=true');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible({ timeout: 15_000 });
}

test('the status bar shows how old the background git index is', async ({ page }) => {
  const at = new Date(Date.now() - 4_000).toISOString();
  const state = { body: groupedPayload(at, false) };
  await gotoBoard(page, state);

  const stamp = page.getByTestId('status-bar-git-state');
  await expect(stamp).toBeVisible({ timeout: 15_000 });
  // Seconds resolution: the acceptance bound is an index age under 10 s after
  // a ref change, which a minutes-only label could not show.
  await expect(stamp).toContainText(/git \d+s/);
  await expect(stamp).not.toContainText('refreshing');
});

test('a stale index keeps the board rendered and clears on the next refresh', async ({ page }) => {
  const staleAt = new Date(Date.now() - 20_000).toISOString();
  const state = { body: groupedPayload(staleAt, true) };
  await gotoBoard(page, state);

  const stamp = page.getByTestId('status-bar-git-state');
  await expect(stamp).toContainText('refreshing', { timeout: 15_000 });
  // The whole point of stale-while-revalidate: cards stay on screen while the
  // index catches up. A blank board here would mean a request waited on git.
  await expect(page.getByTestId('task-card').first()).toBeVisible({ timeout: 15_000 });

  // The index run finishes; the next poll (the same path the SignalR push
  // triggers) carries the fresh stamp and the label drops "refreshing".
  state.body = groupedPayload(new Date().toISOString(), false);
  await expect(stamp).not.toContainText('refreshing', { timeout: 20_000 });
  await expect(page.getByTestId('task-card').first()).toBeVisible();
});

test('a warming index is honest instead of claiming a zero age', async ({ page }) => {
  const state = { body: groupedPayload(null, true) };
  await gotoBoard(page, state);

  await expect(page.getByTestId('status-bar-git-state')).toContainText('git indexing', { timeout: 15_000 });
});
