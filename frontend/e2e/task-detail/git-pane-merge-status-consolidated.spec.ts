import { test, expect, type Page, type Route } from '@playwright/test';
import * as path from 'path';

/**
 * Task-Review merge-status de-duplication (UI-feedback 2026-07-09).
 *
 * The task's on-develop state used to be shown three-to-four times: a
 * "Merged to develop" pill in the top-bar next to Accept, a second
 * "Merged to develop" pill inside the git pane, the landed ladder line, and a
 * per-commit "ON DEVELOP" membership chip. The requirement is ONE compact
 * on-develop display. We keep the landed ladder (it carries both the
 * develop-merged and the main-pending state in a single line) and drop:
 *   - the top-bar `studio-triage-merge-status` pill (the primary still relabels
 *     to "Accept" and its tooltip explains why);
 *   - the git pane `git-landed-state` pill;
 *   - the per-commit `git-commit-membership` chips.
 *
 * Fully mocked (no backend). The git pane is forced visible via the
 * `taskboard.panesVisible` localStorage seam, same as task-detail-multi-commit.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/merge-status-dedup';
const JOB_ID = 'merge-status-dedup-task';
const MERGE_SHA = 'ddddddd9abc1234ef5678901234567890abcdef0';

const COMMIT = {
  sha: '1111111111111111111111111111111111111111',
  shortSha: '1111111',
  message: 'feat: the one task commit',
  filesChanged: 1,
  files: ['src/feature.ts'],
  at: '2026-07-08T10:00:00Z',
  attribution: 'automatic',
  confidence: 0.95,
};

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

function detail() {
  return {
    info: {
      id: JOB_ID,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Merge-status de-dup fixture',
      state: '5-human-review',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-haiku-4-5',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/5-human-review/${JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: COMMIT,
      commits: [COMMIT],
      ownerClientId: 'local-default',
      createdAt: '2026-07-08T09:00:00Z',
      sessionChain: [],
      provenance: {
        branch: `task/${JOB_ID}`,
        base: 'base0000',
        transitions: [],
        merge: {
          mergeCommit: MERGE_SHA,
          workBranchHeadBefore: 'dev00000',
          workBranchHeadAfter: MERGE_SHA,
          atUtc: '2026-07-08T12:30:00Z',
        },
      },
    },
    promptMarkdown: '# Prompt',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    titleHistory: [],
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

/** Merged-to-develop, not-yet-on-main provenance view driving the ladder. */
function provenanceView() {
  return {
    branch: `task/${JOB_ID}`,
    base: 'base0000',
    transitions: [],
    merge: { mergeCommit: MERGE_SHA, workBranchHeadBefore: 'dev00000', workBranchHeadAfter: MERGE_SHA, atUtc: '2026-07-08T12:30:00Z' },
    landedState: 'merged-to-develop',
    ladder: {
      branch: `task/${JOB_ID}`,
      branchTip: 'tip00000',
      integrationBranch: 'develop',
      integrationHead: 'devhead0',
      mergedToIntegration: true,
      releaseBranch: 'main',
      releaseHead: 'mainhead',
      releasedToRelease: false,
    },
    // A commit IS present here on purpose: the per-commit membership chip used
    // to render from this list. The spec asserts it no longer appears.
    commits: [
      { sha: COMMIT.sha, shortSha: COMMIT.shortSha, message: COMMIT.message, alsoOnIntegration: true, alsoOnRelease: false },
    ],
  };
}

async function installRoutes(page: Page): Promise<void> {
  await page.route('**/api/**', (route) => json(route, []));
  await page.route('**/api/tasks', (route) => json(route, []));
  await page.route('**/api/v1/studio/board**', (route) => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
    escalated: [], completed: [], archive: [],
  }));
  await page.route('**/api/watch-paths**', (route) => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', (route) => json(route, {
    isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/clients', (route) => json(route, []));
  await page.route('**/api/agent-rules**', (route) => json(route, []));
  await page.route('**/api/cli/usage**', (route) => json(route, { items: [] }));
  await page.route('**/api/cli/quota**', (route) => json(route, { at: '2026-07-08T00:00:00Z', snapshots: [] }));
  await page.route('**/api/git/summary**', (route) => json(route, []));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) => json(route, {}));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) => json(route, {
    projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } },
  }));

  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/api/tasks/${idEsc}/provenance(\\?|$)`), (route) => json(route, provenanceView()));
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/status(\\?|$)`), (route) => json(route, {
    isRepo: true, branch: 'main', filesChanged: 0, totalAdded: 0, totalRemoved: 0, files: [], error: null,
  }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/commit(\\?|$)`), (route) => json(route, {
    commit: COMMIT,
    files: COMMIT.files.map((p) => ({ status: 'M', path: p, added: 4, removed: 1 })),
  }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/commit/diff(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'text/plain', body: 'diff --git a/x b/x\n+++ b/x\n+task commit diff' }));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) => json(route, detail()));
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

test.describe('Task-Review merge-status shown once', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: false, protocol: false, git: true }));
        localStorage.setItem('taskboard.gitPane.commitGroupCollapsed', '1');
      } catch { /* private mode */ }
    });
  });

  test('git pane shows the landed ladder once, without the develop pill or membership chip', async ({ page }) => {
    await installRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });

    // The single on-develop status line survives — and, crucially for a
    // "show it only once" task, it renders exactly once (not one-of-several).
    const ladder = page.getByTestId('git-landed-ladder');
    await expect(ladder).toHaveCount(1);
    await expect(ladder).toBeVisible();
    await expect(page.getByTestId('git-ladder-integration')).toContainText('develop');
    await expect(page.getByTestId('git-ladder-integration')).toContainText('merged');
    await expect(page.getByTestId('git-ladder-release')).toContainText('main');
    await expect(page.getByTestId('git-ladder-release')).toContainText('pending');

    // ...while the redundant develop indicators are gone.
    await expect(page.getByTestId('git-landed-state')).toHaveCount(0);
    await expect(page.getByTestId('git-commit-membership')).toHaveCount(0);
    // No stray second "Merged to develop" label inside the git pane.
    await expect(page.getByTestId('pane-git').getByText('Merged to develop')).toHaveCount(0);

    if (RESULTS_DIR) {
      await page.getByTestId('pane-git').screenshot({ path: path.join(RESULTS_DIR, 'git-pane-single-ladder--mocked.png') });
    }
  });

  test('top-bar offers Accept with no redundant merge-status pill', async ({ page }) => {
    await installRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('studio-triage-panel')).toBeVisible({ timeout: 10_000 });

    await expect(page.getByTestId('studio-triage-action-mark-done')).toHaveText(/Accept/);
    await expect(page.getByTestId('studio-triage-merge-status')).toHaveCount(0);
    // Belt-and-braces: no "Merged to develop" pill survives ANYWHERE on the
    // page (top-bar, detail header, or git pane) — the develop state lives
    // solely in the git-pane landed ladder rungs, which spell it "merged".
    await expect(page.getByText('Merged to develop')).toHaveCount(0);

    if (RESULTS_DIR) {
      await page.getByTestId('studio-triage-panel').screenshot({ path: path.join(RESULTS_DIR, 'top-bar-no-merge-pill--mocked.png') });
    }
  });
});
