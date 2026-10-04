import { test, expect, type Page, type Route } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { setTheme, type Theme } from '../helpers/theme';

/**
 * AGT-2023 regression — switching between the Project (Overview) and Wiki
 * sections when the Deck tab is ALREADY open must actually move the
 * rail, not silently do nothing.
 *
 * Root cause fixed: {@link StudioTabStateService.open} keyed a Hub tab on
 * project only (not its section), so re-opening the already-present Hub tab
 * on a different section dropped the new payload and just re-focused the
 * stale one. `open()` now adopts the fresh payload in place, and `openHub()`
 * targets Overview explicitly.
 *
 * This spec is fully MOCKED (no backend): it stubs `/api/**` + `/hubs/**`
 * so it reproduces the surface deterministically and captures labelled
 * `--mocked` screenshots for review. The strict unit assertions live in
 * `studio-tab-state.service.spec.ts` and `project-hub-view.component.spec.ts`.
 *
 * Screenshots land in HUB_SWITCH_SHOT_DIR (the job folder's results/ when
 * set); a local fallback keeps a stand-alone run useful.
 */

const PROJECT = 'Agent Taskboard';

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.HUB_SWITCH_SHOT_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'hub-project-wiki-switch');
})();

const screenshotTheme = process.env.DECK_ACCEPTANCE_THEME as Theme | undefined;

function shot(page: Page, name: string) {
  return page.screenshot({ path: path.join(SCREENSHOT_DIR, name), fullPage: true });
}

/** Broadly stub the boot so the studio shell renders one project offline. */
async function installMocks(page: Page): Promise<void> {
  // Register broad fallbacks FIRST; Playwright runs later-registered routes
  // first, so the specific stubs below win over these catch-alls.

  // Everything else: return an empty-but-valid payload. Arrays keep list
  // endpoints happy; the shell renders its empty states around them.
  await page.route('**/api/**', (route: Route) => {
    if (route.request().method() !== 'GET') return route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
    return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
  });
  await page.route('**/api/crash-recovery/pending', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ pending: [] }),
    }),
  );
  await page.route('**/api/v1/studio/auth/status', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        profile: 'local',
        bootstrapRequired: false,
        authenticated: true,
        user: null,
      }),
    }),
  );

  // SignalR negotiate / socket — never let it reach a real backend.
  await page.route('**/hubs/**', (route: Route) => route.abort());

  // The kanban board reads a fully-shaped GroupedJobs payload (every lane an
  // array). An empty `[]` would blow up the board's lane computeds and abort
  // change detection before the sidebar settles.
  const emptyGrouped = {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
    escalated: [], review: [], completed: [], archive: [],
  };
  await page.route('**/api/v1/studio/board', (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(emptyGrouped) }),
  );
  await page.route('**/api/tasks/archive**', (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], total: 0, offset: 0, limit: 50 }) }),
  );
  // Runner status is an object keyed by project; the status bar does
  // Object.values(status.projects) and throws on an array/undefined.
  await page.route('**/api/v1/studio/runner/status', (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ projects: {} }) }),
  );
  // The header quota strip reads a QuotaReport object (has `.snapshots`).
  await page.route('**/api/cli/quota', (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ at: '2026-01-01T00:00:00Z', snapshots: [], ttlSeconds: 600 }) }),
  );
  await page.route('**/api/cli/usage', (route: Route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ snapshots: [], ttlSeconds: 600 }) }),
  );

  const STORAGE = 'C:/repos/agent-taskboard';
  // The Deck Overview panel reads a per-project snapshot and dereferences
  // `snap.settings.autoCommit`; an empty `[]`/`{}` would throw and pop the
  // global error dialog over the surface we're screenshotting.
  await page.route('**/api/projects/*/snapshot', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project: PROJECT,
        capturedAt: '2026-01-01T00:00:00Z',
        paths: { path: STORAGE, rootPath: STORAGE, repositoryPath: STORAGE },
        settings: {
          autoCommit: false, crashRecoveryEnabled: false, autoPushStrategy: 'never',
          runnerMode: null, orchestratorModel: null, laneSortStrategies: {},
        },
        runnerStatus: null,
        orchestratorLogTail: [],
        orchestratorSession: null,
        reviewDecisionsPending: [],
        runnerPendingDecisions: [],
        queueHealth: { severity: 'ok', issueCount: 0, missingJobJson: [], duplicates: [], stateMismatches: [] },
      }),
    }),
  );
  await page.route('**/api/projects/*/visual-evidence', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project: PROJECT,
        capturedAt: '2026-01-01T00:00:00Z',
        unseenCount: 0,
        items: [],
      }),
    }),
  );
  await page.route('**/api/projects/*/token-usage/summary', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project: PROJECT,
        hasData: false,
        lifetimeTotalTokens: 0,
        lifetimeJobTokens: 0,
        lifetimeSupportingTokens: 0,
        lifetimeOrchestratorTokens: 0,
        lifetimeCalls: 0,
        last24hTotalTokens: 0,
        last24hJobTokens: 0,
        last24hSupportingTokens: 0,
        last24hOrchestratorTokens: 0,
        last24hCalls: 0,
        last7dTotalTokens: 0,
        last7dJobTokens: 0,
        last7dSupportingTokens: 0,
        last7dOrchestratorTokens: 0,
        last7dCalls: 0,
        firstActivity: null,
        lastActivity: null,
        fetchedAt: '2026-01-01T00:00:00Z',
        disclaimer: '',
      }),
    }),
  );
  await page.route('**/api/projects/*/deployment/summary', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        project: PROJECT,
        available: false,
        reason: 'No history.',
        source: 'logs/stable-restarts.jsonl',
        lastDeployment: null,
        pendingCount: null,
        pendingCommits: [],
      }),
    }),
  );
  await page.route('**/api/projects/*/wiki/pulse**', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projectName: PROJECT,
        baseDir: STORAGE,
        exists: true,
        generatedAtUtc: '2026-01-01T00:00:00Z',
        feed: { available: true, reason: null, items: [] },
        inbox: { available: true, reason: null, count: 0, items: [] },
        drift: {
          available: true,
          reason: null,
          overallGrade: 'Fresh',
          areas: [],
          counts: { fresh: 0, aging: 0, stale: 0, graded: 0 },
        },
        critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
      }),
    }),
  );
  await page.route('**/api/git/inventory**', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        projectName: PROJECT,
        repositoryPath: STORAGE,
        isRepo: true,
        currentBranch: 'develop',
        worktrees: [],
        recentCommits: [],
        branches: [],
        error: null,
      }),
    }),
  );
  await page.route('**/api/watch-paths', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ name: PROJECT, path: STORAGE, rootPath: STORAGE }]),
    }),
  );

  // A real registry workspace so the project row renders as a normal tree row
  // (a null projectId makes the tree render an inline rename input instead).
  await page.route('**/api/v1/workspaces**', (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        {
          id: 'ws1', displayName: 'Default', sortOrder: 0, isDefault: true, color: null,
          createdAt: '2026-01-01T00:00:00Z',
          projects: [
            {
              id: 'proj1', displayName: PROJECT, shortCode: 'AGT', workspaceId: 'ws1',
              color: null, cliDefault: null, modelDefault: null, sortOrder: 0,
              storageLocation: STORAGE, urls: [], archived: false, createdAt: '2026-01-01T00:00:00Z',
            },
          ],
        },
      ]),
    }),
  );
}

async function gotoStudio(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await installMocks(page);
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await page.evaluate(() => {
    try { localStorage.removeItem('atp.studio.tabs.v1'); } catch { /* ignore */ }
    try { localStorage.removeItem('atp.studio.explorer.expanded'); } catch { /* ignore */ }
  });
  await page.reload();
  await page.waitForLoadState('domcontentloaded');
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 15_000 });
  if (screenshotTheme) await setTheme(page, screenshotTheme);
}

/** Expand the seeded project so its Board / Hub / Wiki child rows show. */
async function expandProject(page: Page): Promise<void> {
  const hubRow = page.getByTestId(`studio-explorer-project-hub-${PROJECT}`);
  if (!(await hubRow.count()) || !(await hubRow.isVisible())) {
    await page.getByTestId(`studio-explorer-project-${PROJECT}`).first().click();
  }
  await expect(hubRow).toBeVisible({ timeout: 5_000 });
}

const hubRow = (page: Page) => page.getByTestId(`studio-explorer-project-hub-${PROJECT}`);
const wikiRow = (page: Page) => page.getByTestId(`studio-explorer-project-wiki-${PROJECT}`);
const rail = (page: Page, key: string) => page.getByTestId(`project-shell-rail-${key}`);

test.describe('Project ⇄ Wiki switch with the Hub tab already open (AGT-2023)', () => {
  test('internal destination opens once and active close returns to MRU tab', async ({ page }) => {
    fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
    await gotoStudio(page);
    await page.evaluate(projectName => {
      localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
        v: 1,
        tabs: [
          { kind: 'board', projectName: '__all__' },
          { kind: 'hub', projectName, section: 'overview' },
          { kind: 'workspace-settings' },
        ],
        activeKey: `hub:${projectName}`,
      }));
    }, PROJECT);
    await page.reload();
    await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 15_000 });
    await page.getByTestId(`studio-tab-hub:${PROJECT}`).click();
    await expect(rail(page, 'overview')).toHaveAttribute('aria-current', 'page');
    await expandProject(page);

    // (a) The Wiki rail is an internal destination, so it opens beside the
    // current Deck tab instead of replacing it.
    await rail(page, 'wiki').click();
    const wikiTab = page.getByTestId(`studio-tab-hub:${PROJECT}:wiki`);
    await expect(wikiTab).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId(`studio-tab-hub:${PROJECT}`)).toHaveCount(1);
    await shot(page, 'acceptance-01-internal-link-opens-app-tab.png');

    // (b) Focus Deck, then use the Explorer's Wiki entry. The already-open
    // destination is activated and its count stays at one.
    await page.getByTestId(`studio-tab-hub:${PROJECT}`).click();
    await wikiRow(page).click();
    await expect(wikiTab).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId(`studio-tab-hub:${PROJECT}:wiki`)).toHaveCount(1);
    await shot(page, 'acceptance-02-repeat-click-focuses-existing-tab.png');

    // (c) Workspace Settings is immediately left of Wiki, but Deck was the
    // previously active tab. Closing Wiki must therefore activate Deck.
    await wikiTab.getByRole('button', { name: 'Close tab' }).click();
    await expect(page.getByTestId(`studio-tab-hub:${PROJECT}`)).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('studio-tab-workspace-settings')).toHaveAttribute('aria-selected', 'false');
    await shot(page, 'acceptance-03-active-close-returns-to-mru.png');
  });

  test('Wiki open → click Project → lands on Overview; then → Wiki again', async ({ page }) => {
    fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
    await gotoStudio(page);
    await expandProject(page);

    // 1) Open the Wiki first — Hub tab opens deep-linked to the Wiki rail.
    await wikiRow(page).click();
    await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });
    await expect(wikiRow(page)).toHaveAttribute('aria-current', 'page', { timeout: 5_000 });
    await expect(rail(page, 'wiki')).toHaveAttribute('aria-current', 'page', { timeout: 5_000 });
    await shot(page, '01-wiki-open--mocked.png');

    // 2) The bug: with the Deck tab already open, clicking "Deck" used
    //    to do nothing. It must now move the rail to Overview.
    await hubRow(page).click();
    await expect(hubRow(page)).toHaveAttribute('aria-current', 'page', { timeout: 5_000 });
    await expect(rail(page, 'overview')).toHaveAttribute('aria-current', 'page', { timeout: 5_000 });
    await expect(wikiRow(page)).not.toHaveAttribute('aria-current', 'page');
    await shot(page, '02-clicked-project-now-overview--mocked.png');

    // 3) And the reverse direction: from Overview, "Wiki" moves back.
    await wikiRow(page).click();
    await expect(rail(page, 'wiki')).toHaveAttribute('aria-current', 'page', { timeout: 5_000 });
    await expect(wikiRow(page)).toHaveAttribute('aria-current', 'page');
    await shot(page, '03-clicked-wiki-back-to-wiki--mocked.png');

    // No duplicate Hub tab was spun up across all the switching — the Open
    // Tabs list holds exactly one entry keyed on the project's Hub tab.
    await expect(page.getByTestId(`studio-explorer-open-tab-hub:${PROJECT}`)).toHaveCount(1);
  });

  test('existing Deck plus Explorer Wiki produces two distinct editor tabs', async ({ page }) => {
    await gotoStudio(page);
    await expandProject(page);

    await hubRow(page).click();
    await expect(rail(page, 'overview')).toHaveAttribute('aria-current', 'page');
    await wikiRow(page).click();

    await expect(rail(page, 'wiki')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId(`studio-explorer-open-tab-hub:${PROJECT}`)).toHaveCount(1);
    await expect(page.getByTestId(`studio-explorer-open-tab-hub:${PROJECT}:wiki`)).toHaveCount(1);
    await expect(page.getByRole('tab', { name: 'AGT · Deck' })).toHaveCount(1);
    await expect(page.getByRole('tab', { name: 'AGT · Wiki' })).toHaveCount(1);
    await shot(page, '04-hub-and-wiki-separate-tabs--mocked.png');
  });

  test('Hub rail Wiki click opens a distinct editor tab', async ({ page }) => {
    await gotoStudio(page);
    await expandProject(page);
    await hubRow(page).click();

    await rail(page, 'wiki').click();

    await expect(rail(page, 'wiki')).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId(`studio-explorer-open-tab-hub:${PROJECT}`)).toHaveCount(1);
    await expect(page.getByTestId(`studio-explorer-open-tab-hub:${PROJECT}:wiki`)).toHaveCount(1);
  });
});
