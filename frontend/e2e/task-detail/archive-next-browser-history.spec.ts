import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog } from '../helpers/theme';

const WATCH_PATH = 'C:/fixtures/archive-next-history';
const PROJECT = 'Archive history fixture';

type FixtureTask = ReturnType<typeof task>;

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

function task(index: number, state = '6-completed') {
  const id = `history-task-${index}`;
  return {
    id,
    key: `AGT-90${index}`,
    displayKey: `AGT-90${index}`,
    taskKey: `${WATCH_PATH}::${id}`,
    title: `Archive history task ${index}`,
    state,
    kind: 'task',
    mode: 'coding',
    order: index,
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5.6-sol',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/${state}/${id}`,
    createdAt: '2026-09-18T10:00:00Z',
    lastActivity: '2026-09-18T10:00:00Z',
    sessionName: null,
    sessionChain: [],
    useOwnSession: null,
    lastUsage: null,
    ownerClientId: 'local-default',
    execution: null,
    commit: null,
    commits: [],
    integration: {
      status: 'integrated',
      deliveryRef: `task/${id}`,
      sha: `abc${index}`,
      integrationBranch: 'develop',
      detail: 'Integrated fixture.',
    },
  };
}

function detail(info: FixtureTask) {
  return {
    info,
    promptMarkdown: `# ${info.title}`,
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    titleHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: null,
  };
}

async function installRoutes(page: Page): Promise<void> {
  const tasks = [task(1), task(2), task(3)];
  const findTask = (reference: string) => tasks.find(candidate =>
    candidate.id === reference || candidate.key === reference);
  const grouped = () => ({
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [], escalated: [],
    completed: tasks.filter(candidate => candidate.state === '6-completed'),
    archive: tasks.filter(candidate => candidate.state === '7-archive'),
  });

  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/watch-paths**', route => json(route, [{
    id: 'fixture', name: PROJECT, shortCode: 'AH', path: WATCH_PATH,
    rootPath: WATCH_PATH, repositoryPath: WATCH_PATH,
  }]));
  await page.route('**/api/workspaces**', route => json(route, [{
    id: 'workspace', displayName: 'Workspace', sortOrder: 0, isDefault: true,
    color: null, createdAt: '2026-09-18T10:00:00Z', projects: [{
      sourceType: 'local-folder', id: 'fixture', displayName: PROJECT, shortCode: 'AH',
      workspaceId: 'workspace', color: null, cliDefault: 'codex', modelDefault: null,
      sortOrder: 0, storageLocation: WATCH_PATH, repositoryPath: WATCH_PATH,
      rootPath: WATCH_PATH, repositoryUrl: null, urls: [], archived: false,
      createdAt: '2026-09-18T10:00:00Z',
    }],
  }]));
  await page.route('**/api/projects/*/workbenches**', route => json(route, { items: [] }));
  await page.route(/\/api\/tasks\/([^/?]+)(?:\?.*)?$/, route => {
    const reference = decodeURIComponent(new URL(route.request().url()).pathname.split('/').at(-1) ?? '');
    const current = findTask(reference);
    return current ? json(route, detail({ ...current })) : json(route, { message: 'Not found' }, 404);
  });
  await page.route('**/api/tasks/archive**', route => json(route, { items: [], total: 0, offset: 0, limit: 50 }));
  await page.route('**/api/tasks', route => json(route, tasks));
  await page.route('**/api/tasks/grouped**', route => json(route, grouped()));
  await page.route(/\/api\/runner\/status(?:\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/cli/usage**', route => json(route, { items: [] }));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-09-18T10:00:00Z', snapshots: [] }));
  await page.route(/\/api\/tasks\/[^/?]+\/pipeline(?:\?.*)?$/, route => json(route, {
    pipeline: { id: 'fixture', displayName: 'Fixture', version: 1, pre: [], core: [], post: [], allSteps: [] },
    execution: null, cost: null, config: {},
  }));
  await page.route(/\/api\/tasks\/[^/?]+\/runs(?:\?.*)?$/, route => json(route, {
    runCount: 0, firstStartedAt: null, lastActivityAt: null, hasActiveRun: false, runs: [],
  }));
  await page.route(/\/api\/tasks\/[^/?]+\/session-events(?:\?.*)?$/, route => json(route, {
    events: [], sessionChain: [],
  }));
  await page.route(/\/api\/tasks\/[^/?]+\/plan(?:\?.*)?$/, route => json(route, {
    hasPlan: false, source: null, snapshotCount: 0, activeItemId: null,
    softEstimateMedian: null, items: [], unassignedSubActions: [],
  }));
  await page.route(/\/api\/tasks\/([^/?]+)\/move(?:\?.*)?$/, async route => {
    const reference = decodeURIComponent(new URL(route.request().url()).pathname.split('/').at(-2) ?? '');
    const current = findTask(reference);
    if (!current) return json(route, { message: 'Not found' });
    const body = route.request().postDataJSON() as { targetState: string };
    current.state = body.targetState;
    current.folderPath = `${WATCH_PATH}/${body.targetState}/${current.id}`;
    return json(route, {});
  });
}

test('Archive & Next reuses one task tab and browser Back restores review history', async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.clear();
    sessionStorage.clear();
  });
  await installRoutes(page);
  await page.goto('/');

  const firstCard = page.getByTestId('task-card').filter({ hasText: 'Archive history task 1' });
  await expect(firstCard).toBeVisible();
  await firstCard.click();

  const pager = page.getByTestId('studio-task-pager-position');
  const archive = page.getByTestId('studio-triage-action-archive');
  await expect(pager).toHaveText(/1\s*\/\s*3/);
  await expect(archive).toHaveText(/Archive & Next/);
  await dismissDevErrorDialog(page);
  const firstUrl = page.url();

  await archive.evaluate((button: HTMLButtonElement) => button.click());
  await expect(page.getByRole('tab', { name: 'Archive history task 2' })).toHaveAttribute('aria-selected', 'true');
  await expect(pager).toHaveText(/1\s*\/\s*2/);
  await dismissDevErrorDialog(page);
  const secondUrl = page.url();
  expect(await page.evaluate(() => history.state?.studioTaskPager?.jobs?.length)).toBe(2);

  await archive.evaluate((button: HTMLButtonElement) => button.click());
  await expect(page.getByRole('tab', { name: 'Archive history task 3' })).toHaveAttribute('aria-selected', 'true');
  await expect(pager).toHaveText(/1\s*\/\s*1/);
  await expect(page.getByTestId(/^studio-tab-task:/)).toHaveCount(1);
  await dismissDevErrorDialog(page);

  await page.goBack();
  await expect(page).toHaveURL(secondUrl);
  expect(await page.evaluate(() => history.state?.studioTaskPager?.jobs?.length)).toBe(2);
  await expect(page.getByRole('tab', { name: 'Archive history task 2' })).toHaveAttribute('aria-selected', 'true');
  await expect(pager).toHaveText(/1\s*\/\s*2/);
  await expect(page.getByTestId(/^studio-tab-task:/)).toHaveCount(1);

  await page.goBack();
  await expect(page).toHaveURL(firstUrl);
  await expect(page.getByRole('tab', { name: 'Archive history task 1' })).toHaveAttribute('aria-selected', 'true');
  await expect(pager).toHaveText(/1\s*\/\s*3/);
  await expect(page.getByTestId(/^studio-tab-task:/)).toHaveCount(1);

  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (resultsDir) {
    mkdirSync(resultsDir, { recursive: true });
    await page.screenshot({ path: join(resultsDir, 'archive-next-history--mocked.png'), fullPage: true });
  }
});
