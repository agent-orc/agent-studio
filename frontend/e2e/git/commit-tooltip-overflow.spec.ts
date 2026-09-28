import { test, expect, type Page } from '@playwright/test';
import { contrastRatio } from '../helpers/contrast';
import { sampleColours, setTheme } from '../helpers/theme';

const PROJECT = 'fixture-commit-contrast';
const WATCH_PATH = 'C:/fixtures/commit-contrast-repo';

const LONG_FILES = [
  'frontend/src/app/features/board/components/task-card/very/deep/path/with/a/long/component-name-that-used-to-overflow-the-tooltip.ts',
  'backend/Services/Runner/CommitAttribution/another/extremely/long/path/for/tooltip-clipping-regression.cs',
  'docs/research/commit-attribution-and-card-contrast-with-a-very-long-file-name.md',
];

function commit(shortSha: string, message: string, files = LONG_FILES) {
  return {
    sha: `${shortSha}${'0'.repeat(40 - shortSha.length)}`,
    shortSha,
    message,
    filesChanged: files.length,
    files,
    at: '2026-06-04T08:00:00Z',
  };
}

function makeTask(id: string, state: string, title: string, order: number, commits: ReturnType<typeof commit>[]) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state,
    order,
    agent: 'codex',
    cliType: 'codex',
    createdAt: '2026-06-04T07:30:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/${state}/${id}`,
    lastActivity: '2026-06-04T08:30:00Z',
    sessionName: null,
    model: 'gpt-5',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: commits[0] ?? null,
    commits,
    ownerClientId: 'local-default',
    tags: [],
  };
}

const AUTO_REVIEW_TASK = makeTask(
  'commit-contrast-auto-review',
  '4-auto-review',
  'Commit contrast auto review fixture',
  1,
  [commit('a614ea0e', 'fix: make commit hashes readable')],
);

const HUMAN_REVIEW_TASK = makeTask(
  'commit-contrast-human-review',
  '5-human-review',
  'Commit contrast human review fixture',
  1,
  [commit('4ecf7f0', 'test: lock commit row contrast')],
);

const GROUPED_PAYLOAD = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [],
  failedPickup: [],
  review: [],
  autoReview: [AUTO_REVIEW_TASK],
  humanReview: [HUMAN_REVIEW_TASK],
  completed: [],
  archive: [],
};

async function installRoutes(page: Page) {
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    if (url.endsWith('/api/tasks') || url.endsWith('/api/tasks')) {
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });

  const grouped = { status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED_PAYLOAD) };
  await page.route('**/api/v1/studio/board**', (route) => route.fulfill(grouped));
  await page.route('**/api/v1/studio/board**', (route) => route.fulfill(grouped));

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
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-04T08:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-04T08:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
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

/**
 * Regression: commit-pill tooltip lists files inside a fixed-width box.
 * Long paths used to spill out past the rounded border. The fix installs
 * overflow/ellipsis on `<li>` rows; this spec proves no list row's
 * right edge exceeds the tooltip box's right edge.
 */
test('commit-pill tooltip clips long file rows inside the box', async ({ page }) => {
  await gotoBoard(page);

  const overlay = page.locator('.overlay--error');
  if (await overlay.isVisible({ timeout: 500 }).catch(() => false)) {
    await overlay.click({ force: true }).catch(() => {});
  }
  // Strip any stale Vite error overlay that intercepts pointer events.
  await page.evaluate(() => {
    document.querySelectorAll('vite-error-overlay').forEach(n => n.remove());
  });

  // Pick the first commit row whose tooltip will carry long file paths.
  // We probe several visible rows to find one with a populated file list.
  const pills = page.getByTestId('task-card-commit-row');
  await expect(pills.first()).toBeVisible({ timeout: 15_000 });
  const count = await pills.count();

  let chosen: ReturnType<typeof page.getByTestId> | null = null;
  for (let i = 0; i < Math.min(count, 60); i++) {
    const pill = pills.nth(i);
    const hasFiles = await pill.getAttribute('data-has-files');
    if (hasFiles === 'true') {
      chosen = pill;
      break;
    }
  }
  expect(chosen, 'no commit pill exposes a file list — seed data has none').not.toBeNull();

  await chosen!.scrollIntoViewIfNeeded();
  await chosen!.hover();

  const tip = page.getByTestId('cac-tooltip');
  await expect(tip).toBeVisible({ timeout: 1_000 });

  // The tooltip must actually contain a <ul> file list to exercise the fix.
  await expect(tip.locator('ul li').first()).toBeVisible();

  const tipBox = await tip.boundingBox();
  expect(tipBox).not.toBeNull();
  const items = tip.locator('ul li');
  const itemCount = await items.count();
  for (let i = 0; i < itemCount; i++) {
    const itemBox = await items.nth(i).boundingBox();
    if (!itemBox) continue;
    // The list item's right edge must stay inside the tooltip's right edge.
    expect(itemBox.x + itemBox.width).toBeLessThanOrEqual(tipBox!.x + tipBox!.width + 0.5);
  }

  const padded = {
    x: Math.max(0, tipBox!.x - 24),
    y: Math.max(0, tipBox!.y - 24),
    width: Math.min(1200, tipBox!.width + 48),
    height: Math.min(900, tipBox!.height + 48)
  };
  await page.screenshot({
    path: 'test-results/commit-tooltip-clipped.png',
    clip: padded
  });
});

for (const theme of ['light', 'dark'] as const) {
  test(`commit hash clears WCAG-AA in ${theme} theme`, async ({ page }, testInfo) => {
    await gotoBoard(page);
    await setTheme(page, theme);
    await page.waitForTimeout(300);

    const card = page.locator('[data-testid="task-card"]', { hasText: AUTO_REVIEW_TASK.title });
    await expect(card).toHaveCount(1);
    const row = card.getByTestId('task-card-commit-row').first();
    await expect(row).toBeVisible();
    await expect(row.locator('.task-card__commit-sha')).toContainText('a614ea0e');

    const { color, bg } = await sampleColours(page, '.task-card__commit-sha', 0);
    const ratio = contrastRatio(color, bg);
    expect(
      ratio,
      `[${theme}] commit SHA contrast ${ratio.toFixed(2)} (${color} on ${bg})`,
    ).toBeGreaterThanOrEqual(4.5);

    const screenshot = await row.screenshot();
    await testInfo.attach(`commit-hash-${theme}.png`, { body: screenshot, contentType: 'image/png' });
    const resultsDir = process.env.JOB_RESULTS_DIR;
    if (resultsDir) {
      await row.screenshot({ path: `${resultsDir}/commit-hash-${theme}.png` });
    }
  });
}
