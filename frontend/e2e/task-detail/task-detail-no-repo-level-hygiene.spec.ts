import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Per-task detail must not surface repo-level git state.
 *
 * Regression contract for `task-detail-no-repo-level-hygiene-banners`:
 * when the repository is ahead of origin/main (the user's 2026-05-09
 * observation), opening any task in a review/completed/archive lane
 * must NOT show a "Push pending" or "ahead of upstream" banner on the
 * task surface. The project-level hygiene badge in the detail header
 * still surfaces the unpushed signal so the operator can act on it -
 * but as a project-level concern, not as a task-completion check.
 *
 * Approval and push are decoupled in the workflow.
 */

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

function makeJobDetail(jobId: string, watchPath: string, state: string) {
  return {
    info: {
      id: jobId,
      jobKey: `${watchPath}::${jobId}`,
      title: 'Repo-level hygiene scope fixture',
      state,
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath,
      projectName: 'fixture',
      folderPath: `${watchPath}/.orchestrator/jobs/${state}/${jobId}`,
      sessionName: '00000000-0000-0000-0000-000000000000',
      lastUsage: null,
      execution: null,
      order: 1,
      commit: {
        sha: 'abcdef1234567890abcdef1234567890abcdef12',
        shortSha: 'abcdef1',
        message: 'feat: this task',
        filesChanged: 2,
        files: ['a.ts', 'b.ts'],
        at: new Date().toISOString()
      }
    },
    promptMarkdown: 'Pretend prompt.',
    statusMarkdown: '## Done\n\nWork accepted.\n',
    log: [],
    promptHistory: [],
    summaryState: { status: 'finished', startedAt: null, finishedAt: null, errorMessage: null }
  };
}

function makeRepoAheadHygiene(state: string): HygieneShape {
  // Repo state: 2 unpushed commits. One of them is this task's
  // stamped commit; the other is unrelated. The task itself is clean.
  return {
    projectName: 'fixture',
    repoRoot: 'C:/fixtures/repo',
    isRepo: true,
    branch: 'main',
    upstream: 'origin/main',
    hasUpstream: true,
    ahead: 2,
    behind: 0,
    isDirty: false,
    stagedCount: 0,
    unstagedCount: 0,
    untrackedCount: 0,
    lastCommitSha: 'abcdef1234567890abcdef1234567890abcdef12',
    lastCommitShortSha: 'abcdef1',
    lastCommitSubject: 'feat: this task',
    lastCommitAtUtc: new Date().toISOString(),
    job: {
      jobId: 'scope-fixture',
      state,
      jobInfoCommitPresent: true,
      stampedCommitSha: 'abcdef1234567890abcdef1234567890abcdef12',
      acceptedTaskUncommitted: false
    },
    error: null
  };
}

const TARGET = { id: 'scope-fixture', watchPath: 'C:/fixtures/repo' };
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

async function installFixtureRoutes(page: Page, state: string) {
  const hygiene = makeRepoAheadHygiene(state);
  const detail = JSON.stringify(makeJobDetail(TARGET.id, TARGET.watchPath, state));
  const projectHygiene = JSON.stringify({ ...hygiene, job: null });
  const jobHygiene = JSON.stringify(hygiene);

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
      body: JSON.stringify([{ name: 'fixture', path: TARGET.watchPath, rootPath: TARGET.watchPath, repositoryPath: TARGET.watchPath }])
    }));
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: projectHygiene }));
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
  // Fixture is NOT the runner's active job - mirrors the user's
  // 2026-05-09 case where they were just reviewing an auto-review task.
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        projects: {
          fixture: {
            projectName: 'fixture',
            mode: 'manual',
            activeJobId: null,
            activeExecution: null,
            queuedJobIds: []
          }
        }
      })
    }));

  const idEsc = TARGET.id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/claude-session(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/hygiene(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: jobHygiene }));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: detail }));
}

async function dismissAnyErrorDialog(page: Page) {
  const close = page.locator('.error-dialog__close').first();
  for (let i = 0; i < 3; i++) {
    if (!(await close.isVisible().catch(() => false))) return;
    await close.click({ timeout: 1_000 }).catch(() => {});
    await page.waitForTimeout(150);
  }
}

async function ensureProtocolPaneOpen(page: Page) {
  await dismissAnyErrorDialog(page);
  if (!(await page.getByTestId('pane-protocol').isVisible())) {
    const toggle = page.getByTestId('pane-toggle-protocol');
    if (await toggle.isVisible()) await toggle.click();
  }
  await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
}

async function assertNoRepoLevelOnTask(page: Page, lane: string) {
  await installFixtureRoutes(page, lane);
  await page.goto(`/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`);
  await ensureProtocolPaneOpen(page);
  await expect(page.getByTestId('hygiene-strip')).toBeVisible({ timeout: 10_000 });

  // Per-task surface must not surface repo-level signals: no push
  // icon, no push-pending banner, no "ahead of upstream" copy.
  await expect(page.getByTestId('hygiene-push')).toHaveCount(0);
  await expect(page.getByTestId('hygiene-warning-unpushed')).toHaveCount(0);
  const strip = page.getByTestId('hygiene-strip');
  await expect(strip).not.toContainText(/push pending/i);
  await expect(strip).not.toContainText(/ahead of upstream/i);

  // Project-level surface (the badge next to the project name) DOES
  // carry the signal - so the operator can still act on it.
  await expect(page.getByTestId('project-hygiene-badge')).toContainText(/unpushed/i);
  await expect(page.getByTestId('project-hygiene-badge')).toContainText(/2/);

  if (RESULTS_DIR) {
    const out = path.join(RESULTS_DIR, `task-detail-no-repo-level-hygiene-${lane}.png`);
    await page.screenshot({ path: out, fullPage: true });
  }
}

test.describe('Task detail page does not surface repo-level hygiene signals', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: true, git: false }));
      } catch { /* private mode */ }
    });
  });

  test('4-auto-review: no push-pending banner; project badge has unpushed', async ({ page }) => {
    await assertNoRepoLevelOnTask(page, '4-auto-review');
  });

  test('5-human-review: no push-pending banner; project badge has unpushed', async ({ page }) => {
    await assertNoRepoLevelOnTask(page, '5-human-review');
  });

  test('6-completed: no push-pending banner; project badge has unpushed', async ({ page }) => {
    await assertNoRepoLevelOnTask(page, '6-completed');
  });
});
