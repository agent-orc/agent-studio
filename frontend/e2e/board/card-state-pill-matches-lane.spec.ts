import { test, expect, type Page } from '@playwright/test';

/**
 * The card "Running" cue must follow the lane, not a stale execution snapshot.
 *
 * Operator bug (2026-05-28): a card sitting in `4-auto-review` still showed
 * the "Running live" pill because the
 * grouped payload carried a `execution.status === 'running'` snapshot left
 * over from when the task was in `3-progress`. The backend now clears
 * `execution` for non-progress lanes (TaskEndpointHelpers.WithRuntime), and
 * the card has a defensive lane guard so that even a stale poll snapshot or
 * the brief optimistic-move window can never paint a running cue on a card
 * whose lane has moved on.
 *
 * This spec drives the defensive layer directly: every fixture below is fed a
 * live `running` execution, but only the `3-progress` card is allowed to keep
 * the host-level active cue. Cards in `4-auto-review` / `5-human-review` / `6-completed`
 * must suppress the pill. The card no longer repeats the lane label as a
 * chip; the lane header owns that state readout.
 *
 * Fully mocked via route interception so it runs against any served frontend
 * without depending on a real backend payload. Targets the dev build
 * (`/api/tasks/grouped`, `data-testid="task-card"`); the stable build still
 * uses the legacy `/api/tasks` route + `job-card` testid.
 */

const PROJECT = 'fixture-pill-lane';
const WATCH_PATH = 'C:/fixtures/pill-lane-repo';

function runningExecution(id: string) {
  return {
    jobId: id,
    taskKey: `${WATCH_PATH}::${id}`,
    processId: 4242,
    startedAt: '2026-05-28T10:00:00Z',
    status: 'running',
    exitCode: null,
    durationSeconds: null,
    model: 'claude-opus-4-7',
    runOutcome: null,
  };
}

function makeTask(id: string, state: string, title: string, order: number) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state,
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-05-28T09:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/${state}/${id}`,
    lastActivity: '2026-05-28T11:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    useOwnSession: null,
    lastUsage: null,
    // The bug condition: a live running snapshot on every card regardless
    // of lane. The lane guard is what must stop it surfacing.
    execution: runningExecution(id),
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
  };
}

// Titles are crafted so none is a substring of another - Playwright `hasText`
// does substring matching.
const PROGRESS_TASK = makeTask('pill-lane-A-progress', '3-progress', 'Pill lane progress alpha', 1);
const AUTO_REVIEW_TASK = makeTask('pill-lane-B-auto', '4-auto-review', 'Pill lane auto review bravo', 1);
const HUMAN_REVIEW_TASK = makeTask('pill-lane-C-human', '5-human-review', 'Pill lane human review charlie', 1);
const COMPLETED_TASK = makeTask('pill-lane-D-done', '6-completed', 'Pill lane completed delta', 1);

const NON_PROGRESS = [AUTO_REVIEW_TASK, HUMAN_REVIEW_TASK, COMPLETED_TASK];

const GROUPED_PAYLOAD = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [PROGRESS_TASK],
  failedPickup: [],
  review: [],
  autoReview: [AUTO_REVIEW_TASK],
  humanReview: [HUMAN_REVIEW_TASK],
  completed: [COMPLETED_TASK],
  archive: [],
};

async function installRoutes(page: Page) {
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    if (url.endsWith('/api/tasks')) {
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });

  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED_PAYLOAD) }));

  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));

  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }),
    }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-05-28T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-05-28T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] },
        },
      }),
    }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
}

async function gotoBoard(page: Page): Promise<void> {
  await seedBoardTab(page);
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible({ timeout: 15_000 });
  await expect(page.locator('[data-testid="task-card"]').first()).toBeVisible({ timeout: 15_000 });
}

function cardByTitle(page: Page, title: string) {
  return page.locator('[data-testid="task-card"]', { hasText: title });
}

test.describe('Card running cue follows lane, not stale execution', () => {
  test('3-progress card keeps the host-level running cue', async ({ page }) => {
    await gotoBoard(page);

    const card = cardByTitle(page, PROGRESS_TASK.title);
    await expect(card).toHaveCount(1);

    // Running cue present on the host. The old text pill is intentionally gone;
    // the whole card now carries the active treatment.
    await expect(card).toHaveAttribute('data-running', 'true');
    await expect(card).toHaveClass(/task-card--running/);
    await expect(card.getByTestId('task-card-progress')).toHaveCount(0);
    await expect(card.locator('.task-card__execution-pill--running')).toHaveCount(0);
    await expect(card, 'running text').not.toContainText('Running live');

    // The card no longer repeats the lane as a chip.
    await expect(card.getByTestId('task-card-state')).toHaveCount(0);
  });

  test('cards past 3-progress suppress the running cue despite a stale snapshot', async ({ page }) => {
    await gotoBoard(page);

    for (const task of NON_PROGRESS) {
      const card = cardByTitle(page, task.title);
      await expect(card, `card for ${task.state}`).toHaveCount(1);

      // No running flag on the host.
      await expect(card, `${task.state} data-running`).not.toHaveAttribute('data-running', 'true');
      // No edge progress strip.
      await expect(card.getByTestId('task-card-progress'), `${task.state} progress strip`).toHaveCount(0);
      // No "Running live" execution pill.
      await expect(card.locator('.task-card__execution-pill--running'), `${task.state} running pill`).toHaveCount(0);
      await expect(card, `${task.state} running text`).not.toContainText('Running live');
      // No lane-mirroring state chip on the card.
      await expect(card.getByTestId('task-card-state'), `${task.state} state pill`).toHaveCount(0);
    }
  });

  for (const theme of ['light', 'dark'] as const) {
    test(`captures the board with lane-correct running cues (${theme})`, async ({ page }, testInfo) => {
      await gotoBoard(page);
      await setTheme(page, theme);
      await page.waitForTimeout(300);

      // Strip any Vite hot-reload error overlay before the frame.
      await page.evaluate(() => {
        document.querySelectorAll('vite-error-overlay').forEach((n) => n.remove());
        document.querySelectorAll('.overlay--error').forEach((n) => ((n as HTMLElement).style.display = 'none'));
      });
      await page.setViewportSize({ width: 1600, height: 1100 });

      // Sanity re-assert in this theme: progress shows the cue, auto-review does not.
      await expect(cardByTitle(page, PROGRESS_TASK.title)).toHaveAttribute('data-running', 'true');
      await expect(cardByTitle(page, AUTO_REVIEW_TASK.title).locator('.task-card__execution-pill--running')).toHaveCount(0);

      const buf = await page.screenshot({ fullPage: false });
      await testInfo.attach(`card-state-pill-by-lane-${theme}.png`, { body: buf, contentType: 'image/png' });
      const resultsDir = process.env.JOB_RESULTS_DIR;
      if (resultsDir) {
        await page.screenshot({ path: `${resultsDir}/card-state-pill-by-lane-${theme}.png`, fullPage: false });
      }
      // Local scratch copy for inline review (test-results/ is gitignored).
      await page.screenshot({ path: `test-results/card-state-pill-by-lane-${theme}.png`, fullPage: false });
    });
  }
});
