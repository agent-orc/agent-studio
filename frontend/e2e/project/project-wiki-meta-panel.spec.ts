import { expect, test, type Page, type Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const PROJECT_NAME = 'Wiki Meta Panel Fixture';
const PROJECT_ID = 'PROJ-002';
const REPOSITORY_PATH = '/tmp/wiki-meta-panel-fixture';
const FIRST_PAGE = 'guide/one.md';
const SECOND_PAGE = 'guide/two.md';
const LAGEBILD_PAGE = 'operations/lagebild-2026-08/index.html';

interface WikiMockOptions {
  failedPage?: string;
  extraHtml?: { relPath: string; title: string; content: string };
}

function json(route: Route, body: unknown): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function mockWiki(page: Page, projectName: string, options: WikiMockOptions = {}): Promise<{
  updateFirstPage: () => void;
}> {
  const project = encodeURIComponent(projectName);
  const livePage = {
    content: '# One\n\n[Second page](two.md)\n\n[AGT-2050](task:AGT-2050)',
    etag: '"wiki-page-v1"',
  };
  let pendingPageUpdate = false;
  // Broad fallback first. Later feature routes have priority in Playwright.
  await page.route('**/api/**', route => json(route, []));
  await page.route('**/api/v1/studio/board**', route => json(route, {
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
  }));
  await page.route('**/api/tasks/archive**', route => json(route, {
    items: [], total: 0, offset: 0, limit: 50,
  }));
  await page.route('**/api/v1/studio/runner/status**', route => json(route, { projects: {} }));
  await page.route('**/api/cli/quota**', route => json(route, {
    at: '2026-08-10T00:00:00Z', ttlSeconds: 600, snapshots: [],
  }));
  await page.route('**/api/cli/usage**', route => json(route, {
    at: '2026-08-10T00:00:00Z', sessions: [],
  }));
  await page.route(`**/api/projects/${project}/workbenches**`, route => json(route, {
    projectName,
    includesHistory: true,
    count: 0,
    items: [],
  }));
  await page.route('**/api/watch-paths**', route => json(route, [{
    name: projectName,
    path: REPOSITORY_PATH,
    rootPath: REPOSITORY_PATH,
    repositoryPath: REPOSITORY_PATH,
  }]));
  await page.route('**/api/v1/workspaces**', route => json(route, [{
    id: 'WS-WIKI',
    displayName: 'Wiki fixtures',
    sortOrder: 0,
    isDefault: true,
    color: null,
    createdAt: '2026-08-10T00:00:00Z',
    projects: [{
      id: PROJECT_ID,
      displayName: projectName,
      shortCode: 'WIK',
      workspaceId: 'WS-WIKI',
      color: null,
      sortOrder: 0,
      storageLocation: REPOSITORY_PATH,
      rootPath: REPOSITORY_PATH,
      repositoryPath: REPOSITORY_PATH,
      urls: [],
      archived: false,
      createdAt: '2026-08-10T00:00:00Z',
    }],
  }]));
  await page.route('**/api/v1/studio/auth/status', route => json(route, {
    profile: 'local',
    bootstrapRequired: false,
    authenticated: false,
    user: null,
  }));
  await page.route(`**/api/projects/${project}/wiki/tree`, route => json(route, {
    projectName,
    baseDir: '/repo/docs',
    exists: true,
    root: [{
      name: 'guide',
      title: 'Guide',
      relPath: 'guide',
      type: 'folder',
      children: [
        { name: 'one.md', title: 'One', relPath: FIRST_PAGE, type: 'md', children: [] },
        { name: 'two.md', title: 'Two', relPath: SECOND_PAGE, type: 'md', children: [] },
      ],
    }, ...(options.extraHtml ? [{
      name: 'operations',
      title: 'Operations',
      relPath: 'operations',
      type: 'folder',
      children: [{
        name: 'lagebild-2026-08',
        title: 'lagebild-2026-08',
        relPath: 'operations/lagebild-2026-08',
        type: 'folder',
        children: [{
          name: 'index.html',
          title: options.extraHtml.title,
          relPath: options.extraHtml.relPath,
          type: 'html',
          children: [],
        }],
      }],
    }] : [])],
  }));
  await page.route(`**/api/projects/${project}/wiki/pulse**`, route => json(route, {
    projectName,
    baseDir: '/repo/docs',
    exists: true,
    generatedAtUtc: '2026-07-23T10:00:00Z',
    feed: { available: true, reason: null, items: [] },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: {
      available: true,
      reason: null,
      overallGrade: 'Empty',
      areas: [],
      counts: { fresh: 0, aging: 0, stale: 0, graded: 0 },
    },
    critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
  }));
  await page.route(`**/api/projects/${project}/wiki/grading/status**`, route =>
    json(route, { status: null }));
  await page.route('**/api/cli/maintenance-model', route => json(route, {
    cliType: 'claude',
    model: 'claude-sonnet-5',
    thinkingLevel: null,
  }));
  await page.route('**/api/tasks/reference-status', route => json(route, { items: [] }));
  await page.route(`**/api/projects/${project}/wiki/files/**`, route => {
    const relPath = decodeURIComponent(route.request().url().split('/wiki/files/')[1] ?? '');
    if (relPath === options.failedPage) {
      return route.fulfill({
        status: 404,
        contentType: 'application/json',
        body: JSON.stringify({
          error: `Page '${relPath}' is not available in Wiki source 'origin/develop'.`,
        }),
      });
    }
    const content = relPath === FIRST_PAGE
      ? livePage.content
      : relPath === options.extraHtml?.relPath
        ? options.extraHtml.content
      : '# Two\n\nSecond page body.';
    return json(route, { relPath, content });
  });
  await page.route(`**/api/projects/${project}/wiki/history/**`, route => {
    const relPath = decodeURIComponent(route.request().url().split('/wiki/history/')[1] ?? '');
    const requestedEtag = route.request().headers()['if-none-match'];
    if (requestedEtag && pendingPageUpdate) {
      livePage.content = '# One updated on disk';
      livePage.etag = '"wiki-page-v2"';
      pendingPageUpdate = false;
    }
    if (requestedEtag === livePage.etag) {
      return route.fulfill({
        status: 304,
        headers: { ETag: livePage.etag },
        body: '',
      });
    }
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      headers: { ETag: livePage.etag },
      body: JSON.stringify({
        relPath,
        model: null,
        metadata: {
          model: null,
          updatedAt: null,
          reason: null,
          taskKey: null,
          status: null,
          runCount: null,
          hasFrontmatter: false,
        },
        commits: [],
      }),
    });
  });
  return {
    updateFirstPage: () => {
      pendingPageUpdate = true;
    },
  };
}

function evidencePath(testInfo: { outputPath(fileName: string): string }, fileName: string): string {
  const results = process.env['JOB_RESULTS_DIR']?.trim();
  if (!results) return testInfo.outputPath(fileName);
  fs.mkdirSync(results, { recursive: true });
  return path.join(results, fileName);
}

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('atp.flag.vsCodeLayout', '1');
    localStorage.setItem('atp.studio.openProjectChatOnEntry.v1', '0');
    localStorage.removeItem('atp.studio.tabs.v1');
  });
});

test('meta-panel and section choices survive wiki navigation and reload', async ({ page }, testInfo) => {
  await mockWiki(page, PROJECT_NAME);

  await page.goto(`/#/projects/${PROJECT_ID}/wiki?page=${encodeURIComponent(FIRST_PAGE)}`);
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(FIRST_PAGE);

  const metaToggle = page.getByTestId('project-wiki-meta-toggle');
  const linkedToggle = page.getByTestId('project-wiki-section-toggle-linked-elements');
  const historyToggle = page.getByTestId('project-wiki-section-toggle-history');
  await expect(metaToggle).toHaveAttribute('aria-expanded', 'true');
  await expect(linkedToggle).toHaveAttribute('aria-expanded', 'true');
  await expect(historyToggle).toHaveAttribute('aria-expanded', 'false');

  const linkedBeforeHistory = await page.evaluate(() => {
    const linked = document.querySelector('[data-testid="project-wiki-linked-elements"]');
    const history = document.querySelector('[data-testid="project-wiki-history-panel"]');
    return !!linked && !!history
      && (linked.compareDocumentPosition(history) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
  });
  expect(linkedBeforeHistory).toBe(true);

  const pageLink = page.getByTestId('project-wiki-linked-element').filter({ hasText: 'Second page' });
  await expect(pageLink).toHaveAttribute('title', 'Open wiki page: Second page');
  await expect(page.getByTestId('project-wiki-linked-element').filter({ hasText: 'AGT-2050' }))
    .toHaveAttribute('title', 'Open task AGT-2050');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: testInfo.outputPath(`wiki-meta-panel-${theme}.png`),
      fullPage: true,
    });
  }

  await pageLink.click();
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(SECOND_PAGE);
  await page.getByTestId(`project-wiki-file-${FIRST_PAGE}`).click();
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(FIRST_PAGE);

  await linkedToggle.click();
  await historyToggle.click();
  await page.getByTestId(`project-wiki-file-${SECOND_PAGE}`).click();
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(SECOND_PAGE);
  await expect(metaToggle).toHaveAttribute('aria-expanded', 'true');
  await expect(linkedToggle).toHaveAttribute('aria-expanded', 'false');
  await expect(historyToggle).toHaveAttribute('aria-expanded', 'true');

  await page.reload();
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(SECOND_PAGE);
  await expect(metaToggle).toHaveAttribute('aria-expanded', 'true');
  await expect(linkedToggle).toHaveAttribute('aria-expanded', 'false');
  await expect(historyToggle).toHaveAttribute('aria-expanded', 'true');
});

test('an external page change waits for explicit reload', async ({ page }) => {
  const wiki = await mockWiki(page, PROJECT_NAME);

  await page.goto(`/#/projects/${PROJECT_ID}/wiki?page=${encodeURIComponent(FIRST_PAGE)}`);
  await expect(page.getByTestId('project-wiki-viewer')).toContainText('One');

  wiki.updateFirstPage();
  const banner = page.getByTestId('project-wiki-update-banner');
  await expect(banner).toContainText('Diese Seite wurde aktualisiert.', { timeout: 20_000 });
  await expect(page.getByTestId('project-wiki-viewer')).not.toContainText('updated on disk');

  await page.getByTestId('project-wiki-update-reload').click();
  await expect(page.getByTestId('project-wiki-viewer')).toContainText('updated on disk');
  await expect(banner).toHaveCount(0);
});

test('Lagebild opens from navigation and a missing source page shows its reason', async ({ page }, testInfo) => {
  const lagebild = fs.readFileSync(
    path.resolve(process.cwd(), '..', 'docs', LAGEBILD_PAGE),
    'utf8',
  );
  await mockWiki(page, PROJECT_NAME, {
    failedPage: SECOND_PAGE,
    extraHtml: {
      relPath: LAGEBILD_PAGE,
      title: 'Lagebild 03.08.2026',
      content: lagebild,
    },
  });

  await page.goto(`/#/projects/${PROJECT_ID}/wiki?page=${encodeURIComponent(FIRST_PAGE)}`);
  await page.getByTestId('project-wiki-chevron-operations').click();
  await page.getByTestId('project-wiki-chevron-operations/lagebild-2026-08').click();
  await page.getByTestId(`project-wiki-file-${LAGEBILD_PAGE}`).click();
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(LAGEBILD_PAGE);
  await expect(page.getByTestId('project-wiki-html-frame').contentFrame().locator('h1'))
    .toContainText('Wo wir stehen');

  await page.goto(`/#/projects/${PROJECT_ID}/wiki?page=${encodeURIComponent(SECOND_PAGE)}`);
  await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(SECOND_PAGE);
  const error = page.getByTestId('project-wiki-load-error');
  await expect(error).toHaveAttribute('role', 'alert');
  await expect(error).toContainText(
    `Page '${SECOND_PAGE}' is not available in Wiki source 'origin/develop'.`,
  );

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({
      path: evidencePath(testInfo, `wiki-source-error--mocked-${theme}.png`),
      fullPage: true,
    });
  }
});
