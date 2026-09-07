import { expect, test, type Page, type Route, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT = 'Dossier Demo';
const PROJECT_ID = 'project-dossier-demo';
const OTHER_PROJECT = 'Other Project';
const WATCH_PATH = '/fixtures/dossier-demo';

test.use({ serviceWorkers: 'block' });

const EMPTY_GROUPED = {
  backlog: [], preparation: [], orchestratorPrep: [],
  ready: [], progress: [], failedPickup: [], codeNotComplete: [],
  review: [], autoReview: [], humanReview: [], escalated: [],
  completed: [], archive: [],
};

const items = [
  dossier('decision-four', 'DDA-4', 'Decision routing', 'decision-pending', '2026-08-08T08:00:00Z', 4),
  dossier('decision-one', 'DDA-1', 'Release boundary', 'decision-pending', '2026-08-10T08:00:00Z', 1),
  dossier('active-new', 'DDA-9', 'Active delivery', 'active', '2026-08-11T08:00:00Z'),
  dossier('tracking', 'DDA-7', 'Tracked delivery', 'decided', '2026-08-09T08:00:00Z'),
  dossier('archived', 'DDA-2', 'Discarded direction', 'archived', '2026-08-07T08:00:00Z'),
  dossier('other-active', 'OTH-3', 'Other project delivery', 'active', '2026-08-06T08:00:00Z', 0, OTHER_PROJECT),
];

function dossier(
  id: string,
  key: string,
  title: string,
  status: string,
  updatedAtUtc: string,
  openDecisionCount = 0,
  projectName = PROJECT,
) {
  return {
    projectName,
    workbench: {
      id,
      key,
      title,
      summary: `${title} summary for the overview proof.`,
      status,
      phase: status === 'active' ? 'Testing' : null,
      updatedAtUtc,
      entryPath: `docs/operations/${id}/index.html`,
      valid: true,
      error: null,
      sourceTaskKeys: [],
      openDecisionCount,
    },
  };
}

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

function evidencePath(testInfo: TestInfo, fileName: string): string {
  const resultRoot = process.env['JOB_RESULTS_DIR']?.trim();
  const directory = resultRoot ? path.resolve(resultRoot) : testInfo.outputDir;
  fs.mkdirSync(directory, { recursive: true });
  return path.join(directory, fileName);
}

async function installMocks(page: Page): Promise<void> {
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/watch-paths', route => json(route, [
    { name: PROJECT, path: WATCH_PATH, rootPath: WATCH_PATH, repositoryPath: WATCH_PATH },
  ]));
  await page.route('**/api/workspaces**', route => json(route, [{
    id: 'workspace-dossier-demo',
    displayName: 'Dossier evidence',
    sortOrder: 0,
    isDefault: true,
    projects: [{
      id: PROJECT_ID,
      displayName: PROJECT,
      shortCode: 'DDA',
      workspaceId: 'workspace-dossier-demo',
      storageLocation: WATCH_PATH,
      sortOrder: 0,
      archived: false,
      urls: [],
    }],
  }]));
  await page.route('**/api/environment**', route => json(route, {
    isDev: false,
    devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false },
  }));
  await page.route('**/api/runner/status**', route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-11T10:00:00Z', snapshots: [], ttlSeconds: 600,
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-08-11T10:00:00Z', sessions: [],
  }));
  await page.route(/\/api\/cli\/[^/]+\/models(?:\?.*)?$/, route => json(route, {
    models: [], source: 'dossier-overview-evidence',
  }));
  await page.route('**/api/crash-recovery/pending**', route => json(route, { pending: [] }));
  await page.route('**/api/tasks/archive**', route => json(route, {
    items: [], total: 0, offset: 0, limit: 50,
  }));
  await page.route('**/api/tasks/grouped**', route => json(route, EMPTY_GROUPED));
  await page.route('**/api/tasks', route => json(route, []));
  await page.route(/\/api\/projects\/[^/]+\/workbenches(?:\?.*)?$/, route => {
    const projectItems = items.filter(item => item.projectName === PROJECT).map(item => item.workbench);
    return json(route, {
      projectName: PROJECT,
      includesHistory: true,
      count: projectItems.length,
      items: projectItems,
    });
  });
  await page.route(/\/api\/workbenches(?:\?.*)?$/, route => {
    const requestedProject = new URL(route.request().url()).searchParams.get('project');
    const overviewItems = requestedProject
      ? items.filter(item => item.projectName === requestedProject)
      : items;
    return json(route, {
      projectName: requestedProject,
      count: overviewItems.length,
      currentCount: overviewItems.filter(item => !['archived', 'documented'].includes(item.workbench.status)).length,
      historyCount: overviewItems.filter(item => ['archived', 'documented'].includes(item.workbench.status)).length,
      items: overviewItems,
    });
  });
}

test('Dossier sort and live filter share URL and session state across both overview scopes', async ({ page }, testInfo) => {
  test.setTimeout(90_000);
  await installMocks(page);
  await page.goto('/');
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  await page.getByTestId('studio-ab-workbenches').click();
  await expect(page.getByTestId('workbench-overview')).toBeVisible();
  await expect(page.locator('[data-testid^="workbench-overview-sort-"]')).toHaveCount(5);

  const pending = page.getByTestId('workbench-overview-decision-pending')
    .locator('[data-testid^="workbench-overview-item-"]');
  await expect(pending.nth(0)).toHaveAttribute('data-testid', `workbench-overview-item-${PROJECT}-decision-four`);
  await expect(pending.nth(1)).toHaveAttribute('data-testid', `workbench-overview-item-${PROJECT}-decision-one`);

  for (const key of ['status', 'updatedAt', 'project', 'key', 'openDecisions']) {
    const button = page.getByTestId(`workbench-overview-sort-${key}`);
    await button.click();
    await expect(button).toHaveAttribute('aria-pressed', 'true');
  }

  const decisionSort = page.getByTestId('workbench-overview-sort-openDecisions');
  await decisionSort.click();
  await expect(decisionSort).toHaveAttribute('aria-label', /ascending/i);
  await decisionSort.click();
  await expect(decisionSort).toHaveAttribute('aria-label', /descending/i);

  await page.getByTestId('workbench-overview-filter').fill('Decision pending');
  await expect(page.locator('[data-testid^="workbench-overview-item-"]')).toHaveCount(2);
  await expect(page.getByTestId('workbench-overview-current-count')).toHaveText('2 current');
  await expect.poll(() => decodeURIComponent(new URL(page.url()).hash))
    .toContain('dossier=q=Decision+pending&sort=openDecisions&dir=desc');

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: evidencePath(testInfo, `dossier-overview-central-sort-filter-${theme}--mocked.png`),
      fullPage: true,
    });
  }

  await page.reload();
  await expect(page.getByTestId('workbench-overview-filter')).toHaveValue('Decision pending');
  await expect(decisionSort).toHaveAttribute('aria-pressed', 'true');

  const projectRoute = `/#/projects/${PROJECT_ID}/workbenches`;
  const projectRow = page.getByTestId(`studio-explorer-project-${PROJECT}`);
  if (await projectRow.getAttribute('aria-expanded') === 'false') await projectRow.click();
  await page.getByTestId(`studio-explorer-project-workbenches-${PROJECT}`).click();
  await expect(page.getByTestId('workbench-overview-scope')).toHaveText(PROJECT);
  const orchestratorClose = page.locator(
    'app-orchestrator-side-sheet.is-open [data-testid="sidesheet-close"]',
  );
  if (await orchestratorClose.isVisible()) await orchestratorClose.click();
  await expect(page.getByTestId('workbench-overview-filter')).toHaveValue('');
  await page.getByTestId('workbench-overview-filter').fill('DDA-');
  const movementSort = page.getByTestId('workbench-overview-sort-updatedAt');
  await movementSort.click();
  await movementSort.click();
  await expect(movementSort).toHaveAttribute('aria-label', /ascending/i);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: evidencePath(testInfo, `dossier-overview-project-sort-filter-${theme}--mocked.png`),
      fullPage: true,
    });
  }

  await page.goto(projectRoute);
  await expect(page.getByTestId('workbench-overview-filter')).toHaveValue('DDA-');
  await expect(page.getByTestId('workbench-overview-sort-updatedAt')).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => decodeURIComponent(new URL(page.url()).hash))
    .toContain('dossier=q=DDA-&sort=updatedAt&dir=asc');

  await page.getByTestId('workbench-overview-reset').click();
  await expect(page.getByTestId('workbench-overview-filter')).toHaveValue('');
  await expect(page).toHaveURL(new RegExp(`${PROJECT_ID}/workbenches$`));
  await expect(pending.nth(0)).toHaveAttribute('data-testid', `workbench-overview-item-${PROJECT}-decision-four`);
});

test('collapsing pending decisions brings the active Dossiers into the first viewport and persists', async ({ page }, testInfo) => {
  await installMocks(page);
  const crowdedItems = [
    ...Array.from({ length: 10 }, (_, index) => dossier(
      `decision-${index}`,
      `DDA-${index + 20}`,
      `Pending decision ${index + 1}`,
      'decision-pending',
      `2026-08-${String(index + 1).padStart(2, '0')}T08:00:00Z`,
      1,
    )),
    dossier('active-first-viewport', 'DDA-99', 'Active Dossier in view', 'active', '2026-08-11T08:00:00Z'),
  ];
  await page.route(/\/api\/workbenches(?:\?.*)?$/, route => json(route, {
    projectName: PROJECT,
    count: crowdedItems.length,
    currentCount: crowdedItems.length,
    historyCount: 0,
    items: crowdedItems,
  }));
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.goto('/');
  await page.evaluate(key => localStorage.removeItem(key),
    `dossier-overview:${PROJECT_ID}:needs-decision`);
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  const projectRow = page.getByTestId(`studio-explorer-project-${PROJECT}`);
  await expect(projectRow).toBeVisible();
  if (await projectRow.getAttribute('aria-expanded') === 'false') await projectRow.click();
  await page.getByTestId(`studio-explorer-project-workbenches-${PROJECT}`).click();
  await expect(page.getByTestId('workbench-overview-scope')).toHaveText(PROJECT);

  const decisionHeader = page.getByTestId('workbench-overview-decision-toggle');
  const activeList = page.getByTestId('workbench-overview-active-list');
  await expect(decisionHeader).toHaveAttribute('aria-expanded', 'true');
  await decisionHeader.click();
  await expect(decisionHeader).toHaveAttribute('aria-expanded', 'false');
  await expect(page.locator('#workbench-overview-decision-list')).toHaveCount(0);
  await expect(activeList).toBeInViewport();
  expect(await activeList.evaluate(element => element.getBoundingClientRect().top < window.innerHeight)).toBe(true);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: evidencePath(testInfo, `dossier-overview-collapsed-decisions-${theme}--mocked.png`),
      fullPage: true,
    });
  }

  await page.reload();
  await expect(decisionHeader).toHaveAttribute('aria-expanded', 'false');
  await expect(activeList).toBeInViewport();
});
