import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'fs';
import { SNAPSHOT } from '../../src/mockups/usage-chips/app/usage-chips.fixtures';

test.use({ serviceWorkers: 'block', trace: 'off', video: 'off' });

/**
 * Card mode badge (planning / research / concept recognizable at a glance).
 *
 * Task "Kanban-Karte: Mode-Icon": a viewer should be able to tell at a glance
 * why a project has several parallel tasks (coding, planning, research, concept).
 * The card reads `TaskInfo.mode` and renders a small tinted pill for the two
 * read-only modes; `coding` (the default) stays quiet so the board is not
 * noisy in the common case.
 *
 * Fully mocked via route interception so it runs against any served frontend
 * without a real backend payload. Mirrors the structure of
 * `card-state-pill-matches-lane.spec.ts`.
 */

const PROJECT = 'fixture-mode-badge';
const WATCH_PATH = 'C:/fixtures/mode-badge-repo';
const REGISTERED_PROJECT = {
  id: PROJECT, displayName: PROJECT, shortCode: 'MODE', workspaceId: 'ws-mode',
  storageLocation: WATCH_PATH, sortOrder: 0, archived: false, createdAt: '2026-06-03T09:00:00Z',
};

function makeTask(id: string, title: string, order: number, mode: string | undefined) {
  return {
    id,
    taskKey: `${WATCH_PATH}::${id}`,
    title,
    state: '2-ready',
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-06-03T09:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/2-ready/${id}`,
    lastActivity: '2026-06-03T11:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    mode,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [],
    ownerClientId: 'local-default',
    tags: [],
  };
}

// Titles are crafted so none is a substring of another - Playwright `hasText`
// does substring matching.
const PLANNING_TASK = { ...makeTask('mode-A-planning', 'Mode badge planning alpha', 1, 'planning'), taggingStatus: 'tags-proposed' };
const RESEARCH_TASK = { ...makeTask('mode-B-research', 'Mode badge research bravo', 2, 'research'), taggingStatus: 'tagged' };
const CONCEPT_TASK = makeTask('mode-C-concept', 'Mode badge concept charlie', 3, 'concept');
const CONCEPT_REVIEW_TASK = {
  ...makeTask('mode-E-concept-review', 'Concept review host hold', 1, 'concept'),
  state: '4-auto-review',
  integration: { status: 'pending', detail: 'Delivery pending.', integrationBranch: 'develop' },
  testEvidence: {
    evidenceState: 'unassigned', summary: 'Review build-tests Not proven',
    sources: [{ kind: 'review-build-tests', id: 'review-1', summary: 'Review build-tests Not proven' }],
  },
  liveStatus: {
    attempt: 1, activeStep: null, nextSteps: [],
    queue: { kind: 'review', position: null, reason: 'Review host saturated: load/core 3.2 > 1.0' },
    latestEventAt: '2026-06-03T11:00:00Z',
  },
};
const CODING_TASK = { ...makeTask('mode-D-coding', 'Mode badge coding delta', 4, 'coding'), taggingStatus: 'future-status' };

const GROUPED_PAYLOAD = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [PLANNING_TASK, RESEARCH_TASK, CONCEPT_TASK, CODING_TASK],
  progress: [],
  failedPickup: [],
  codeNotComplete: [],
  review: [],
  autoReview: [CONCEPT_REVIEW_TASK],
  humanReview: [],
  escalated: [],
  completed: [],
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

  await page.route('**/api/tasks/archive**', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ items: [], total: 0, offset: 0, limit: 50 }),
  }));

  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(GROUPED_PAYLOAD) }));

  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true }),
    }));

  await page.route('**/api/v1/workspaces**', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify([{
      id: 'ws-mode', displayName: 'Mode fixtures', sortOrder: 0, isDefault: true,
      createdAt: '2026-06-03T09:00:00Z', projects: [REGISTERED_PROJECT],
    }]),
  }));
  await page.route(/\/api\/v1\/projects(\?|$)/, route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify([REGISTERED_PROJECT]),
  }));
  await page.route(/\/api\/workspaces\/[^/]+\/quota\/admission(?:\?|$)/, route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({
      workspaceId: 'ws-mode',
      workspace: { projectId: null, project: null, status: 'ok', message: null,
        overrideActive: false, codingAllowed: true, hostingAllowed: true, limited: false },
      projects: [],
    }),
  }));
  await page.route('**/api/usage/cockpit**', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ ...SNAPSHOT, workspaceId: 'ws-mode' }),
  }));
  await page.route('**/api/v1/management/credential-inventory**', route => route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({ generatedAt: '2026-06-03T09:00:00Z', items: [] }),
  }));

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
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-03T07:00:00Z', sessions: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-06-03T07:00:00Z', ttlSeconds: 600, snapshots: [] }) }));
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
  await page.goto('/?includeFixtures=true', { waitUntil: 'domcontentloaded', timeout: 45_000 });
  await page.waitForLoadState('domcontentloaded');
  await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
    .toBeVisible({ timeout: 15_000 });
  await expect(page.locator('[data-testid="task-card"]').first()).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('error-dialog')).toHaveCount(0);
}

function cardByTitle(page: Page, title: string) {
  return page.locator('[data-testid="task-card"]', { hasText: title });
}

test.describe('Card mode badge (planning / research / concept recognizable on the board)', () => {
  test('auto-tag markers distinguish a proposal from applied tags in both themes', async ({ page }) => {
    await gotoBoard(page);
    const proposal = cardByTitle(page, PLANNING_TASK.title).getByTestId('tags-proposed');
    const tagged = cardByTitle(page, RESEARCH_TASK.title).getByTestId('task-card-tagging-status');
    await expect(proposal).toHaveCount(1);
    await expect(proposal).toContainText('Tags proposed');
    await expect(tagged).toHaveText('Auto-tagged');
    await expect(cardByTitle(page, CODING_TASK.title).getByTestId('task-card-tagging-status')).toHaveCount(0);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(proposal).toBeVisible();
      await expect(tagged).toBeVisible();
      await expect(page.getByTestId('error-dialog')).toHaveCount(0);
      const path = `${process.env.JOB_RESULTS_DIR ?? 'test-results'}/auto-tag-card-${theme}--mocked.png`;
      await cardByTitle(page, PLANNING_TASK.title).screenshot({ path, timeout: 30_000 });
    }
  });
  test('planning card shows a planning mode pill that names the mode', async ({ page }) => {
    await gotoBoard(page);

    const card = cardByTitle(page, PLANNING_TASK.title);
    await expect(card).toHaveCount(1);

    const pill = card.getByTestId('task-card-mode');
    await expect(pill).toBeVisible();
    await expect(pill).toHaveAttribute('data-mode', 'planning');
    await expect(pill).toContainText('Planning');
  });

  test('research card shows a research mode pill', async ({ page }) => {
    await gotoBoard(page);

    const card = cardByTitle(page, RESEARCH_TASK.title);
    await expect(card).toHaveCount(1);

    const pill = card.getByTestId('task-card-mode');
    await expect(pill).toBeVisible();
    await expect(pill).toHaveAttribute('data-mode', 'research');
    await expect(pill).toContainText('Research');
  });

  test('concept card shows a concept mode pill', async ({ page }) => {
    await gotoBoard(page);

    const card = cardByTitle(page, CONCEPT_TASK.title);
    await expect(card).toHaveCount(1);

    const pill = card.getByTestId('task-card-mode');
    await expect(pill).toBeVisible();
    await expect(pill).toHaveAttribute('data-mode', 'concept');
    await expect(pill).toContainText('Concept');
  });

  test('concept review card names the host hold and omits code-delivery chips', async ({ page }) => {
    await gotoBoard(page);
    const resultPath = process.env.JOB_RESULTS_DIR ?? 'test-results';
    mkdirSync(resultPath, { recursive: true });
    const card = cardByTitle(page, CONCEPT_REVIEW_TASK.title);
    await expect(card.getByTestId('task-live-current')).toContainText('Review host saturated: load/core 3.2 > 1.0');
    await expect(card.getByTestId('task-card-test-evidence')).toHaveCount(0);
    await expect(card.getByTestId('task-card-change-context')).toHaveCount(0);
    await expect(card).not.toContainText('main checkout');
    await expect(card).not.toContainText('Review build-tests');
    await expect(card).not.toContainText('Delivery pending');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await card.screenshot({ path: `${resultPath}/concept-review-host-hold-${theme}--mocked.png` });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByTestId('mobile-lane-picker').locator('select').selectOption('4-auto-review');
    await expect(card).toBeVisible();
    await card.screenshot({ path: `${resultPath}/concept-review-host-hold-390px--mocked.png` });
  });

  test('coding card stays quiet (no mode pill for the default mode)', async ({ page }) => {
    await gotoBoard(page);

    const card = cardByTitle(page, CODING_TASK.title);
    await expect(card).toHaveCount(1);
    await expect(card.getByTestId('task-card-mode')).toHaveCount(0);
  });

  for (const theme of ['light', 'dark'] as const) {
    test(`captures the board with mode badges (${theme})`, async ({ page }, testInfo) => {
      await gotoBoard(page);
      await setTheme(page, theme);
      await page.waitForTimeout(300);

      // Strip any Vite hot-reload error overlay before the frame.
      await page.evaluate(() => {
        document.querySelectorAll('vite-error-overlay').forEach((n) => n.remove());
        document.querySelectorAll('.overlay--error').forEach((n) => ((n as HTMLElement).style.display = 'none'));
      });
      const errorDialogClose = page.getByTestId('error-dialog-close');
      if (await errorDialogClose.count()) await errorDialogClose.first().click();
      await page.setViewportSize({ width: 1600, height: 1100 });

      // Sanity re-assert in this theme: every non-coding mode carries the pill.
      await expect(cardByTitle(page, PLANNING_TASK.title).getByTestId('task-card-mode')).toHaveAttribute('data-mode', 'planning');
      await expect(cardByTitle(page, RESEARCH_TASK.title).getByTestId('task-card-mode')).toHaveAttribute('data-mode', 'research');
      await expect(cardByTitle(page, CONCEPT_TASK.title).getByTestId('task-card-mode')).toHaveAttribute('data-mode', 'concept');
      await expect(cardByTitle(page, CODING_TASK.title).getByTestId('task-card-mode')).toHaveCount(0);

      const buf = await page.screenshot({ fullPage: false });
      await testInfo.attach(`card-mode-badge-${theme}--mocked.png`, { body: buf, contentType: 'image/png' });
      const resultsDir = process.env.JOB_RESULTS_DIR;
      if (resultsDir) {
        await page.screenshot({ path: `${resultsDir}/card-mode-badge-${theme}--mocked.png`, fullPage: false });
      }
      // Local scratch copy for inline review (test-results/ is gitignored).
      await page.screenshot({ path: `test-results/card-mode-badge-${theme}--mocked.png`, fullPage: false });
    });
  }
});
