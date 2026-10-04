import { test, expect, Page } from '@playwright/test';
import * as path from 'path';

/**
 * Multi-commit task detail rendering.
 *
 * Tasks regularly produce more than one commit across iterations -
 * continue-mode follow-up, crash-recovery commit + repair, operator-
 * driven steers. The detail view must render the full commit chain,
 * not just the latest commit.
 *
 * <para>
 * Default view (this is the behaviour the spec pins): the combined diff
 * across <em>all</em> task commits. The chain strip carries an "All"
 * filter chip that is selected by default; the per-commit chips below it
 * let a reviewer narrow the view to a single commit. A lone-commit task
 * shows that commit directly and offers no filter (covered elsewhere).
 * </para>
 * <list type="bullet">
 *   <item>All three commits render in chronological order with
 *     timestamps and message previews;</item>
 *   <item>The aggregated "All commits" view is selected by default and
 *     its combined diff loads;</item>
 *   <item>Clicking a commit chip narrows to that commit; clicking "All"
 *     returns to the aggregate.</item>
 * </list>
 */

const PROJECT = 'fixture';
const WATCH_PATH = 'C:/fixtures/multi-commit-repo';
const JOB_ID = 'multi-commit-task';
const WORKTREE_JOB_ID = 'worktree-commit-menu-task';

interface CommitFixture {
  sha: string;
  shortSha: string;
  message: string;
  filesChanged: number;
  files: string[];
  at: string;
  runAttemptId?: string;
  supersededBySha?: string;
  supersededByAttempt?: string;
}

const COMMITS: CommitFixture[] = [
  {
    sha: '1111111111111111111111111111111111111111',
    shortSha: '1111111',
    message: 'feat: initial slice',
    filesChanged: 2,
    files: ['src/feature.ts', 'src/feature.spec.ts'],
    at: '2026-05-09T10:00:00Z'
  },
  {
    sha: '2222222222222222222222222222222222222222',
    shortSha: '2222222',
    message: 'fix: handle empty input edge case',
    filesChanged: 1,
    files: ['src/feature.ts'],
    at: '2026-05-10T10:30:00Z'
  },
  {
    sha: '3333333333333333333333333333333333333333',
    shortSha: '3333333',
    message: 'chore: update docs after operator steer',
    filesChanged: 3,
    files: ['src/feature.ts', 'README.md', 'CHANGELOG.md'],
    at: '2026-05-11T11:15:00Z'
  }
];

// Union of every file touched across the chain — what the aggregated
// "All commits" file list surfaces.
const AGGREGATE_FILES = [
  'src/feature.ts',
  'src/feature.spec.ts',
  'README.md',
  'CHANGELOG.md',
  'frontend/e2e/project/project-overview-dashboard.spec.ts',
  'frontend/src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.spec.ts',
  'frontend/src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.ts',
  'frontend/src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.html',
  'frontend/src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.scss',
  'frontend/src/app/features/project-detail/components/project-overview-urls/project-overview-urls.spec.ts',
  'frontend/src/app/features/project-detail/components/project-overview-urls/project-overview-urls.ts',
  'frontend/src/app/features/project-detail/components/project-overview-urls/project-overview-urls.html',
  'frontend/src/app/features/project-detail/components/project-overview-urls/project-overview-urls.scss',
  'frontend/src/app/features/project-detail/components/project-deployment-panel/project-deployment-panel.spec.ts',
  'frontend/src/app/features/project-detail/components/project-deployment-panel/project-deployment-panel.ts',
  'frontend/src/app/features/project-detail/components/project-deployment-panel/project-deployment-panel.html',
  'frontend/src/app/features/project-detail/components/project-deployment-panel/project-deployment-panel.scss',
  'frontend/src/app/features/project-detail/components/project-git-panel/project-git-panel.component.spec.ts',
  'frontend/src/app/features/project-detail/components/project-git-panel/project-git-panel.component.ts',
  'frontend/src/app/features/project-detail/components/project-git-panel/project-git-panel.component.html',
  'frontend/src/app/features/project-detail/components/project-git-panel/project-git-panel.component.scss',
  'frontend/src/app/features/project-detail/components/project-shell/project-shell.component.spec.ts',
  'frontend/src/app/features/project-detail/components/project-shell/project-shell.component.ts',
  'frontend/src/app/features/project-detail/components/project-shell/project-shell.component.html',
  'frontend/src/app/features/project-detail/components/project-shell/project-shell.component.scss',
];

// Graph-derived provenance (ASS-1724): the landed ladder shown above the
// commit group. Mocked so `git.provenance()` resolves to a well-formed
// view rather than the generic `[]` catch-all body, which the ladder
// template dereferences (`prov.ladder.branch`) and would otherwise crash on.
const PROVENANCE = {
  branch: 'task/multi-commit-task',
  base: 'develop',
  transitions: [],
  merge: null,
  landedState: 'merged-to-develop',
  ladder: {
    branch: 'task/multi-commit-task',
    branchTip: COMMITS[2].sha,
    integrationBranch: 'develop',
    integrationHead: 'abcdef1234567890abcdef1234567890abcdef12',
    mergedToIntegration: true,
    releaseBranch: 'main',
    releaseHead: 'fedcba0987654321fedcba0987654321fedcba09',
    releasedToRelease: false,
  },
  commits: [],
};

function makeDetail() {
  const newest = COMMITS[COMMITS.length - 1];
  return {
    info: {
      id: JOB_ID,
      jobKey: `${WATCH_PATH}::${JOB_ID}`,
      taskKey: `${WATCH_PATH}::${JOB_ID}`,
      title: 'Multi-commit fixture',
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
      ownerClientId: 'local-default'
    },
    promptMarkdown: 'Pretend prompt with three iterations.',
    statusMarkdown: '## Done\n\nThree commits across iterations.\n',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'finished', startedAt: null, finishedAt: null, errorMessage: null }
  };
}

async function installRoutes(page: Page) {
  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }));
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
  await page.route(/\/api\/(?:workspaces|v1\/workspaces(?:\?|$))/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/(?:projects|v1\/projects(?:\?|$))/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
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
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-05-29T00:00:00Z', snapshots: [] }) }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: null, activeExecution: null, queuedJobIds: [] } } })
    }));

  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail();

  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/claude-session(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: 'null' }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/hygiene(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projectName: PROJECT, isRepo: true, isDirty: false, hasUpstream: true, ahead: 0, behind: 0, job: { jobId: JOB_ID, state: '5-human-review', jobInfoCommitPresent: true, stampedCommitSha: COMMITS[2].sha, acceptedTaskUncommitted: false }, error: null }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/status(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isRepo: true, branch: 'main', filesChanged: 0, totalAdded: 0, totalRemoved: 0, files: [], error: null }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/provenance(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(PROVENANCE) }));
  // Code-review listing: the real endpoint returns `{ entries: [...] }`. Pin the
  // real shape here rather than leaning on the `**/api/**` catch-all's bare `[]`,
  // whose `.entries` is `Array.prototype.entries` (a function) and would slip
  // past the service's guard and crash the commit-row rating badge's computed.
  await page.route(new RegExp(`/api/tasks/${idEsc}/code-review/list(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ entries: [] }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/commit(\\?|$)`), (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        commit: COMMITS[2],
        files: COMMITS[2].files.map(p => ({ status: 'M', path: p, added: 4, removed: 1 }))
      })
    }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/commit/diff(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'text/plain', body: 'diff --git a/x b/x\n+++ b/x\n+latest commit diff' }));
  // Aggregate endpoints — the default view fetches the combined file list
  // and the combined diff across every task commit.
  await page.route(new RegExp(`/api/tasks/${idEsc}/commits/files(\\?|$)`), (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ files: AGGREGATE_FILES.map(p => ({ status: 'M', path: p, added: 6, removed: 2 })) })
    }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/commits/diff(\\?|$)`), (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ diff: `diff --git a/${AGGREGATE_FILES[0]} b/${AGGREGATE_FILES[0]}\n--- a/${AGGREGATE_FILES[0]}\n+++ b/${AGGREGATE_FILES[0]}\n@@ -1,3 +1,5 @@\n context line\n+aggregated across all commits\n-removed line` })
    }));
  for (const c of COMMITS) {
    await page.route(new RegExp(`/api/tasks/${idEsc}/commits/${c.sha}/files(\\?|$)`), (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ sha: c.sha, files: c.files.map(p => ({ status: 'M', path: p, added: 1, removed: 0 })) })
      }));
    await page.route(new RegExp(`/api/tasks/${idEsc}/commits/${c.sha}/diff(\\?|$)`), (route) =>
      route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({ diff: `diff --git a/${c.files[0]} b/${c.files[0]}\n+++ b/${c.files[0]}\n+${c.shortSha} change` })
      }));
  }
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }));
}

async function installMechanicallySupersededDetail(page: Page) {
  const idEsc = JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = makeDetail();
  detail.info.commits = [
    { ...COMMITS[0], runAttemptId: 'round-1', supersededBySha: COMMITS[1].sha },
    { ...COMMITS[1], runAttemptId: 'round-2' },
    { ...COMMITS[2], runAttemptId: 'round-2' },
  ];
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }));
}

async function installWorktreeRoutes(page: Page) {
  await page.route('**/api/**', (route) => {
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }).catch(() => undefined);
  });
  await page.route('**/api/v1/studio/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }),
    }));
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
  await page.route('**/api/environment**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }) }));
  await page.route('**/api/agent-rules', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/clients', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/api/cli/usage**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  await page.route('**/api/cli/quota**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-05-29T00:00:00Z', snapshots: [] }) }));
  await page.route('**/api/git/summary**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(/\/api\/git\/hygiene(\?|$)/, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ projects: { [PROJECT]: { projectName: PROJECT, mode: 'manual', activeJobId: WORKTREE_JOB_ID, activeExecution: null, queuedJobIds: [] } } })
    }));

  const idEsc = WORKTREE_JOB_ID.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const detail = {
    info: {
      id: WORKTREE_JOB_ID,
      jobKey: `${WATCH_PATH}::${WORKTREE_JOB_ID}`,
      taskKey: `${WATCH_PATH}::${WORKTREE_JOB_ID}`,
      title: 'Worktree commit menu fixture',
      state: '5-human-review',
      agent: 'codex',
      cliType: 'codex',
      model: 'gpt-5',
      watchPath: WATCH_PATH,
      projectName: PROJECT,
      folderPath: `${WATCH_PATH}/.orchestrator/jobs/5-human-review/${WORKTREE_JOB_ID}`,
      sessionName: null,
      lastUsage: null,
      execution: null,
      order: 1,
      commit: null,
      commits: [],
      ownerClientId: 'local-default'
    },
    promptMarkdown: 'Pretend active worktree prompt.',
    statusMarkdown: '## Review\n\nUncommitted worktree changes.\n',
    log: [],
    promptHistory: [],
    contextUsage: null,
    reviewEvidence: [],
    summaryState: { status: 'finished', startedAt: null, finishedAt: null, errorMessage: null }
  };

  await page.route(new RegExp(`/api/tasks/${idEsc}/output(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/runs(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/session-events(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ events: [], sessionChain: [] }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/hygiene(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projectName: PROJECT, isRepo: true, isDirty: true, hasUpstream: true, ahead: 0, behind: 0, job: null, error: null }) }));
  await page.route(new RegExp(`/api/tasks/${idEsc}/git/status(\\?|$)`), (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({
        isRepo: true,
        branch: 'feature/worktree-menu',
        filesChanged: 1,
        totalAdded: 8,
        totalRemoved: 2,
        files: [{ status: 'M', path: 'src/worktree.ts', added: 8, removed: 2 }],
        error: null
      })
    }));
  await page.route(new RegExp(`/api/v1/projects/[^/]+/tasks/${idEsc}(\\?|$)`), (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(detail) }));
}

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

async function expectTreeSplitContained(page: Page): Promise<void> {
  const tree = page.getByTestId('git-files');
  const treeCol = page.getByTestId('git-tree-col');
  const splitter = page.getByTestId('git-tree-splitter');
  const diffCol = page.getByTestId('git-diff-col');

  const [treeBox, treeColBox, splitterBox, diffColBox] = await Promise.all([
    tree.boundingBox(),
    treeCol.boundingBox(),
    splitter.boundingBox(),
    diffCol.boundingBox(),
  ]);
  expect(treeBox && treeColBox && splitterBox && diffColBox, 'split panes have measurable bounds').toBeTruthy();

  expect(treeBox!.x + treeBox!.width).toBeLessThanOrEqual(treeColBox!.x + treeColBox!.width + 1);
  expect(splitterBox!.x).toBeGreaterThanOrEqual(treeColBox!.x + treeColBox!.width);
  expect(diffColBox!.x).toBeGreaterThan(splitterBox!.x);
  expect(Math.abs(splitterBox!.y - treeColBox!.y), 'splitter starts flush with tree pane').toBeLessThanOrEqual(1);
  expect(
    Math.abs(splitterBox!.y + splitterBox!.height - (treeColBox!.y + treeColBox!.height)),
    'splitter ends flush with tree pane',
  ).toBeLessThanOrEqual(1);
  expect(
    Math.abs(splitterBox!.y + splitterBox!.height - (diffColBox!.y + diffColBox!.height)),
    'splitter ends flush with diff pane',
  ).toBeLessThanOrEqual(1);

  const clip = await tree.evaluate((element) => ({
    clientWidth: element.clientWidth,
    scrollWidth: element.scrollWidth,
    overflowX: getComputedStyle(element).overflowX,
  }));
  expect(clip.overflowX).toBe('hidden');
  expect(clip.scrollWidth).toBeLessThanOrEqual(clip.clientWidth + 1);

  const hintBoxes = await page.getByTestId('git-tree-dir-hint').evaluateAll((elements) =>
    elements.map((element) => {
      const rect = element.getBoundingClientRect();
      return { text: element.textContent?.trim() ?? '', right: rect.right };
    }),
  );
  expect(hintBoxes.some((hint) => hint.text === 'project/')).toBe(true);
  expect(hintBoxes.some((hint) => hint.text === 'project-overview-dashboard/')).toBe(true);
  for (const hint of hintBoxes) {
    expect(hint.right, `directory hint ${hint.text} stays inside the tree`).toBeLessThanOrEqual(treeColBox!.x + treeColBox!.width + 1);
  }

  const splitterGeometry = await splitter.evaluate((element) => ({
    visibleWidth: element.getBoundingClientRect().width,
    hitWidth: Number.parseFloat(getComputedStyle(element, '::before').width),
  }));
  expect(splitterGeometry.visibleWidth).toBeLessThanOrEqual(2);
  expect(splitterGeometry.hitWidth).toBeGreaterThanOrEqual(16);
}

async function saveTreePressureShot(page: Page, name: string): Promise<void> {
  if (!RESULTS_DIR) return;
  await page.getByTestId('pane-git').screenshot({ path: path.join(RESULTS_DIR, `${name}--mocked.png`) });
}

async function expectedCommitChainMetas(page: Page): Promise<string[]> {
  return page.evaluate((commits) =>
    commits.map((commit) => {
      // formatCompactDateTime always renders en-US; the browser default locale
      // follows the host OS and would expect `08.06. 10:00` on a de-DE machine.
      const date = new Date(commit.at);
      const day = date.toLocaleDateString('en-US', { month: '2-digit', day: '2-digit' });
      const time = date.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
      return `${commit.filesChanged}f · ${day} ${time}`;
    }),
    COMMITS
  );
}

test.describe('Task-detail multi-commit chain', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: false, protocol: false, git: true }));
        localStorage.setItem('taskboard.gitPane.commitGroupCollapsed', '1');
      } catch { /* private mode */ }
    });
  });

  test('defaults to a collapsed aggregated all-commits group', async ({ page }) => {
    await installRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });

    // The group is the single aggregate control: collapsed by default,
    // summarising the active "All commits" view without a second
    // "All commits" header below it.
    const groupToggle = page.getByTestId('git-commit-group-toggle');
    await expect(groupToggle).toBeVisible();
    await expect(groupToggle).toHaveAttribute('aria-expanded', 'false');
    await expect(groupToggle).toContainText('All 3 commits');
    await expect(page.getByTestId('git-commit-chain')).toHaveCount(0);
    await expect(page.getByTestId('git-commit-aggregate-header')).toHaveCount(0);

    // The combined diff still renders without expanding the group.
    await expect(page.getByTestId('git-diff')).toContainText('aggregated across all commits', { timeout: 5_000 });

    if (RESULTS_DIR) {
      const pane = page.getByTestId('pane-git');
      await pane.screenshot({ path: path.join(RESULTS_DIR, 'multi-commit-aggregate-collapsed.png') });
    }
  });

  test('renders all three commits in chronological order', async ({ page }) => {
    await installRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('git-commit-group-toggle').click();
    const chain = page.getByTestId('git-commit-chain');
    await expect(chain).toBeVisible();
    const items = page.getByTestId('git-commit-chain-item');
    await expect(items).toHaveCount(3);

    // Order: oldest first, newest last - the test fixture's at-times
    // are strictly increasing across the array.
    await expect(items.nth(0)).toHaveAttribute('data-sha', COMMITS[0].sha);
    await expect(items.nth(1)).toHaveAttribute('data-sha', COMMITS[1].sha);
    await expect(items.nth(2)).toHaveAttribute('data-sha', COMMITS[2].sha);

    await expect(items.nth(0)).toContainText(COMMITS[0].shortSha);
    await expect(items.nth(1)).toContainText(COMMITS[1].shortSha);
    await expect(items.nth(2)).toContainText(COMMITS[2].shortSha);
    const expectedMetas = await expectedCommitChainMetas(page);
    const metas = page.getByTestId('git-commit-chain-meta');
    await expect(metas.nth(0)).toHaveText(expectedMetas[0]);
    await expect(metas.nth(1)).toHaveText(expectedMetas[1]);
    await expect(metas.nth(2)).toHaveText(expectedMetas[2]);
    expect(new Set(expectedMetas.map(meta => meta.split(' · ')[1].split(' ')[0])).size).toBe(3);
    // Subjects render alongside the SHA.
    await expect(items.nth(0)).toContainText('feat: initial slice');
    await expect(items.nth(2)).toContainText('chore: update docs');
  });

  test('clicking a commit narrows to it, then "All" returns to the aggregate', async ({ page }) => {
    await installRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });

    // Some test runs leave the board filter sidesheet auto-opened
    // depending on URL state; close it so it cannot intercept the
    // click on the chain chip below.
    const filterClose = page.locator('[data-testid="kanban-filter-sidesheet-close"], dialog [aria-label="Close filter panel"]').first();
    if (await filterClose.isVisible().catch(() => false)) {
      await filterClose.click().catch(() => undefined);
    }

    const items = page.getByTestId('git-commit-chain-item');
    await page.getByTestId('git-commit-group-toggle').click();
    await expect(items).toHaveCount(3);

    // Confirm the default selection is the aggregate before clicking.
    await expect(page.getByTestId('git-commit-chain-all').getByRole('button')).toHaveAttribute('aria-pressed', 'true');

    // Pick the first (oldest) commit. The detail header swaps to its short
    // SHA + message, the chip moves to the selected state, and the
    // aggregate chip deselects. Click programmatically — the Angular event
    // listener fires either way, and this sidesteps the kanban-filter
    // sidesheet that may overlay the page on URL boot.
    await page.evaluate((sha) => {
      const buttons = Array.from(document.querySelectorAll<HTMLElement>('[data-testid="git-commit-chain-item"]'));
      const target = buttons.find(li => li.getAttribute('data-sha') === sha)?.querySelector('button') as HTMLButtonElement | null;
      target?.click();
    }, COMMITS[0].sha);

    await expect(items.nth(0).getByRole('button')).toHaveAttribute('aria-pressed', 'true', { timeout: 5_000 });
    await expect(page.getByTestId('git-commit-chain-all').getByRole('button')).toHaveAttribute('aria-pressed', 'false');
    await expect(page.getByTestId('git-commit-sha')).toContainText(COMMITS[0].shortSha);
    await expect(page.getByTestId('git-commit-message')).toContainText('feat: initial slice');

    // Click "All" to return to the aggregated view.
    await page.evaluate(() => {
      const btn = document.querySelector<HTMLButtonElement>('[data-testid="git-commit-chain-all"] button');
      btn?.click();
    });
    await expect(page.getByTestId('git-commit-chain-all').getByRole('button')).toHaveAttribute('aria-pressed', 'true', { timeout: 5_000 });
    await expect(page.getByTestId('git-commit-aggregate-header')).toHaveCount(0);
    await expect(page.getByTestId('git-commit-group-toggle')).toContainText('All 3 commits');
  });

  test('pane title surfaces the multi-commit count', async ({ page }) => {
    await installRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });

    // The pane title for a multi-commit task reads "3 task commits"
    // rather than the singular "Task commit" so reviewers see the
    // chain length without scanning the strip.
    await expect(page.locator('[data-testid="pane-git"] .pane__title')).toContainText('3 task commits');
  });

  for (const theme of ['light', 'dark'] as const) {
    test(`shows mechanically superseded delivery rounds separately in ${theme} theme`, async ({ page }) => {
      await page.addInitScript((selectedTheme) => {
        localStorage.setItem('atp.studio.theme', selectedTheme);
      }, theme);
      await installRoutes(page);
      await installMechanicallySupersededDetail(page);
      await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
      await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });

      await page.getByTestId('git-commit-group-toggle').click();
      await expect(page.getByTestId('git-commit-chain-item')).toHaveCount(2);
      await expect(page.getByTestId('git-superseded-rounds')).toBeVisible();
      await page.getByTestId('git-superseded-rounds').evaluate((details: HTMLDetailsElement) => {
        details.open = true;
      });
      await expect(page.getByTestId('git-superseded-round')).toContainText('Round 1, mechanically replaced by SHA 222222222');
      await expect(page.getByTestId('git-superseded-commit')).toHaveAttribute('data-sha', COMMITS[0].sha);

      if (RESULTS_DIR) {
        await page.getByTestId('pane-git').screenshot({
          path: path.join(RESULTS_DIR, `mechanically-superseded-delivery-round-${theme}--mocked.png`),
        });
      }
    });
  }

  test('diff layout toggle switches side-by-side <-> unified and persists the choice', async ({ page }) => {
    await installRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });

    // Default layout is side-by-side (pressed). The combined diff must be
    // rendered for the toolbar toggle to be present.
    await expect(page.getByTestId('git-diff')).toContainText('aggregated across all commits', { timeout: 5_000 });
    const toggle = page.getByTestId('git-diff-mode-toggle');
    await expect(toggle).toBeVisible();
    await expect(toggle).toHaveAttribute('aria-pressed', 'true');
    await expect(toggle).toHaveText('Side-by-side');

    // Flip to unified/inline; the label + pressed state follow and the
    // choice is written to localStorage.
    await toggle.click();
    await expect(toggle).toHaveText('Unified');
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    // diff2html re-renders line-by-line (no side-by-side split columns).
    await expect(page.locator('[data-testid="git-diff"] .d2h-file-side-diff')).toHaveCount(0);
    const stored = await page.evaluate(() => localStorage.getItem('taskboard.gitPane.diffViewMode'));
    expect(stored).toBe('line-by-line');

    if (RESULTS_DIR) {
      await page.getByTestId('pane-git').screenshot({ path: path.join(RESULTS_DIR, 'diff-toggle-unified--mocked.png') });
    }
  });

  test('commit-meta head collapses to a compact strip and back', async ({ page }) => {
    await installRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });

    const head = page.getByTestId('git-head-collapse-toggle');
    await expect(head).toBeVisible();
    await expect(head).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('git-commit-group')).toBeVisible();

    await head.click();
    await expect(head).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByTestId('git-commit-group')).toHaveCount(0);
    await expect(page.getByTestId('git-head-summary')).toBeVisible();
    const stored = await page.evaluate(() => localStorage.getItem('taskboard.gitPane.headCollapsed'));
    expect(stored).toBe('1');

    if (RESULTS_DIR) {
      await page.getByTestId('pane-git').screenshot({ path: path.join(RESULTS_DIR, 'commit-head-collapsed--mocked.png') });
    }
  });

  test('tree|diff splitter resizes the tree in the maximized split layout and persists', async ({ page }) => {
    await installRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('git-diff')).toContainText('aggregated across all commits', { timeout: 5_000 });

    // The splitter is a split-layout affordance: maximize the pane so the
    // tree sits left of the diff and the divider becomes draggable.
    await page.getByTestId('pane-maximize-git').click();
    const splitter = page.getByTestId('git-tree-splitter');
    await expect(splitter).toBeVisible();

    const treeCol = page.getByTestId('git-tree-col');
    const before = (await treeCol.boundingBox())!.width;
    const box = (await splitter.boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + 140, box.y + box.height / 2, { steps: 8 });
    await page.mouse.up();

    const after = (await treeCol.boundingBox())!.width;
    expect(after).toBeGreaterThan(before + 40);
    const stored = await page.evaluate(() => Number(localStorage.getItem('taskboard.gitPane.treeWidth')));
    expect(stored).toBeGreaterThan(before);

    if (RESULTS_DIR) {
      await page.getByTestId('git-view-body').screenshot({ path: path.join(RESULTS_DIR, 'tree-splitter-resized--mocked.png') });
    }
  });

  for (const theme of ['light', 'dark'] as const) {
    test(`long tree stays clipped under width pressure and wide layout remains stable in ${theme} theme`, async ({ page }) => {
      await page.setViewportSize({ width: 1440, height: 900 });
      await page.addInitScript((selectedTheme) => {
        try {
          localStorage.setItem('atp.studio.theme', selectedTheme);
          localStorage.setItem('taskboard.gitPane.treeWidth', '300');
        } catch { /* private mode */ }
      }, theme);
      await installRoutes(page);
      await page.goto(`/?job=${encodeURIComponent(JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
      await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });
      await expect(page.getByTestId('git-diff')).toContainText('aggregated across all commits', { timeout: 5_000 });
      await page.getByTestId('pane-maximize-git').click();

      const treeCol = page.getByTestId('git-tree-col');
      const wideTreeWidth = (await treeCol.boundingBox())!.width;
      const wideDiffWidth = (await page.getByTestId('git-diff-col').boundingBox())!.width;
      expect(wideTreeWidth).toBeGreaterThan(280);
      expect(wideDiffWidth).toBeGreaterThan(wideTreeWidth);
      await expectTreeSplitContained(page);
      await saveTreePressureShot(page, `git-tree-split-wide-${theme}`);

      await page.setViewportSize({ width: 760, height: 720 });
      await expect.poll(async () => (await page.getByTestId('git-view-body').boundingBox())?.width)
        .toBeLessThan(600);

      const splitter = page.getByTestId('git-tree-splitter');
      const splitterBox = (await splitter.boundingBox())!;
      const startX = splitterBox.x + 3;
      const startY = splitterBox.y + splitterBox.height / 2;
      const hitTarget = await page.evaluate(({ x, y }) => {
        const element = document.elementFromPoint(x, y);
        return {
          tag: element?.tagName ?? null,
          testId: element?.closest('[data-testid]')?.getAttribute('data-testid') ?? null,
        };
      }, { x: startX, y: startY });
      expect(hitTarget.testId, `expanded hit area belongs to the splitter (${hitTarget.tag})`).toBe('git-tree-splitter');

      const widthBeforeDrag = (await treeCol.boundingBox())!.width;
      await page.mouse.move(startX, startY);
      await page.mouse.down();
      await page.mouse.move(startX - 80, startY, { steps: 8 });
      await page.mouse.up();
      const widthAfterDrag = (await treeCol.boundingBox())!.width;
      // At this pressure width the proportional tree/diff floors leave only
      // a few pixels of legal travel. The drag must still reach that clamp
      // instead of being lost behind either pane.
      expect(widthAfterDrag).toBeLessThan(widthBeforeDrag - 2);

      await expectTreeSplitContained(page);
      const tree = page.getByTestId('git-files');
      await tree.evaluate((element) => { element.scrollTop = element.scrollHeight; });
      await expect.poll(() => tree.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
      await expect(page.getByTestId('git-diff')).toContainText('aggregated across all commits');
      await expectTreeSplitContained(page);
      await saveTreePressureShot(page, `git-tree-split-narrow-scrolled-${theme}`);
    });
  }

  test('studio overflow menu exposes worktree commit actions', async ({ page }) => {
    await installWorktreeRoutes(page);
    await page.goto(`/?job=${encodeURIComponent(WORKTREE_JOB_ID)}&watchPath=${encodeURIComponent(WATCH_PATH)}`);
    await expect(page.getByTestId('pane-git')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('git-files-count')).toHaveText('1 files', { timeout: 10_000 });

    await page.getByTestId('studio-triage-overflow-btn').click();
    const menu = page.getByTestId('studio-triage-overflow-panel');
    await expect(menu).toBeVisible();
    await expect(page.getByTestId('studio-triage-overflow-item-generate-commit-message')).toHaveText('Generate Commit Message');
    await expect(page.getByTestId('studio-triage-overflow-item-add-commit')).toContainText('Add Commit...');

    if (RESULTS_DIR) {
      await menu.screenshot({ path: path.join(RESULTS_DIR, 'studio-overflow-commit-actions.png') });
    }
  });
});
