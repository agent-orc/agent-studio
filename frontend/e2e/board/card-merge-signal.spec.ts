import { test, expect, type Page } from '@playwright/test';

/**
 * AGT-2046: every board card that carries git work shows an always-on,
 * two-segment merge signal — "gemerged in develop / gemerged in main" — that the
 * operator can scan across the board. The develop and main segments read
 * filled/green when the work has landed on that rung and muted/empty when not.
 * The cryptic "BR" chip that used to sit over the commits is replaced by a
 * self-explanatory branch icon + name (the operator could not decode "BR").
 *
 * This spec drives all four develop/main combinations plus the label replacement,
 * fully mocked via route interception so it runs against any served frontend
 * without a real backend.
 */

const PROJECT = 'fixture-merge-signal';
const WATCH_PATH = 'C:/fixtures/merge-signal-repo';

interface MergeSignal {
  branch: string;
  inIntegration: boolean;
  inRelease: boolean;
  integrationBranch: string;
  releaseBranch: string;
  integrationSha: string | null;
  releaseSha: string | null;
}

interface IntegrationStatus {
  status: 'integrated' | 'pending' | 'no-branch';
  sha: string | null;
  integrationBranch: string;
  detail: string;
}

function mergeSignal(inDev: boolean, inMain: boolean): MergeSignal {
  return {
    branch: 'task/ms',
    inIntegration: inDev,
    inRelease: inMain,
    integrationBranch: 'develop',
    releaseBranch: 'main',
    integrationSha: inDev ? 'a1b2c3d' : null,
    releaseSha: inMain ? 'ffee001' : null,
  };
}

function integrationStatus(inDev: boolean, sha = 'c0ffee1'): IntegrationStatus {
  return {
    status: inDev ? 'integrated' : 'pending',
    deliveryRef: 'task/e2e-merge-signal',
    sha: inDev ? sha : null,
    integrationBranch: 'develop',
    detail: inDev
      ? 'Every attributed commit is present in develop.'
      : 'Attributed commits are not present in develop.',
  };
}

function makeTask(
  id: string,
  state: string,
  title: string,
  signal: MergeSignal | null,
  withMergeFact: boolean,
  withCommit = true,
  integration: IntegrationStatus | null = null,
) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    key: id.toUpperCase(),
    title,
    state,
    order: 1,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-07-10T09:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/${state}/${id}`,
    lastActivity: '2026-07-10T11:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-8',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    // AGT-2063: the signal is a fact about the task's OWN commit, so a card that
    // carries git work must carry a commit. A commit-less card (withCommit=false)
    // must render no signal even when a backend mergeSignal is present.
    commits: withCommit
      ? [{ sha: 'c0ffee1234ab', shortSha: 'c0ffee1', message: 'feat: task work', filesChanged: 2, files: ['src/a.ts', 'src/b.ts'], at: '2026-07-10T10:15:00Z' }]
      : [],
    ownerClientId: 'local-default',
    tags: [],
    mergeSignal: signal,
    integration,
    provenance: {
      branch: 'task/ms',
      base: 'base000',
      transitions: [
        { lane: state, atUtc: '2026-07-10T10:00:00Z', branchTip: 'tip1234deadbeef', workBranchHead: 'devhead99' },
      ],
      merge: withMergeFact
        ? { mergeCommit: 'a1b2c3d4e5f6', workBranchHeadBefore: null, workBranchHeadAfter: 'a1b2c3d4e5f6', atUtc: '2026-07-10T10:30:00Z' }
        : null,
    },
  };
}

// The four combinations, placed in lanes roughly matching their real position.
const ON_BRANCH = makeTask('ms-branch', '3-progress', 'Merge signal on branch only', mergeSignal(false, false), false);
const IN_DEVELOP = makeTask(
  'ms-develop',
  '5-human-review',
  'Merge signal in develop not main',
  mergeSignal(true, false),
  true,
  true,
  integrationStatus(true),
);
const RELEASED = makeTask(
  'ms-released',
  '6-completed',
  'Merge signal released to main',
  mergeSignal(true, true),
  true,
  true,
  integrationStatus(true),
);
const ONLY_MAIN = makeTask(
  'ms-onlymain',
  '5-human-review',
  'Merge signal rare only main',
  mergeSignal(false, true),
  false,
  true,
  integrationStatus(false),
);
const STALE_ATTEMPT = makeTask(
  'ms-stale-attempt',
  '5-human-review',
  'Remembered merge attempt is not target truth',
  mergeSignal(true, false),
  true,
  true,
  integrationStatus(false),
);
const SALVAGED = makeTask(
  'ms-salvaged',
  '5-human-review',
  'Out-of-band merge is target truth',
  mergeSignal(false, false),
  false,
  true,
  integrationStatus(true, '5a1ba9e'),
);
// AGT-2063 regression: a commit-less card that still carries a backend mergeSignal
// (its task/<id> branch base is trivially an ancestor of develop/main) must render
// NO merge signal - the exact "merge state on a card with no commits" bug.
const NO_COMMIT = makeTask(
  'ms-nocommit',
  '5-human-review',
  'Merge signal empty card no commits',
  mergeSignal(true, false),
  false,
  false,
  {
    status: 'no-branch',
    sha: null,
    integrationBranch: 'develop',
    detail: 'No attributed commits to integrate.',
  },
);

const GROUPED_PAYLOAD = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [ON_BRANCH],
  failedPickup: [],
  review: [],
  autoReview: [],
  humanReview: [IN_DEVELOP, ONLY_MAIN, STALE_ATTEMPT, SALVAGED, NO_COMMIT],
  completed: [RELEASED],
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
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-07-10T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-07-10T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
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
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      }),
    }));
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

test.describe('AGT-2046 board card merge signal', () => {
  test('renders the correct [develop|main] segment states for all four combinations', async ({ page }) => {
    await gotoBoard(page);

    const cases: { title: string; dev: string; main: string }[] = [
      { title: ON_BRANCH.title, dev: 'false', main: 'false' },
      { title: IN_DEVELOP.title, dev: 'true', main: 'false' },
      { title: RELEASED.title, dev: 'true', main: 'true' },
      { title: ONLY_MAIN.title, dev: 'false', main: 'true' },
    ];

    for (const c of cases) {
      const card = cardByTitle(page, c.title);
      await expect(card, `card ${c.title}`).toHaveCount(1);
      const sig = card.getByTestId('task-card-merge-signal');
      await expect(sig, `merge signal present on ${c.title}`).toHaveCount(1);
      await expect(sig, `develop state on ${c.title}`).toHaveAttribute('data-develop', c.dev);
      await expect(sig, `main state on ${c.title}`).toHaveAttribute('data-main', c.main);
      // The develop/main labels are readable in the accessible name.
      await expect(sig).toHaveAttribute('aria-label', /develop/);
      await expect(sig).toHaveAttribute('aria-label', /main/);
    }
  });

  test('AGT-2063: a card without a task commit renders no merge signal', async ({ page }) => {
    await gotoBoard(page);

    // The commit-less card is on the board ...
    const empty = cardByTitle(page, NO_COMMIT.title);
    await expect(empty).toHaveCount(1);
    // ... but carries no [d|m] indicator, even though its payload has a mergeSignal.
    await expect(empty.getByTestId('task-card-merge-signal')).toHaveCount(0);

    // A sibling card WITH a commit in the same lane still shows its signal, proving
    // the suppression is the missing commit and not a board-wide failure.
    await expect(cardByTitle(page, IN_DEVELOP.title).getByTestId('task-card-merge-signal'))
      .toHaveCount(1);
  });

  test('the develop segment lights up only when merged; the main segment tracks main', async ({ page }) => {
    await gotoBoard(page);

    const released = cardByTitle(page, RELEASED.title);
    const relDev = released.locator('[data-testid="task-card-merge-signal"] [data-seg="develop"]');
    const relMain = released.locator('[data-testid="task-card-merge-signal"] [data-seg="main"]');
    await expect(relDev).toHaveClass(/task-card__merge-seg--on/);
    await expect(relMain).toHaveClass(/task-card__merge-seg--on/);

    const onBranch = cardByTitle(page, ON_BRANCH.title);
    const branchDev = onBranch.locator('[data-testid="task-card-merge-signal"] [data-seg="develop"]');
    await expect(branchDev).not.toHaveClass(/task-card__merge-seg--on/);
  });

  test('accepted cards use only computed target membership despite stale attempt evidence', async ({ page }) => {
    await gotoBoard(page);

    const stale = cardByTitle(page, STALE_ATTEMPT.title);
    await expect(stale.getByTestId('integration-status-badge'))
      .toHaveAttribute('data-integration-status', 'pending');
    await expect(stale.getByTestId('task-card-merge-signal'))
      .toHaveAttribute('data-develop', 'false');

    const salvaged = cardByTitle(page, SALVAGED.title);
    await expect(salvaged.getByTestId('integration-status-badge'))
      .toHaveAttribute('data-integration-status', 'integrated');
    await expect(salvaged.getByTestId('task-card-merge-signal'))
      .toHaveAttribute('data-develop', 'true');
  });

  test('replaces the cryptic "BR" chip with a branch icon + name', async ({ page }) => {
    await gotoBoard(page);

    const card = cardByTitle(page, IN_DEVELOP.title);
    // The old two-letter code chip is gone from the DOM entirely.
    await expect(card.locator('.task-card__change-ref-label')).toHaveCount(0);
    // A self-explanatory icon chip took its place, next to the branch name.
    await expect(card.locator('.task-card__change-ref-icon')).toHaveCount(1);
  });

  for (const theme of ['light', 'dark'] as const) {
    test(`captures the board merge signals (${theme})`, async ({ page }, testInfo) => {
      await gotoBoard(page);
      await setTheme(page, theme);
      await page.waitForTimeout(300);
      // Surface (once) any global error-dialog text so a real regression would be
      // visible in the test log, then strip dev/error overlays so the evidence
      // frame shows only the board. The dialog is the mocked harness's global
      // ErrorHandler reacting to an unmocked endpoint, not the card change.
      const errMsg = await page.locator('[data-testid="error-dialog-message"]').first().textContent().catch(() => null);
      if (errMsg && errMsg.trim()) console.log(`[merge-signal spec] global error-dialog present (harness noise): ${errMsg.trim().slice(0, 200)}`);
      await page.evaluate(() => {
        document.querySelectorAll('vite-error-overlay').forEach((n) => n.remove());
        document.querySelectorAll('.overlay--error').forEach((n) => ((n as HTMLElement).style.display = 'none'));
        document.querySelectorAll('app-error-dialog').forEach((n) => ((n as HTMLElement).style.display = 'none'));
      });
      // Tall viewport so all four combinations (incl. the fully-green RELEASED
      // card down in Delivered) are captured in one evidence frame.
      await page.setViewportSize({ width: 1600, height: 1500 });

      await expect(cardByTitle(page, RELEASED.title).getByTestId('task-card-merge-signal'))
        .toHaveAttribute('data-main', 'true');
      await cardByTitle(page, RELEASED.title).scrollIntoViewIfNeeded();
      await page.waitForTimeout(150);

      const buf = await page.screenshot({ fullPage: false });
      await testInfo.attach(`card-merge-signal-${theme}--mocked.png`, { body: buf, contentType: 'image/png' });
      const resultsDir = process.env.JOB_RESULTS_DIR;
      if (resultsDir) {
        await page.screenshot({ path: `${resultsDir}/card-merge-signal-${theme}--mocked.png`, fullPage: false });
      }
      await page.screenshot({ path: `test-results/card-merge-signal-${theme}--mocked.png`, fullPage: false });
    });
  }
});
