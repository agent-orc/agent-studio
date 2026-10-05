import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * AGT-2453 Git-View topology and layout evidence shots.
 *
 * Drives the two surfaces the layout overhaul targets, with every backend
 * route mocked, and writes full-page screenshots for both themes:
 *   1. Project Hub · Git View  (`project-git-panel`) with a commit + diff open.
 *   2. Extended Git/Diff pane   (`git-pane`) maximized into its split layout.
 *
 * The output directory + phase label are driven by env so the same spec can be
 * run against the stable frontend (BEFORE) and the worktree dev frontend
 * (AFTER) and drop clearly-named, source-labelled (`--mocked`) files:
 *   SHOT_DIR   absolute output directory (default: playwright-screenshots/agt-2011)
 *   SHOT_PHASE "before" | "after"        (default: "before")
 *
 * This spec is evidence-only; the behavioural regressions live in the existing
 * project-hub-git-view / git-tree-and-split / gitview-polish specs.
 */

const PROJECT = 'demo-project';
const REPO_PATH = 'C:/repo/demo-project';
const HUB_TAB_KEY = `hub:${PROJECT}`;
const BOOT_TIMEOUT = 60_000;

const COMMIT_SHA = 'c'.repeat(40);

const INVENTORY = {
  projectName: PROJECT,
  repositoryPath: REPO_PATH,
  isRepo: true,
  currentBranch: 'main',
  worktrees: [
    { path: REPO_PATH, branch: 'main', headSha: 'a'.repeat(40), headShortSha: 'aaaaaaa', isPrimary: true, isDetached: false, isBare: false },
    { path: 'C:/repo/demo-project-task-1', branch: 'task/1', headSha: 'b'.repeat(40), headShortSha: 'bbbbbbb', isPrimary: false, isDetached: false, isBare: false },
  ],
  branches: [
    { name: 'main', category: 'main', tipSha: 'a'.repeat(40), tipShortSha: 'aaaaaaa', isCurrent: true, upstream: 'origin/main', ahead: 0, behind: 0, lastCommitSubject: 'seed the repository', lastCommitAtUtc: '2026-07-01T00:00:00Z', worktreePath: REPO_PATH },
    { name: 'develop', category: 'develop', tipSha: 'd'.repeat(40), tipShortSha: 'ddddddd', isCurrent: false, upstream: 'origin/develop', ahead: 1, behind: 0, lastCommitSubject: 'integrate feature work', lastCommitAtUtc: '2026-07-02T00:00:00Z', worktreePath: null },
    { name: 'feature/login', category: 'feature', tipSha: 'e'.repeat(40), tipShortSha: 'eeeeeee', isCurrent: false, upstream: null, ahead: 0, behind: 0, lastCommitSubject: 'feat: login form scaffolding', lastCommitAtUtc: '2026-07-02T00:00:00Z', worktreePath: null },
    { name: 'feature/git-view', category: 'feature', tipSha: '1'.repeat(40), tipShortSha: '1111111', isCurrent: false, upstream: null, ahead: 3, behind: 1, lastCommitSubject: 'wip: git view layout', lastCommitAtUtc: '2026-07-09T00:00:00Z', worktreePath: null },
    { name: 'task/1', category: 'task', tipSha: 'b'.repeat(40), tipShortSha: 'bbbbbbb', isCurrent: false, upstream: null, ahead: 2, behind: 0, lastCommitSubject: 'task work in progress', lastCommitAtUtc: '2026-07-03T00:00:00Z', worktreePath: 'C:/repo/demo-project-task-1' },
    { name: 'task/2', category: 'task', tipSha: '2'.repeat(40), tipShortSha: '2222222', isCurrent: false, upstream: null, ahead: 4, behind: 0, lastCommitSubject: 'another task branch', lastCommitAtUtc: '2026-07-04T00:00:00Z', worktreePath: null },
  ],
  recentCommits: [],
  history: {
    offset: 0, pageSize: 50, nextOffset: null, hasMore: false,
    commits: [
      {
        sha: COMMIT_SHA, shortSha: 'ccccccc', parentShas: ['f'.repeat(40), 'e'.repeat(40)],
        authorDateUtc: '2026-07-05T10:00:00Z', author: 'dev',
        subject: 'Merge task/1 into develop', filesChanged: 3, added: 42, removed: 8,
        refs: [{ name: 'develop', kind: 'branch', isRemote: false }, { name: 'origin/develop', kind: 'branch', isRemote: true }],
        tasks: [{ taskKey: `${PROJECT}::task-1`, key: 'AGT-1', title: 'Widget pipeline', lane: '3-progress' }],
        presence: { inIntegration: true, inRelease: false, integrationBranch: 'develop', releaseBranch: 'main' },
        deployments: [{ target: 'runner', sha: COMMIT_SHA, shortSha: 'ccccccc' }],
      },
      {
        sha: 'f'.repeat(40), shortSha: 'fffffff', parentShas: ['9'.repeat(40)],
        authorDateUtc: '2026-07-04T09:00:00Z', author: 'dev', subject: 'chore: prepare integration line',
        filesChanged: 2, added: 5, removed: 21, refs: [], tasks: [],
        presence: { inIntegration: true, inRelease: true, integrationBranch: 'develop', releaseBranch: 'main' },
        deployments: [],
      },
      {
        sha: 'e'.repeat(40), shortSha: 'eeeeeee', parentShas: ['b'.repeat(40)],
        authorDateUtc: '2026-07-04T08:30:00Z', author: 'dev', subject: 'feat: finish task branch',
        filesChanged: 2, added: 18, removed: 3,
        refs: [{ name: 'task/1', kind: 'branch', isRemote: false }], tasks: [], presence: null, deployments: [],
      },
      {
        sha: 'b'.repeat(40), shortSha: 'bbbbbbb', parentShas: ['9'.repeat(40)],
        authorDateUtc: '2026-07-03T12:00:00Z', author: 'dev', subject: 'feat: branch from shared base',
        filesChanged: 1, added: 12, removed: 0, refs: [], tasks: [], presence: null, deployments: [],
      },
      {
        sha: '9'.repeat(40), shortSha: '9999999', parentShas: [],
        authorDateUtc: '2026-07-01T08:00:00Z', author: 'dev', subject: 'refactor: extract the diff renderer',
        filesChanged: 5, added: 63, removed: 40, refs: [], tasks: [], presence: null, deployments: [],
      },
    ],
  },
  activeCheckouts: [{
    task: { taskKey: `${PROJECT}::task-1`, key: 'AGT-1', title: 'Widget pipeline', lane: '3-progress' },
    branch: 'task/1', headSha: 'b'.repeat(40), location: 'remote',
    runner: 'agent-runner-01', worktreePath: null, activeSince: '2026-07-03T09:00:00Z',
  }],
  error: null,
};

const COMMIT_FILES = [
  { status: 'M', path: 'frontend/src/app/features/git/components/git-view.component.ts', added: 18, removed: 4 },
  { status: 'A', path: 'frontend/src/app/features/git/components/git-view.component.scss', added: 20, removed: 0 },
  { status: 'M', path: 'frontend/src/app/features/git/models/git.model.ts', added: 4, removed: 4 },
];

function diffBody(filePath: string): string {
  return [
    `diff --git a/${filePath} b/${filePath}`,
    `index 1111111..2222222 100644`,
    `--- a/${filePath}`,
    `+++ b/${filePath}`,
    '@@ -1,8 +1,10 @@',
    ' import { Component } from \'@angular/core\';',
    ' ',
    '-const legacyMaxWidth = 960;',
    '+// AGT-2011: fill the viewport, no artificial max-width cap',
    '+const fillViewport = true;',
    ' ',
    ' export class GitViewComponent {',
    '-  padding = 24;',
    '+  padding = 8;',
    '   render() {',
    '     return this.buildTree();',
    '   }',
    ' }',
    '',
  ].join('\n');
}

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

/* --------------------------------------------------------------------- */
/* Surface 1: Project Hub · Git View                                     */
/* --------------------------------------------------------------------- */

async function installHubRoutes(page: Page): Promise<void> {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/**', r => r.fulfill(json([])).catch(() => { /* late */ }));
  await page.route('**/api/runner/orchestrator-feed**', r => r.fulfill(json({ entries: [], generatedAtUtc: '2026-07-31T00:00:00Z' })));
  await page.route('**/api/v1/studio/auth/status', r => r.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route(/\/api\/v1\/studio\/board/, r => r.fulfill(json(EMPTY_GROUPED)));
  await page.route(/\/api\/(?:jobs|tasks)(\?|$)/, r => r.fulfill(json([])));
  await page.route('**/api/watch-paths**', r => r.fulfill(json([{ name: PROJECT, path: REPO_PATH, rootPath: REPO_PATH, repositoryPath: REPO_PATH }])));
  await page.route('**/api/environment**', r => r.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, r => r.fulfill(json({ projects: {} })));
  await page.route('**/api/clients', r => r.fulfill(json([])));
  await page.route('**/api/cli/usage**', r => r.fulfill(json({ items: [] })));
  await page.route('**/api/cli/quota**', r => r.fulfill(json({ ttlSeconds: 600, snapshots: [] })));
  await page.route('**/api/git/summary**', r => r.fulfill(json([])));
  await page.route('**/api/git/cleanup/plan**', r => r.fulfill(json({ isRepo: true, integrationBranch: 'develop', candidates: [] })));

  await page.route('**/api/git/inventory**', r => r.fulfill(json(INVENTORY)));
  await page.route('**/api/git/project-commit/files**', r => {
    const sha = new URL(r.request().url()).searchParams.get('sha') ?? COMMIT_SHA;
    return r.fulfill(json({ sha, files: COMMIT_FILES }));
  });
  await page.route('**/api/git/project-commit/diff**', r => {
    const p = new URL(r.request().url()).searchParams.get('path') ?? COMMIT_FILES[0].path;
    return r.fulfill(json({ diff: diffBody(p), hasDiff: true, emptyReason: null }));
  });
}

async function openHubOnGit(page: Page): Promise<void> {
  await page.evaluate(({ tabKey, project }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [
        { kind: 'board', projectName: '__all__' },
        { kind: 'hub', projectName: project, section: 'git' },
      ],
      activeKey: tabKey,
    }));
    history.replaceState(null, '', '/');
  }, { tabKey: HUB_TAB_KEY, project: PROJECT });
  await page.reload();
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
}

/* --------------------------------------------------------------------- */
/* Surface 2: Extended Git/Diff pane (task detail, maximized split)       */
/* --------------------------------------------------------------------- */

const WATCH_PATH = 'C:/fixtures/git-view-layout';
const JOB_ID = 'git-view-layout-shot';

const PANE_COMMIT = {
  sha: 'feedbeef1234567890feedbeef1234567890feed',
  shortSha: 'feedbee',
  message: 'feat: git view layout overhaul\n\nFill the available surface, tighten the header, and let the tree + diff grow.',
  filesChanged: COMMIT_FILES.length,
  files: COMMIT_FILES.map(f => f.path),
  at: '2026-07-09T08:00:00Z',
};

function makePaneDetail(): unknown {
  return {
    info: {
      id: JOB_ID,
      jobKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Git view layout fixture',
      state: '5-human-review',
      agent: 'claude',
      cliType: 'claude',
      model: 'claude-opus-4-8',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/5-human-review/${JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: PANE_COMMIT,
      commits: [PANE_COMMIT],
      ownerClientId: 'local-default',
    },
    promptMarkdown: 'Git view layout fixture prompt.',
    statusMarkdown: '',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'none', startedAt: null, finishedAt: null, errorMessage: null },
  };
}

async function installPaneRoutes(page: Page): Promise<void> {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makePaneDetail();
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/**', r => r.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => { /* late */ }));
  await page.route('**/api/runner/orchestrator-feed**', r => r.fulfill(json({ entries: [], generatedAtUtc: '2026-07-31T00:00:00Z' })));
  await page.route('**/api/v1/studio/auth/status', r => r.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route(/\/api\/(?:jobs|tasks)(\?|$)/, r => r.fulfill(json([])));
  await page.route(/\/api\/v1\/studio\/board/, r => r.fulfill(json(EMPTY_GROUPED)));
  await page.route('**/api/watch-paths**', r => r.fulfill(json([{ name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH }])));
  await page.route('**/api/v1/workspaces**', r => r.fulfill(json([])));
  await page.route('**/api/v1/projects**', r => r.fulfill(json([])));
  await page.route('**/api/environment**', r => r.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })));
  await page.route('**/api/clients', r => r.fulfill(json([])));
  await page.route('**/api/cli/usage**', r => r.fulfill(json({ items: [] })));
  await page.route('**/api/cli/quota**', r => r.fulfill(json({ items: [] })));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, r => r.fulfill(json({
    projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } },
  })));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/output(\\?|$)`), r => r.fulfill(json([])));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/runs(\\?|$)`), r => r.fulfill(json({ runs: [] })));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/session-events(\\?|$)`), r => r.fulfill(json({ events: [], sessionChain: [] })));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/claude-session(\\?|$)`), r => r.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/git/hygiene(\\?|$)`), r => r.fulfill(json({
    projectName: PROJECT, isRepo: true, isDirty: false, hasUpstream: true, ahead: 0, behind: 0,
    job: { jobId: JOB_ID, state: '5-human-review', jobInfoCommitPresent: true, stampedCommitSha: PANE_COMMIT.sha, acceptedTaskUncommitted: false },
    error: null,
  })));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/git/status(\\?|$)`), r => r.fulfill(json({
    isRepo: true, branch: 'main', filesChanged: 0, totalAdded: 0, totalRemoved: 0, files: [], error: null,
  })));
  // Commit-mode per-file diff: the endpoint returns JSON `{ diff }` (both the
  // aggregate `commits/diff` and per-sha `commits/{sha}/diff`). Returning text
  // here makes Angular's JSON parse throw -> a spurious error dialog + blank
  // diff, so this MUST be JSON.
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/commits/(?:[0-9a-f]+/)?diff\\b`), r => {
    const p = new URL(r.request().url()).searchParams.get('path') ?? PANE_COMMIT.files[0];
    return r.fulfill(json({ diff: diffBody(p), hasDiff: true, emptyReason: null }));
  });
  // Worktree-mode diff is served as text/plain.
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/git/diff\\?.*`), r => {
    const p = new URL(r.request().url()).searchParams.get('path') ?? PANE_COMMIT.files[0];
    return r.fulfill({ status: 200, contentType: 'text/plain', body: diffBody(p) });
  });
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/commits/(?:[0-9a-f]+/)?files`), r => r.fulfill(json({ sha: PANE_COMMIT.sha, files: COMMIT_FILES })));
  await page.route(new RegExp(`/api/(?:jobs|tasks)/${idEsc}/commit(\\?|$)`), r => r.fulfill(json({ commit: PANE_COMMIT, files: COMMIT_FILES })));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), r => r.fulfill(json(detail)));
}

/* --------------------------------------------------------------------- */
/* Shared helpers                                                         */
/* --------------------------------------------------------------------- */

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate((t) => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* ignore */ }
  }, theme);
  await page.waitForTimeout(120);
}

async function dismissErrorDialog(page: Page): Promise<void> {
  // app.html renders <app-error-dialog> in two branches, so a testid locator is
  // non-unique (strict-mode throws). Click every overlay's close/backdrop and,
  // as a belt-and-braces step for the screenshot, remove any that linger — the
  // dialog is a mock-fixture artifact and must not obscure the layout evidence.
  await page.evaluate(() => {
    document.querySelectorAll<HTMLElement>('[data-testid="error-dialog-close"]').forEach(el => el.click());
    document.querySelectorAll<HTMLElement>('[data-testid="error-dialog-overlay"]').forEach(el => el.click());
  });
  await page.waitForTimeout(120);
  await page.evaluate(() => {
    document.querySelectorAll<HTMLElement>('[data-testid="error-dialog-overlay"]').forEach(el => el.remove());
  });
}

function shotDir(): string {
  const fromEnv = process.env.SHOT_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'agt-2011');
}
const PHASE = (process.env.SHOT_PHASE ?? 'before').trim();

async function save(page: Page, testInfo: import('@playwright/test').TestInfo, name: string): Promise<void> {
  const dir = shotDir();
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${name}-${PHASE}--mocked.png`);
  await page.screenshot({ path: file, fullPage: true });
  await testInfo.attach(`${name}-${PHASE}--mocked.png`, { path: file, contentType: 'image/png' });
}

/* --------------------------------------------------------------------- */

test.describe('AGT-2453 Git-View topology and layout shots (mocked)', () => {
  test.use({ viewport: { width: 1680, height: 1050 } });
  test.setTimeout(180_000);

  for (const theme of ['dark', 'light'] as const) {
    test(`Project Hub Git View — ${theme}`, async ({ page }, testInfo) => {
      await installHubRoutes(page);
      await page.goto('/');
      await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
      await openHubOnGit(page);

      await expect(page.getByTestId('project-git-panel')).toBeVisible({ timeout: 15_000 });
      await expect(page.locator('[data-testid="git-graph-segment"][data-kind="merge"]')).toBeVisible();
      await expect(page.locator('[data-testid="git-graph-node"][data-lane="1"]')).toHaveCount(2);
      await setTheme(page, theme);

      // Open a commit so the files list + diff render (the busy state).
      await page.getByRole('button', { name: 'Inspect changes in ccccccc' }).click();
      await expect(page.getByTestId('git-changes')).toBeVisible({ timeout: 15_000 });
      await expect(page.getByTestId('git-file-row').first()).toBeVisible();
      await expect(page.getByTestId('git-diff')).toBeVisible({ timeout: 15_000 });
      const firstCommit = page.getByTestId('git-commit-row').first();
      await expect(firstCommit).toContainText('Integrated · develop');
      await expect(firstCommit).toContainText('Deployed · runner');
      await expect(firstCommit).toContainText('AGT-1');
      await expect(firstCommit).toContainText('In progress');
      await expect(firstCommit).toContainText('Remote');
      await expect(firstCommit).not.toContainText('origin/develop');
      await page.getByTestId('git-chip-legend-toggle').click();
      await expect(page.getByTestId('git-chip-legend-popover')).toBeVisible();
      await dismissErrorDialog(page);
      await page.waitForTimeout(150);

      await save(page, testInfo, `git-hub-panel--${theme}`);
    });

    test(`Extended Git pane (split) — ${theme}`, async ({ page }, testInfo) => {
      await page.addInitScript(() => {
        try {
          localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: false, protocol: true, git: true }));
          localStorage.setItem('taskboard.activeInspectorTab', '"activity"');
        } catch { /* private mode */ }
        // Suppress any transient mock-fixture error dialog so it never obscures
        // the layout evidence. A CSP nonce blocks injected <style>, so remove the
        // overlay node in JS via a MutationObserver — it re-fires on every
        // re-render, keeping the surface clean for the screenshot.
        const kill = () => document
          .querySelectorAll('[data-testid="error-dialog-overlay"]')
          .forEach(el => el.remove());
        const start = () => { kill(); new MutationObserver(kill).observe(document.documentElement, { childList: true, subtree: true }); };
        if (document.documentElement) start();
        else document.addEventListener('DOMContentLoaded', start);
      });
      await installPaneRoutes(page);
      await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
      // Guard: keep any transient (mock-fixture) error dialog from obscuring the
      // layout evidence. With the JSON diff mock this should not fire at all.
      await page.addStyleTag({ content: '[data-testid="error-dialog-overlay"]{display:none !important;}' }).catch(() => { /* pre-nav */ });
      await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 15_000 });
      await setTheme(page, theme);
      await dismissErrorDialog(page);

      // Select the first changed file so the diff column is populated. Click
      // via evaluate so a transient error-dialog overlay cannot intercept it.
      const firstFile = page.locator('[data-testid="git-tree-file"]').first();
      await expect(firstFile).toBeVisible({ timeout: 15_000 });
      await dismissErrorDialog(page);
      await page.evaluate(() => document.querySelector<HTMLElement>('[data-testid="git-tree-file"]')?.click());
      await expect(page.getByTestId('git-diff')).toBeVisible({ timeout: 15_000 });

      // Maximize the pane into the tree-left / diff-right split.
      await dismissErrorDialog(page);
      await page.evaluate(() => document.querySelector<HTMLElement>('[data-testid="pane-maximize-git"]')?.click());
      await expect(page.getByTestId('git-tree-col')).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('git-diff-col')).toBeVisible();
      await page.waitForTimeout(200);
      await dismissErrorDialog(page);

      await save(page, testInfo, `git-pane-split--${theme}`);
    });
  }
});
