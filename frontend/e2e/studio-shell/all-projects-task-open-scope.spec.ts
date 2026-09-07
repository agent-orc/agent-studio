import { test, expect, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog } from '../helpers/theme';

/**
 * AGT-2692 - opening a task from the All-projects board keeps the All-projects
 * scope.
 *
 * Operator sighting (2026-08-29): from the cross-project board, opening a task
 * SWITCHED the active workspace into that task's single project. The picker
 * flipped from "All projects" to the project name, the Explorer marked that
 * project active, and closing the task left the operator stranded in a
 * single-project board instead of back on All projects.
 *
 * The fix separates the two questions that were conflated:
 *   - which project's data does this detail need  -> the task's own project
 *     handle, still used to load the detail;
 *   - which project is the app scoped to          -> unchanged when the task
 *     was opened from the cross-project board.
 *
 * Front-end-only concern, so the boot endpoints are stubbed with two projects
 * (mirrors activity-bar-board-removed.spec.ts) and no live backend is required.
 */

const ALL_BOARD_KEY = 'board:__all__';

function task(over: Record<string, unknown>) {
  return {
    id: 'task-a',
    taskKey: 'C:/watch-a::task-a',
    title: 'Alpha task',
    state: '2-ready',
    order: 1,
    agent: 'codex',
    createdAt: '2026-01-01T00:00:00Z',
    watchPath: 'C:/watch-a',
    projectName: 'Project Alpha',
    folderPath: 'C:/watch-a/.orchestrator/jobs/task-a',
    lastActivity: '2026-01-01T00:00:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    kind: 'task',
    ...over,
  };
}

const ALPHA = task({});
const BETA = task({
  id: 'task-b',
  taskKey: 'C:/watch-b::task-b',
  title: 'Beta task',
  watchPath: 'C:/watch-b',
  projectName: 'Project Beta',
  folderPath: 'C:/watch-b/.orchestrator/jobs/task-b',
  order: 2,
});

const GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [ALPHA, BETA], progress: [], failedPickup: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

function detailOf(info: Record<string, unknown>) {
  return {
    info,
    promptMarkdown: `# ${info['title']}`,
    promptHistory: [], titleHistory: [],
    statusMarkdown: null, contextUsage: null, log: [],
    summaryState: null, reviewEvidence: [],
  };
}

function projectEntry(id: string, name: string, watchPath: string, sortOrder: number) {
  return {
    sourceType: 'local-folder', id, displayName: name, shortCode: id.toUpperCase(),
    workspaceId: 'workspace', color: null, cliDefault: 'codex', modelDefault: null,
    sortOrder, storageLocation: watchPath, repositoryPath: watchPath,
    rootPath: watchPath, repositoryUrl: null, urls: [], archived: false,
    createdAt: '2026-01-01T00:00:00Z',
  };
}

/**
 * Stub the whole boot surface. The route order matters: specific endpoints
 * first, then the catch-all. Everything unmatched answers an empty list rather
 * than falling through to the dev proxy - with no backend behind it, a
 * pass-through hangs the boot, and a wrong-shaped body crashes a consumer into
 * an error modal that swallows every later click.
 */
async function mockApplication(page: Page): Promise<void> {
  const json = (route: Route, body: unknown) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/crash-recovery/pending', route => json(route, { pending: [] }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { id: 'alpha', name: 'Project Alpha', shortCode: 'ALPHA', path: 'C:/watch-a', rootPath: 'C:/watch-a', repositoryPath: 'C:/watch-a' },
    { id: 'beta', name: 'Project Beta', shortCode: 'BETA', path: 'C:/watch-b', rootPath: 'C:/watch-b', repositoryPath: 'C:/watch-b' },
  ]));
  await page.route('**/api/workspaces**', route => json(route, [{
    id: 'workspace', displayName: 'Workspace', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-01-01T00:00:00Z',
    projects: [
      projectEntry('alpha', 'Project Alpha', 'C:/watch-a', 0),
      projectEntry('beta', 'Project Beta', 'C:/watch-b', 1),
    ],
  }]));
  await page.route('**/api/cli/usage**', route => json(route, { items: [] }));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-01-01T00:00:00Z', snapshots: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route(/\/api\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/tasks', route => json(route, [ALPHA, BETA]));
  await page.route('**/api/tasks/grouped**', route => json(route, GROUPED));
  await page.route('**/api/tasks/*/pipeline**', route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null, executions: [], config: {}, cost: null,
  }));
  await page.route(/\/api\/tasks\/task-a(\?|$)/, route => json(route, detailOf(ALPHA)));
  await page.route(/\/api\/tasks\/task-b(\?|$)/, route => json(route, detailOf(BETA)));
}

/** Boot the studio on the cross-project board with two projects in the feed. */
async function bootAllProjectsBoard(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
    // Start from a genuinely workspace-wide scope so any narrowing that shows
    // up later was caused by the task open, not by leftover storage.
    localStorage.removeItem('activeProjects');
  });

  await mockApplication(page);

  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 20_000 });
  await dismissDevErrorDialog(page);
  await expect(tabBy(page, ALL_BOARD_KEY)).toHaveClass(/studio-tab--active/);
}

function tabBy(page: Page, key: string) {
  return page.locator(`.studio-tab[data-tab-key="${key}"]`);
}

/**
 * Open a task from the board by its card title.
 *
 * `force` skips Playwright's stability wait: with no live backend the polling
 * requests fail continuously and the connection banner reflows the layout, so
 * a card is never two frames identical. Visibility is asserted separately, so
 * the check that matters is not lost.
 */
async function openTaskCard(page: Page, title: string): Promise<void> {
  const card = page.getByTestId('task-card').filter({ hasText: title }).first();
  await expect(card).toBeVisible();
  await card.getByTestId('task-card-title').click({ force: true });
}

/** The persisted app-wide project scope; `[]` is "All projects". */
async function activeScope(page: Page): Promise<string[]> {
  return page.evaluate(() => JSON.parse(localStorage.getItem('activeProjects') ?? '[]'));
}

test.describe('studio-shell · task opened from the All-projects board', () => {
  test.setTimeout(60_000);

  test('opening a task leaves the active scope on All projects', async ({ page }) => {
    await bootAllProjectsBoard(page);

    const picker = page.getByTestId('studio-project-picker-trigger');
    await expect(picker).toContainText('All projects');
    expect(await activeScope(page)).toEqual([]);

    await openTaskCard(page, 'Alpha task');

    // The task detail opened in place ...
    await expect(tabBy(page, 'task:C:/watch-a::task-a')).toHaveClass(/studio-tab--active/);
    // ... and the board context behind it is still All projects.
    await expect(picker).toContainText('All projects');
    expect(await activeScope(page)).toEqual([]);
  });

  test('the All-projects board tab survives and is still workspace-wide on close', async ({ page }) => {
    await bootAllProjectsBoard(page);
    await openTaskCard(page, 'Alpha task');
    await expect(tabBy(page, 'task:C:/watch-a::task-a')).toHaveClass(/studio-tab--active/);

    // Closing the task returns to the board it was opened from, not to a
    // single-project board.
    await tabBy(page, 'task:C:/watch-a::task-a').locator('.studio-tab__close').click();

    await expect(tabBy(page, ALL_BOARD_KEY)).toHaveClass(/studio-tab--active/);
    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText('All projects');
    expect(await activeScope(page)).toEqual([]);
    // Both projects are back on the board - the operator is not stranded.
    await expect(page.getByTestId('task-card')).toHaveCount(2);
  });

  test('no Explorer project row is marked active while an All-projects task is open', async ({ page }) => {
    await bootAllProjectsBoard(page);
    await openTaskCard(page, 'Alpha task');
    await expect(tabBy(page, 'task:C:/watch-a::task-a')).toHaveClass(/studio-tab--active/);

    // The Explorer's active-project marker follows the app-wide scope. With
    // the scope on All projects, neither project row claims it.
    for (const name of ['Project Alpha', 'Project Beta']) {
      const row = page.getByTestId(`studio-explorer-project-${name}`);
      await expect(row).not.toHaveAttribute('aria-current', 'page');
    }
  });

  test('a task opened from a single-project board still scopes the app to it', async ({ page }) => {
    await bootAllProjectsBoard(page);

    // Switch to Project Alpha's board first, then open the same task. This is
    // the case the original behaviour got right and the fix must not break.
    await page.getByTestId('studio-explorer-project-row-Project Alpha').click();
    await page.getByTestId('studio-explorer-project-board-Project Alpha').click();
    await expect(tabBy(page, 'board:Project Alpha')).toHaveClass(/studio-tab--active/);
    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText('Project Alpha');

    await openTaskCard(page, 'Alpha task');

    await expect(tabBy(page, 'task:C:/watch-a::task-a')).toHaveClass(/studio-tab--active/);
    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText('Project Alpha');
    expect(await activeScope(page)).toEqual(['Project Alpha']);
  });
});
