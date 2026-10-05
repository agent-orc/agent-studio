import { expect, test } from '../fixtures/dev-backend';
import { setTheme, type Theme } from '../helpers/theme';

const PAGE = 'concepts/overview.md';
const READS = {
  total: 23,
  lastReadAt: '2026-07-22T10:15:00Z',
  recent: [
    { at: '2026-07-22T10:15:00Z', taskKey: 'AGT-2242' },
    { at: '2026-07-21T09:00:00Z', taskKey: 'AGT-2200' },
  ],
};

async function mockWiki(page: import('@playwright/test').Page): Promise<void> {
  const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/watch-paths', route => route.fulfill(json([
    { name: 'demo', path: '/throwaway/tasks', rootPath: '/throwaway/repo' },
  ])));
  await page.route('**/api/v1/workspaces', route => route.fulfill(json([{
    id: 'workspace-demo',
    displayName: 'Workspace',
    sortOrder: 0,
    isDefault: true,
    color: null,
    createdAt: '2026-07-22T10:00:00Z',
    projects: [{
      sourceType: 'local-folder',
      id: 'PROJ-DEMO',
      displayName: 'demo',
      shortCode: 'DEM',
      workspaceId: 'workspace-demo',
      color: null,
      cliDefault: null,
      modelDefault: null,
      sortOrder: 0,
      storageLocation: '/throwaway/tasks',
      urls: [],
      archived: false,
      createdAt: '2026-07-22T10:00:00Z',
    }],
  }])));
  await page.route('**/api/crash-recovery/pending', route => route.fulfill(json({ pending: [] })));
  await page.route('**/api/projects/demo/wiki/tree', route => route.fulfill(json({
    projectName: 'demo',
    baseDir: '/repo/docs',
    exists: true,
    root: [{
      name: 'concepts',
      title: 'Concepts',
      relPath: 'concepts',
      type: 'folder',
      children: [{
        name: 'overview.md',
        title: 'Overview',
        relPath: PAGE,
        type: 'md',
        children: [],
        metadata: {
          documentMode: 'documentation',
          temporalState: 'present',
          implementationState: 'implemented',
          companionPath: `${PAGE}.meta.json`,
          agentReads: READS,
        },
      }],
    }],
  })));
  await page.route('**/api/projects/demo/wiki/folder/concepts', route => route.fulfill(json({
    path: 'concepts',
    name: 'Concepts',
    children: [{
      name: 'overview.md',
      relPath: PAGE,
      kind: 'page',
      fileType: 'md',
      title: 'Overview',
      summary: 'Observed agent usage belongs to the page companion.',
      updatedAt: '2026-07-22T10:00:00Z',
      size: 2048,
      childCount: null,
      agentReads: READS,
    }],
  })));
  await page.route(`**/api/projects/demo/wiki/files/${PAGE}`, route => route.fulfill(json({
    relPath: PAGE,
    content: '# Overview\n\nObserved agent usage belongs to the page companion.',
  })));
  await page.route(`**/api/projects/demo/wiki/history/${PAGE}`, route => route.fulfill(json({
    relPath: PAGE,
    model: null,
    metadata: {
      model: null, updatedAt: null, reason: null, taskKey: null,
      status: null, runCount: null, hasFrontmatter: false,
    },
    commits: [],
  })));
  await page.route('**/api/projects/demo/wiki/pulse**', route => route.fulfill(json({
    projectName: 'demo',
    baseDir: '/repo/docs',
    exists: true,
    generatedAtUtc: '2026-07-22T10:15:00Z',
    feed: { available: true, reason: null, items: [] },
    inbox: { available: true, reason: null, count: 0, items: [] },
    drift: {
      available: true, reason: null, overallGrade: 'Empty', areas: [],
      counts: { fresh: 0, aging: 0, stale: 0, graded: 0 },
    },
    critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
  })));
  await page.route('**/api/projects/demo/wiki/grading/status**', route =>
    route.fulfill(json({ status: null })));
  await page.route('**/api/projects/demo/wiki/home', route =>
    route.fulfill(json({ sections: [] })));
  await page.route('**/api/cli/maintenance-model', route =>
    route.fulfill(json({ cliType: 'claude', model: 'claude-sonnet-5', thinkingLevel: null })));
  await page.route('**/api/projects/demo/style-guides', route => route.fulfill(json({
    projectKey: 'demo',
    projectDisplayName: 'Demo',
    technologies: [],
    guides: [],
    warnings: [],
    snapshotId: 'agent-read-test',
    capturedAtUtc: '2026-07-22T10:15:00Z',
    refreshAfterUtc: '2026-07-22T10:20:00Z',
  })));
}

test.describe('Wiki agent read evidence', () => {
  for (const theme of ['light', 'dark'] as const satisfies readonly Theme[]) {
    test(`shows folder Reads and page history in ${theme} theme`, async ({ page, devBackend }, testInfo) => {
      expect(devBackend.port).toBe(5030);
      await mockWiki(page);
      await page.goto('/#/projects/demo/wiki');
      await setTheme(page, theme);
      await expect(page.getByTestId('project-wiki-section')).toBeVisible({ timeout: 10_000 });

      await page.getByTestId('project-wiki-folder-label-concepts').click();
      await expect(page.getByRole('columnheader', { name: 'Reads' })).toBeVisible();
      await expect(page.getByTestId(`wiki-folder-reads-${PAGE}`)).toHaveText('23');
      const pageTitle = page.getByTestId(`wiki-folder-title-${PAGE}`);
      await expect(pageTitle).toHaveText('Overview');
      const titleBox = await pageTitle.evaluate(element => ({
        clientWidth: element.clientWidth,
        scrollWidth: element.scrollWidth,
      }));
      expect(titleBox.clientWidth).toBeGreaterThanOrEqual(titleBox.scrollWidth);
      await page.screenshot({
        path: testInfo.outputPath(`wiki-agent-reads-folder-${theme}--mocked.png`),
        fullPage: true,
      });

      await page.getByTestId(`wiki-folder-row-${PAGE}`).click();
      const panel = page.getByTestId('project-wiki-agent-reads-panel');
      await expect(panel).toBeVisible();
      await expect(page.getByTestId('project-wiki-agent-reads-total')).toHaveText('23');
      await expect(page.getByTestId('project-wiki-agent-reads-recent')).toContainText('AGT-2242');
      await expect(page.getByTestId('project-wiki-agent-reads-recent')).toContainText('AGT-2200');
      await page.screenshot({
        path: testInfo.outputPath(`wiki-agent-reads-meta-${theme}--mocked.png`),
        fullPage: true,
      });
    });
  }
});
