import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Task-detail worktree isolation rule.
 *
 * The Git working tree is shared across the whole repository. When an
 * agent runs on the active task (the one in `3-progress`), its
 * uncommitted edits naturally show up in `git status`. The detail
 * surface for any OTHER task must not speak for those edits: the
 * "Accepted task work uncommitted" hygiene warning, the live working-
 * tree file list, and the `git diff` derived from the working tree all
 * belong to whichever task is currently active. Showing them on a
 * non-active task produces false alarms and trains operators to ignore
 * hygiene warnings, which masks the real ones.
 *
 * <para>
 * These specs pin the rule end-to-end through the rendered DOM:
 * </para>
 * <list type="bullet">
 *   <item>A task in `5-human-review` while another task is the
 *     project's active job suppresses the working-tree pane and the
 *     hygiene warning entirely; only its committed evidence renders.</item>
 *   <item>The active task's detail continues to show the live
 *     working-tree pane as before.</item>
 * </list>
 */

interface OutLine { timestamp: string; stream: string; text: string; }

interface JobCommitFixture {
  sha: string;
  shortSha: string;
  message: string;
  filesChanged: number;
  files: string[];
  at: string;
}

interface HygieneShape {
  projectName: string;
  repoRoot: string;
  isRepo: boolean;
  branch: string;
  upstream: string | null;
  hasUpstream: boolean;
  ahead: number;
  behind: number;
  isDirty: boolean;
  stagedCount: number;
  unstagedCount: number;
  untrackedCount: number;
  lastCommitSha: string;
  lastCommitShortSha: string;
  lastCommitSubject: string;
  lastCommitAtUtc: string;
  job: {
    jobId: string;
    state: string;
    jobInfoCommitPresent: boolean;
    stampedCommitSha: string | null;
    acceptedTaskUncommitted: boolean;
  } | null;
  error: string | null;
}

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/iso-repo';

function makeJobDetail(
  jobId: string,
  state: string,
  commits: JobCommitFixture[]
) {
  const newest = commits[commits.length - 1] ?? null;
  return {
    info: {
      id: jobId,
      jobKey: `${WATCH_PATH}::${jobId}`,
      title: `Fixture ${jobId}`,
      state,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/${state}/${jobId}`,
      sessionName: '00000000-0000-0000-0000-000000000000',
      lastUsage: null,
      execution: null,
      order: 1,
      commit: newest,
      commits,
      ownerClientId: 'local-default'
    },
    promptMarkdown: `Pretend prompt for ${jobId}.`,
    statusMarkdown: '## Done\n\nWork accepted.\n',
    log: [] as OutLine[],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'finished', startedAt: null, finishedAt: null, errorMessage: null }
  };
}

function makeHygiene(state: string, isDirty: boolean, hasCommit: boolean): HygieneShape {
  return {
    projectName: PROJECT,
    repoRoot: WATCH_PATH,
    isRepo: true,
    branch: 'main',
    upstream: 'origin/main',
    hasUpstream: true,
    ahead: 0,
    behind: 0,
    isDirty,
    stagedCount: isDirty ? 1 : 0,
    unstagedCount: isDirty ? 1 : 0,
    untrackedCount: isDirty ? 1 : 0,
    lastCommitSha: 'abcdef1234567890abcdef1234567890abcdef12',
    lastCommitShortSha: 'abcdef1',
    lastCommitSubject: 'feat: deliver fixture',
    lastCommitAtUtc: new Date().toISOString(),
    // Backend gate already suppresses acceptedTaskUncommitted on
    // non-active tasks; the test fixture mirrors that contract.
    job: {
      jobId: 'irrelevant',
      state,
      jobInfoCommitPresent: hasCommit,
      stampedCommitSha: hasCommit ? 'abcdef1234567890abcdef1234567890abcdef12' : null,
      acceptedTaskUncommitted: false
    },
    error: null
  };
}

async function installCommonRoutes(page: Page, opts: { activeJobId: string | null }) {
  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {});
  });
  await page.route('**/api/tasks', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        preparation: [], orchestratorPrep: [],
        ready: [], progress: [], failedPickup: [],
        autoReview: [], humanReview: [], completed: [], archive: []
      })
    }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }])
    }));
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
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
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          [PROJECT]: {
            projectName: PROJECT,
            mode: 'manual',
            activeJobId: opts.activeJobId,
            activeExecution: null,
            queuedJobIds: []
          }
        }
      })
    }));
}

async function installJobRoutes(page: Page, jobId: string, detail: ReturnType<typeof makeJobDetail>, hygiene: HygieneShape, gitFiles: { path: string; status: string; added: number; removed: number }[]) {
  const idEsc = jobId.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/claude-session(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/hygiene(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(hygiene) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/status(\\?|$)`), (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        isRepo: true,
        branch: 'main',
        filesChanged: gitFiles.length,
        totalAdded: gitFiles.reduce((acc, f) => acc + f.added, 0),
        totalRemoved: gitFiles.reduce((acc, f) => acc + f.removed, 0),
        files: gitFiles,
        error: null
      })
    }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/commit(\\?|$)`), (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        commit: detail.info.commit,
        files: detail.info.commit ? detail.info.commit.files.map(p => ({ status: 'M', path: p, added: 1, removed: 0 })) : []
      })
    }));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }));
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

async function dismissAnyErrorDialog(page: Page) {
  const close = page.locator('.error-dialog__close').first();
  for (let i = 0; i < 3; i++) {
    if (!(await close.isVisible().catch(() => false))) return;
    await close.click({ timeout: 1_000 }).catch(() => {});
    await page.waitForTimeout(150);
  }
}

async function ensurePanesOpen(page: Page) {
  await dismissAnyErrorDialog(page);
  // Make sure both protocol AND git panes are visible. localStorage init
  // alone is not always honored by the time the per-instance
  // LayoutPanesService boots, so click the toggles defensively if the
  // panes aren't already mounted.
  const protocolPane = page.getByTestId('pane-protocol');
  if (!(await protocolPane.isVisible().catch(() => false))) {
    const t = page.getByTestId('pane-toggle-protocol');
    if (await t.isVisible().catch(() => false)) await t.click();
  }
  const gitPane = page.getByTestId('pane-git');
  if (!(await gitPane.isVisible().catch(() => false))) {
    const t = page.getByTestId('pane-toggle-git');
    if (await t.isVisible().catch(() => false)) await t.click();
  }
  await expect(gitPane).toBeVisible({ timeout: 10_000 });
  await expect(protocolPane).toBeVisible({ timeout: 10_000 });
}

test.describe('Task-detail worktree isolation', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: false, protocol: true, git: true }));
      } catch { /* private mode */ }
    });
  });

  test('non-active task in 5-human-review hides worktree noise and the false-alarm hygiene warning', async ({ page }) => {
    // Two tasks: the agent is currently editing on `active-task` (in
    // 3-progress); the operator opens the detail of `human-review-task`
    // (in 5-human-review). Both share the same dirty working tree
    // because the repo's tree is global.
    await installCommonRoutes(page, { activeJobId: 'active-task' });
    const reviewedDetail = makeJobDetail('human-review-task', '5-human-review', [
      { sha: 'aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111', shortSha: 'aaaa111', message: 'feat: review evidence', filesChanged: 1, files: ['evidence.md'], at: '2026-05-09T10:00:00Z' }
    ]);
    const dirtyHygiene = makeHygiene('5-human-review', /*isDirty*/ true, /*hasCommit*/ true);
    const dirtyFiles = [
      { path: 'agent-edit-1.ts', status: 'M', added: 5, removed: 1 },
      { path: 'agent-edit-2.ts', status: 'M', added: 3, removed: 0 }
    ];
    await installJobRoutes(page, 'human-review-task', reviewedDetail, dirtyHygiene, dirtyFiles);

    await page.goto(`/?job=${encodeURIComponent('human-review-task')}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await ensurePanesOpen(page);

    // Hygiene strip: "Accepted task work uncommitted" warning is suppressed.
    await expect(page.getByTestId('hygiene-strip')).toBeVisible();
    await expect(page.getByTestId('hygiene-warning-dirty-after-accept')).toHaveCount(0);

    // Git pane is in commit-only view. The worktree file list and commit
    // form are gone; only the recorded commit's evidence is visible.
    await expect(page.getByTestId('pane-git')).toHaveAttribute('data-active-job', 'false');
    await expect(page.getByTestId('git-commit-header')).toBeVisible();
    await expect(page.getByTestId('git-commit-msg')).toHaveCount(0);
    // Worktree files chip never renders on a non-active task.
    await expect(page.getByTestId('git-files-count')).toHaveCount(0);

    if (RESULTS_DIR) {
      await page.screenshot({ path: path.join(RESULTS_DIR, 'worktree-isolation-non-active.png'), fullPage: true });
    }
  });

  test('non-active task with no recorded commit shows the suppressed-non-active placeholder', async ({ page }) => {
    // The harder edge case: the reviewed task has no committed evidence
    // at all (auto-commit failed or the lane was reached manually). The
    // git pane must NOT fall through to the worktree view; it must show
    // the placeholder so the operator sees that the dirty tree is not
    // their concern on this card.
    await installCommonRoutes(page, { activeJobId: 'active-task' });
    const reviewedDetail = makeJobDetail('lonely-task', '5-human-review', []);
    const dirtyHygiene = makeHygiene('5-human-review', /*isDirty*/ true, /*hasCommit*/ false);
    const dirtyFiles = [{ path: 'agent-edit.ts', status: 'M', added: 1, removed: 0 }];
    await installJobRoutes(page, 'lonely-task', reviewedDetail, dirtyHygiene, dirtyFiles);

    await page.goto(`/?job=${encodeURIComponent('lonely-task')}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await ensurePanesOpen(page);

    await expect(page.getByTestId('hygiene-warning-dirty-after-accept')).toHaveCount(0);
    await expect(page.getByTestId('git-view-suppressed-non-active')).toBeVisible();
    // The worktree file count chip never renders on the placeholder.
    await expect(page.getByTestId('git-files-count')).toHaveCount(0);
  });

  test('active task in 3-progress still shows the live working tree', async ({ page }) => {
    await installCommonRoutes(page, { activeJobId: 'active-task' });
    const activeDetail = makeJobDetail('active-task', '3-progress', []);
    const dirtyHygiene = makeHygiene('3-progress', true, false);
    const dirtyFiles = [
      { path: 'feature-a.ts', status: 'M', added: 7, removed: 2 },
      { path: 'feature-b.ts', status: 'A', added: 12, removed: 0 }
    ];
    await installJobRoutes(page, 'active-task', activeDetail, dirtyHygiene, dirtyFiles);

    await page.goto(`/?job=${encodeURIComponent('active-task')}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await ensurePanesOpen(page);

    // Active task gets the live working-tree view: file-count chip,
    // commit form, the data-active-job marker, and no suppression
    // placeholder. Wait for the first git-status poll to land before
    // asserting the file count (5s default interval; 10s allowance).
    await expect(page.getByTestId('pane-git')).toHaveAttribute('data-active-job', 'true');
    await expect(page.getByTestId('git-files-count')).toContainText('2 files', { timeout: 12_000 });
    await expect(page.getByTestId('git-view-suppressed-non-active')).toHaveCount(0);
  });
});
