import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * F55: State-aware Git View display modes.
 *
 * The live working-tree view (branch, file count, diffstat, file list)
 * lives in the Git pane (app-git-pane). It renders only for the runner's
 * currently-active job, since the working tree is shared across the repo
 * and its changes belong to whichever task the agent is editing.
 *
 * The Activity tab no longer carries any inline git view: the old
 * run-git-viewer inline worktree/idle panel was removed so the Activity
 * log does not duplicate the Git pane. run-git-viewer is now purely the
 * per-run diff overlay.
 *
 * Committed-mode used to render an inline "COMMITTED N commits" strip
 * with hash cards here too, but that duplicated the Git pane. It was
 * replaced by a small numeric badge on the Git pane-toggle (see the
 * "Git pane-toggle commit badge" describe block below) so review-lane
 * tasks no longer carry a redundant `COMMITTED 0 commits` strip.
 *
 * Each scenario is tested with fully-mocked API routes so there is no
 * dependency on a running backend or real git repository.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/state-aware-git';

const COMMITS = [
  {
    sha: 'aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111',
    shortSha: 'aaaa111',
    message: 'feat(settings): F47 cleanup',
    filesChanged: 3,
    files: ['src/settings.ts', 'src/settings.spec.ts', 'docs/settings.md'],
    at: '2026-05-24T08:00:00Z',
  },
  {
    sha: 'bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222',
    shortSha: 'bbbb222',
    message: 'feat(board): theme-aware job-card borders',
    filesChanged: 2,
    files: ['src/card.ts', 'src/card.scss'],
    at: '2026-05-24T11:00:00Z',
  },
  {
    sha: 'cccc3333cccc3333cccc3333cccc3333cccc3333',
    shortSha: 'cccc333',
    message: 'fix: WCAG contrast on diff lines in light theme',
    filesChanged: 1,
    files: ['src/diff.scss'],
    at: '2026-05-24T14:30:00Z',
  },
];

function makeDetail(state: string, includeCommits: boolean) {
  const newest = includeCommits ? COMMITS[COMMITS.length - 1] : null;
  return {
    info: {
      id: 'git-view-state-test',
      taskKey: `${WATCH_PATH}::git-view-state-test`,
      title: 'State-aware git view fixture',
      state,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/git-view-state-test`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: newest,
      commits: includeCommits ? COMMITS : [],
      ownerClientId: 'local-default',
    },
    promptMarkdown: 'Test prompt.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

const JOB_ID = 'git-view-state-test';

async function installRoutes(
  page: Page,
  state: string,
  includeCommits: boolean,
  gitStatus?: object,
  activeJobId: string | null = null,
) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail(state, includeCommits);

  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {});
  });
  await page.route('**/api/tasks', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        preparation: [],
        orchestratorPrep: [],
        ready: [],
        progress: [],
        failedPickup: [],
        autoReview: [],
        humanReview: [],
        completed: [],
        archive: [],
      }),
    }),
  );
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
      ]),
    }),
  );
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(/\/api\/(?:projects|v1\/projects(?:\?|$))/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }),
  );
  await page.route('**/api/environment**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        isDev: false,
        devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
      }),
    }),
  );
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ items: [] }),
    }),
  );
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ at: '2026-05-29T00:00:00Z', snapshots: [] }),
    }),
  );
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'manual',
            activeJobId,
            activeExecution: null,
            queuedJobIds: [],
          },
        },
      }),
    }),
  );

  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ runs: [] }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ events: [], sessionChain: [] }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/claude-session(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/hygiene(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projectName: PROJECT,
        isRepo: true,
        isDirty: false,
        hasUpstream: true,
        ahead: 0,
        behind: 0,
        job: {
          jobId: JOB_ID,
          state,
          jobInfoCommitPresent: includeCommits,
          stampedCommitSha: includeCommits ? COMMITS[2].sha : null,
          acceptedTaskUncommitted: false,
        },
        error: null,
      }),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/status(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(
        gitStatus ?? {
          isRepo: true,
          branch: 'main',
          filesChanged: 0,
          totalAdded: 0,
          totalRemoved: 0,
          files: [],
          error: null,
        },
      ),
    }),
  );
  await page.route(new RegExp(`/api/tasks/${idEsc}/commit(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        commit: includeCommits ? COMMITS[2] : null,
        files: includeCommits
          ? COMMITS[2].files.map((p) => ({ status: 'M', path: p, added: 4, removed: 1 }))
          : [],
      }),
    }),
  );
  for (const c of COMMITS) {
    await page.route(
      new RegExp(`/api/tasks/${idEsc}/commits/${c.sha}/diff(\\?|$)`),
      (route) =>
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            diff: `diff --git a/${c.files[0]} b/${c.files[0]}\n--- a/${c.files[0]}\n+++ b/${c.files[0]}\n@@ -1,3 +1,5 @@\n context line\n+added by ${c.shortSha}\n-removed line`,
          }),
        }),
    );
  }
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(detail),
    }),
  );
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

async function dismissErrorDialog(page: Page): Promise<void> {
  const overlay = page.getByTestId('error-dialog-overlay');
  if (await overlay.isVisible().catch(() => false)) {
    await page.evaluate(() => {
      const el = document.querySelector<HTMLElement>('[data-testid="error-dialog-overlay"]');
      el?.click();
    });
    await overlay.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {});
  }
}

test.describe('F55: Git View state-aware display', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem(
          'taskboard.panesVisible',
          JSON.stringify({ prompt: false, protocol: true, git: false }),
        );
        localStorage.setItem('taskboard.activeInspectorTab', '"activity"');
      } catch {
        /* private mode */
      }
    });
  });

  test('review-lane task no longer shows the inline COMMITTED strip', async ({ page }) => {
    // Multi-commit case (3 commits): we want to verify the inline panel is gone.
    await installRoutes(page, '6-completed', true);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    // Detail-view chrome (always present once the detail mounts) is the
    // proxy for "task detail rendered". The Git pane-toggle lives in the
    // studio slim tab-bar header (the legacy in-detail toggle bar is hidden
    // in studio mode) and appears whether or not the Git pane is visible.
    await dismissErrorDialog(page);
    await expect(page.getByTestId('studio-pane-toggle-git')).toBeVisible({ timeout: 10_000 });

    // The old inline view is gone — both the container and the commit cards.
    await expect(page.getByTestId('rgv-inline-committed')).toHaveCount(0);
    await expect(page.getByTestId('rgv-commit-card')).toHaveCount(0);
  });

  test('Git pane-toggle carries a numeric commit badge', async ({ page }) => {
    await installRoutes(page, '6-completed', true);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    await dismissErrorDialog(page);
    await expect(page.getByTestId('studio-pane-toggle-git')).toBeVisible({ timeout: 10_000 });

    const badge = page.getByTestId('studio-pane-toggle-git-badge');
    await expect(badge).toBeVisible({ timeout: 10_000 });
    await expect(badge).toHaveText('3');

    if (RESULTS_DIR) {
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'studio-pane-toggle-git-badge-3.png'),
      });
    }
  });

  test('Git pane-toggle hides the badge when no commits exist', async ({ page }) => {
    // Review-lane state with no commits — the prior inline view rendered a
    // misleading "COMMITTED 0 commits" strip; the new design must render
    // nothing on the toggle either.
    await installRoutes(page, '4-auto-review', false);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    await dismissErrorDialog(page);
    await expect(page.getByTestId('studio-pane-toggle-git')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('rgv-inline-committed')).toHaveCount(0);
    await expect(page.getByTestId('studio-pane-toggle-git-badge')).toHaveCount(0);
  });

  test('Activity tab no longer renders the inline worktree git view', async ({ page }) => {
    // Progress task with live changes — this is exactly the case that used
    // to render the inline "LIVE WORKTREE" panel below the run timeline.
    // The worktree view moved to the Git pane, so the Activity tab must
    // carry neither the worktree nor the idle inline panel.
    const gitStatus = {
      isRepo: true,
      branch: 'main',
      filesChanged: 3,
      totalAdded: 12,
      totalRemoved: 4,
      files: [
        { status: 'M', path: 'src/component.ts', added: 8, removed: 3 },
        { status: 'A', path: 'src/component.spec.ts', added: 4, removed: 0 },
        { status: 'D', path: 'src/old-helper.ts', added: 0, removed: 1 },
      ],
      error: null,
    };

    await installRoutes(page, '3-progress', false, gitStatus, JOB_ID);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    await dismissErrorDialog(page);
    // The Activity tab itself is up once its log section renders.
    await expect(page.getByTestId('protocol-maximize-log').or(page.getByTestId('protocol-open-verbose-debug'))).toBeVisible({ timeout: 10_000 });

    await expect(page.getByTestId('rgv-inline-worktree')).toHaveCount(0);
    await expect(page.getByTestId('rgv-inline-idle')).toHaveCount(0);
  });

  test('Git pane shows the live worktree for the active job', async ({ page }) => {
    const gitStatus = {
      isRepo: true,
      branch: 'main',
      filesChanged: 3,
      totalAdded: 12,
      totalRemoved: 4,
      files: [
        { status: 'M', path: 'src/component.ts', added: 8, removed: 3 },
        { status: 'A', path: 'src/component.spec.ts', added: 4, removed: 0 },
        { status: 'D', path: 'src/old-helper.ts', added: 0, removed: 1 },
      ],
      error: null,
    };

    // activeJobId === JOB_ID so the worktree-isolation gate lets the pane
    // render live `git status`.
    await installRoutes(page, '3-progress', false, gitStatus, JOB_ID);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    await dismissErrorDialog(page);
    // Open the Git pane — first open lazy-loads `git status`.
    await page.getByTestId('studio-pane-toggle-git').click();

    const paneGit = page.getByTestId('pane-git');
    await expect(paneGit).toBeVisible({ timeout: 10_000 });
    await expect(paneGit).toHaveAttribute('data-active-job', 'true');

    await expect(page.getByTestId('git-files-count')).toHaveText('3 files', { timeout: 10_000 });
    await expect(paneGit).toContainText('main');
    await expect(paneGit).toContainText('+12');
    // The file tree collapses the shared `src/` prefix into a folder node,
    // so leaves render as basenames under it rather than full paths.
    await expect(paneGit).toContainText('src/');
    await expect(paneGit).toContainText('component.ts');
    await expect(paneGit).toContainText('component.spec.ts');
    await expect(paneGit).toContainText('old-helper.ts');

    if (RESULTS_DIR) {
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'f55-git-pane-worktree-mode.png'),
      });
    }
  });

  // ASS-1731: the live Git view must be scoped to the task's RUN-LOCATION, not
  // the shared repo root. When the task runs in its own `task/<id>` worktree the
  // backend status carries isWorktree=true + the worktree's branch; the pane
  // must label it `(Worktree)` and show that branch — never a sibling run's
  // dirty files from the main checkout (the cross-attribution bug).
  test('Git pane labels the run-location as (Worktree) and shows the task branch', async ({
    page,
  }) => {
    const gitStatus = {
      isRepo: true,
      branch: 'task/git-view-state-test',
      filesChanged: 1,
      totalAdded: 5,
      totalRemoved: 0,
      files: [{ status: 'A', path: 'src/this-task-work.ts', added: 5, removed: 0 }],
      error: null,
      isWorktree: true,
    };

    await installRoutes(page, '3-progress', false, gitStatus, JOB_ID);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    await dismissErrorDialog(page);
    await page.getByTestId('studio-pane-toggle-git').click();

    const paneGit = page.getByTestId('pane-git');
    await expect(paneGit).toBeVisible({ timeout: 10_000 });

    const location = page.getByTestId('git-run-location');
    await expect(location).toHaveText('(Worktree)', { timeout: 10_000 });
    await expect(location).toHaveClass(/git-view__location--worktree/);
    await expect(paneGit).toContainText('task/git-view-state-test');
    await expect(paneGit).toContainText('this-task-work.ts');

    if (RESULTS_DIR) {
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'ass-1731-git-pane-worktree-label.png'),
      });
    }
  });

  test('Git pane labels the run-location as (Haupt-Checkout) when no worktree exists', async ({
    page,
  }) => {
    const gitStatus = {
      isRepo: true,
      branch: 'develop',
      filesChanged: 1,
      totalAdded: 2,
      totalRemoved: 0,
      files: [{ status: 'M', path: 'src/main-checkout.ts', added: 2, removed: 0 }],
      error: null,
      isWorktree: false,
    };

    await installRoutes(page, '3-progress', false, gitStatus, JOB_ID);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    await dismissErrorDialog(page);
    await page.getByTestId('studio-pane-toggle-git').click();

    const paneGit = page.getByTestId('pane-git');
    await expect(paneGit).toBeVisible({ timeout: 10_000 });

    const location = page.getByTestId('git-run-location');
    await expect(location).toHaveText('(Haupt-Checkout)', { timeout: 10_000 });
    await expect(location).not.toHaveClass(/git-view__location--worktree/);
    await expect(paneGit).toContainText('develop');

    if (RESULTS_DIR) {
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'ass-1731-git-pane-main-checkout-label.png'),
      });
    }
  });

  test('Git pane suppresses the worktree for a non-active task', async ({ page }) => {
    const gitStatus = {
      isRepo: true,
      branch: 'main',
      filesChanged: 2,
      totalAdded: 5,
      totalRemoved: 1,
      files: [{ status: 'M', path: 'src/other.ts', added: 5, removed: 1 }],
      error: null,
    };

    // No active job (activeJobId stays null) — the working tree belongs to
    // whichever task the agent is editing, so a non-active task must not
    // render live status even though the route would return some.
    await installRoutes(page, '3-progress', false, gitStatus, null);
    await page.goto(
      `/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`,
    );

    await dismissErrorDialog(page);
    await page.getByTestId('studio-pane-toggle-git').click();

    const paneGit = page.getByTestId('pane-git');
    await expect(paneGit).toBeVisible({ timeout: 10_000 });
    await expect(paneGit).toHaveAttribute('data-active-job', 'false');
    await expect(page.getByTestId('git-view-suppressed-non-active')).toBeVisible();
    await expect(page.getByTestId('git-files-count')).toHaveCount(0);
  });
});
