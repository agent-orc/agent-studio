import { test, expect } from '@playwright/test';
import { installTagUiFixture, TAG_PROJECT, TAG_DOSSIER_ID } from './tag-ui.fixture';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

const resultsDir = path.resolve(process.env['JOB_RESULTS_DIR'] ?? 'test-results');
const slug = (value: string) => value.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
test.use({ serviceWorkers: 'block', trace: 'off', video: 'off' });
test.setTimeout(240_000);
test.beforeEach(async ({ page }) => { await installTagUiFixture(page); });

test('workspace filters expose and clear a saved project-only tag at desktop and phone width', async ({ page }) => {
  mkdirSync(resultsDir, { recursive: true });
  await page.addInitScript(() => localStorage.setItem('sharedTagFilters', JSON.stringify(['project-only'])));
  await page.route('**/api/crash-recovery/pending**', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ pending: [] }),
  }));
  await page.goto('/#/board&filters=tags%3Aproject-only', { waitUntil: 'domcontentloaded', timeout: 45_000 });
  const filters = page.getByTestId('shared-tag-filters').first();
  await expect(filters).toBeVisible({ timeout: 30_000 });
  await expect(filters.getByTestId('shared-facet-filter')).toHaveValue('');
  const selected = filters.getByRole('button', { name: 'Remove tag filter project-only' });
  await expect(selected).toBeVisible();
  await page.screenshot({ timeout: 30_000, path: path.join(resultsDir, 'tag-ui-workspace-selection-desktop--mocked.png') });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(selected).toBeVisible();
  await page.screenshot({ timeout: 30_000, path: path.join(resultsDir, 'tag-ui-workspace-selection-phone--mocked.png') });
  await selected.click();
  await expect(selected).toHaveCount(0);
  await expect.poll(() => new URL(page.url()).searchParams.get('tag')).toBeNull();
});

test('a failed project registry preserves saved tags until a successful reload', async ({ page }) => {
  const project = TAG_PROJECT;
  let unavailable = true;
  await page.route('**/api/projects/*/tags', route => route.fulfill({
    status: unavailable ? 503 : 200,
    contentType: 'application/json',
    body: JSON.stringify(unavailable ? { error: 'Registry temporarily unavailable' } : { items: [
      { id: 'project-only', label: 'Project only', color: '#777', description: '', kind: 'facet' },
    ] }),
  }));
  await page.route('**/api/crash-recovery/pending**', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({ pending: [] }),
  }));
  await page.addInitScript(() => {
    // Seed once so reload exercises the state left by the failed request.
    if (!sessionStorage.getItem('tag-filter-seeded')) {
      localStorage.setItem('sharedTagFilters', JSON.stringify(['project-only']));
      sessionStorage.setItem('tag-filter-seeded', 'true');
    }
  });
  const failed = page.waitForResponse(response => response.url().includes('/tags') && response.status() === 503,
    { timeout: 60_000 });
  await page.goto(`/#/projects/${slug(project)}/workbenches`,
    { waitUntil: 'domcontentloaded', timeout: 45_000 });
  await failed;
  const filters = page.getByTestId('workbench-overview').getByTestId('shared-tag-filters');
  await expect(filters).toBeVisible();
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('sharedTagFilters') ?? '[]'))).toEqual(['project-only']);
  unavailable = false;
  await page.reload();
  await expect(filters.getByTestId('shared-facet-filter')).toHaveValue('project-only');
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('sharedTagFilters') ?? '[]'))).toEqual(['project-only']);
});

test('area and facet selection follows board, Dossier list and wiki at desktop and phone width', async ({ page }) => {
  const project = TAG_PROJECT;
  const featured = { id: TAG_DOSSIER_ID };
  mkdirSync(resultsDir, { recursive: true });
  await page.route(/\/api\/projects\/[^/]+\/areas\/execution-and-runner\/glossary$/, route => route.fulfill({
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
    localStorage.setItem('tagProposalsMock', JSON.stringify([
      { id: 'mock-tag-proposal', projectName, subjectKind: 'dossier', subjectId: dossierId,
        tagIds: ['decision'], confidence: 0.7, state: 'pending' },
      { id: 'mock-tag-proposal-2', projectName, subjectKind: 'dossier', subjectId: dossierId,
        tagIds: ['evidence'], confidence: 0.6, state: 'pending' },
    ]));
  }, { projectName: project, dossierId: featured!.id });
  await page.goto('/#/board', { waitUntil: 'domcontentloaded', timeout: 45_000 });
  // API fixtures do not carry the live hub.
  await page.addStyleTag({ content: 'app-offline-banner { display: none !important; }' });
  const closeOverlay = page.locator('app-orchestrator-side-sheet [data-testid="sidesheet-close"]');
  if (await closeOverlay.isVisible()) await closeOverlay.click({ force: true });
  const boardFilters = page.getByTestId('shared-tag-filters').first();
  await expect(boardFilters).toBeVisible({ timeout: 30_000 });
  const areaSelect = boardFilters.getByTestId('shared-area-filter');
  await expect.poll(() => areaSelect.locator('option').count()).toBeGreaterThan(1);
  await areaSelect.selectOption('execution-and-runner');
  await expect(areaSelect).toHaveValue('execution-and-runner');
  await page.screenshot({ timeout: 30_000, path: path.join(resultsDir, 'tag-ui-board-desktop--mocked.png') });

  await page.evaluate(projectSlug => { window.location.hash = `#/projects/${projectSlug}/workbenches`; }, slug(project));
  const dossier = page.getByTestId('workbench-overview');
  await expect(dossier).toBeVisible({ timeout: 30_000 });
  await expect(dossier.getByTestId('shared-area-filter')).toHaveValue('execution-and-runner');
  await expect(dossier.getByTestId('tag-chips')).toContainText('Execution and runner');
  await expect(dossier.getByTestId('tags-proposed')).toContainText('Tags proposed');
  await page.screenshot({ timeout: 30_000, path: path.join(resultsDir, 'tag-ui-dossiers-desktop--mocked.png') });
  await dossier.getByRole('button', { name: 'Accept', exact: true }).first().click();
  await expect(dossier.getByRole('button', { name: 'Accept', exact: true })).toHaveCount(1);
  await dossier.getByRole('button', { name: 'Reject', exact: true }).click();
  await expect(dossier.getByTestId('tags-proposed')).toHaveCount(0);

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
  await page.screenshot({ timeout: 30_000, path: path.join(resultsDir, 'tag-ui-wiki-desktop--mocked.png') });

  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByTestId('studio-ab-explorer').click();
  await page.getByTestId('project-wiki-toggle-nav').click();
  await page.screenshot({ timeout: 30_000, path: path.join(resultsDir, 'tag-ui-wiki-phone--mocked.png') });
  await page.evaluate(() => document.documentElement.setAttribute('data-studio-theme', 'dark'));
  await page.screenshot({ timeout: 30_000, path: path.join(resultsDir, 'tag-ui-wiki-phone-dark--mocked.png') });
  await page.evaluate(() => document.documentElement.setAttribute('data-studio-theme', 'light'));
  await page.evaluate(projectSlug => { window.location.hash = `#/projects/${projectSlug}/workbenches`; }, slug(project));
  await expect(dossier).toBeVisible({ timeout: 30_000 });
  await page.screenshot({ timeout: 30_000, path: path.join(resultsDir, 'tag-ui-dossiers-phone--mocked.png') });
  await page.evaluate(() => document.documentElement.setAttribute('data-studio-theme', 'dark'));
  await page.screenshot({ timeout: 30_000, path: path.join(resultsDir, 'tag-ui-dossiers-phone-dark--mocked.png') });
  await page.evaluate(() => document.documentElement.setAttribute('data-studio-theme', 'light'));
  await page.evaluate(() => { window.location.hash = '#/board'; });
  await expect(page.getByTestId('shared-area-filter').first()).toBeVisible({ timeout: 30_000 });
  await page.screenshot({ timeout: 30_000, path: path.join(resultsDir, 'tag-ui-board-phone--mocked.png') });
  await page.evaluate(() => {
    localStorage.setItem('atp.studio.theme', 'dark');
    document.documentElement.setAttribute('data-studio-theme', 'dark');
  });
  await page.screenshot({ timeout: 30_000, path: path.join(resultsDir, 'tag-ui-board-phone-dark--mocked.png') });
  await page.unrouteAll({ behavior: 'ignoreErrors' });
});

test('glossary keeps the latest area after a slower response arrives', async ({ page }) => {
  const project = TAG_PROJECT;
  let releaseRunner!: () => void;
  const runnerReleased = new Promise<void>(resolve => { releaseRunner = resolve; });
  let runnerStarted!: () => void;
  const runnerRequested = new Promise<void>(resolve => { runnerStarted = resolve; });
  let runnerFinished!: () => void;
  const runnerHandled = new Promise<void>(resolve => { runnerFinished = resolve; });
  await page.route('**/api/projects/*/areas', route => route.fulfill({ json: { items: [
    { id: 'runner', label: 'Runner', description: 'Execution terms', glossaryPath: 'docs/areas/runner/glossary.md' },
    { id: 'gates', label: 'Gates', description: 'Review terms', glossaryPath: 'docs/areas/gates/glossary.md' },
  ] } }));
  await page.route('**/api/projects/*/areas/*/glossary', async route => {
    const runner = route.request().url().includes('/runner/');
    if (runner) { runnerStarted(); await runnerReleased; }
    await route.fulfill({ json: { areaId: runner ? 'runner' : 'gates', label: runner ? 'Runner' : 'Gates',
      path: `docs/areas/${runner ? 'runner' : 'gates'}/glossary.md`, exists: true,
      terms: [{ term: runner ? 'Stale runner' : 'Current gate', definition: 'Only the selected area owns this glossary.', synonyms: [] }],
    } });
    if (runner) runnerFinished();
  });
  await page.route('**/api/crash-recovery/pending**', route => route.fulfill({ json: { pending: [] } }));
  await page.goto(`/#/projects/${slug(project)}/wiki`, { waitUntil: 'domcontentloaded', timeout: 45_000 });
  // This API fixture intentionally has no live SignalR hub.
  await page.addStyleTag({ content: 'app-offline-banner { display: none !important; }' });
  await expect(page.getByTestId('area-glossary-open')).toBeVisible({ timeout: 30_000 });
  await page.getByTestId('area-glossary-open').click();
  const select = page.getByTestId('area-glossary-select');
  await select.selectOption('runner');
  await runnerRequested;
  await select.selectOption('gates');
  const glossary = page.getByTestId('area-glossary');
  await expect(glossary).toContainText('Current gate');
  releaseRunner();
  await runnerHandled;
  await expect(select).toHaveValue('gates');
  await expect(glossary).not.toContainText('Stale runner');
  mkdirSync(resultsDir, { recursive: true });
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 844 });
    if (width === 390) {
      await page.getByTestId('studio-ab-explorer').click();
      await page.getByTestId('project-wiki-toggle-nav').click();
    }
    for (const theme of ['light', 'dark']) {
      await page.evaluate(value => document.documentElement.setAttribute('data-studio-theme', value), theme);
      await expect(glossary).toContainText('Current gate');
      await page.screenshot({ timeout: 30_000, path: path.join(resultsDir, `tag-ui-glossary-latest-${width}-${theme}--mocked.png`) });
    }
  }
  await page.unrouteAll({ behavior: 'ignoreErrors' });
});
