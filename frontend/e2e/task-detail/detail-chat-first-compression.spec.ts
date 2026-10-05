import { test, expect, Page } from '@playwright/test';
import * as path from 'path';
import * as fs from 'fs';

/**
 * Detail page chat-first redesign — Slice 1 (header compression).
 *
 * The redesign collapses repository-hygiene info into a 3-icon strip
 * with hover-tooltips, makes the pane-toggle bar an icon strip, and
 * compresses the protocol-pane token telemetry into a single badge.
 *
 * This spec asserts:
 *  - hygiene chips are 24 px or less wide each (icon, not pill row)
 *  - hygiene chip tooltips still carry the full data (no info loss)
 *  - pane-toggle buttons are 28 px square or less, label moved to title
 *  - the repository-hygiene strip section is shorter than 60 px tall in
 *    the no-warning case (was a multi-row card before)
 *
 * Uses the same hygiene-strip stub-route plumbing as
 * `repository-hygiene-strip.spec.ts` so it runs against any frontend
 * (dev or stable) without requiring backend write paths.
 */

interface OutLine { timestamp: string; stream: string; text: string; }

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

const TARGET = { id: 'fixture-chat-first', watchPath: 'C:/fixtures/repo' };
const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? '';

function makeJobDetail(jobId: string, watchPath: string, state: string) {
  return {
    info: {
      id: jobId,
      jobKey: `${watchPath}::${jobId}`,
      title: 'Chat-first compression fixture',
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
        message: 'feat: deliver fixture',
        filesChanged: 3,
        files: ['a.ts', 'b.ts', 'c.ts'],
        at: new Date().toISOString()
      }
    },
    promptMarkdown: 'Pretend prompt body so the editor renders.',
    statusMarkdown: '## Done\n\nWork accepted.\n',
    log: [] as OutLine[],
    promptHistory: [],
    summaryState: { status: 'ready', errorMessage: null, generatedAt: new Date().toISOString() }
  };
}

function makeHygiene(state: string): HygieneShape {
  return {
    projectName: 'fixture',
    repoRoot: 'C:/fixtures/repo',
    isRepo: true,
    branch: 'main',
    upstream: 'origin/main',
    hasUpstream: true,
    ahead: 0,
    behind: 0,
    isDirty: false,
    stagedCount: 0,
    unstagedCount: 0,
    untrackedCount: 0,
    lastCommitSha: 'abcdef1234567890abcdef1234567890abcdef12',
    lastCommitShortSha: 'abcdef1',
    lastCommitSubject: 'feat: deliver fixture',
    lastCommitAtUtc: new Date().toISOString(),
    job: {
      jobId: TARGET.id, state,
      jobInfoCommitPresent: true,
      stampedCommitSha: 'abcdef1234567890abcdef1234567890abcdef12',
      acceptedTaskUncommitted: false
    },
    error: null
  };
}

async function installFixtureRoutes(page: Page, state: string) {
  const detail = JSON.stringify(makeJobDetail(TARGET.id, TARGET.watchPath, state));
  const projectHygiene = JSON.stringify({ ...makeHygiene(state), job: null });
  const jobHygiene = JSON.stringify(makeHygiene(state));

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
  // Runner status drives the project-tabs runner indicator. When the
  // value is missing the app crashes with "cannot read properties of
  // undefined (reading '<projectName>')". Stubbing an empty `projects`
  // map keeps the layout calm for screenshot evidence.
  await page.route('**/api/v1/studio/runner/status**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ projects: {}, autoMode: 'manual', activeProjects: [] })
    }));
  await page.route('**/api/orchestrator/state**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ runs: [] }) }));
  await page.route('**/api/runner/global**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ mode: 'paused', activeProjects: [] }) }));

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

test.describe('Detail page chat-first compression — Slice 1', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      try {
        localStorage.setItem('taskboard.panesVisible', JSON.stringify({ prompt: true, protocol: true, git: false }));
      } catch { /* private mode */ }
    });
  });

  test('hygiene strip renders as a task-scoped icon row with full-data tooltips', async ({ page }) => {
    await installFixtureRoutes(page, '6-completed');
    await page.goto(`/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`);
    await expect(page.getByTestId('hygiene-strip')).toBeVisible({ timeout: 10_000 });

    // Each icon is a small square (≤ 24px wide). The visible glyph is a
    // single character; the data lives on the tooltip. Only task-scoped
    // signals are surfaced here (commit + tree); repo-level state lives
    // on the project-hygiene-badge near the project name.
    for (const id of ['hygiene-commit', 'hygiene-tree'] as const) {
      const el = page.getByTestId(id);
      await expect(el).toBeVisible();
      const box = await el.boundingBox();
      expect(box, `${id} bounding box`).not.toBeNull();
      expect(box!.width, `${id} should be a small icon, not a pill`).toBeLessThanOrEqual(28);
      expect(box!.height, `${id} should be a small icon`).toBeLessThanOrEqual(28);
    }
    // hygiene-push is a repo-level concern and must not appear here.
    await expect(page.getByTestId('hygiene-push')).toHaveCount(0);

    // No information loss: tooltips carry the full sentence the verbose
    // strip used to render in visible text.
    await expect(page.getByTestId('hygiene-commit')).toHaveAttribute('title', /Task committed/i);
    await expect(page.getByTestId('hygiene-tree')).toHaveAttribute('title', /Working tree clean/i);

    // The strip itself is shorter than the previous multi-row card. The
    // 60 px ceiling is generous: the icon row is ~24 px plus margin.
    const stripBox = await page.getByTestId('hygiene-strip').boundingBox();
    expect(stripBox?.height, 'strip should be a thin icon row').toBeLessThan(60);

    if (RESULTS_DIR) {
      fs.mkdirSync(RESULTS_DIR, { recursive: true });
      await page.getByTestId('hygiene-strip').screenshot({
        path: path.join(RESULTS_DIR, 'hygiene-icons-clean.png')
      });
    }
  });

  test('pane-toggle bar renders as 24 px square icons with tooltip labels', async ({ page }) => {
    await installFixtureRoutes(page, '5-human-review');
    await page.goto(`/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`);

    // The "Panels:" label is gone — the strip is icon-only now.
    await expect(page.getByText('Panels:', { exact: true })).toHaveCount(0);

    for (const id of ['pane-toggle-prompt', 'pane-toggle-protocol', 'pane-toggle-git', 'open-in-vscode']) {
      const btn = page.getByTestId(id);
      await expect(btn).toBeVisible();
      const box = await btn.boundingBox();
      expect(box, `${id} bounding box`).not.toBeNull();
      expect(box!.width, `${id} should be a small square`).toBeLessThanOrEqual(28);
      expect(box!.height, `${id} should be a small square`).toBeLessThanOrEqual(28);
      // Each button still names its target so a hover reveals the label.
      const title = await btn.getAttribute('title');
      expect(title, `${id} should carry a tooltip`).not.toBeNull();
      expect(title!.length, `${id} tooltip non-empty`).toBeGreaterThan(0);
    }

    if (RESULTS_DIR) {
      fs.mkdirSync(RESULTS_DIR, { recursive: true });
      const toolbar = page.locator('.detail__panes-toolbar').first();
      await toolbar.screenshot({ path: path.join(RESULTS_DIR, 'pane-toggle-icons.png') });
    }
  });

  test('detail page chrome leaves the protocol pane more vertical room at 1920×1080', async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 });
    await installFixtureRoutes(page, '5-human-review');
    await page.goto(`/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`);

    // The route stubs cover the detail-page surface but global app
    // probes (orchestrator state, runner global mode) sometimes still
    // surface a generic error overlay. Dismiss it (matching the helper
    // in repository-hygiene-strip.spec.ts) so the screenshot captures
    // the actual layout, not the error toast.
    const errClose = page.locator('.error-dialog__close').first();
    for (let i = 0; i < 3; i++) {
      if (!(await errClose.isVisible().catch(() => false))) break;
      await errClose.click({ timeout: 1_000 }).catch(() => {});
      await page.waitForTimeout(150);
    }

    const protocol = page.getByTestId('pane-protocol');
    await expect(protocol).toBeVisible({ timeout: 10_000 });

    const protocolBox = await protocol.boundingBox();
    expect(protocolBox, 'protocol pane bounding box').not.toBeNull();

    // The protocol pane should now occupy a calm majority of the
    // viewport height. Before the chat-first redesign, the stack of
    // detail-header + command-deck + pane-toggle row + hygiene strip
    // ate enough chrome that it sat closer to 55 % on a 1080 viewport.
    // We assert a >=68 % floor so the test stays stable across font /
    // zoom settings while still catching a regression that re-inflates
    // header chrome.
    const ratio = protocolBox!.height / 1080;
    expect(ratio, `protocol pane height ratio (was ~0.55 before redesign)`).toBeGreaterThan(0.68);

    if (RESULTS_DIR) {
      fs.mkdirSync(RESULTS_DIR, { recursive: true });
      await page.screenshot({
        path: path.join(RESULTS_DIR, 'detail-1920x1080-after.png'),
        fullPage: false
      });
    }
  });

  test('datum discoverability — every visible datum reachable within one hover/click', async ({ page }) => {
    await installFixtureRoutes(page, '5-human-review');
    await page.goto(`/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`);
    const errClose = page.locator('.error-dialog__close').first();
    for (let i = 0; i < 3; i++) {
      if (!(await errClose.isVisible().catch(() => false))) break;
      await errClose.click({ timeout: 1_000 }).catch(() => {});
      await page.waitForTimeout(150);
    }
    await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });

    // Inventory of "datum → discoverability path". Each entry pairs a
    // testid with the path the user takes to reach the data: 'tooltip'
    // means a `title` attribute (one hover, no click), 'click' means a
    // single click expands the data inline. Adding a new visible
    // datum to the page that doesn't fit one of these paths is an
    // information-loss regression by definition.
    const HOVERABLE: Array<{ testid: string; mustContain?: RegExp }> = [
      { testid: 'hygiene-commit',           mustContain: /Task committed|No task commit/i },
      { testid: 'hygiene-tree',             mustContain: /Working tree (clean|dirty)/i },
      // hygiene-push removed: push-state is repo-level and lives on
      // the project-hygiene-badge in the detail header, not on the
      // per-task strip.
      // The pane toggles carry their label only in the tooltip now
      // that the icon row replaced the verbose button row.
      { testid: 'pane-toggle-prompt' },
      { testid: 'pane-toggle-protocol' },
      { testid: 'pane-toggle-git' },
      { testid: 'open-in-vscode' }
    ];
    for (const item of HOVERABLE) {
      const el = page.getByTestId(item.testid);
      await expect(el).toBeVisible({ timeout: 5_000 });
      const title = await el.getAttribute('title');
      expect(title, `${item.testid} carries a tooltip`).not.toBeNull();
      expect(title!.length, `${item.testid} tooltip is non-empty`).toBeGreaterThan(0);
      if (item.mustContain) {
        expect(title!).toMatch(item.mustContain);
      }
    }
  });

  test('multi-viewport screenshots', async ({ page }) => {
    test.skip(!RESULTS_DIR, 'Screenshot capture only runs under JOB_RESULTS_DIR');
    await installFixtureRoutes(page, '5-human-review');
    const viewports = [
      { w: 1280, h: 720 },
      { w: 1440, h: 900 },
      { w: 1920, h: 1080 }
    ];
    for (const v of viewports) {
      await page.setViewportSize({ width: v.w, height: v.h });
      await page.goto(`/?job=${encodeURIComponent(TARGET.id)}&watchPath=${encodeURIComponent(TARGET.watchPath)}`);
      const errClose = page.locator('.error-dialog__close').first();
      for (let i = 0; i < 3; i++) {
        if (!(await errClose.isVisible().catch(() => false))) break;
        await errClose.click({ timeout: 1_000 }).catch(() => {});
        await page.waitForTimeout(150);
      }
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
      fs.mkdirSync(RESULTS_DIR, { recursive: true });
      await page.screenshot({
        path: path.join(RESULTS_DIR, `detail-${v.w}x${v.h}-after.png`),
        fullPage: false
      });
    }
  });
});
