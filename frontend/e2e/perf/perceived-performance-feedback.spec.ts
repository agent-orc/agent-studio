import { test, expect, type Page, type Route } from '@playwright/test';
import { dismissDevErrorDialog, setTheme, type Theme } from '../helpers/theme';

const WATCH_PATH = 'C:/fixtures/perceived-performance';
const FIRST_ID = 'agt-2112-accept';
const NEXT_ID = 'agt-2112-next';

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

function task(id: string, order: number) {
  return {
    id, taskKey: `${WATCH_PATH}::${id}`, displayKey: id.toUpperCase(),
    title: id === FIRST_ID ? 'Perceived performance acceptance' : 'Next review task',
    state: '5-human-review', kind: 'coding', agent: 'codex', cliType: 'codex', model: 'gpt-5.2-codex',
    watchPath: WATCH_PATH, projectName: 'fixture', folderPath: `${WATCH_PATH}/${id}`,
    order, createdAt: '2026-07-11T12:00:00Z', execution: null, commits: [],
    provenance: { branch: `task/${id}`, base: 'base', transitions: [], merge: { mergeCommit: 'abc1234', atUtc: '2026-07-11T12:30:00Z' } },
  };
}

function detail(id: string) {
  return {
    info: task(id, id === FIRST_ID ? 1 : 2), promptMarkdown: '# Perceived performance',
    statusMarkdown: '', log: [], promptHistory: [], titleHistory: [], reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

function grouped() {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    codeNotComplete: [], autoReview: [], humanReview: [task(FIRST_ID, 1), task(NEXT_ID, 2)],
    escalated: [], completed: [], archive: [],
  };
}

async function baseRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/environment**', route => json(route, { isDev: false, devTools: {} }));
  await page.route('**/api/watch-paths**', route => json(route, [
    { name: 'fixture', path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/cli/usage**', route => json(route, { items: [] }));
  await page.route('**/api/cli/quota**', route => json(route, { at: '2026-07-11T12:00:00Z', snapshots: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, {
    items: [], total: 0, offset: 0, limit: 50,
  }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, route => json(route, { projects: {} }));
  await page.route(/\/api\/tasks\/[^/]+\/provenance(\?|$)/, route => json(route, {
    landedState: 'merged-to-develop', ladder: { mergedToIntegration: true, releasedToRelease: false }, commits: [],
  }));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${FIRST_ID}(\\?|$)`), route => json(route, detail(FIRST_ID)));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${NEXT_ID}(\\?|$)`), route => json(route, detail(NEXT_ID)));
}

for (const theme of ['light', 'dark'] as Theme[]) {
  test(`delayed board skeleton follows the 200 ms rule in ${theme} theme`, async ({ page }, testInfo) => {
    await baseRoutes(page);
    let releaseBoard!: () => void;
    let markBoardRequestStarted!: () => void;
    const boardGate = new Promise<void>(resolve => { releaseBoard = resolve; });
    const boardRequestStarted = new Promise<void>(resolve => { markBoardRequestStarted = resolve; });
    await page.route('**/api/tasks', async route => {
      markBoardRequestStarted();
      await boardGate;
      await json(route, [task(FIRST_ID, 1), task(NEXT_ID, 2)]);
    });
    await page.route('**/api/v1/studio/board**', async route => {
      await boardGate;
      await json(route, grouped());
    });
    await page.addInitScript(t => localStorage.setItem('atp.studio.theme', t), theme);
    const navigation = page.goto('/', { waitUntil: 'domcontentloaded' });
    await boardRequestStarted;

    await expect(page.getByTestId('loading-surface-board')).toBeVisible();
    await expect(page.getByText('Loading board…')).toBeVisible({ timeout: 2_000 });
    await setTheme(page, theme);
    await testInfo.attach(`board-skeleton-${theme}--mocked`, {
      body: await page.screenshot(), contentType: 'image/png',
    });
    releaseBoard();
    await navigation;
    await expect(page.getByTestId('loading-surface-board')).toHaveCount(0, { timeout: 5_000 });
  });
}

test('Accept paints immediately and offers Undo while persistence is still pending', async ({ page }, testInfo) => {
  await baseRoutes(page);
  await page.route('**/api/tasks', route => json(route, [task(FIRST_ID, 1), task(NEXT_ID, 2)]));
  let groupedReady!: () => void;
  const groupedLoaded = new Promise<void>(resolve => { groupedReady = resolve; });
  await page.route('**/api/v1/studio/board**', async route => {
    await json(route, grouped());
    groupedReady();
  });

  let releaseMove!: () => void;
  let moveIntercepted = false;
  const moveGate = new Promise<void>(resolve => { releaseMove = resolve; });
  await page.route(`**/api/v1/projects/*/tasks/${FIRST_ID}/move**`, async route => {
    moveIntercepted = true;
    await moveGate;
    await json(route, {});
  });

  await page.goto(`/?job=${FIRST_ID}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
  await groupedLoaded;
  const accept = page.getByTestId('studio-triage-action-mark-done');
  await expect(accept).toHaveText(/Accept/);
  await dismissDevErrorDialog(page);
  await accept.click({ force: true });

  await expect(page).not.toHaveURL(new RegExp(`job=${FIRST_ID}`));
  await expect.poll(() => moveIntercepted).toBe(true);
  await expect(page.getByTestId('undo-action')).toBeVisible();
  await expect(page.getByText(/Accepted.*Perceived performance acceptance/)).toBeVisible();
  await testInfo.attach('accept-optimistic-undo--mocked', {
    body: await page.getByTestId('notification-info').screenshot(), contentType: 'image/png',
  });

  releaseMove();
});
