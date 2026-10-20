import { expect, test } from '../fixtures/dev-backend';
import type { Page, TestInfo } from '@playwright/test';
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
  devBackend,
}, testInfo) => {
  test.setTimeout(120_000);
  const paths = await (await fetch(`${devBackend.baseUrl}/api/watch-paths`)).json() as { name: string }[];
  let catalogue: Catalogue | null = null;
  for (const candidate of paths) {
    const response = await fetch(
      `${devBackend.baseUrl}/api/projects/${encodeURIComponent(candidate.name)}/workbenches?history=true`,
    );
    if (!response.ok) continue;
    const value = await response.json() as Catalogue;
    if (value.items.some(item => item.entryPath.replace(/\\/g, '/') ===
      'docs/operations/admin-design-guideline/index.html')) {
      catalogue = value;
      break;
    }
  }
  expect(catalogue, 'expected the Workbench catalogue to own the living Style Guide').not.toBeNull();
  const styleGuide = catalogue!.items.find(item => item.entryPath.replace(/\\/g, '/') ===
    'docs/operations/admin-design-guideline/index.html')!;
  const pending = catalogue!.items.find(item => item.status === 'decision-pending' && item.id !== styleGuide.id);
  const implementation = catalogue!.items.find(item => item.status === 'active' || item.status === 'decided');
  expect(pending, 'expected a decision-pending Dossier fixture').toBeTruthy();
  expect(implementation, 'expected an in-implementation Dossier fixture').toBeTruthy();

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
        projectName: catalogue!.projectName,
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
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => {
    localStorage.removeItem('atp.studio.explorerSections');
    localStorage.removeItem('atp.studio.explorer.expanded');
    localStorage.removeItem('atp.studio.panelState.v1');
    localStorage.setItem('atp.studio.sidebarWidth', '240');
    sessionStorage.removeItem('atp.studio.explorer.workbenches.state.v2');
  });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await closeOrchestratorIfOpen(page);

  const projectRow = page.locator('[data-testid^="studio-explorer-project-row-"]').first();
  await expect(projectRow).toBeVisible({ timeout: 20_000 });
  const projectName = (await projectRow.getAttribute('data-testid'))!
    .replace('studio-explorer-project-row-', '');

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
  await expect(pendingTooltip).toContainText('Decision pending');

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

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await cardList.screenshot({ path: evidencePath(testInfo, `dossier-cards-${theme}.png`) });
    await page.getByTestId('studio-sidebar').screenshot({
      path: evidencePath(testInfo, `workspace-tree-after-narrow-${theme}--real.png`),
    });
  }

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
