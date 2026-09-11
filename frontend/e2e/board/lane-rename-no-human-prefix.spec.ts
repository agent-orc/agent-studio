import { test, expect, Page } from '@playwright/test';

/**
 * Regression guard for the lane-rename task: the board used to surface the
 * 2-ready and 5-human-review lanes as "Human Ready" and "Human Review".
 * The user dropped that human/non-human naming SCHEME - 2-ready reads simply
 * "Ready", and the orchestrator-owned pass (4-auto-review) reads "Post
 * Processing" rather than "Auto Review".
 *
 * SUPERSEDED IN PART BY AGT-2715. The 5-human-review heading used to read
 * "Review" here. The operator then reported the opposite problem: one lane
 * wearing four names at once, because the board said "Review" while the
 * Result tab said "Human review lane", a badge said "Human review", and the
 * project workflow section said "Awaiting human review." Every surface now
 * reads the single name from `src/app/models/lane-presentation.ts`, which is
 * "Human review". The original intent of this spec is preserved: the retired
 * SCHEME ("Human Ready", "Auto Review", and the title-cased "Human Review"
 * that paired with them) must never come back.
 *
 * The underlying state keys (2-ready, 5-human-review, 4-auto-review) are
 * unchanged - this is a display-label change only - so the mock fixture
 * still groups jobs under the same state keys; only the rendered headings
 * differ.
 *
 * This spec mocks every API call, so it renders the board from the lane
 * config alone and needs no live backend.
 */

const FIXTURE_WATCH = 'C:/fixtures/lane-rename-demo';
const FIXTURE_PROJECT = 'lane-rename-demo';

function jobInfo(id: string, state: string, title: string): Record<string, unknown> {
  return {
    id,
    jobKey: `${FIXTURE_WATCH}::${id}`,
    title,
    state,
    order: 1,
    agent: 'claude',
    createdAt: '2026-06-02T08:00:00Z',
    watchPath: FIXTURE_WATCH,
    projectName: FIXTURE_PROJECT,
    folderPath: `${FIXTURE_WATCH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-06-02T09:00:00Z',
    sessionName: null,
    model: 'claude-opus-4-7',
    cliType: 'claude',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    pendingIntent: null,
    autoLoop: null,
    summaryState: null,
    ownerClientId: null,
    tags: [],
    taskType: 'chore'
  };
}

function fixtureGrouped(): Record<string, unknown[]> {
  const autoReview = [jobInfo('fx-auto-1', '4-auto-review', 'Orchestrator deciding')];
  const humanReview = [jobInfo('fx-human-1', '5-human-review', 'Awaiting your accept')];
  return {
    preparation: [jobInfo('fx-prep-1', '1-preparation', 'Drafting next thing')],
    ready: [jobInfo('fx-ready-1', '2-ready', 'Ready to run')],
    progress: [jobInfo('fx-progress-1', '3-progress', 'Live run')],
    autoReview,
    humanReview,
    review: autoReview, // legacy alias
    completed: [jobInfo('fx-done-1', '6-completed', 'Wrapped up')],
    archive: [jobInfo('fx-arch-1', '7-archive', 'Old work')]
  };
}

async function installBoardMocks(page: Page): Promise<void> {
  const grouped = fixtureGrouped();
  const allJobs = [
    ...grouped.preparation, ...grouped.ready, ...grouped.progress,
    ...grouped.autoReview, ...grouped.humanReview,
    ...grouped.completed, ...grouped.archive
  ];

  await page.route('**/api/**', async (route) => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    await route.fallback();
  });
  // The shell renders behind an auth gate. Without this the blanket `[]` GET
  // stub above answers /api/auth/status, the app shows the sign-in card, and
  // every board assertion below fails on a missing element rather than on a
  // wrong heading.
  await page.route('**/api/auth/status', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }) });
  });
  await page.route('**/api/watch-paths', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify([{ name: FIXTURE_PROJECT, path: FIXTURE_WATCH, rootPath: FIXTURE_WATCH }]) });
  });
  await page.route('**/api/tasks', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(allJobs) });
  });
  await page.route('**/api/tasks/grouped', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(grouped) });
  });
  await page.route('**/api/runner/status', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ projects: { [FIXTURE_PROJECT]: { projectName: FIXTURE_PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }) });
  });
  await page.route('**/api/environment', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) });
  });
  await page.route('**/api/projects/settings', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({}) });
  });
}

test.describe('lane rename - no "Human" prefix', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test.beforeEach(async ({ page }) => {
    await installBoardMocks(page);
  });

  test('renders Ready / Human review / Post Processing headings and never legacy human-prefixed or auto-review headings', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('[data-testid="studio-board"], [data-testid="kanban-dashboard"]').first())
      .toBeVisible({ timeout: 10_000 });

    // The renamed lanes. 5-human-review reads "Human review" since AGT-2715
    // gave every surface the same word; see the header note above.
    await expect(page.getByRole('heading', { name: 'Human review', exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Ready', exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Post Processing' })).toBeVisible();

    // The dropped SCHEME must be gone from every heading on the board. These
    // are case-sensitive on purpose: the retired pairing was the title-cased
    // "Human Ready" / "Human Review" / "Auto Review" family.
    await expect(page.getByRole('heading', { name: /Human Ready/ })).toHaveCount(0);
    await expect(page.getByRole('heading', { name: /Auto Review/ })).toHaveCount(0);
    await expect(page.getByRole('heading', { name: /Human review lane/ })).toHaveCount(0);

    await page.screenshot({ path: 'test-results/lane-rename-no-human-prefix-1440x900.png', fullPage: false });
  });
});
