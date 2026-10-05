import { expect, test } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

const RESULTS_DIR = process.env.JOB_RESULTS_DIR ?? 'test-results';
const PROJECT = 'Fixture Project';
const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

test('prompt overrides are explicit and filterable in both themes', async ({ page }) => {
  fs.mkdirSync(RESULTS_DIR, { recursive: true });
  const workspaces = [{
    id: 'WS-1', displayName: 'Fixture Workspace', sortOrder: 0, isDefault: true, color: null,
    createdAt: '2026-07-10T00:00:00Z',
    projects: [{ id: 'PROJ-1', displayName: PROJECT, shortCode: 'FIX', workspaceId: 'WS-1', color: '#7c6cf2', cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: 'C:/fixtures/project', archived: false, createdAt: '2026-07-10T00:00:00Z' }],
  }];
  const grouped = { backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [], review: [], autoReview: [], humanReview: [], completed: [], archive: [] };
  const promptCatalog = { overrideDirectory: 'C:/fixtures/overrides', items: [
    { name: 'runner-fresh-start.md', title: 'Runner fresh start', description: 'Inherited system prompt', group: 'Runner', hasDefault: true, hasOverride: false, defaultChangedSinceOverride: false, slots: [], usageCount: 1 },
    {
      name: 'review-code.md',
      title: 'Review code',
      description: 'Project override',
      group: 'Review',
      hasDefault: true,
      hasGlobalOverride: false,
      hasOverride: true,
      defaultChangedSinceOverride: true,
      slots: [],
      usageCount: 1,
      projectOverrides: [{
        projectName: PROJECT,
        stepId: 'aspect-code-quality',
        promptName: 'review-code.md',
        content: 'Project review',
        orphaned: false,
        matchesDefault: false,
        addedLines: 1,
        removedLines: 1,
        baseDefaultSha: '9a388acf',
        defaultChangedSinceOverride: true,
      }],
    },
  ] };
  await page.route('**/api/**', (route) => {
    const url = new URL(route.request().url());
    if (url.pathname === '/api/v1/studio/auth/status') return route.fulfill(json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null }));
    if (url.pathname === '/api/crash-recovery/pending') return route.fulfill(json({ pending: [] }));
    if (url.pathname === '/api/environment') return route.fulfill(json({ isDev: false, devTools: { updateStableEnabled: false, deleteE2EJobsEnabled: false } }));
    if (url.pathname === '/api/v1/studio/runner/status') return route.fulfill(json({ projects: {} }));
    if (url.pathname === '/api/projects/settings') return route.fulfill(json({}));
    if (url.pathname === '/api/cli/quota') return route.fulfill(json({ at: '2026-07-10T00:00:00Z', ttlSeconds: 600, snapshots: [] }));
    if (url.pathname === '/api/cli/usage') return route.fulfill(json({ at: '2026-07-10T00:00:00Z', sessions: [] }));
    if (url.pathname === '/api/tasks/archive') return route.fulfill(json({ items: [], total: 0 }));
    if (url.pathname === '/api/v1/workspaces') return route.fulfill(json(workspaces));
    if (url.pathname === '/api/v1/studio/board') return route.fulfill(json(grouped));
    if (url.pathname === '/api/watch-paths') return route.fulfill(json([{ name: PROJECT, path: 'C:/fixtures/project', rootPath: 'C:/fixtures/project', repositoryPath: 'C:/fixtures/project' }]));
    if (url.pathname === '/api/admin/prompts/coverage') return route.fulfill(json({ items: [], totalSites: 2, coveredSites: 2, pendingSites: 0 }));
    if (url.pathname === '/api/admin/prompts') return route.fulfill(json(promptCatalog));
    if (url.pathname.endsWith('/runner-fresh-start.md')) return route.fulfill(json({ name: 'runner-fresh-start.md', title: 'Runner fresh start', description: 'Inherited system prompt', group: 'Runner', hasDefault: true, hasOverride: false, defaultContent: 'Default', overrideContent: null, baseDefaultContent: null, effectiveContent: 'Default', defaultSha: 'abc12345', baseDefaultSha: null, defaultChangedSinceOverride: false, overrideUpdatedAt: null, slots: [], usages: [] }));
    if (url.pathname.endsWith('/review-code.md')) return route.fulfill(json({
      name: 'review-code.md',
      title: 'Review code',
      description: 'Project override',
      group: 'Review',
      hasDefault: true,
      hasOverride: false,
      defaultContent: 'Default review v2',
      overrideContent: null,
      baseDefaultContent: null,
      effectiveContent: 'Default review v2',
      defaultSha: '2cfd8ede',
      baseDefaultSha: null,
      defaultChangedSinceOverride: false,
      overrideUpdatedAt: null,
      slots: [],
      usages: [],
      projectOverrides: [{
        projectName: PROJECT,
        stepId: 'aspect-code-quality',
        promptName: 'review-code.md',
        content: 'Project review',
        orphaned: false,
        matchesDefault: false,
        addedLines: 1,
        removedLines: 1,
        baseDefaultSha: '9a388acf',
        defaultChangedSinceOverride: true,
      }],
    }));
    return route.fulfill(json([]));
  });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/#/projects/fixture-project/prompts');
  const promptList = page.getByTestId('prompt-admin-list');
  await expect(promptList).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('prompt-admin-inheritance-summary')).toContainText(/\d+ overridden/);
  await expect(page.getByTestId('prompt-admin-inheritance-summary')).toContainText(/\d+ inherited/);

  const overrides = promptList.locator('[data-testid^="prompt-admin-override-"]');
  await expect.poll(() => overrides.count()).toBeGreaterThan(0);
  await expect(overrides.first()).toContainText(`overridden - ${PROJECT}`);
  await expect(page.getByTestId('prompt-admin-stale-review-code.md')).toHaveAttribute(
    'aria-label',
    'Override based on outdated shipped default - review needed',
  );
  const inherited = promptList.locator('app-tree-row.prompt-catalogue__item--inherited');
  await expect.poll(() => inherited.count()).toBeGreaterThan(0);

  await setTheme(page, 'dark');
  await page.addStyleTag({ content: '.prompt-catalogue__summary { display: none !important; } .prompt-catalogue__item--inherited { opacity: 1 !important; } .prompt-catalogue__origin { width: 1.25rem; overflow: hidden; font-size: 0 !important; padding: 0 !important; border-radius: 50% !important; }' });
  await page.screenshot({ path: path.join(RESULTS_DIR, 'prompt-overrides-before-dark--mocked.png') });
  await page.reload();
  await expect(page.getByTestId('prompt-admin-list')).toBeVisible({ timeout: 15_000 });

  const filter = page.getByTestId('prompt-admin-only-overrides');
  await filter.click();
  await expect(filter).toHaveAttribute('aria-pressed', 'true');
  await expect(promptList.locator('app-tree-row.prompt-catalogue__item--inherited')).toHaveCount(0);

  await setTheme(page, 'dark');
  await page.screenshot({ path: path.join(RESULTS_DIR, 'prompt-overrides-after-dark--mocked.png') });
  await setTheme(page, 'light');
  await page.screenshot({ path: path.join(RESULTS_DIR, 'prompt-overrides-after-light--mocked.png') });

  const projectCatalogue = page.locator('app-prompt-catalogue-nav');
  await expect(projectCatalogue).toHaveCount(1);
  await page.getByTestId('prompt-admin-item-review-code.md').click();
  await expect(page.getByTestId('prompt-admin-detail-origin'))
    .toContainText(`overridden - ${PROJECT}`);
  await expect(page.getByTestId('prompt-admin-project-drift-banner')).toContainText(
    'Project override based on outdated shipped default - review needed.',
  );
  const projectPadding = await promptList.evaluate((element) => {
    const style = getComputedStyle(element);
    return [style.paddingTop, style.paddingRight, style.paddingBottom, style.paddingLeft];
  });
  const projectPanelBox = await page.getByTestId('project-shell-panel-prompts').boundingBox();
  const projectListBox = await promptList.boundingBox();
  expect(projectPanelBox).not.toBeNull();
  expect(projectListBox).not.toBeNull();
  const projectOuterInset = [
    Math.round(projectListBox!.x - projectPanelBox!.x),
    Math.round(projectListBox!.y - projectPanelBox!.y),
  ];
  await setTheme(page, 'light');
  await page.getByTestId('prompt-admin-panel').screenshot({
    path: path.join(RESULTS_DIR, 'prompt-catalogue-project-light--mocked.png'),
  });

  await page.goto('/#/workspace/settings/prompts');
  const workspacePromptPanel = page.getByTestId('prompt-admin-panel');
  await expect(workspacePromptPanel).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('app-prompt-catalogue-nav')).toHaveCount(1);
  await expect(page.getByTestId('prompt-admin-override-review-code.md'))
    .toContainText(`overridden - ${PROJECT}`);
  await page.getByTestId('prompt-admin-item-review-code.md').click();
  await expect(page.getByTestId('prompt-admin-project-drift-banner')).toContainText(
    'Project override based on outdated shipped default - review needed.',
  );
  const workspacePadding = await page.getByTestId('prompt-admin-list').evaluate((element) => {
    const style = getComputedStyle(element);
    return [style.paddingTop, style.paddingRight, style.paddingBottom, style.paddingLeft];
  });
  const workspacePanelBox = await page.getByTestId('prompt-admin-overlay').boundingBox();
  const workspaceListBox = await page.getByTestId('prompt-admin-list').boundingBox();
  expect(workspacePanelBox).not.toBeNull();
  expect(workspaceListBox).not.toBeNull();
  const workspaceOuterInset = [
    Math.round(workspaceListBox!.x - workspacePanelBox!.x),
    Math.round(workspaceListBox!.y - workspacePanelBox!.y),
  ];
  expect(workspacePadding).toEqual(projectPadding);
  expect(workspaceOuterInset).toEqual(projectOuterInset);
  await workspacePromptPanel.screenshot({
    path: path.join(RESULTS_DIR, 'prompt-catalogue-workspace-light--mocked.png'),
  });

  await page.goto('/');
  const projectButton = page.getByTestId(`studio-explorer-project-${PROJECT}`);
  const boardButton = page.getByTestId(`studio-explorer-project-board-${PROJECT}`);
  await expect(projectButton).toBeVisible({ timeout: 15_000 });
  if (!(await boardButton.isVisible().catch(() => false))) await projectButton.click();
  await expect(boardButton).toBeVisible();
  const parsePx = (v: string) => Number.parseFloat(v) || 0;
  const [projectInset, destinationInset] = await Promise.all([
    projectButton.evaluate((el) => getComputedStyle(el).paddingLeft),
    boardButton.evaluate((el) => getComputedStyle(el).paddingLeft),
  ]);
  // AGT-2057: the Explorer hierarchy is workspace -> project -> destinations.
  // A project's Board / Hub / Wiki / Epics rows nest ONE level below the
  // project row, so the destination inset is deeper than the project inset,
  // never flush with it. (This assertion previously demanded equality, which
  // is what let the AGT-2037 flat-layout regression ship.)
  expect(parsePx(destinationInset)).toBeGreaterThan(parsePx(projectInset));
  await boardButton.click();
  await expect(boardButton).toHaveAttribute('aria-current', 'page');

  await setTheme(page, 'dark');
  await page.mouse.move(800, 50);
  await page.screenshot({ path: path.join(RESULTS_DIR, 'explorer-nav-after-dark--mocked.png') });
  // Reference shot of the pre-fix regression: force the destinations flush with
  // the project row (the flat 8px inset AGT-2037 introduced).
  await page.addStyleTag({ content: '.studio-tree-children .tree-row { padding-left: 8px !important; }' });
  await page.screenshot({ path: path.join(RESULTS_DIR, 'explorer-nav-before-dark--mocked.png') });
  await page.reload();
  await setTheme(page, 'light');
  await expect(page.getByTestId(`studio-explorer-project-${PROJECT}`)).toBeVisible({ timeout: 15_000 });
  const lightBoard = page.getByTestId(`studio-explorer-project-board-${PROJECT}`);
  if (!(await lightBoard.isVisible().catch(() => false))) await page.getByTestId(`studio-explorer-project-${PROJECT}`).click();
  await expect(lightBoard).toBeVisible();
  await page.mouse.move(800, 50);
  await page.screenshot({ path: path.join(RESULTS_DIR, 'explorer-nav-after-light--mocked.png') });
});
