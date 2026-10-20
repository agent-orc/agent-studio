import { expect, test, type Page, type TestInfo } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

interface CatalogueItem {
  id: string;
  key?: string | null;
  title: string;
  status: 'active' | 'decision-pending' | 'decided' | 'documented' | 'archived' | 'invalid';
  entryPath: string;
  openDecisionCount?: number;
  updatedAtUtc?: string;
  summary: string;
  phase: string | null;
  valid: boolean;
  error: string | null;
  sourceTaskKeys: string[];
  relatedTaskKeys: string[];
  pattern: string;
}

interface Catalogue {
  projectName: string;
  items: CatalogueItem[];
}

function evidencePath(testInfo: TestInfo, fileName: string): string {
  const configured = process.env['JOB_RESULTS_DIR']?.trim();
  if (!configured) return testInfo.outputPath(fileName);
  fs.mkdirSync(configured, { recursive: true });
  return path.join(configured, fileName);
}

async function closeOrchestratorIfOpen(page: Page): Promise<void> {
  const close = page.locator('app-orchestrator-side-sheet [data-testid="sidesheet-close"]');
  if (await close.isVisible()) await close.click({ force: true });
}

test('Dossier navigation focuses one catalogue path, persists disclosures, and collapses all', async ({
  page,
}, testInfo) => {
  test.setTimeout(120_000);
  await page.clock.install({ time: new Date('2026-10-20T09:00:00Z') });
  const projectName = 'Website';
  const catalogue: Catalogue = { projectName, items: [
    { id: 'style-guide', title: 'Style Guide', summary: 'The living admin UI style guide.',
      status: 'documented', phase: null, updatedAtUtc: '2026-10-20T08:00:00Z', valid: true,
      error: null, sourceTaskKeys: [], relatedTaskKeys: [], openDecisionCount: 0, pattern: 'ui',
      entryPath: 'docs/operations/admin-design-guideline/index.html' },
    { id: 'pending-dossier', key: 'AGT-3205', title: 'Pending decision',
      summary: 'A decision needs an operator choice.', status: 'decision-pending', phase: 'decision',
      updatedAtUtc: '2026-10-20T08:00:00Z', valid: true, error: null,
      sourceTaskKeys: [], relatedTaskKeys: [], entryPath: 'docs/decisions/pending.html', openDecisionCount: 2,
      pattern: 'concept' },
    { id: 'implementation-dossier', key: 'AGT-3204', title: 'Dossier cards and grouping',
      summary: 'Implementation slices for the Dossier view.', status: 'active', phase: 'implementation',
      updatedAtUtc: '2026-10-20T08:00:00Z', valid: true, error: null, sourceTaskKeys: ['AGT-3204'],
      relatedTaskKeys: [], entryPath: 'docs/decisions/implementation.html', pattern: 'concept' },
  ] };
  const styleGuide = catalogue.items[0];
  const pending = catalogue.items[1];
  const implementation = catalogue.items[2];

  await page.route('**/favicon.ico', route => route.fulfill({ status: 204, body: '' }));
  await page.route('**/healthz', route => route.fulfill({ status: 200, body: 'ok' }));
  await page.route('**/update/status**', route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify({ available: false }) }));
  await page.route('**/api/**', route => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.route('**/hubs/v1/studio/negotiate?*', route => route.fulfill({ status: 200,
    headers: { 'access-control-allow-origin': '*' },
    contentType: 'application/json', body: JSON.stringify({ negotiateVersion: 1,
      connectionId: 'dossier-navigation-e2e', connectionToken: 'dossier-navigation-e2e',
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }] }) }));
  await page.routeWebSocket('**/hubs/v1/studio*', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send(`{}\u001e`);
    });
  });
  await page.route('**/api/crash-recovery/pending', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ pending: [] }),
  }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
    }),
  }));
  await page.route('**/api/watch-paths**', route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify([{ name: projectName, path: '/fixtures/website', rootPath: '/fixtures/website', repositoryPath: '/fixtures/website' }]) }));
  await page.route('**/api/projects/*/workbenches**', route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify(catalogue) }));
  await page.route('**/api/workbenches**', route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify({ items: catalogue.items.map(workbench => ({
      projectName, workbench,
    })) }) }));
  await page.route(/\/api\/v1\/studio\/board(?:\?.*)?$/, route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify({
      backlog: [], preparation: [], orchestratorPrep: [], failedPickup: [], codeNotComplete: [],
      autoReview: [], review: [], completed: [], archive: [], ready: [],
      progress: [{ id: 'agt-3204', taskKey: 'Website::agt-3204', key: 'AGT-3204',
        title: 'Dossier cards and grouping', state: '3-progress', lane: '3-progress', order: 0,
        mode: 'coding', kind: 'task', watchPath: '/fixtures/website', projectName,
        folderPath: '/fixtures/website/AGT-3204', createdAt: '2026-10-20T09:00:00Z',
        lastActivity: '2026-10-20T09:00:00Z', sessionName: null, model: null, cliType: null }],
      humanReview: [], escalated: [],
    }) }));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify({ projects: {} }) }));
  await page.route('**/api/usage/cockpit**', route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify({ workspaceId: 'web', clis: [], cost: null }) }));
  await page.route('**/api/cli/quota**', route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify({ ttlMs: 60_000, snapshots: [] }) }));
  await page.route('**/api/v1/management/credential-inventory**', route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify({ generatedAt: '2026-10-20T09:00:00Z', items: [] }) }));
  const task = { id: 'agt-3204', taskKey: 'Website::agt-3204', key: 'AGT-3204',
    title: 'Dossier cards and grouping', state: '3-progress', lane: '3-progress', order: 0,
    mode: 'coding', kind: 'task', watchPath: '/fixtures/website', projectName,
    folderPath: '/fixtures/website/AGT-3204', createdAt: '2026-10-20T09:00:00Z',
    lastActivity: '2026-10-20T09:00:00Z', sessionName: null, model: null, cliType: null };
  await page.route('**/api/tasks**', route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify([task]) }));
  await page.route('**/api/tasks/reference-status**', route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify({ items: [] }) }));
  await page.route('**/api/v1/projects/*/tasks**', route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify([task]) }));
  await page.route('**/api/projects/**/workbenches/*', async route => {
    if (!route.request().url().split('?')[0].endsWith(`/${encodeURIComponent(implementation!.id)}`)) {
      await route.continue();
      return;
    }
    await route.fulfill({ status: 200, contentType: 'application/json', json: {
      workbench: implementation,
      html: '<main><h1>Dossier fixture</h1></main>',
      branch: null,
      revision: null,
      workingTreeModified: false,
      fingerprint: null,
      cards: {
        projectName: catalogue.projectName,
        dossierKey: implementation!.key ?? null,
        dossierPath: implementation!.entryPath,
        implementationSlices: 3,
        completedSlices: 1,
        cards: [
          { key: 'AGT-3203', id: 'done', title: 'Completed delivery slice',
            lane: '6-completed', state: 'completed', environments: ['production'],
            section: '§3', decision: null, implementationIndex: 0 },
          { key: 'AGT-3204', id: 'doing', title: 'Dossier cards and grouping',
            lane: '3-progress', state: 'running', environments: ['staging'],
            section: '§4', decision: 'NV-D2', implementationIndex: 1 },
        ],
      },
    } });
  });
  const consoleErrors: string[] = [];
  page.on('console', message => {
    if (message.type() !== 'error') return;
    const text = message.text();
    const lower = text.toLowerCase();
    if ((lower.includes('sandboxed and lacks the') && lower.includes('flag'))
      || (text.includes("Setting the document's base URI to 'about:blank'")
        && text.includes("base-uri 'none'"))) return;
    consoleErrors.push(text);
  });
  page.on('pageerror', error => {
    const message = error.message.toLowerCase();
    if (message.includes('sandboxed and lacks the') && message.includes('flag')) return;
    consoleErrors.push(error.message);
  });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.addInitScript(() => {
    localStorage.removeItem('atp.studio.explorerSections');
    localStorage.removeItem('atp.studio.explorer.expanded');
    localStorage.removeItem('atp.studio.panelState.v1');
    localStorage.setItem('atp.studio.sidebarWidth', '240');
    sessionStorage.removeItem('atp.studio.explorer.workbenches.state.v2');
  });
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await closeOrchestratorIfOpen(page);

  const projectRow = page.locator('[data-testid^="studio-explorer-project-row-"]').first();
  await expect(projectRow).toBeVisible({ timeout: 20_000 });
  const project = page.getByTestId(`studio-explorer-project-${projectName}`);
  await expect(project).toBeVisible({ timeout: 20_000 });
  if (await project.getAttribute('aria-expanded') === 'false') await project.click();

  const dossiers = page.getByTestId(`studio-explorer-project-workbenches-${projectName}`);
  const styleGuideRow = page.getByTestId(`studio-explorer-project-style-guide-${projectName}`);
  await expect(dossiers).toBeVisible();
  await expect(styleGuideRow).toBeVisible({ timeout: 20_000 });
  await expect(styleGuideRow).toContainText('Style Guide');
  if (styleGuide.key) {
    await expect(styleGuideRow).not.toContainText(styleGuide.key);
  }

  const waitingDecisions = catalogue!.items
    .filter(item => item.status === 'decision-pending')
    .reduce((sum, item) => sum + (item.openDecisionCount ?? 1), 0);
  await expect(dossiers).toHaveAttribute('aria-label', `Dossiers, ${waitingDecisions} decisions waiting`);

  await dossiers.click();
  const needsDecision = page.getByTestId(
    `studio-explorer-workbench-group-${projectName}-needs-decision`);
  const inImplementation = page.getByTestId(
    `studio-explorer-workbench-group-${projectName}-in-implementation`);
  const history = page.getByTestId(`studio-explorer-workbench-history-${projectName}`);
  await expect(needsDecision).toHaveAttribute('aria-expanded', 'true');
  await expect(inImplementation).toHaveAttribute('aria-expanded', 'true');
  if (await history.getAttribute('aria-expanded') === 'true') await history.click();
  await expect(history).toHaveAttribute('aria-expanded', 'false');
  await expect(page.getByTestId(`studio-explorer-workbench-status-${projectName}-${pending!.id}`))
    .toHaveAttribute('data-status', 'decision-pending');

  const pendingRow = page.getByTestId(`studio-explorer-workbench-${projectName}-${pending!.id}`);
  const pendingBox = await pendingRow.boundingBox();
  expect(pendingBox?.height).toBe(24);
  const needsDecisionBox = await needsDecision.boundingBox();
  expect(needsDecisionBox?.height).toBe(24);
  const groupChevronBox = await needsDecision.getByLabel('Collapse').boundingBox();
  const pendingGlyphBox = await page.getByTestId(
    `studio-explorer-workbench-${projectName}-${pending!.id}-glyph`,
  ).boundingBox();
  expect(groupChevronBox).toBeTruthy();
  expect(pendingGlyphBox).toBeTruthy();
  expect(Math.abs(groupChevronBox!.x - pendingGlyphBox!.x)).toBeLessThanOrEqual(1);
  expect(await pendingRow.evaluate(row => getComputedStyle(row).whiteSpace)).toBe('nowrap');
  await pendingRow.hover();
  const pendingTooltip = page.getByTestId(
    `studio-explorer-workbench-tooltip-${projectName}-${pending!.id}`,
  );
  await expect(pendingTooltip).toContainText(pending!.title);
  if (pending!.key) await expect(pendingTooltip).toContainText(pending!.key);
  await expect(pendingTooltip).toContainText('Pending decision');

  await needsDecision.click();
  await expect(needsDecision).toHaveAttribute('aria-expanded', 'false');
  const implementationRow = page.getByTestId(
    `studio-explorer-workbench-${projectName}-${implementation!.id}`);
  await implementationRow.click();
  await expect(implementationRow).toHaveAttribute('aria-current', 'page');
  const cardList = page.getByTestId('dossier-card-list');
  await expect(cardList).toBeVisible();
  await expect(cardList.getByTestId('dossier-slice-progress')).toContainText('1 / 3 implementation slices complete');
  await expect(cardList.getByTestId('dossier-card-AGT-3204')).toContainText('Dossier cards and grouping');
  await expect(cardList).toContainText('staging');
  await expect(cardList).toContainText('§4');
  await expect(cardList).toContainText('NV-D2');
  await expect(needsDecision).toHaveAttribute('aria-expanded', 'false');
  const rowBox = await implementationRow.boundingBox();
  const sidebarBox = await page.getByTestId('studio-sidebar').boundingBox();
  expect(rowBox).toBeTruthy();
  expect(sidebarBox).toBeTruthy();
  expect(rowBox!.y).toBeGreaterThanOrEqual(sidebarBox!.y);
  expect(rowBox!.y + rowBox!.height).toBeLessThanOrEqual(sidebarBox!.y + sidebarBox!.height);
  expect(rowBox!.height).toBe(24);

  const implementationItems = catalogue!.items
    .filter(item => item.status === 'active' || item.status === 'decided');
  const iconPositions: number[] = [];
  for (const item of implementationItems) {
    const row = page.getByTestId(`studio-explorer-workbench-${projectName}-${item.id}`);
    if (!await row.isVisible()) continue;
    const box = await row.boundingBox();
    const iconBox = await page.getByTestId(
      `studio-explorer-workbench-${projectName}-${item.id}-glyph`,
    ).boundingBox();
    expect(box?.height).toBe(24);
    if (iconBox) iconPositions.push(iconBox.x);
  }
  expect(new Set(iconPositions.map(value => Math.round(value))).size).toBe(1);

  const projectNameNode = page.getByTestId(`studio-explorer-project-${projectName}-name`);
  const projectNameFits = await projectNameNode.evaluate(node => node.scrollWidth <= node.clientWidth);
  expect(projectNameFits).toBe(true);
  const projectOverlay = page.getByTestId(`studio-explorer-project-${projectName}-glyph-overlay`);
  await expect(projectOverlay.getByTestId(`studio-explorer-project-auto-pickup-${projectName}`))
    .toHaveCount(1);
  await expect(page.getByTestId(`studio-explorer-project-hub-link-${projectName}`)).toBeHidden();
  await page.mouse.move(700, 300);

  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 900 });
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: evidencePath(testInfo, `dossier-${width}-${theme}--real.png`), fullPage: true });
    }
    if (width === 1440) {
      await page.getByTestId('studio-sidebar').screenshot({
        path: evidencePath(testInfo, `workspace-tree-after-narrow-${width}--real.png`),
      });
    }
  }

  await cardList.getByTestId('dossier-card-AGT-3204').click();
  await expect(page.getByTestId('studio-tab-task:Website::agt-3204')).toBeVisible();
  expect(consoleErrors).toEqual([]);

  await dossiers.getByLabel('Collapse').click();
  await expect(dossiers).toHaveAttribute('aria-expanded', 'false');
  await styleGuideRow.click();
  await expect(styleGuideRow).toHaveAttribute('aria-current', 'page');
  await expect(dossiers).toHaveAttribute('aria-expanded', 'false');

  await page.getByTestId('studio-sidebar-collapse-all').click();
  const workspaceHead = page.getByTestId('studio-explorer-workspace-head');
  await expect(workspaceHead).toHaveAttribute('aria-expanded', 'false');
  await workspaceHead.click();
  await expect(page.locator('[data-testid^="studio-explorer-project-row-"]:visible')).toHaveCount(0);

  await page.reload({ waitUntil: 'domcontentloaded' });
  await closeOrchestratorIfOpen(page);
  await expect(styleGuideRow).toHaveAttribute('aria-current', 'page', { timeout: 20_000 });
  await expect(dossiers).toHaveAttribute('aria-expanded', 'false');
});
