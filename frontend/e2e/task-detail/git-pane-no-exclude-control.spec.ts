import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Evidence spec for the "remove Exclude-commit operator override" chore.
 *
 * The per-commit "-"/Exclude control, the "(N excluded)" expander, and
 * the "+ Add commit" picker were removed from the git pane. This spec
 * renders the Task-Commits panel (commit mode, multi-commit chain - the
 * exact surface those controls lived on) fully mocked against the
 * current `/api/tasks/*` routes, asserts none of the override controls
 * exist, and screenshots the pane for review.
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/multi-commit-repo';
const JOB_ID = 'no-exclude-task';

interface CommitFixture {
  sha: string;
  shortSha: string;
  message: string;
  filesChanged: number;
  files: string[];
  at: string;
  attribution?: string;
  confidence?: number;
}

const COMMITS: CommitFixture[] = [
  {
    sha: '1111111111111111111111111111111111111111',
    shortSha: '1111111',
    message: 'feat: initial slice',
    filesChanged: 2,
    files: ['src/feature.ts', 'src/feature.spec.ts'],
    at: '2026-05-09T10:00:00Z',
    attribution: 'automatic',
    confidence: 0.9,
  },
  {
    sha: '2222222222222222222222222222222222222222',
    shortSha: '2222222',
    message: 'fix: handle empty input edge case',
    filesChanged: 1,
    files: ['src/feature.ts'],
    at: '2026-05-09T10:30:00Z',
    attribution: 'automatic',
    confidence: 0.82,
  },
  {
    sha: '3333333333333333333333333333333333333333',
    shortSha: '3333333',
    message: 'chore: update docs after operator steer',
    filesChanged: 3,
    files: ['src/feature.ts', 'README.md', 'CHANGELOG.md'],
    at: '2026-05-09T11:15:00Z',
    attribution: 'automatic',
    confidence: 0.75,
  },
];

function makeDetail() {
  const newest = COMMITS[COMMITS.length - 1];
  return {
    info: {
      id: JOB_ID,
      jobKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'No-exclude fixture',
      state: '5-human-review',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-7',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/5-human-review/${JOB_ID}`,
      sessionName: '00000000-0000-0000-0000-000000000000',
      lastUsage: null,
      execution: null,
      order: 1,
      commit: newest,
      commits: COMMITS,
      ownerClientId: 'local-default',
    },
    promptMarkdown: 'Pretend prompt with three iterations.',
    statusMarkdown: '## Done\n\nThree commits across iterations.\n',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'finished', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installRoutes(page: Page) {
  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => {});
  });
  await page.route('**/api/v1/studio/board**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        preparation: [], orchestratorPrep: [],
        ready: [], progress: [], failedPickup: [],
        autoReview: [], humanReview: [], completed: [], archive: [],
      }),
    }));
  await page.route('**/api/watch-paths**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }]),
    }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/environment**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  await page.route('**/api/agent-rules**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } }),
    }));

  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail();

  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/hygiene(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projectName: PROJECT, isRepo: true, isDirty: false, hasUpstream: true, ahead: 0, behind: 0, job: { jobId: JOB_ID, state: '5-human-review', jobInfoCommitPresent: true, stampedCommitSha: COMMITS[2].sha, acceptedTaskUncommitted: false }, error: null }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/status(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isRepo: true, branch: 'main', filesChanged: 0, totalAdded: 0, totalRemoved: 0, files: [], error: null }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/commit(\\?|$)`), (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        commit: COMMITS[2],
        files: COMMITS[2].files.map((p) => ({ status: 'M', path: p, added: 4, removed: 1 })),
      }),
    }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/commit/diff(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'text/plain', body: 'diff --git a/x b/x\n+++ b/x\n+latest commit diff' }));
  for (const c of COMMITS) {
    await page.route(new RegExp(`/api/tasks/${idEsc}/commits/${c.sha}/files(\\?|$)`), (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ sha: c.sha, files: c.files.map((p) => ({ status: 'M', path: p, added: 1, removed: 0 })) }),
      }));
    await page.route(new RegExp(`/api/tasks/${idEsc}/commits/${c.sha}/diff(\\?|$)`), (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ diff: `diff --git a/${c.files[0]} b/${c.files[0]}\n+++ b/${c.files[0]}\n+${c.shortSha} change` }),
      }));
  }
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }));
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

test.describe('Git pane — Exclude-commit override removed', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: false, protocol: false, git: true }));
      } catch { /* private mode */ }
    });
  });

  test('renders the commit chain with no exclude / include / add-commit controls', async ({ page }) => {
    await installRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });

    // Defensively dismiss any unrelated error-dialog overlay (e.g. a
    // mocked-endpoint shape mismatch) so it cannot obscure the panel
    // screenshot below.
    const errClose = page.locator('app-error-dialog [aria-label="Close"], app-error-dialog button:has-text("×")').first();
    if (await errClose.isVisible().catch(() => false)) {
      await errClose.click().catch(() => {});
    }

    const chain = page.getByTestId('git-commit-chain');
    await expect(chain).toBeVisible();
    await expect(page.getByTestId('git-commit-chain-item')).toHaveCount(3);

    const pane = page.getByTestId('pane-git');

    // The operator-override surface is gone: no per-commit "-" exclude
    // button, no excluded expander, no "+ Add commit" picker, no manual
    // attribution marker.
    await expect(pane.locator('.git-view__commit-exclude')).toHaveCount(0);
    await expect(pane.locator('.git-view__excluded')).toHaveCount(0);
    await expect(pane.locator('.git-view__excluded-toggle')).toHaveCount(0);
    await expect(pane.locator('.git-view__add')).toHaveCount(0);
    await expect(pane.locator('.git-view__add-toggle')).toHaveCount(0);
    await expect(pane.locator('.git-view__commit-manual')).toHaveCount(0);
    await expect(pane.getByText('Exclude this commit', { exact: false })).toHaveCount(0);
    await expect(pane.getByText('Add commit', { exact: false })).toHaveCount(0);

    if (RESULTS_DIR) {
      await pane.screenshot({ path: path.join(RESULTS_DIR, 'git-pane-no-exclude-control.png') });
    }
  });
});
