import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { test, expect, type Page, type Route } from '@playwright/test';

/**
 * AGT-2692, operator sighting (2026-08-29).
 *
 * Opening a task from the cross-project "All projects" board used to switch
 * the whole workspace into that task's single project: the titlebar picker
 * renamed itself, the Explorer lost its all-projects marker, and the board
 * behind the tab narrowed. Closing the task then left the operator stranded
 * inside one project instead of back on the board they came from.
 *
 * Opening a task is navigation into a detail, not a project switch. The task
 * tab records the board context it was opened from, so:
 *
 *   1. the active scope stays "All projects" while the detail is open;
 *   2. closing the tab returns to the All-projects board, still unscoped;
 *   3. the detail still loads through the task's own project handle, and that is
 *      a data scope for the detail view, not the app's active project.
 *
 * A task opened from a single-project board keeps behaving as before, which
 * the last case pins so the fix cannot over-reach.
 *
 * Fully stubbed: this is a front-end navigation contract and must not depend
 * on a live backend or on which fixtures happen to exist in it.
 */

const ALL_BOARD_KEY = 'board:__all__';

interface Fixture {
  project: string;
  shortCode: string;
  watchPath: string;
  id: string;
  key: string;
  title: string;
}

const ALPHA: Fixture = {
  project: 'Alpha Systems',
  shortCode: 'ALP',
  watchPath: 'C:/fixtures/alpha-systems',
  id: 'agt-2692-alpha',
  key: 'ALP-11',
  title: 'Alpha lane task',
};

const BETA: Fixture = {
  project: 'Beta Works',
  shortCode: 'BET',
  watchPath: 'C:/fixtures/beta-works',
  id: 'agt-2692-beta',
  key: 'BET-22',
  title: 'Beta lane task',
};

const FIXTURES = [ALPHA, BETA];

function taskKeyOf(fixture: Fixture): string {
  return `${fixture.watchPath}::${fixture.id}`;
}

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function task(fixture: Fixture) {
  return {
    id: fixture.id,
    taskKey: taskKeyOf(fixture),
    key: fixture.key,
    displayKey: fixture.key,
    title: fixture.title,
    state: '5-human-review',
    kind: 'task',
    mode: 'coding',
    agent: 'claude',
    cliType: 'claude',
    model: 'claude-opus-5',
    order: 1,
    createdAt: '2026-08-29T08:00:00Z',
    lastActivity: '2026-08-29T10:00:00Z',
    watchPath: fixture.watchPath,
    projectName: fixture.project,
    folderPath: `${fixture.watchPath}/${fixture.id}`,
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    estimatedTokens: 0,
  };
}

function detail(fixture: Fixture) {
  return {
    info: task(fixture),
    promptMarkdown: `# ${fixture.title}\n\nDetail loaded for ${fixture.project}.`,
    statusMarkdown: 'Ready for review.',
    contextUsage: null,
    log: [],
    promptHistory: [],
    titleHistory: [],
    reviewEvidence: [],
    summaryState: null,
  };
}

function grouped() {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [],
    humanReview: FIXTURES.map(task),
    escalated: [], review: [], completed: [], archive: [],
  };
}

/** Detail requests the app fired, so we can prove the data scope survives. */
type DetailLog = string[];

async function mockApplication(page: Page): Promise<DetailLog> {
  const detailRequests: DetailLog = [];
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/watch-paths**', route => json(route, FIXTURES.map(f => ({
    id: f.shortCode, name: f.project, shortCode: f.shortCode,
    path: f.watchPath, rootPath: f.watchPath, repositoryPath: f.watchPath,
  }))));
  await page.route('**/api/workspaces**', route => json(route, [{
    id: 'workspace', displayName: 'Fixture Workspace', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-08-29T08:00:00Z',
    projects: FIXTURES.map((f, index) => ({
      sourceType: 'local-folder', id: f.shortCode, displayName: f.project, shortCode: f.shortCode,
      workspaceId: 'workspace', color: null, cliDefault: 'claude', modelDefault: null,
      sortOrder: index, storageLocation: f.watchPath, repositoryPath: f.watchPath,
      rootPath: f.watchPath, repositoryUrl: null, urls: [], archived: false,
      createdAt: '2026-08-29T08:00:00Z',
    })),
  }]));
  // Expanding a project row mounts the Explorer's Dossier list, which reads
  // `catalogue.items`; the bare `[]` from the catch-all would crash it.
  await page.route('**/api/projects/*/workbenches**', route => json(route, { projectName: '', items: [] }));
  await page.route('**/api/workbenches**', route => json(route, { items: [] }));
  await page.route('**/api/cli/usage**', route => json(route, { items: [] }));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-08-29T10:00:00Z', snapshots: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route(/\/api\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/tasks', route => json(route, FIXTURES.map(task)));
  await page.route('**/api/tasks/grouped**', route => json(route, grouped()));
  for (const fixture of FIXTURES) {
    await page.route(new RegExp(`/api/tasks/${fixture.id}/pipeline`), route => json(route, {
      pipeline: { id: fixture.shortCode, displayName: fixture.project, version: 1, pre: [], core: [], post: [], allSteps: [] },
      execution: null, executions: [], config: {}, cost: null,
    }));
    // The Protocol pane projects the run timeline and the session chain; a
    // bare `[]` from the catch-all is truthy and crashes both projections.
    await page.route(new RegExp(`/api/tasks/${fixture.id}/runs`), route => json(route, {
      runs: [], runnerEvents: [],
    }));
    await page.route(new RegExp(`/api/tasks/${fixture.id}/session-events`), route => json(route, {
      events: [], sessionChain: [],
    }));
    await page.route(new RegExp(`/api/tasks/${fixture.id}(\\?|$)`), route => {
      detailRequests.push(route.request().url());
      return json(route, detail(fixture));
    });
  }
  return detailRequests;
}

async function bootStudio(page: Page): Promise<DetailLog> {
  await page.setViewportSize({ width: 1600, height: 900 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    // Start from a clean, explicit scope: the All-projects board is the only
    // open tab and no project filter is active.
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
    localStorage.setItem('activeProjects', '[]');
  });
  const detailRequests = await mockApplication(page);
  await page.goto('/');
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: 15_000 });
  return detailRequests;
}

function tabBy(page: Page, key: string) {
  return page.locator(`.studio-tab[data-tab-key="${key}"]`);
}

function cardFor(page: Page, fixture: Fixture) {
  return page.locator(`[data-testid="task-card"][data-project="${fixture.project}"]`);
}

/** The globally persisted active-project scope; `[]` means "All projects". */
function activeProjectScope(page: Page): Promise<string[]> {
  return page.evaluate(() => JSON.parse(localStorage.getItem('activeProjects') ?? '[]') as string[]);
}

async function capture(page: Page, name: string): Promise<void> {
  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (!resultsDir) return;
  const screenshots = path.join(resultsDir, 'screenshots');
  mkdirSync(screenshots, { recursive: true });
  await page.screenshot({ path: path.join(screenshots, `${name}.png`) });
}

test.describe('studio-shell · opening a task from the All-projects board', () => {
  test('leaves the active scope on All projects and returns there on close', async ({ page }) => {
    const detailRequests = await bootStudio(page);

    const picker = page.getByTestId('studio-project-picker-trigger');

    // --- before: the cross-project board with both projects' cards ---------
    await expect(tabBy(page, ALL_BOARD_KEY)).toHaveClass(/studio-tab--active/);
    await expect(picker).toContainText('All projects');
    await expect(cardFor(page, ALPHA)).toBeVisible();
    await expect(cardFor(page, BETA)).toBeVisible();
    expect(await activeProjectScope(page)).toEqual([]);
    await capture(page, 'agt-2692-all-projects-board');

    // --- open a task from that board --------------------------------------
    await cardFor(page, BETA).click();
    await expect(tabBy(page, `task:${taskKeyOf(BETA)}`)).toHaveClass(/studio-tab--active/);
    await expect(page.getByText(BETA.title).first()).toBeVisible();
    // Captured before the assertions so a regression run still produces the
    // comparable "here is what the shell looks like" frame.
    await capture(page, 'agt-2692-task-open-from-all-projects');

    // The board context did NOT follow the task into its project: the picker
    // still names the workspace-wide scope, the sidebar's per-project CLI
    // section stays on its "no project in scope" empty state, and the
    // persisted active-project selection is untouched.
    await expect(picker).toContainText('All projects');
    await expect(picker).not.toContainText(BETA.project);
    await page.getByTestId('studio-ab-cli').click();
    await expect(page.getByTestId('studio-cli-permission-modes-empty')).toBeVisible();
    await page.getByTestId('studio-ab-explorer').click();
    expect(await activeProjectScope(page)).toEqual([]);
    // …while the detail still loaded through its own project handle.
    expect(detailRequests.some(url => url.includes(BETA.id))).toBe(true);

    // --- close: back on the All-projects board, still unscoped -------------
    await tabBy(page, `task:${taskKeyOf(BETA)}`).locator('.studio-tab__close').click();
    await expect(tabBy(page, `task:${taskKeyOf(BETA)}`)).toHaveCount(0);
    await expect(tabBy(page, ALL_BOARD_KEY)).toHaveClass(/studio-tab--active/);
    await expect(picker).toContainText('All projects');
    await expect(cardFor(page, ALPHA)).toBeVisible();
    await expect(cardFor(page, BETA)).toBeVisible();
    expect(await activeProjectScope(page)).toEqual([]);
    await capture(page, 'agt-2692-back-on-all-projects-board');
  });

  test('a task opened from a project board still scopes the app to that project', async ({ page }) => {
    await bootStudio(page);

    // Enter the single-project board through the Explorer (the project row
    // expands, its Board child opens the board), then open its task.
    await page.getByTestId(`studio-explorer-project-${ALPHA.project}`).click();
    await page.getByTestId(`studio-explorer-project-board-${ALPHA.project}`).click();
    await expect(tabBy(page, `board:${ALPHA.project}`)).toHaveClass(/studio-tab--active/);
    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText(ALPHA.project);

    await cardFor(page, ALPHA).click();
    await expect(tabBy(page, `task:${taskKeyOf(ALPHA)}`)).toHaveClass(/studio-tab--active/);

    await expect(page.getByTestId('studio-project-picker-trigger')).toContainText(ALPHA.project);
    expect(await activeProjectScope(page)).toEqual([ALPHA.project]);
  });
});
