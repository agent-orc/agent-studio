import { test, expect } from '../fixtures/dev-backend';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

const resultsDir = path.resolve(process.env['JOB_RESULTS_DIR'] ?? 'test-results');
const slug = (value: string) => value.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

test('area and facet selection follows board, Dossier list and wiki at desktop and phone width', async ({ page, devBackend }) => {
  const watchPaths = await (await fetch(`${devBackend.baseUrl}/api/watch-paths`)).json() as { name: string }[];
  expect(watchPaths.length).toBeGreaterThan(0);
  const project = watchPaths[0].name;
  const catalogue = await (await fetch(`${devBackend.baseUrl}/api/projects/${encodeURIComponent(project)}/workbenches`)).json() as {
    items: { id: string; status: string }[];
  };
  const featured = catalogue.items.find(item => ['active', 'decided', 'decision-pending'].includes(item.status));
  expect(featured).toBeTruthy();
  mkdirSync(resultsDir, { recursive: true });
  await page.route('**/api/workbenches*', async route => {
    const response = await route.fetch();
    const body = await response.json();
    const row = body.items?.find((item: { workbench?: { id: string } }) => item.workbench?.id === featured!.id);
    if (row) row.workbench.tags = ['execution-and-runner'];
    await route.fulfill({ response, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.route('**/api/projects/*/wiki/tree*', async route => {
    const response = await route.fetch();
    const body = await response.json();
    let tagged = false;
    const visit = (nodes: { type: string; children: unknown[]; tags?: string[] }[]): void => {
      for (const node of nodes) {
        if (node.type === 'folder') visit(node.children as typeof nodes);
        else if (!tagged) { node.tags = ['execution-and-runner']; tagged = true; }
      }
    };
    visit(body.root ?? []);
    await route.fulfill({ response, contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.route('**/api/projects/*/areas/execution-and-runner/glossary', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({
      areaId: 'execution-and-runner', label: 'Execution and runner', path: 'docs/areas/execution-and-runner/glossary.md',
      exists: true, terms: [{ term: 'Runner', definition: 'The service that drives a task to a terminal outcome.', synonyms: ['run loop'] }],
    }),
  }));
  await page.route('**/api/crash-recovery/pending**', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ pending: [] }),
  }));
  await page.addInitScript(({ projectName, dossierId }) => {
    localStorage.setItem('atp.studio.theme', 'light');
    localStorage.setItem('tagProposalsMock', JSON.stringify([{ id: 'mock-tag-proposal', projectName,
      subjectKind: 'dossier', subjectId: dossierId, tagIds: ['decision'], confidence: 0.7, state: 'pending' }]));
  }, { projectName: project, dossierId: featured!.id });
  await page.goto('/#/board');
  const closeOverlay = page.locator('app-orchestrator-side-sheet [data-testid="sidesheet-close"]');
  if (await closeOverlay.isVisible()) await closeOverlay.click({ force: true });
  const boardFilters = page.getByTestId('shared-tag-filters').first();
  await expect(boardFilters).toBeVisible({ timeout: 30_000 });
  const areaSelect = boardFilters.getByTestId('shared-area-filter');
  await expect.poll(() => areaSelect.locator('option').count()).toBeGreaterThan(1);
  await areaSelect.selectOption('execution-and-runner');
  await expect(areaSelect).toHaveValue('execution-and-runner');
  await page.screenshot({ path: path.join(resultsDir, 'tag-ui-board-desktop.png') });

  await page.evaluate(projectSlug => { window.location.hash = `#/projects/${projectSlug}/workbenches`; }, slug(project));
  const dossier = page.getByTestId('workbench-overview');
  await expect(dossier).toBeVisible({ timeout: 30_000 });
  await expect(dossier.getByTestId('shared-area-filter')).toHaveValue('execution-and-runner');
  await expect(dossier.getByTestId('tag-chips')).toContainText('Execution and runner');
  await expect(dossier.getByTestId('tags-proposed')).toContainText('Tags proposed');
  await page.screenshot({ path: path.join(resultsDir, 'tag-ui-dossiers-desktop.png') });

  await page.evaluate(projectSlug => { window.location.hash = `#/projects/${projectSlug}/wiki`; }, slug(project));
  const wiki = page.getByTestId('project-wiki-tree');
  await expect(wiki).toBeVisible({ timeout: 30_000 });
  await expect(wiki.getByTestId('shared-area-filter')).toHaveValue('execution-and-runner');
  if (await closeOverlay.isVisible()) await closeOverlay.click({ force: true });
  await wiki.getByTestId('area-glossary-open').click();
  await expect(page.getByTestId('area-glossary')).toBeVisible();
  await page.getByTestId('area-glossary-select').selectOption('execution-and-runner');
  await expect(page.getByTestId('area-glossary')).toContainText('The service that drives a task');
  await expect(page.getByTestId('area-glossary')).toContainText('Tagged items');
  await page.screenshot({ path: path.join(resultsDir, 'tag-ui-wiki-desktop.png') });

  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByTestId('studio-ab-explorer').click();
  await page.getByTestId('project-wiki-toggle-nav').click();
  await page.screenshot({ path: path.join(resultsDir, 'tag-ui-wiki-phone.png') });
  await page.evaluate(() => document.documentElement.setAttribute('data-studio-theme', 'dark'));
  await page.screenshot({ path: path.join(resultsDir, 'tag-ui-wiki-phone-dark.png') });
  await page.evaluate(() => document.documentElement.setAttribute('data-studio-theme', 'light'));
  await page.evaluate(projectSlug => { window.location.hash = `#/projects/${projectSlug}/workbenches`; }, slug(project));
  await expect(dossier).toBeVisible({ timeout: 30_000 });
  await page.screenshot({ path: path.join(resultsDir, 'tag-ui-dossiers-phone.png') });
  await page.evaluate(() => document.documentElement.setAttribute('data-studio-theme', 'dark'));
  await page.screenshot({ path: path.join(resultsDir, 'tag-ui-dossiers-phone-dark.png') });
  await page.evaluate(() => document.documentElement.setAttribute('data-studio-theme', 'light'));
  await page.evaluate(() => { window.location.hash = '#/board'; });
  await expect(page.getByTestId('shared-area-filter').first()).toBeVisible({ timeout: 30_000 });
  await page.screenshot({ path: path.join(resultsDir, 'tag-ui-board-phone.png') });
  await page.evaluate(() => {
    localStorage.setItem('atp.studio.theme', 'dark');
    document.documentElement.setAttribute('data-studio-theme', 'dark');
  });
  await page.screenshot({ path: path.join(resultsDir, 'tag-ui-board-phone-dark.png') });
});
