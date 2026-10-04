import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const RESULTS_DIR = process.env.PROJECT_GRAPH_RESULTS_DIR
  ?? path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-graph');
const PROJECT_NAME = 'Operator Demo';
const PROJECT_ID = 'PROJ-900';

const project = {
  sourceType: 'local-folder',
  id: PROJECT_ID,
  displayName: PROJECT_NAME,
  shortCode: 'OPD',
  workspaceId: 'ws-product',
  color: null,
  cliDefault: null,
  modelDefault: null,
  storageLocation: '/mock/tasks/operator-demo',
  rootPath: '/mock/repos/operator-demo',
  repositoryPath: '/mock/repos/operator-demo',
  sortOrder: 0,
  archived: false,
  createdAt: '2026-07-13T10:00:00Z',
  repositoryUrl: null,
  urls: [],
};

const graph = {
  schemaVersion: 1,
  generatorVersion: 'project-graph-v1',
  snapshotId: 'pg-e2e-001',
  previousSnapshotId: null,
  captureMode: 'explicit-api',
  capturedAtUtc: '2026-07-13T12:00:00Z',
  focusProjectId: PROJECT_ID,
  focusProjectKey: 'OPD',
  projects: [{
    id: PROJECT_ID,
    key: 'OPD',
    shortCode: 'OPD',
    displayName: PROJECT_NAME,
    status: 'ready',
    repositoryLabel: `${PROJECT_ID} · ${PROJECT_NAME}`,
    sourceRevision: '0123456789abcdef0123456789abcdef01234567',
    sourceState: 'clean',
    solutions: ['OperatorDemo.slnx'],
    workflows: ['.github/workflows/ci.yml'],
    technologies: [{ slug: 'dotnet', label: '.NET 10' }, { slug: 'angular', label: 'Angular 21' }, { slug: 'github-actions', label: 'GitHub Actions' }],
    componentIds: ['opd:api', 'opd:web'],
    size: { files: 950, lines: 120_000 },
    warnings: [],
  }, {
    id: 'PROJ-901',
    key: 'LIB',
    shortCode: 'LIB',
    displayName: 'Shared Runner',
    status: 'ready',
    repositoryLabel: 'PROJ-901 · Shared Runner',
    sourceRevision: null,
    sourceState: 'unavailable',
    solutions: ['SharedRunner.slnx'],
    workflows: [],
    technologies: [{ slug: 'dotnet', label: '.NET 10' }],
    componentIds: ['lib:runner'],
    size: { files: 120, lines: 14_000 },
    warnings: [],
  }],
  components: [
    { id: 'opd:api', projectId: PROJECT_ID, projectKey: 'OPD', name: 'OperatorDemo.Api', kind: 'dotnet', relativePath: 'src/OperatorDemo.Api/OperatorDemo.Api.csproj', technologies: [{ slug: 'dotnet', label: '.NET 10' }, { slug: 'aspnet-core', label: 'ASP.NET Core' }], size: { files: 420, lines: 70_000 } },
    { id: 'opd:web', projectId: PROJECT_ID, projectKey: 'OPD', name: 'operator-demo-web', kind: 'npm', relativePath: 'web/package.json', technologies: [{ slug: 'angular', label: 'Angular 21' }, { slug: 'typescript', label: 'TypeScript' }], size: { files: 530, lines: 50_000 } },
    { id: 'lib:runner', projectId: 'PROJ-901', projectKey: 'LIB', name: 'SharedRunner', kind: 'dotnet', relativePath: 'src/SharedRunner/SharedRunner.csproj', technologies: [{ slug: 'dotnet', label: '.NET 10' }], size: { files: 120, lines: 14_000 } },
  ],
  dependencies: [
    { fromComponentId: 'opd:api', toComponentId: 'lib:runner', kind: 'package', resolution: 'resolved', targetHint: null, evidence: 'src/OperatorDemo.Api/OperatorDemo.Api.csproj: SharedRunner' },
    { fromComponentId: 'opd:web', toComponentId: 'opd:api', kind: 'package', resolution: 'resolved', targetHint: null, evidence: 'web/package.json: @operator/api-client' },
    { fromComponentId: 'opd:web', toComponentId: null, kind: 'package', resolution: 'unresolved', targetHint: 'local-ui file:<local-path>', evidence: 'web/package.json: local-ui file:<local-path>' },
  ],
};

function grouped() {
  return {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    codeNotComplete: [], autoReview: [], humanReview: [], escalated: [], review: [], completed: [], archive: [],
  };
}

async function fulfillJson(route: Route, body: unknown): Promise<void> {
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function mockApplication(page: Page): Promise<void> {
  await page.route('**/api/**', route => {
    const url = new URL(route.request().url());
    const pathname = url.pathname;
    if (pathname.endsWith('/graph')) return fulfillJson(route, graph);
    if (pathname === '/api/watch-paths') return fulfillJson(route, [{ name: PROJECT_NAME, path: project.storageLocation, rootPath: project.rootPath, repositoryPath: project.repositoryPath }]);
    if (pathname === '/api/v1/workspaces') return fulfillJson(route, [{
      id: 'ws-product', displayName: 'Product Engineering', sortOrder: 0, isDefault: true,
      color: '#6c8cff', createdAt: '2026-07-13T10:00:00Z', projects: [project],
    }]);
    if (pathname === '/api/v1/projects') return fulfillJson(route, [project]);
    if (pathname === '/api/tasks') return fulfillJson(route, []);
    if (pathname === '/api/v1/studio/board') return fulfillJson(route, grouped());
    if (pathname === '/api/tasks/archive') return fulfillJson(route, { items: [], total: 0, offset: 0, limit: 50 });
    if (pathname === '/api/v1/studio/runner/status') return fulfillJson(route, { projects: {} });
    if (pathname === '/api/cli/quota') return fulfillJson(route, { at: '2026-07-13T12:00:00Z', ttlSeconds: 600, snapshots: [] });
    if (pathname === '/api/crash-recovery/pending') return fulfillJson(route, { pending: [] });
    return fulfillJson(route, []);
  });
}

async function openGraph(page: Page): Promise<void> {
  await mockApplication(page);
  await page.goto('/#/projects/operator-demo/project-graph');
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('project-graph')).toBeVisible({ timeout: 15_000 });
}

test.beforeAll(() => fs.mkdirSync(RESULTS_DIR, { recursive: true }));

test('renders the manifest graph and complete list in dark and light themes', async ({ page }) => {
  await openGraph(page);
  await expect(page.getByTestId('project-shell-rail-project-graph')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('project-graph-component-count')).toHaveText('2');
  await expect(page.getByTestId('project-graph-canvas').locator('.project-graph__node')).toHaveCount(3);
  await expect(page.getByTestId('project-graph-source-provenance')).toContainText('0123456789ab · clean');

  for (const theme of ['dark', 'light'] as const) {
    await setTheme(page, theme);
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
    await page.screenshot({ path: path.join(RESULTS_DIR, `project-graph-${theme}.png`), fullPage: true });
  }

  await page.getByTestId('project-graph-view-list').click();
  await expect(page.getByTestId('project-graph-component-list').locator('tbody tr')).toHaveCount(2);
  await expect(page.getByTestId('project-graph-component-list')).toContainText('src/OperatorDemo.Api/OperatorDemo.Api.csproj');
  const relationSummary = page.getByTestId('project-graph-component-list').locator('summary').first();
  await relationSummary.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByTestId('project-graph-component-list')).toContainText('resolved');
});
