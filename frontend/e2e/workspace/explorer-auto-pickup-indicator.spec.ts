import { expect, test, type Page, type TestInfo } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';

const PROJECTS = [
  { name: 'Pickup Active', mode: 'auto-continuous', pickupAllowed: true, profileStatus: 'pipeline-ready' },
  { name: 'Pickup Paused', mode: 'paused', pickupAllowed: true, profileStatus: 'pipeline-ready' },
  { name: 'Pickup Manual', mode: 'manual', pickupAllowed: true, profileStatus: null },
  { name: 'Pickup Blocked', mode: 'auto-continuous', pickupAllowed: false, profileStatus: 'declared' },
] as const;

function grouped() {
  return {
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    ready: [],
    progress: [],
    failedPickup: [],
    codeNotComplete: [],
    autoReview: [],
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

  const watchPaths = PROJECTS.map((project, index) => ({
    name: project.name,
    path: `C:/fixtures/pickup-${index}`,
    rootPath: `C:/fixtures/pickup-${index}`,
    repositoryPath: `C:/fixtures/pickup-${index}`,
  }));
  const workspaceProjects = PROJECTS.map((project, index) => ({
    sourceType: 'local-folder',
    id: `PROJ-22${index}`,
    displayName: project.name,
    shortCode: `P${index}`,
    workspaceId: 'ws-pickup',
    color: null,
    cliDefault: null,
    modelDefault: null,
    sortOrder: index,
    storageLocation: watchPaths[index].path,
    repositoryPath: watchPaths[index].path,
    rootPath: watchPaths[index].path,
    repositoryUrl: null,
    urls: [],
    archived: false,
    createdAt: '2026-07-23T08:00:00Z',
  }));
  const runnerProjects = Object.fromEntries(PROJECTS.map(project => [
    project.name,
    {
      projectName: project.name,
      mode: project.mode,
      activeJobId: null,
      activeExecution: null,
      queuedJobIds: [],
    },
  ]));
  const projectSettings = Object.fromEntries(PROJECTS.map(project => [
    project.name,
    {
      autoCommit: false,
      crashRecoveryEnabled: true,
      autoPushStrategy: 'never',
      runnerMode: project.mode,
      orchestratorModel: null,
      buildProfilePickupAllowed: project.pickupAllowed,
      buildProfile: project.profileStatus ? { status: project.profileStatus } : null,
      laneSortStrategies: {},
    },
  ]));

  const workspaces = [{
    id: 'ws-pickup',
    displayName: 'Auto-pickup states',
    sortOrder: 0,
    isDefault: true,
    color: null,
    createdAt: '2026-07-23T08:00:00Z',
    projects: workspaceProjects,
  }];
  const environment = {
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  };
  const devFlags = {
    updateStableEnabled: false,
    deleteE2EJobsEnabled: false,
  };
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    let body: unknown = [];
    if (path === '/api/v1/studio/board') body = grouped();
    else if (path === '/api/watch-paths') body = watchPaths;
    else if (path === '/api/v1/workspaces') body = workspaces;
    else if (path === '/api/projects/settings') body = projectSettings;
    else if (path === '/api/v1/studio/runner/status') body = { projects: runnerProjects };
    else if (path === '/api/v1/studio/auth/status') {
      body = { profile: 'local', bootstrapRequired: false, authenticated: false, user: null };
    }
    else if (path === '/api/environment') body = environment;
    else if (path === '/api/dev-tools/flags') body = devFlags;
    else if (path === '/api/cli/usage') body = { at: '2026-07-23T08:00:00Z', sessions: [] };
    else if (path === '/api/cli/quota') body = { at: '2026-07-23T08:00:00Z', ttlSeconds: 600, snapshots: [] };
    else if (path === '/api/git/hygiene') body = { isRepo: false, error: null };
    await json(body)(route);
  });
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

async function openExplorer(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [],
      activeKey: null,
    }));
    localStorage.removeItem('atp.studio.explorerSections');
    localStorage.removeItem('atp.studio.explorer.expanded');
  });
  await installRoutes(page);
  await page.goto('/');
  await page.waitForLoadState('domcontentloaded');
  await expect(page.getByTestId('studio-sidebar')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('studio-explorer-project-auto-pickup-Pickup Active')).toBeVisible();
}

async function saveEvidence(page: Page, testInfo: TestInfo, name: string, locator?: ReturnType<Page['locator']>) {
  const body = locator
    ? await locator.screenshot()
    : await page.screenshot({ fullPage: false });
  await testInfo.attach(`${name}.png`, { body, contentType: 'image/png' });

  const resultsDir = process.env.JOB_RESULTS_DIR;
  if (resultsDir) {
    const screenshots = join(resultsDir, 'screenshots');
    await mkdir(screenshots, { recursive: true });
    if (locator) {
      await locator.screenshot({ path: join(screenshots, `${name}.png`) });
    } else {
      await page.screenshot({ path: join(screenshots, `${name}.png`), fullPage: false });
    }
  }
}

test('Explorer project rows show mixed auto-pickup states without changing row height', async ({ page }, testInfo) => {
  await openExplorer(page);

  for (const project of PROJECTS) {
    const expectedState = project.name.replace('Pickup ', '').toLowerCase();
    await expect(page.getByTestId(`studio-explorer-project-auto-pickup-${project.name}`))
      .toHaveAttribute('data-auto-pickup-state', expectedState);
  }

  const heights = await Promise.all(PROJECTS.map(async project => {
    const box = await page.getByTestId(`studio-explorer-project-${project.name}`).boundingBox();
    expect(box).toBeTruthy();
    return box!.height;
  }));
  expect(new Set(heights).size).toBe(1);

  await saveEvidence(
    page,
    testInfo,
    'explorer-auto-pickup-mixed-states-light',
    page.getByTestId('studio-sidebar'),
  );

  await page.evaluate(() => {
    document.documentElement.dataset['studioTheme'] = 'dark';
    localStorage.setItem('atp.studio.theme', 'dark');
  });
  await expect(page.locator('html')).toHaveAttribute('data-studio-theme', 'dark');
  await saveEvidence(
    page,
    testInfo,
    'explorer-auto-pickup-mixed-states-dark',
    page.getByTestId('studio-sidebar'),
  );
});

test('blocked auto-pickup tooltip exposes the build-profile reason', async ({ page }, testInfo) => {
  await openExplorer(page);

  const blocked = page.getByTestId('studio-explorer-project-auto-pickup-Pickup Blocked');
  await expect(blocked).toHaveAttribute('aria-label', 'Auto-pickup blocked: build profile declared');
  await expect(blocked).toHaveAttribute('data-auto-pickup-reason', 'build profile declared');
  await blocked.hover();

  const tooltip = page.getByTestId('cac-tooltip');
  await expect(tooltip).toBeVisible();
  await expect(tooltip).toHaveText('Auto-pickup blocked: build profile declared');
  await saveEvidence(page, testInfo, 'explorer-auto-pickup-blocked-tooltip');
});
