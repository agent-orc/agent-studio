import { expect, test, type Locator, type Page, type TestInfo } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';

const PROJECT = 'tooltip-standard-fixture';
const WATCH_PATH = 'C:/fixtures/tooltip-standard-repo';

const LONG_FILES = [
  'frontend/src/app/components/tooltip/tooltip.controller.ts',
  'frontend/src/app/components/chat/tool-burst-chip/tool-burst-chip.component.html',
  'frontend/src/app/components/media-lightbox/media-lightbox.component.html',
];

function commit(shortSha: string, message: string) {
  return {
    sha: `${shortSha}${'0'.repeat(40 - shortSha.length)}`,
    shortSha,
    message,
    filesChanged: LONG_FILES.length,
    files: LONG_FILES,
    at: '2026-06-09T08:00:00Z',
  };
}

function taskInfo() {
  return {
    id: 'tooltip-standard-card',
    jobKey: `${WATCH_PATH}::tooltip-standard-card`,
    taskKey: `${WATCH_PATH}::tooltip-standard-card`,
    title: 'Canonical tooltip standard fixture',
    state: '4-auto-review',
    order: 1,
    agent: 'codex',
    cliType: 'codex',
    model: 'gpt-5',
    createdAt: '2026-06-09T07:30:00Z',
    watchPath: WATCH_PATH,
    projectName: PROJECT,
    folderPath: `${WATCH_PATH}/.orchestrator/tasks/4-auto-review/tooltip-standard-card`,
    lastActivity: '2026-06-09T08:30:00Z',
    sessionName: null,
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    commits: [commit('9ab4c10', 'feat: standardize tooltip rendering')],
    codeActivityDetected: true,
    ownerClientId: 'local-default',
    taskType: 'feature',
    mode: 'coding',
    tags: [],
    references: { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] },
  };
}

function grouped() {
  return {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    autoReview: [taskInfo()],
    humanReview: [],
    escalated: [],
    review: [],
    completed: [],
    archive: [],
  };
}

async function installRoutes(page: Page): Promise<void> {
  const json = (body: unknown) => async (route: import('@playwright/test').Route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(body),
    });
  };

  await page.route('**/api/**', json([]));
  await page.route(/\/api\/(?:jobs|tasks)(\?.*)?$/, json([taskInfo()]));
  await page.route('**/api/v1/studio/board**', json(grouped()));
  await page.route('**/api/v1/studio/board**', json(grouped()));
  await page.route('**/api/watch-paths**', json([
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/environment**', json({
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/dev-tools/flags**', json({
    updateStableEnabled: false,
    deleteE2EJobsEnabled: false,
  }));
  await page.route(/\/api\/v1\/studio\/runner\/status(\?|$)/, json({
    projects: {
      [PROJECT]: {
        projectName: PROJECT,
        mode: 'manual',
        activeJobId: null,
        activeExecution: null,
        queuedJobIds: [],
      },
    },
  }));
  await page.route('**/api/agent-rules**', json([]));
  await page.route('**/api/clients**', json([]));
  await page.route('**/api/cli/usage**', json({ at: '2026-06-09T08:00:00Z', sessions: [] }));
  await page.route('**/api/cli/quota**', json({ at: '2026-06-09T08:00:00Z', ttlSeconds: 600, snapshots: [] }));
  await page.route('**/api/git/summary**', json([]));
  await page.route(/\/api\/git\/hygiene(\?|$)/, json({ isRepo: false, error: null }));
  await page.route('**/api/tags**', json([]));
  await page.route(/\/update\/status(\?|$)/, json({
    isRunning: false,
    phase: 'idle',
    currentRunId: null,
    lastRunFinishedAt: null,
    message: null,
    verificationFailures: [],
  }));
  await page.route(/\/update\/history(\?|$)/, json([]));
}

async function seedBoardTab(page: Page): Promise<void> {
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }],
      activeKey: 'board:__all__',
    }));
  });
}

async function openBoard(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await seedBoardTab(page);
  await installRoutes(page);
  await page.goto('/?includeFixtures=true');
  await page.waitForLoadState('domcontentloaded');
  await page.evaluate(() => {
    document.querySelectorAll('vite-error-overlay').forEach((node) => node.remove());
  });
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('task-card')).toBeVisible({ timeout: 15_000 });
}

async function expectTooltip(
  page: Page,
  target: Locator,
  expectedText: RegExp,
  screenshotName: string,
  testInfo: TestInfo,
): Promise<void> {
  await target.scrollIntoViewIfNeeded();
  await expect(target).not.toHaveAttribute('title', /.+/);

  const start = Date.now();
  await target.hover();
  const tip = page.getByTestId('cac-tooltip');
  await expect(tip).toBeVisible({ timeout: 250 });
  expect(Date.now() - start).toBeLessThan(500);
  await expect(tip).toContainText(expectedText);
  await expect(page.locator('[data-testid="cac-tooltip"]')).toHaveCount(1);
  await expect(page.locator('.cac-tooltip')).toHaveCount(1);

  const shot = await page.screenshot({ fullPage: false });
  await testInfo.attach(`${screenshotName}.png`, { body: shot, contentType: 'image/png' });

  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (resultsDir) {
    await mkdir(resultsDir, { recursive: true });
    await page.screenshot({ path: join(resultsDir, `${screenshotName}.png`), fullPage: false });
  }

  await page.mouse.move(0, 0);
  await expect(tip).toBeHidden({ timeout: 500 });
}

test('canonical tooltip layer is lazy, instant, singleton, and visually shared across surfaces', async ({ page }, testInfo) => {
  await openBoard(page);

  await expect(page.getByTestId('cac-tooltip')).toHaveCount(0);

  // AGT-2035 removed the compact-cards toggle; the Lanes/Epics toggle is the
  // representative board tab-bar control that carries the shared tooltip.
  await expectTooltip(
    page,
    page.getByTestId('studio-board-epic-toggle'),
    /Group tasks by epic|Show lane columns/i,
    'tooltip-standard-board-control',
    testInfo,
  );

  await expectTooltip(
    page,
    page.getByTestId('status-bar-settings'),
    /Workspace settings/i,
    'tooltip-standard-statusbar',
    testInfo,
  );

  await expectTooltip(
    page,
    page.getByTestId('task-card-commit-row').first(),
    /tooltip\.controller\.ts/i,
    'tooltip-standard-commit',
    testInfo,
  );
});
