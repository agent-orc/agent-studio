import { test, expect, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * AGT-2794 stale-branch sweep panel in Project Hub - Git.
 *
 * Every backend route is mocked, so this spec needs only the frontend dev
 * server. It covers the operator contract the unit tests cannot see rendered:
 * the panel opens on the stored report, only policy-eligible refs can be
 * ticked, the confirm step is explicit, "reclaim all allowed" walks the
 * eligible set in batches of at most 100 refs per request, and the stop button
 * ends the walk between batches. Screenshots are written for both themes.
 */

const PROJECT = 'demo-project';
const REPO_PATH = '/repo/demo-project';
const HUB_TAB_KEY = `hub:${PROJECT}`;
const BOOT_TIMEOUT = 60_000;

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
  failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
  humanReview: [], completed: [], archive: [],
};

const INVENTORY = {
  projectName: PROJECT,
  repositoryPath: REPO_PATH,
  isRepo: true,
  currentBranch: 'main',
  worktrees: [{ path: REPO_PATH, branch: 'main', headSha: 'a'.repeat(40), headShortSha: 'aaaaaaa', isPrimary: true, isDetached: false, isBare: false }],
  branches: [{ name: 'main', category: 'main', tipSha: 'a'.repeat(40), tipShortSha: 'aaaaaaa', isCurrent: true, upstream: 'origin/main', ahead: 0, behind: 0, lastCommitSubject: 'seed', lastCommitAtUtc: '2026-09-01T00:00:00Z', worktreePath: REPO_PATH }],
  recentCommits: [],
  history: { offset: 0, pageSize: 50, nextOffset: null, hasMore: false, commits: [] },
  activeCheckouts: [],
  error: null,
};

interface Candidate {
  ref: string;
  class: string;
  taskKey: string | null;
  taskState: string | null;
  tipSha: string;
  tipShortSha: string;
  tipCommittedAtUtc: string;
  ageDays: number;
  containedInMain: boolean;
  containedInDevelop: boolean;
  referencedByOpenCard: boolean;
  decision: string;
  eligible: boolean;
  reason: string;
}

function candidate(index: number, eligible: boolean, overrides: Partial<Candidate> = {}): Candidate {
  const sha = index.toString(16).padStart(40, '0');
  return {
    ref: `task/DEM-${index}`,
    class: 'task',
    taskKey: `DEM-${index}`,
    taskState: eligible ? '7-archive' : '3-progress',
    tipSha: sha,
    tipShortSha: sha.slice(0, 7),
    tipCommittedAtUtc: '2025-06-01T00:00:00Z',
    ageDays: 400,
    containedInMain: eligible,
    containedInDevelop: eligible,
    referencedByOpenCard: false,
    decision: eligible ? 'Delete' : 'NotMergedIntoDevelop',
    eligible,
    reason: eligible
      ? 'Eligible for deletion; deletion policy met.'
      : 'Tip is not contained in integration branch.',
    ...overrides,
  };
}

/**
 * Two refs the policy keeps followed by 250 eligible ones (three batches), in
 * the ordinal ref order the backend reports.
 */
const CANDIDATES: Candidate[] = [
  candidate(901, false, {
    ref: 'main', class: 'protected', taskKey: null, taskState: null,
    decision: 'UnsupportedNamespace', reason: 'Branch is outside managed namespaces.',
  }),
  candidate(900, false),
  ...Array.from({ length: 250 }, (_, index) => candidate(index + 1, true)),
];

function report(mode: 'report-only' | 'reclaim' = 'report-only') {
  const eligible = CANDIDATES.filter(c => c.eligible).length;
  return {
    project: PROJECT,
    repositoryPath: REPO_PATH,
    mode,
    startedAtUtc: '2026-09-14T12:00:00Z',
    completedAtUtc: '2026-09-14T12:00:07Z',
    windows: { taskDays: 7, salvageDays: 14, quarantineDays: 30, abandonedDays: 90 },
    refsBefore: CANDIDATES.length,
    refsAfter: CANDIDATES.length,
    totals: [
      { class: 'task', total: CANDIDATES.length - 1, eligible, kept: CANDIDATES.length - 1, deleted: 0 },
      { class: 'protected', total: 1, eligible: 0, kept: 1, deleted: 0 },
    ],
    ageHistogram: [
      { label: '30-89 days', refs: 2 },
      { label: '365+ days', refs: CANDIDATES.length - 2 },
    ],
    candidates: CANDIDATES,
    deletions: [],
    error: null,
  };
}

/**
 * Holds every execute response until the test releases it, so the walk can be
 * observed one batch at a time instead of racing a sleep.
 */
class ExecuteGate {
  readonly sizes: number[] = [];
  private readonly pending: (() => void)[] = [];

  hold(size: number): Promise<void> {
    this.sizes.push(size);
    return new Promise<void>(resolve => this.pending.push(resolve));
  }

  releaseAll(): void {
    while (this.pending.length) this.pending.shift()!();
  }
}

/** Installs every backend route the shell and the sweep panel touch. */
async function installRoutes(page: Page, gate: ExecuteGate): Promise<void> {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

  await page.route('**/api/**', r => r.fulfill(json([])).catch(() => { /* late */ }));
  await page.route('**/api/auth/status', r => r.fulfill(json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null })));
  await page.route('**/api/runner/orchestrator-feed**', r => r.fulfill(json({ entries: [], generatedAtUtc: '2026-09-14T12:00:00Z' })));
  await page.route(/\/api\/(?:jobs|tasks)\/grouped/, r => r.fulfill(json(EMPTY_GROUPED)));
  await page.route(/\/api\/(?:jobs|tasks)(\?|$)/, r => r.fulfill(json([])));
  await page.route('**/api/watch-paths**', r => r.fulfill(json([{ name: PROJECT, path: REPO_PATH, rootPath: REPO_PATH, repositoryPath: REPO_PATH }])));
  await page.route('**/api/environment**', r => r.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } })));
  await page.route(/\/api\/runner\/status(\?|$)/, r => r.fulfill(json({ projects: {} })));
  await page.route('**/api/clients', r => r.fulfill(json([])));
  await page.route('**/api/git/inventory**', r => r.fulfill(json(INVENTORY)));
  await page.route('**/api/git/branch-sweep/settings**', r => r.fulfill(json({
    project: PROJECT, mode: 'report-only',
    windows: { taskDays: 7, salvageDays: 14, quarantineDays: 30, abandonedDays: 90 },
    defaults: { taskDays: 7, salvageDays: 14, quarantineDays: 30, abandonedDays: 90 },
  })));
  await page.route('**/api/git/branch-sweep/latest**', r => r.fulfill(json(report())));
  await page.route('**/api/git/branch-sweep/plan**', r => r.fulfill(json(report())));
  await page.route('**/api/git/branch-sweep/execute**', async r => {
    const body = r.request().postDataJSON() as { items: unknown[] };
    await gate.hold(body.items.length);
    return r.fulfill(json({
      project: PROJECT, isRepo: true, deletedCount: body.items.length, keptCount: 0, actions: [], error: null,
    }));
  });
}

/**
 * The catch-all `[]` mock makes some unrelated boot call fail, which raises the
 * app's error dialog. Its overlay would intercept every click in this spec, so
 * strip it continuously - it is a fixture artifact, not the surface under test.
 */
async function suppressErrorDialog(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const kill = () => document
      .querySelectorAll('[data-testid="error-dialog-overlay"]')
      .forEach(el => el.remove());
    const start = () => {
      kill();
      new MutationObserver(kill).observe(document.documentElement, { childList: true, subtree: true });
    };
    if (document.documentElement) start();
    else document.addEventListener('DOMContentLoaded', start);
  });
}

async function openHubOnGit(page: Page): Promise<void> {
  await page.evaluate(({ tabKey, project }) => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName: project, section: 'git' }],
      activeKey: tabKey,
    }));
    history.replaceState(null, '', '/');
  }, { tabKey: HUB_TAB_KEY, project: PROJECT });
  await page.reload();
  await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
  await expect(page.getByTestId('project-git-panel')).toBeVisible({ timeout: 20_000 });
}

async function setTheme(page: Page, theme: 'dark' | 'light'): Promise<void> {
  await page.evaluate(t => {
    document.documentElement.dataset['studioTheme'] = t;
    try { localStorage.setItem('atp.studio.theme', t); } catch { /* private mode */ }
  }, theme);
  await page.waitForTimeout(120);
}

function shotDir(): string {
  const fromEnv = process.env.JOB_RESULTS_DIR?.trim();
  if (fromEnv) return path.join(fromEnv, 'screenshots');
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'agt-2794');
}

async function save(page: Page, testInfo: import('@playwright/test').TestInfo, name: string): Promise<void> {
  const dir = shotDir();
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${name}--mocked.png`);
  await page.screenshot({ path: file, fullPage: true });
  await testInfo.attach(`${name}--mocked.png`, { path: file, contentType: 'image/png' });
}

test.describe('AGT-2794 stale-branch sweep panel (mocked)', () => {
  test.use({ viewport: { width: 1680, height: 1050 } });
  test.setTimeout(180_000);

  test('opens on the stored report and only offers policy-eligible refs', async ({ page }, testInfo) => {
    await suppressErrorDialog(page);
    await installRoutes(page, new ExecuteGate());
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
    await openHubOnGit(page);

    const panel = page.getByTestId('branch-sweep');
    await expect(panel).toBeVisible();
    await panel.getByTestId('branch-sweep-toggle').click();

    await expect(panel.getByTestId('branch-sweep-mode')).toHaveText('report-only');
    await expect(panel.getByTestId('branch-sweep-totals')).toContainText('protected');
    await expect(panel.getByTestId('branch-sweep-histogram')).toContainText('365+ days');

    // Nothing is pre-selected and the destructive action stays disabled until
    // the operator ticks something.
    await expect(panel.getByTestId('branch-sweep-delete')).toBeDisabled();
    await expect(panel.getByTestId('branch-sweep-reclaim-all')).toContainText('(250)');

    // Only eligible refs are listed by default, and the kept ones are not
    // selectable when the full list is shown.
    await expect(panel.locator('[data-testid="branch-sweep-candidates"] li')).toHaveCount(200);
    await panel.getByRole('button', { name: 'Show every ref' }).click();
    await expect(panel.getByRole('checkbox', { name: 'Select main' })).toBeDisabled();

    // R5: the surface has to hold in both themes, so both are captured.
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
      await save(page, testInfo, `branch-sweep-panel--${theme}`);
    }
  });

  test('reclaim all allowed walks the eligible set in batches and stops on request', async ({ page }, testInfo) => {
    const gate = new ExecuteGate();
    await suppressErrorDialog(page);
    await installRoutes(page, gate);
    await page.goto('/');
    await expect(page.getByTestId('app-root')).toBeVisible({ timeout: BOOT_TIMEOUT });
    await openHubOnGit(page);

    const panel = page.getByTestId('branch-sweep');
    await panel.getByTestId('branch-sweep-toggle').click();
    await expect(panel.getByTestId('branch-sweep-reclaim-all')).toBeEnabled();
    await panel.getByTestId('branch-sweep-reclaim-all').click();

    // 250 eligible refs become three requests of at most 100 refs each; the
    // first is in flight and held open while the panel shows its progress.
    const progress = panel.getByTestId('branch-sweep-progress');
    await expect(progress).toContainText('batch 1/3');
    await expect(progress).toContainText('0/250 refs');
    expect(gate.sizes).toEqual([100]);
    await setTheme(page, 'dark');
    await save(page, testInfo, 'branch-sweep-progress--dark');

    // Stop is honoured between batches: batch 1 finishes, batch 2 never starts.
    await panel.getByTestId('branch-sweep-stop').click();
    gate.releaseAll();
    await expect(progress).toContainText('stopped', { timeout: 20_000 });
    await expect(progress).toContainText('100/250 refs');
    expect(gate.sizes).toEqual([100]);
  });
});
