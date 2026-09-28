import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Project Hub Git View (AGT-1807) — mocked full-stack drive.
 *
 * The Project Hub exposes a dedicated, read-only Git View page: a grouped
 * branch / worktree tree on the left, and a commit graph with lazy diff
 * inspector on the right. This spec mounts the
 * Project Hub straight onto the `git` rail (persisted studio-tab state), with
 * every backend route mocked, and asserts each acceptance bullet:
 *   - the Git View rail is present in the Project Hub navigation and active;
 *   - the tree distinguishes main / develop / feature / task branches and lists
 *     the on-disk worktree/checkout folders;
 *   - explicitly inspecting a commit loads its files and renders the diff through the
 *     shared <app-diff-content> renderer;
 *   - selecting a branch highlights its tree row without mutating git.
 *
 * All API traffic is stubbed, so no live backend is required; the screenshot
 * saved to results/ is therefore labelled `--mocked`.
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
    { name: 'main', category: 'main', tipSha: 'a'.repeat(40), tipShortSha: 'aaaaaaa', isCurrent: true, upstream: 'origin/main', ahead: 0, behind: 0, lastCommitSubject: 'seed', lastCommitAtUtc: '2026-07-01T00:00:00Z', worktreePath: REPO_PATH, isLocal: true, hasRemote: true },
    { name: 'develop', category: 'develop', tipSha: 'd'.repeat(40), tipShortSha: 'ddddddd', isCurrent: false, upstream: 'origin/develop', ahead: 1, behind: 0, lastCommitSubject: 'dev work', lastCommitAtUtc: '2026-07-02T00:00:00Z', worktreePath: null, isLocal: true, hasRemote: true },
    { name: 'feature/login', category: 'feature', tipSha: 'e'.repeat(40), tipShortSha: 'eeeeeee', isCurrent: false, upstream: null, ahead: 0, behind: 0, lastCommitSubject: 'feat: login form', lastCommitAtUtc: '2026-07-02T00:00:00Z', worktreePath: null, isLocal: false, hasRemote: true },
    { name: 'task/1', category: 'task', tipSha: 'b'.repeat(40), tipShortSha: 'bbbbbbb', isCurrent: false, upstream: null, ahead: 2, behind: 0, lastCommitSubject: 'task work', lastCommitAtUtc: '2026-07-03T00:00:00Z', worktreePath: 'C:/repo/demo-project-task-1', isLocal: true, hasRemote: false, tasks: [{ taskKey: `${PROJECT}::task-1`, key: 'AGT-1', title: 'Task work', lane: '3-progress' }] },
    { name: 'runner/agent-runner-01/AGT-1', category: 'runner', tipSha: COMMIT_SHA, tipShortSha: 'ccccccc', isCurrent: false, upstream: null, ahead: 0, behind: 0, lastCommitSubject: 'feat: add thing', lastCommitAtUtc: '2026-07-03T10:00:00Z', worktreePath: null, isLocal: false, hasRemote: true, tasks: [{ taskKey: `${PROJECT}::task-1`, key: 'AGT-1', title: 'Task work', lane: '3-progress' }] },
  ],
  recentCommits: [],
  history: {
    offset: 0, pageSize: 50, nextOffset: 5, hasMore: true,
    commits: [
      {
        sha: COMMIT_SHA, shortSha: 'ccccccc', parentShas: ['f'.repeat(40), 'e'.repeat(40)],
        authorDateUtc: '2026-07-05T10:00:00Z', author: 'dev', subject: 'Merge task/1 into develop',
        filesChanged: 1, added: 3, removed: 1,
        refs: [{ name: 'origin/develop', kind: 'branch', isRemote: true }],
        tasks: [{ taskKey: `${PROJECT}::task-1`, key: 'AGT-1', title: 'Task work', lane: '3-progress' }],
        presence: { inIntegration: true, inRelease: false, integrationBranch: 'develop', releaseBranch: 'main' },
        deployments: [
          { target: 'backend', sha: COMMIT_SHA, shortSha: 'ccccccc' },
          { target: 'runner', sha: COMMIT_SHA, shortSha: 'ccccccc' },
          { target: 'frontend', sha: COMMIT_SHA, shortSha: 'ccccccc' },
        ],
      },
      {
        sha: 'f'.repeat(40), shortSha: 'fffffff', parentShas: ['9'.repeat(40)],
        authorDateUtc: '2026-07-04T09:00:00Z', author: 'dev', subject: 'chore: prepare integration line',
        filesChanged: 2, added: 5, removed: 2, refs: [], tasks: [],
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
        sha: '9'.repeat(40), shortSha: '9999999', parentShas: ['8'.repeat(40)],
        authorDateUtc: '2026-07-02T08:00:00Z', author: 'dev', subject: 'refactor: shared graph base',
        filesChanged: 3, added: 21, removed: 11, refs: [], tasks: [], presence: null, deployments: [],
      },
    ],
  },
  activeCheckouts: [{
    task: { taskKey: `${PROJECT}::task-1`, key: 'AGT-1', title: 'Task work', lane: '3-progress' },
    branch: 'task/1', headSha: 'b'.repeat(40), location: 'remote',
    runner: 'agent-runner-01', worktreePath: null, activeSince: '2026-07-03T09:00:00Z',
  }, {
    task: { taskKey: `${PROJECT}::task-2`, key: 'AGT-2', title: 'Local work', lane: '3-progress' },
    branch: 'task/2', headSha: '2'.repeat(40), location: 'local',
    runner: 'stable', worktreePath: 'C:/repo/demo-project-task-2', activeSince: '2026-07-03T09:30:00Z',
  }],
  deployments: [
    { target: 'backend', sha: COMMIT_SHA, shortSha: 'ccccccc' },
    { target: 'runner', sha: COMMIT_SHA, shortSha: 'ccccccc' },
    { target: 'frontend', sha: COMMIT_SHA, shortSha: 'ccccccc' },
  ],
  error: null,
};

function diffBody(filePath: string): string {
  return [
    `diff --git a/${filePath} b/${filePath}`,
    `--- a/${filePath}`,
    `+++ b/${filePath}`,
    '@@ -1,2 +1,3 @@',
    ' const a = 1;',
    '+const b = 2;',
    '-const c = 3;',
    '',
  ].join('\n');
}

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

async function installRoutes(page: Page): Promise<void> {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  // Catch-all first (returns []); specific routes registered afterwards win.
  await page.route('**/api/**', r => r.fulfill(json([])).catch(() => { /* late */ }));
  await page.route('**/api/runner/orchestrator-feed**', r => r.fulfill(json({ entries: [], generatedAtUtc: '2026-07-31T00:00:00Z' })));
  await page.route('**/api/auth/status', r => r.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route(/\/api\/(?:jobs|tasks)\/grouped/, r => r.fulfill(json(EMPTY_GROUPED)));
  await page.route(/\/api\/(?:jobs|tasks)(\?|$)/, r => r.fulfill(json([])));
  await page.route('**/api/watch-paths**', r => r.fulfill(json([{ name: PROJECT, path: REPO_PATH, rootPath: REPO_PATH, repositoryPath: REPO_PATH }])));
  await page.route('**/api/environment**', r => r.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })));
  await page.route(/\/api\/runner\/status(\?|$)/, r => r.fulfill(json({ projects: {} })));
  await page.route('**/api/clients', r => r.fulfill(json([])));
  await page.route('**/api/cli/usage**', r => r.fulfill(json({ items: [] })));
  await page.route('**/api/cli/quota**', r => r.fulfill(json({ ttlSeconds: 600, snapshots: [] })));
  await page.route('**/api/git/summary**', r => r.fulfill(json([])));
  await page.route('**/api/git/branch-sweep/settings**', r => r.fulfill(json({ project: PROJECT, mode: 'report-only', windows: {}, defaults: {} })));
  await page.route('**/api/git/branch-sweep/latest**', r => r.fulfill(json({
    project: PROJECT, repositoryPath: REPO_PATH, mode: 'report-only',
    startedAtUtc: '2026-09-18T10:00:00Z', completedAtUtc: '2026-09-18T10:00:01Z',
    windows: { taskDays: 7, salvageDays: 14, quarantineDays: 30, abandonedDays: 90 },
    refsBefore: 1, refsAfter: 1,
    totals: [{ class: 'quarantine', total: 1, eligible: 1, kept: 1, deleted: 0 }],
    ageHistogram: [{ label: '365+ days', refs: 1 }],
    candidates: [{
      ref: 'agent-studio/quarantine/agent-runner-01/AGT-2230/unknown-generation/very-long-distinguishing-tail',
      class: 'quarantine', taskKey: 'AGT-2230', taskState: '7-archive', tipSha: '7'.repeat(40),
      tipShortSha: '7777777', tipCommittedAtUtc: '2025-01-01T00:00:00Z', ageDays: 620,
      containedInMain: true, containedInDevelop: true, referencedByOpenCard: false,
      decision: 'Delete', eligible: true, reason: 'Eligible for deletion; deletion policy met.',
    }], deletions: [], error: null,
  })));

  // The Git View endpoints under test.
  await page.route('**/api/git/inventory**', r => r.fulfill(json(INVENTORY)));
  await page.route('**/api/git/history**', r => r.fulfill(json({
    offset: 5,
    pageSize: 50,
    nextOffset: null,
    hasMore: false,
    commits: [{
      sha: '8'.repeat(40), shortSha: '8888888', parentShas: [],
      authorDateUtc: '2026-07-01T08:00:00Z', author: 'dev', subject: 'older paged commit',
      filesChanged: 0, added: 0, removed: 0, refs: [], tasks: [],
      presence: { inIntegration: true, inRelease: true, integrationBranch: 'develop', releaseBranch: 'main' },
      deployments: [],
    }],
  })));
  await page.route('**/api/git/project-commit/files**', r => {
    const sha = new URL(r.request().url()).searchParams.get('sha') ?? COMMIT_SHA;
    return r.fulfill(json({ sha, files: [{ status: 'M', path: 'src/thing.ts', added: 3, removed: 1 }] }));
  });
  await page.route('**/api/git/project-commit/diff**', r => {
    const p = new URL(r.request().url()).searchParams.get('path') ?? 'src/thing.ts';
    return r.fulfill(json({ diff: diffBody(p), hasDiff: true, emptyReason: null }));
  });
}

/** Seed a persisted Project Hub tab pinned to the git rail, then reload. */
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
  await page.reload({ waitUntil: 'domcontentloaded', timeout: BOOT_TIMEOUT });
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
}

function resultsDir(): string {
  const fromEnv = process.env.GIT_VIEW_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-hub-git-view');
}

test.describe('Project Hub · Git View (mocked)', () => {
  test.setTimeout(180_000);

  test('opens from the Project Hub rail, browses the tree, and renders a commit diff', async ({ page }, testInfo) => {
    await installRoutes(page);
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });

    await openHubOnGit(page);

    // The Project Hub is open on the Git View rail.
    await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('project-shell-rail-git')).toBeVisible();

    const panel = page.getByTestId('project-git-panel');
    await expect(panel).toBeVisible({ timeout: 15_000 });

    // Repository path + branch/worktree/active groups are shown.
    await expect(page.getByTestId('git-repo-path')).toContainText(REPO_PATH);
    await expect(page.getByTestId('git-tree-group-active')).toContainText('remote');
    await expect(page.getByTestId('git-tree-group-active')).toContainText('local');
    await expect(page.getByTestId('git-tree-group-worktrees')).toBeVisible();
    await expect(page.getByTestId('git-tree-group-integration')).toBeVisible();
    await expect(page.getByTestId('git-tree-group-feature')).toBeVisible();
    await expect(page.getByTestId('git-tree-group-task')).toBeVisible();
    await expect(page.getByTestId('git-tree-group-runner')).toBeVisible();
    const firstCommit = page.getByTestId('git-commit-row').first();
    await expect(firstCommit).toContainText('Integrated · develop');
    await expect(firstCommit).toContainText('Deployed · backend');
    await expect(firstCommit).toContainText('Deployed · runner');
    await expect(firstCommit).toContainText('Deployed · frontend');
    await expect(firstCommit).toContainText('AGT-1');
    await expect(firstCommit).toContainText('In progress');
    await expect(firstCommit).toContainText('Remote');
    await expect(firstCommit).not.toContainText('origin/develop');
    await expect(firstCommit).not.toContainText('deploy:');
    await expect(firstCommit.locator('[data-tone="remote"]')).toHaveAttribute('title', /origin\/develop/);
    await expect(page.getByTestId('git-commit-row').nth(1)).toContainText('Released · main');
    await expect(page.getByTestId('git-graph-node').first()).toBeVisible();
    await expect(page.locator('[data-testid="git-graph-segment"][data-kind="merge"]')).toBeVisible();
    await expect(page.locator('[data-testid="git-graph-node"][data-lane="1"]')).toHaveCount(2);
    await expect(panel.getByText('Read only', { exact: true })).toHaveCount(2);
    const cardChip = page.getByRole('button', { name: 'Select commit attributed to card AGT-1: Task work' }).first();
    await expect(cardChip).toBeVisible();
    await page.evaluate(() => document.querySelectorAll<HTMLElement>('[data-testid="error-dialog-overlay"]')
      .forEach(overlay => overlay.remove()));
    await cardChip.click();
    await expect(page.getByTestId('git-commit-details')).toContainText(COMMIT_SHA);
    await page.evaluate(() => document.querySelectorAll<HTMLElement>('[data-testid="error-dialog-overlay"]')
      .forEach(overlay => overlay.remove()));
    await page.getByRole('button', { name: 'Close commit details' }).click();

    const firstSummary = firstCommit.getByTestId('git-commit-summary');
    await firstSummary.focus();
    await firstSummary.press('Enter');
    await expect(page.getByTestId('git-commit-details')).toContainText(COMMIT_SHA);
    await firstSummary.press('Enter');
    await expect(page.getByTestId('git-commit-details')).toHaveCount(0);

    // The header popover defines every member of the one commit-chip vocabulary.
    await page.getByTestId('git-chip-legend-toggle').click();
    const legend = page.getByTestId('git-chip-legend-popover');
    await expect(legend).toBeVisible();
    await expect(legend).toContainText('Integrated · develop');
    await expect(legend).toContainText('Released · main');
    await expect(legend).toContainText('Deployed · runner');
    await expect(legend).toContainText('AGT-N');
    await expect(legend).toContainText('In progress');
    await expect(legend).toContainText('Remote');
    await page.getByTestId('git-chip-legend-toggle').click();

    // Branch rows carry their category badge; main is the current branch.
    await expect(page.locator('[data-testid="git-branch-row"][data-branch="main"]')).toContainText('main');
    await expect(page.locator('[data-testid="git-branch-row"][data-branch="task/1"]')).toContainText('task');

    // Older history is bounded and fetched only on explicit demand.
    await page.getByTestId('git-history-load-more').click();
    await expect(page.getByTestId('git-history')).toContainText('older paged commit');

    const historyBox = await page.getByTestId('git-history').boundingBox();
    const lastRowBox = await page.getByTestId('git-commit-row').last().boundingBox();
    expect(historyBox).not.toBeNull();
    expect(lastRowBox).not.toBeNull();
    expect(historyBox!.y + historyBox!.height - (lastRowBox!.y + lastRowBox!.height)).toBeLessThan(3);

    // Inspect changes explicitly. The graph itself remains cheap and read-only.
    await page.getByRole('button', { name: 'Inspect changes in ccccccc' }).click();
    await expect(page.getByTestId('git-changes')).toContainText('Merge task/1 into develop');
    await expect(page.getByTestId('git-file-row').first()).toContainText('src/thing.ts');
    await expect(page.getByTestId('git-diff')).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('[data-testid="git-diff"] .d2h-file-name')).toContainText('thing.ts', { timeout: 15_000 });

    // Evidence screenshot (mocked API).
    await page.getByTestId('git-chip-legend-toggle').click();
    await expect(page.getByTestId('git-chip-legend-popover')).toBeVisible();
    fs.mkdirSync(resultsDir(), { recursive: true });
    const shotPath = path.join(resultsDir(), 'project-hub-git-view--mocked.png');
    await page.screenshot({ path: shotPath, fullPage: true });
    await testInfo.attach('project-hub-git-view--mocked.png', { path: shotPath, contentType: 'image/png' });

    // Selecting a branch only changes selection; it never exposes a git mutation.
    await page.locator('[data-testid="git-branch-row"][data-branch="task/1"]').click();
    await expect(page.locator('[data-testid="git-branch-row"][data-branch="task/1"]')).toHaveAttribute('aria-current', 'true');
    await expect(page.getByTestId('git-cleanup')).toHaveCount(0);
  });

  // AGT-2011: the Git View is a full-bleed tool surface — it must fill the whole
  // Project Hub panel (no shared max-width cap, no outer panel padding) so the
  // tree + diff use the entire viewport. Regression guard for the project-shell
  // `data-rail-key='git'` opt-in; without it the panel reverts to the padded,
  // 1280px-capped prose column that left the lower half of the view empty.
  test('git panel fills the whole hub panel (no max-width cap, no padding gap)', async ({ page }) => {
    // Wide viewport so the panel itself is comfortably wider than the shared
    // 1280px content cap — that is the only regime where the cap would bite.
    await page.setViewportSize({ width: 2000, height: 1000 });
    await installRoutes(page);
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
    await openHubOnGit(page);

    const panel = page.getByTestId('project-shell-panel-git');
    const gitPanel = page.getByTestId('project-git-panel');
    await expect(gitPanel).toBeVisible({ timeout: 15_000 });

    const panelBox = await panel.boundingBox();
    const gitBox = await gitPanel.boundingBox();
    expect(panelBox).not.toBeNull();
    expect(gitBox).not.toBeNull();

    // Sanity: the panel is wide enough that a 1280px cap would leave a visible gap.
    expect(panelBox!.width).toBeGreaterThan(1300);
    // Fills the full width: the panel drops its inline padding and the shared
    // 1280px content cap for this rail, so the git surface spans the panel edge
    // to edge (allow a few px for sub-pixel rounding).
    expect(gitBox!.width).toBeGreaterThan(panelBox!.width - 4);
    // Proof the cap is gone (a capped surface would pin at ≈1280).
    expect(gitBox!.width).toBeGreaterThan(1300);
    // Fills the full height: the panes reach the bottom instead of leaving the
    // lower half of the viewport empty.
    expect(gitBox!.height).toBeGreaterThan(panelBox!.height - 4);
  });

  for (const width of [1280, 1600, 1920]) {
    test(`responsive panes, sweep containment and commit details at ${width}px`, async ({ page }, testInfo) => {
      const phase = process.env['GIT_VIEW_EVIDENCE_PHASE'] === 'before' ? 'before' : 'after';
      await page.setViewportSize({ width, height: 900 });
      await installRoutes(page);
      await page.goto('/', { waitUntil: 'domcontentloaded', timeout: BOOT_TIMEOUT });
      await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
      await openHubOnGit(page);

      const panel = page.getByTestId('project-git-panel');
      await expect(panel).toBeVisible({ timeout: 15_000 });
      await page.evaluate(() => document.querySelector<HTMLElement>('[data-testid="branch-sweep-toggle"]')?.click());
      await expect(page.getByTestId('branch-sweep-delete')).toBeDisabled();

      const sweepBox = await page.getByTestId('branch-sweep').boundingBox();
      const deleteBox = await page.getByTestId('branch-sweep-delete').boundingBox();
      const reclaimBox = await page.getByTestId('branch-sweep-reclaim-all').boundingBox();
      expect(sweepBox && deleteBox && reclaimBox).toBeTruthy();
      if (phase === 'after') {
        expect(deleteBox!.x + deleteBox!.width).toBeLessThanOrEqual(sweepBox!.x + sweepBox!.width + 1);
        expect(reclaimBox!.x + reclaimBox!.width).toBeLessThanOrEqual(sweepBox!.x + sweepBox!.width + 1);
      }

      await page.evaluate(() => document.querySelectorAll<HTMLElement>('[data-testid="error-dialog-overlay"]')
        .forEach(overlay => overlay.remove()));
      fs.mkdirSync(resultsDir(), { recursive: true });
      if (phase === 'before') {
        const beforePath = path.join(resultsDir(), `project-git-responsive-${width}-before--mocked.png`);
        await page.screenshot({ path: beforePath, fullPage: true });
        await testInfo.attach(path.basename(beforePath), { path: beforePath, contentType: 'image/png' });
        return;
      }

      await page.evaluate(() => document.querySelector<HTMLElement>('[data-testid="git-commit-row"]')?.click());
      await expect(page.getByTestId('git-commit-details')).toContainText(COMMIT_SHA);
      await expect(page.getByTestId('project-git-tree-splitter')).toHaveAttribute('role', 'separator');
      await expect(page.getByTestId('project-git-inspector-splitter')).toHaveAttribute('role', 'separator');

      const historyBox = await page.getByTestId('git-history').boundingBox();
      const detailsBox = await page.getByTestId('git-commit-details').boundingBox();
      expect(historyBox && detailsBox).toBeTruthy();
      expect(historyBox!.x + historyBox!.width).toBeLessThanOrEqual(detailsBox!.x + 1);

      await page.getByTestId('branch-sweep-delete').scrollIntoViewIfNeeded();
      const shotPath = path.join(resultsDir(), `project-git-responsive-${width}-after--mocked.png`);
      await page.screenshot({ path: shotPath, fullPage: true });
      await testInfo.attach(path.basename(shotPath), { path: shotPath, contentType: 'image/png' });
    });
  }
});
