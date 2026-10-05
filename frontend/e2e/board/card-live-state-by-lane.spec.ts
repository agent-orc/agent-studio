import { test, expect, Page } from '@playwright/test';

/**
 * Task cards must hide live working-tree state outside `3-progress`.
 *
 * Before this change every card whose project happened to be in
 * `LANES_WITH_GIT` (3-progress + both review lanes) rendered the project's
 * live branch + dirty-file count. That reading is useful while the agent is
 * actively touching the repo, but on a card sitting in `4-auto-review` or
 * `5-human-review` it advertises whatever branch the dev checkout happens
 * to be on right now, which has nothing to do with the task that produced
 * the card. The operator wants a quiet, frozen indicator on review cards:
 * just "N files in commit", sourced from the per-job auto-commit metadata
 * the runner stamps into `job.json` on the `3-progress -> 4-auto-review`
 * transition (same numbers a `GET /jobs/{id}/runs/{i}/commits` call would
 * return for the final run).
 *
 * Acceptance, end to end:
 *   3-progress             -> branch/working-tree pill present.
 *   4-auto-review / 5-human-review with a commit -> "N files" pill, no branch.
 *   4-auto-review / 5-human-review without a commit -> no pill at all
 *     (suppression: never render "0 files" as a phantom indicator).
 *
 * The spec is fixture-driven and uses Playwright route interception so it
 * runs against any backend that is up - the assertions never depend on the
 * real `/api/tasks/grouped` payload.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/lane-state-pill-repo';

interface CommitFixture {
  sha: string;
  shortSha: string;
  message: string;
  filesChanged: number;
  files: string[];
  at: string;
}

const COMMIT_SEVEN: CommitFixture = {
  sha: '7777777777777777777777777777777777777777',
  shortSha: '7777777',
  message: 'feat: review-lane fixture',
  filesChanged: 7,
  files: ['src/a.ts', 'src/b.ts', 'src/c.ts', 'src/d.ts', 'src/e.ts', 'src/f.ts', 'src/g.ts'],
  at: '2026-05-16T10:00:00Z'
};

const COMMIT_TWELVE: CommitFixture = {
  sha: '1212121212121212121212121212121212121212',
  shortSha: '1212121',
  message: 'feat: human-review fixture',
  filesChanged: 12,
  files: Array.from({ length: 12 }, (_, i) => `src/file-${i + 1}.ts`),
  at: '2026-05-16T10:15:00Z'
};

function makeJob(id: string, state: string, order: number, commit: CommitFixture | null) {
  return {
    id,
    jobKey: `${WATCH_PATH}::${id}`,
    title: `${state} fixture ${id}`,
    state,
    order,
    agent: 'claude',
    cliType: 'claude',
    createdAt: '2026-05-16T09:00:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${id}`,
    lastActivity: '2026-05-16T11:00:00Z',
    sessionName: null,
    model: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit,
    commits: commit ? [commit] : [],
    ownerClientId: 'local-default'
  };
}

// IDs are chosen so no fixture title is a substring of another - Playwright's
// `hasText` does substring matching, so "human-review-with-commit" would match
// both the with-commit and without-commit cards if they shared a common stem.
const PROGRESS_JOB = makeJob('e2e-pill-A-progress', '3-progress', 1, null);
const AUTO_REVIEW_WITH_COMMIT = makeJob('e2e-pill-B-auto-review', '4-auto-review', 1, COMMIT_SEVEN);
const HUMAN_REVIEW_WITH_COMMIT = makeJob('e2e-pill-C-human-review-with-commit', '5-human-review', 1, COMMIT_TWELVE);
const HUMAN_REVIEW_NO_COMMIT = makeJob('e2e-pill-D-human-review-without-commit', '5-human-review', 2, null);

const GROUPED_PAYLOAD = {
  backlog: [],
  preparation: [],
  orchestratorPrep: [],
  ready: [],
  progress: [PROGRESS_JOB],
  failedPickup: [],
  review: [],
  autoReview: [AUTO_REVIEW_WITH_COMMIT],
  humanReview: [HUMAN_REVIEW_WITH_COMMIT, HUMAN_REVIEW_NO_COMMIT],
  completed: [],
  archive: []
};

async function installRoutes(page: Page) {
  // Catch-all 200 for everything we did not explicitly script so the app
  // never spends seconds blocked on a poll. The board only needs the
  // grouped + watch-paths + ancillary read-only feeds below.
  await page.route('**/api/**', (route) => {
    const url = route.request().url();
    // Default empty array for unknown endpoints; routes registered below
    // win because Playwright matches in registration order (most recent first).
    if (url.endsWith('/api/tasks')) {
      route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
      return;
    }
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });

  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(GROUPED_PAYLOAD)
    }));

  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }])
    }));

  // Live git summary: returns a real branch + 4 dirty files for the
  // fixture project. The progress card must show this; review cards must
  // not, even though the data is available for the same project.
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{
        projectName: PROJECT,
        rootPath: WATCH_PATH,
        isRepo: true,
        branch: 'feat/should-only-show-on-progress',
        filesChanged: 4,
        totalAdded: 12,
        totalRemoved: 3
      }])
    }));

  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } })
    }));
  await page.route('**/api/tags', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
}

test.describe('Task-card live-state visibility by lane', () => {
  test.beforeEach(async ({ page }) => {
    await installRoutes(page);
    await page.goto('/?includeFixtures=true');
    // Wait for the board to render at least one card; this proves the
    // grouped route fired before we start poking the DOM.
    await expect(page.getByTestId('job-card').first()).toBeVisible({ timeout: 10_000 });
  });

  test('3-progress card carries the branch / working-tree pill', async ({ page }) => {
    const progressCard = page.locator('[data-testid="job-card"]', { hasText: `3-progress fixture ${PROGRESS_JOB.id}` });
    await expect(progressCard).toHaveCount(1);

    const gitPill = progressCard.getByTestId('job-card-git');
    await expect(gitPill).toBeVisible();
    await expect(gitPill).toContainText('feat/should-only-show-on-progress');
    await expect(gitPill).toContainText('4 files');
  });

  test('4-auto-review card hides the branch pill and shows only "N files"', async ({ page }) => {
    const card = page.locator('[data-testid="job-card"]', { hasText: `4-auto-review fixture ${AUTO_REVIEW_WITH_COMMIT.id}` });
    await expect(card).toHaveCount(1);

    // No live working-tree readout on review cards.
    await expect(card.getByTestId('job-card-git')).toHaveCount(0);

    // The commit pill renders in the review variant: SHA is hidden, the
    // files count is the only thing in the pill.
    const commit = card.getByTestId('job-card-commit');
    await expect(commit).toBeVisible();
    await expect(commit).toHaveAttribute('data-variant', 'review');
    await expect(commit).toContainText(`${COMMIT_SEVEN.filesChanged} files`);
    // No "⏺ shortSha" content on the pill itself; the branch ⎇ glyph is
    // also gone because the git pill is suppressed.
    await expect(commit).not.toContainText(COMMIT_SEVEN.shortSha);
    await expect(card.locator('text=/\\u2387/')).toHaveCount(0); // ⎇ glyph
  });

  test('5-human-review card with a commit shows only "N files" and no branch pill', async ({ page }) => {
    const card = page.locator('[data-testid="job-card"]', { hasText: `5-human-review fixture ${HUMAN_REVIEW_WITH_COMMIT.id}` });
    await expect(card).toHaveCount(1);

    await expect(card.getByTestId('job-card-git')).toHaveCount(0);

    const commit = card.getByTestId('job-card-commit');
    await expect(commit).toBeVisible();
    await expect(commit).toHaveAttribute('data-variant', 'review');
    await expect(commit).toContainText('12 files');
    await expect(commit).not.toContainText(COMMIT_TWELVE.shortSha);
  });

  test('5-human-review card with no commit shows no pill at all', async ({ page }) => {
    const card = page.locator('[data-testid="job-card"]', { hasText: `5-human-review fixture ${HUMAN_REVIEW_NO_COMMIT.id}` });
    await expect(card).toHaveCount(1);

    // Neither pill renders: AC #4 says "never show 0 files as a phantom
    // indicator", and the branch pill is suppressed in review lanes.
    await expect(card.getByTestId('job-card-git')).toHaveCount(0);
    await expect(card.getByTestId('job-card-commit')).toHaveCount(0);
  });

  test('captures a labelled screenshot per lane variant', async ({ page }, testInfo) => {
    // Vite's dev server occasionally injects an error overlay on top of
    // the page during hot reload races; strip it before screenshotting
    // so the captured frame shows the card, not the overlay.
    await page.evaluate(() => {
      document.querySelectorAll('vite-error-overlay').forEach(n => n.remove());
      document.querySelectorAll('.overlay--error').forEach(n => (n as HTMLElement).style.display = 'none');
    });
    // A taller viewport keeps the full card on-screen for the element
    // screenshot - the default 720 height crops the bottom pills on
    // dense fixtures.
    await page.setViewportSize({ width: 1600, height: 1200 });

    const targets: { jobId: string; label: string; jobText: string }[] = [
      { jobId: PROGRESS_JOB.id,             label: 'progress',     jobText: `3-progress fixture ${PROGRESS_JOB.id}` },
      { jobId: AUTO_REVIEW_WITH_COMMIT.id,  label: 'auto-review',  jobText: `4-auto-review fixture ${AUTO_REVIEW_WITH_COMMIT.id}` },
      { jobId: HUMAN_REVIEW_WITH_COMMIT.id, label: 'human-review', jobText: `5-human-review fixture ${HUMAN_REVIEW_WITH_COMMIT.id}` }
    ];
    for (const t of targets) {
      const card = page.locator('[data-testid="job-card"]', { hasText: t.jobText });
      await expect(card).toHaveCount(1);
      await expect(card).toBeVisible();
      await card.scrollIntoViewIfNeeded();
      // Write the PNG to disk so the review reviewer can find it without
      // unpacking trace.zip; also attach to the report for the HTML
      // viewer. test-results/ is gitignored scratch per AGENTS.md.
      const buf = await card.screenshot({ path: `test-results/card-live-state-${t.label}.png` });
      await testInfo.attach(`card-${t.label}.png`, { body: buf, contentType: 'image/png' });
    }
  });
});
