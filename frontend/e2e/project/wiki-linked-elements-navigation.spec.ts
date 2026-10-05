import { mkdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { test, expect, type Page, type Route } from '@playwright/test';
import { setTheme } from '../helpers/theme';

const DOSSIER_PATH = 'operations/orchestrator-waechter/index.html';
const DOSSIER_HTML = readFileSync(resolve(__dirname, '..', '..', '..', 'docs', DOSSIER_PATH), 'utf8');
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim();
const PROJECT_ID = 'PROJ-ANCHORS';
const PROJECT_NAME = 'Wiki Anchor Proof';

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

async function hideMockOfflineChrome(page: Page): Promise<void> {
  await page.evaluate(() => {
    for (const testId of ['offline-banner', 'notification-stack', 'notification-stack-bottom-right']) {
      document.querySelectorAll<HTMLElement>(`[data-testid="${testId}"]`)
        .forEach(element => { element.style.display = 'none'; });
    }
  });
}

async function mockProjectWiki(page: Page): Promise<void> {
  const project = encodeURIComponent(PROJECT_NAME);
  const projectRecord = {
    id: PROJECT_ID,
    displayName: PROJECT_NAME,
    shortCode: 'WAP',
    workspaceId: 'WS-ANCHORS',
    storageLocation: '/mock/tasks/wiki-anchor-proof',
    rootPath: '/mock/repos/wiki-anchor-proof',
    repositoryPath: '/mock/repos/wiki-anchor-proof',
    sortOrder: 0,
    archived: false,
    urls: [],
  };
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  }));
  await page.route('**/api/v1/workspaces**', route => json(route, [{
    id: 'WS-ANCHORS', displayName: 'Anchor proof', sortOrder: 0, isDefault: true,
    projects: [projectRecord],
  }]));
  await page.route('**/api/watch-paths**', route => json(route, [{
    name: PROJECT_NAME,
    path: projectRecord.storageLocation,
    rootPath: projectRecord.rootPath,
    repositoryPath: projectRecord.repositoryPath,
  }]));
  await page.route('**/api/v1/studio/board**', route => json(route, {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], autoReview: [], humanReview: [],
    escalated: [], review: [], completed: [], archive: [],
  }));
  await page.route(/\/api\/v1\/studio\/runner\/status(?:\?|$)/, route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-10T00:00:00Z', ttlSeconds: 600, snapshots: [],
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-08-10T00:00:00Z', sessions: [],
  }));
  await page.route('**/api/cli/maintenance-model', route => json(route, {
    cliType: 'claude', model: 'claude-sonnet-5', thinkingLevel: null,
  }));
  await page.route('**/api/tasks/reference-status', route => json(route, { items: [] }));
  await page.route(`**/api/projects/${project}/wiki/tree`, route => json(route, {
    projectName: PROJECT_NAME,
    baseDir: '/mock/repos/wiki-anchor-proof/docs',
    exists: true,
    root: [{
      name: 'operations', title: 'operations', relPath: 'operations', type: 'folder',
      children: [{
        name: 'orchestrator-waechter', title: 'orchestrator-waechter',
        relPath: 'operations/orchestrator-waechter', type: 'folder',
        children: [{
          name: 'index.html', title: 'Global Orchestrator Watcher',
          relPath: DOSSIER_PATH, type: 'html', children: [],
        }],
      }],
    }],
  }));
  await page.route(`**/api/projects/${project}/wiki/pulse**`, route => json(route, {
    projectName: PROJECT_NAME,
    baseDir: '/mock/repos/wiki-anchor-proof/docs',
    exists: true,
    generatedAtUtc: '2026-08-10T00:00:00Z',
    feed: { available: true, reason: null, items: [] },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: { available: true, reason: null, overallGrade: 'Empty', areas: [],
      counts: { fresh: 0, aging: 0, stale: 0, graded: 0 } },
    critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
  }));
  await page.route(`**/api/projects/${project}/wiki/grading/status**`, route =>
    json(route, { status: null }));
  await page.route(`**/api/projects/${project}/wiki/files/**`, route => json(route, {
    relPath: DOSSIER_PATH,
    content: DOSSIER_HTML,
  }));
  await page.route(`**/api/projects/${project}/wiki/history/**`, route => {
    if (route.request().headers()['if-none-match'] === '"anchor-v1"') {
      return route.fulfill({ status: 304, headers: { ETag: '"anchor-v1"' }, body: '' });
    }
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      headers: { ETag: '"anchor-v1"' },
      body: JSON.stringify({
        relPath: DOSSIER_PATH,
        model: null,
        metadata: {
          model: null, updatedAt: null, reason: null, taskKey: 'AGT-2557',
          status: null, runCount: null, hasFrontmatter: false,
        },
        commits: [],
      }),
    });
  });
}

test('linked dossier anchors navigate, track scrolling, return, and expose missing targets', async ({
  page,
}, testInfo) => {
  await mockProjectWiki(page);
  const screenshotPath = (name: string): string => {
    if (!RESULTS_DIR) return testInfo.outputPath(name);
    mkdirSync(RESULTS_DIR, { recursive: true });
    return join(RESULTS_DIR, name);
  };

  await page.goto(`/#/projects/${slugFor(PROJECT_NAME)}/wiki?page=${encodeURIComponent(DOSSIER_PATH)}`);
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(DOSSIER_PATH, {
    timeout: 30_000,
  });

  const frameElement = page.getByTestId('project-wiki-html-frame');
  const dossier = page.frameLocator('[data-testid="project-wiki-html-frame"]');
  await expect(frameElement).toBeVisible();
  await expect(dossier.locator('#mandate')).toBeVisible();

  const links = page.getByTestId('project-wiki-linked-element');
  await expect(links).toHaveCount(8);
  await expect(links.filter({ hasText: '#mandate' })).toHaveAttribute('data-anchor-state', 'active');
  for (const target of [
    '#mandate', '#triggers', '#capabilities', '#visibility',
    '#economy', '#boundaries', '#operations', '#slices',
  ]) {
    await expect(links.filter({ hasText: target })).not.toHaveAttribute('data-anchor-state', 'missing');
  }

  const visibilityLink = links.filter({ hasText: '#visibility' });
  await visibilityLink.click();
  await expect.poll(() => dossier.locator('#visibility').evaluate(element =>
    Math.abs(element.getBoundingClientRect().top))).toBeLessThan(120);
  await expect(visibilityLink).toHaveAttribute('aria-current', 'location');

  await dossier.locator('#economy').evaluate(element => {
    const root = document.documentElement;
    const previous = root.style.scrollBehavior;
    root.style.scrollBehavior = 'auto';
    element.scrollIntoView({ behavior: 'auto', block: 'start' });
    root.style.scrollBehavior = previous;
  });
  await expect.poll(() => dossier.locator('#economy').evaluate(element =>
    Math.abs(element.getBoundingClientRect().top))).toBeLessThan(120);
  const economyLink = links.filter({ hasText: '#economy' });
  await expect(economyLink).toHaveAttribute('aria-current', 'location');

  const mandateLink = links.filter({ hasText: '#mandate' });
  await mandateLink.click();
  await expect(mandateLink).toHaveAttribute('aria-current', 'location');
  await expect.poll(() => dossier.locator('#mandate').evaluate(element =>
    Math.abs(element.getBoundingClientRect().top))).toBeLessThan(120);

  await hideMockOfflineChrome(page);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: screenshotPath(`orchestrator-watcher-linked-elements-${theme}--mocked.png`),
      fullPage: true,
    });
  }

  await page.route('**/api/projects/*/wiki/files/**', async route => {
    if (!decodeURIComponent(route.request().url()).includes(DOSSIER_PATH)) {
      await route.continue();
      return;
    }
    await json(route, {
      relPath: DOSSIER_PATH,
      content: DOSSIER_HTML.replace('id="slices"', 'data-removed-id="slices"'),
    });
  });
  await page.reload();
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(DOSSIER_PATH);
  const missing = page.getByTestId('project-wiki-linked-element').filter({ hasText: '#slices' });
  await expect(missing).toHaveAttribute('data-anchor-state', 'missing');
  await expect(missing).toHaveAttribute('aria-disabled', 'true');
  await expect(missing).toContainText('Missing');
  await missing.scrollIntoViewIfNeeded();
  await hideMockOfflineChrome(page);
  await setTheme(page, 'light');
  await page.screenshot({
    path: screenshotPath('orchestrator-watcher-linked-elements-missing--mocked.png'),
    fullPage: true,
  });
});
