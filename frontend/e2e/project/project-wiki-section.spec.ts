import { test, expect, type Locator, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';
import { setTheme } from '../helpers/theme';

/**
 * Project-level Knowledge surface. Verifies that the root tree renders grouped
 * by category, that selecting a page opens its rendered Markdown
 * in the viewer, and that the filter narrows the tree. Navigation uses the
 * deep-link hash contract (`#/projects/<slug>/wiki`) so the spec does not
 * depend on the kanban landing-page open button.
 *
 * Screenshots land in the orchestrator job results dir when
 * PROJECT_WIKI_RESULTS_DIR is set; otherwise a sibling of the spec so a
 * stand-alone run stays useful.
 */

interface WatchPath { name: string; path: string }
interface WikiTreeNodeFixture {
  type: 'folder' | 'md' | 'html' | 'json';
  children?: WikiTreeNodeFixture[];
  metadata?: unknown;
}
interface WikiTreeFixture {
  exists: boolean;
  root: WikiTreeNodeFixture[];
}

const SCREENSHOT_DIR = (() => {
  const fromEnv = process.env.PROJECT_WIKI_RESULTS_DIR;
  if (fromEnv && fromEnv.trim()) return fromEnv;
  return path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-wiki-section');
})();

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function countWikiDocs(nodes: readonly WikiTreeNodeFixture[] = []): number {
  return nodes.reduce((count, node) => {
    if (node.type === 'folder') {
      return count + countWikiDocs(node.children ?? []);
    }
    return count + 1;
  }, 0);
}

async function resetWikiStorage(page: Page): Promise<void> {
  await page.goto('/');
  await page.evaluate(() => {
    for (const key of Object.keys(localStorage)) {
      if (key.startsWith('atp.projectWiki.v1.')) localStorage.removeItem(key);
      if (key.startsWith('atp.projectShell.v1.')) localStorage.removeItem(key);
      if (key === 'atp.studio.panelState.v1') localStorage.removeItem(key);
      if (key === 'atp.wikiMetaPanel.v1') localStorage.removeItem(key);
    }
  });
}

async function dragHorizontal(page: Page, locator: Locator, deltaX: number): Promise<void> {
  const box = await locator.boundingBox();
  expect(box, 'splitter bounds').toBeTruthy();
  const startX = box!.x + (box!.width / 2);
  const startY = box!.y + (box!.height / 2);
  await page.mouse.move(startX, startY);
  await page.mouse.down();
  await page.mouse.move(startX + deltaX, startY, { steps: 5 });
  await page.mouse.up();
}

test.describe('Project detail - Knowledge section', () => {
  let projectName = '';

  test.beforeAll(async () => {
    fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
    const paths = await api<WatchPath[]>('/api/watch-paths');
    expect(paths.length).toBeGreaterThan(0);

    const prioritized = [
      ...paths.filter(p => /agent.?software|agent.?studio|agent.?task/i.test(p.name)),
      ...paths
    ];
    const candidates = Array.from(new Map(prioritized.map(p => [p.name, p])).values());

    for (const candidate of candidates) {
      const tree = await api<WikiTreeFixture>(
        `/api/projects/${encodeURIComponent(candidate.name)}/wiki/tree`
      );
      if (tree.exists && countWikiDocs(tree.root) > 1) {
        projectName = candidate.name;
        break;
      }
    }

    expect(projectName, 'expected at least one project with two populated wiki pages').not.toBe('');
  });

  test.beforeEach(async ({ page }) => {
    await resetWikiStorage(page);
  });

  test('missing Dossier deep link explains the missing page in English', async ({ page }) => {
    const output = process.env.JOB_RESULTS_DIR?.trim() || SCREENSHOT_DIR;
    fs.mkdirSync(output, { recursive: true });
    const missingPath = 'notes-export/index.html';
    for (const theme of ['light', 'dark'] as const) {
      await page.goto(`/#/projects/${slugFor(projectName)}/wiki?page=${encodeURIComponent(missingPath)}`);
      await setTheme(page, theme);
      const notice = page.getByTestId('project-wiki-deeplink-missing');
      await expect(notice).toContainText(`The linked page ${missingPath} was not found.`);
      await expect(notice.getByRole('button', { name: 'Dismiss notice' })).toBeVisible();
      await notice.screenshot({ path: path.join(output, `wiki-missing-dossier-${theme}.png`) });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`/#/projects/${slugFor(projectName)}/wiki?page=${encodeURIComponent(missingPath)}`);
    await setTheme(page, 'light');
    const notice = page.getByTestId('project-wiki-deeplink-missing');
    await expect(notice).toContainText(`The linked page ${missingPath} was not found.`);
    await notice.screenshot({ path: path.join(output, 'wiki-missing-dossier-390px.png') });
  });

  test('wiki search choice renders in both themes', async ({ page }) => {
    await page.route('**/api/projects/*/wiki/search*', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ query: 'guide', semanticUsed: false, expandedTerms: [], durationMs: 12,
        results: [{ relPath: 'concepts/overview.md', title: 'Concept overview', kind: 'md',
          snippet: 'A <em>guide</em> to the project', score: 0.9, updatedAt: '2026-07-10T08:00:00Z' }] }),
    }));
    const output = process.env.JOB_RESULTS_DIR?.trim() || SCREENSHOT_DIR;
    fs.mkdirSync(output, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);
      await page.evaluate(value => localStorage.setItem('atp.studio.theme', value), theme);
      await page.reload();
      const search = page.getByTestId('project-wiki-search');
      await expect(search).toBeVisible({ timeout: 30_000 });
      await search.fill('guide');
      const results = page.getByTestId('wiki-search-results');
      await expect(results.getByRole('group', { name: 'Darstellung der Suchtreffer' })).toBeVisible();
      await results.screenshot({ path: path.join(output, `wiki-search-${theme}--mocked.png`), animations: 'disabled' });
    }
  });

  test('Knowledge rail mounts the root tree and opens a page', async ({ page }) => {
    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);

    const panel = page.getByTestId('project-shell-panel-wiki');
    await expect(panel).toBeVisible({ timeout: 30_000 });
    await expect(panel).toHaveAttribute('data-rail-key', 'wiki');

    const section = panel.getByTestId('project-wiki-section');
    await expect(section).toBeVisible({ timeout: 10_000 });
    await page.getByTestId('project-wiki-overview-node').click();
    await expect(section.getByTestId('project-wiki-tabs-empty')).toBeVisible();

    const panelBox = await panel.boundingBox();
    const sectionBox = await section.boundingBox();
    expect(panelBox, 'wiki panel bounds').toBeTruthy();
    expect(sectionBox, 'wiki section bounds').toBeTruthy();
    expect(Math.abs(sectionBox!.x - panelBox!.x)).toBeLessThanOrEqual(1);
    expect(Math.abs(sectionBox!.width - panelBox!.width)).toBeLessThanOrEqual(1);

    // The tree renders with at least one document button.
    const tree = page.getByTestId('project-wiki-tree');
    await expect(tree).toBeVisible();
    await expect(page.getByTestId('project-wiki-root-path')).toBeVisible();
    const firstFolder = tree.locator('.pwiki__row--group').first();
    await expect(firstFolder).toBeVisible();
    await expect(firstFolder).toHaveAttribute('aria-expanded', 'false');
    await firstFolder.locator('.pwiki__label').click();
    await expect(firstFolder).toBeFocused();
    await expect(firstFolder).toHaveAttribute('aria-expanded', 'true');
    await page.keyboard.press('ArrowLeft');
    await expect(firstFolder).toHaveAttribute('aria-expanded', 'false');
    await page.keyboard.press('ArrowRight');
    await expect(firstFolder).toHaveAttribute('aria-expanded', 'true');
    await page.keyboard.press('ArrowDown');
    await expect(tree.locator('.pwiki__row').nth(1)).toBeFocused();

    const treeBoxBeforeResize = await tree.boundingBox();
    expect(treeBoxBeforeResize, 'tree bounds before resize').toBeTruthy();
    await dragHorizontal(page, page.getByTestId('project-wiki-nav-splitter'), 56);
    const treeBoxAfterResize = await tree.boundingBox();
    expect(treeBoxAfterResize, 'tree bounds after resize').toBeTruthy();
    expect(treeBoxAfterResize!.width).toBeGreaterThan(treeBoxBeforeResize!.width + 32);

    await page.getByTestId('project-wiki-toggle-nav').click();
    await expect(page.getByTestId('project-wiki-tree')).toHaveCount(0);
    await page.getByTestId('project-wiki-toggle-nav').click();
    await expect(tree).toBeVisible();
    await page.getByTestId('project-wiki-overview-node').click();

    // The meta rail folds via its own labelled head toggle. The rail stays
    // mounted (slim strip); only the body hides, and the head reports state.
    const metaToggle = page.getByTestId('project-wiki-meta-toggle');
    await expect(metaToggle).toHaveAttribute('aria-expanded', 'true');
    await metaToggle.click();
    await expect(metaToggle).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByTestId('project-wiki-workspace-meta')).toBeHidden();
    await metaToggle.click();
    await expect(metaToggle).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('project-wiki-workspace-meta')).toBeVisible();

    const firstFile = tree.locator('[data-testid^="project-wiki-file-"]').first();
    await expect(firstFile).toBeVisible({ timeout: 10_000 });
    await expect(tree.locator('[data-testid^="project-wiki-ratings-"]').first()).toBeVisible({ timeout: 10_000 });

    // Count badge reflects a non-empty tree.
    await expect(page.getByTestId('project-wiki-count')).toContainText(/\d+ pages/);

    // Viewer starts in its empty state until a document is picked.
    await expect(page.getByTestId('project-wiki-viewer-empty')).toBeVisible();

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-wiki-tree.png'), fullPage: true });

    // Open the first document and confirm rendered Markdown shows up.
    await firstFile.click();
    await expect(page.getByTestId('project-wiki-viewer-path')).toBeVisible({ timeout: 10_000 });
    const contextPanel = page.getByTestId('project-wiki-context-panel');
    await expect(contextPanel).toBeVisible({ timeout: 10_000 });
    const contextBoxBeforeResize = await contextPanel.boundingBox();
    expect(contextBoxBeforeResize, 'context panel bounds before resize').toBeTruthy();
    await dragHorizontal(page, page.getByTestId('project-wiki-context-splitter'), -48);
    const contextBoxAfterResize = await contextPanel.boundingBox();
    expect(contextBoxAfterResize, 'context panel bounds after resize').toBeTruthy();
    expect(contextBoxAfterResize!.width).toBeGreaterThan(contextBoxBeforeResize!.width + 24);

    const viewer = page.getByTestId('project-wiki-viewer');
    await expect(viewer).toBeVisible();
    await expect(viewer.locator('h1, h2, h3').first()).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('project-wiki-tab-source').click();
    await expect(page.getByTestId('project-wiki-source-editor')).toBeVisible();
    await expect(page.getByTestId('project-wiki-source-line').first()).toContainText('1');
    await page.getByTestId('project-wiki-tab-doc').click();
    await expect(viewer).toBeVisible();
    await expect(page.getByTestId('project-wiki-history-panel')).toBeVisible();

    // Meta sections have independent defaults and Linked elements precedes
    // History in DOM/reading order.
    const linkedToggle = page.getByTestId('project-wiki-section-toggle-linked-elements');
    const historyToggle = page.getByTestId('project-wiki-section-toggle-history');
    await expect(linkedToggle).toHaveAttribute('aria-expanded', 'true');
    await expect(historyToggle).toHaveAttribute('aria-expanded', 'false');
    const linkedBeforeHistory = await page.evaluate(() => {
      const linked = document.querySelector('[data-testid="project-wiki-linked-elements"]');
      const history = document.querySelector('[data-testid="project-wiki-history-panel"]');
      return !!linked && !!history
        && (linked.compareDocumentPosition(history) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
    });
    expect(linkedBeforeHistory).toBe(true);

    await linkedToggle.click();
    await historyToggle.click();
    await expect(linkedToggle).toHaveAttribute('aria-expanded', 'false');
    await expect(historyToggle).toHaveAttribute('aria-expanded', 'true');

    // Navigating between documents keeps the open meta rail open.
    const secondFile = tree.locator('[data-testid^="project-wiki-file-"]').nth(1);
    await expect(secondFile).toBeVisible({ timeout: 10_000 });
    await secondFile.click();
    await expect(page.getByTestId('project-wiki-meta-toggle')).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('project-wiki-meta-panel')).toBeVisible();
    await expect(page.getByTestId('project-wiki-section-toggle-linked-elements'))
      .toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByTestId('project-wiki-section-toggle-history'))
      .toHaveAttribute('aria-expanded', 'true');

    // A reload reads both section choices from the same browser-storage record.
    await page.reload({ waitUntil: 'networkidle' });
    await expect(page.getByTestId('project-wiki-meta-toggle')).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('project-wiki-section-toggle-linked-elements'))
      .toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByTestId('project-wiki-section-toggle-history'))
      .toHaveAttribute('aria-expanded', 'true');

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-wiki-document.png'), fullPage: true });

    // Close returns to the empty state.
    await page.getByTestId('project-wiki-close').click();
    await expect(page.getByTestId('project-wiki-viewer-empty')).toBeVisible();
  });

  test('Filter narrows the document tree', async ({ page }) => {
    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);
    await expect(page.getByTestId('project-wiki-section')).toBeVisible({ timeout: 10_000 });

    const tree = page.getByTestId('project-wiki-tree');
    const before = await tree.locator('[data-testid^="project-wiki-file-"]').count();
    expect(before).toBeGreaterThan(0);

    await page.getByTestId('project-wiki-filter-toggle').click();
    await expect(page.getByTestId('project-wiki-filter')).toBeVisible();

    // A needle that cannot match any path collapses the tree to the
    // no-match state without crashing.
    await page.getByTestId('project-wiki-filter').fill('zzz-no-such-doc-zzz');
    await expect(page.getByTestId('project-wiki-no-match')).toBeVisible({ timeout: 5_000 });
    await expect(tree.locator('[data-testid^="project-wiki-file-"]')).toHaveCount(0);

    // Clearing the filter restores the full tree.
    await page.getByTestId('project-wiki-filter').fill('');
    await expect(tree.locator('[data-testid^="project-wiki-file-"]').first()).toBeVisible({ timeout: 5_000 });
  });

  test('Classification chips open the document and its reasoning report; edit mode is reachable', async ({ page }) => {
    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);
    await expect(page.getByTestId('project-wiki-section')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('project-wiki-filter-toggle').click();
    await page.getByTestId('project-wiki-filter').fill('structure-target');

    const targetPath = 'system/architecture/backend-structure/structure-target.md';
    const file = page.getByTestId(`project-wiki-file-${targetPath}`);
    await expect(file).toBeVisible({ timeout: 10_000 });

    await page.getByTestId(`project-wiki-metric-${targetPath}-drift`).click();
    await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(targetPath);
    await page.getByTestId('project-wiki-tab-report').click();
    const reportFrame = page.getByTestId('project-wiki-report-frame');
    await expect(reportFrame).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('project-wiki-tab-report')).toHaveClass(/pwiki__tab--active/);
    await expect(reportFrame).toHaveAttribute('srcdoc', /Findings/);

    await page.reload();
    await expect(page.getByTestId('project-wiki-section')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(targetPath);
    await page.getByTestId('project-wiki-tab-report').click();
    await expect(reportFrame).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('project-wiki-edit').click();
    await expect(page.getByTestId('project-wiki-editor-shell')).toBeVisible({ timeout: 10_000 });
  });

  test('Default page opens the page drift modal without starting a CLI', async ({ page }) => {
    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);
    await expect(page.getByTestId('project-wiki-viewer-empty')).toBeVisible({ timeout: 10_000 });

    await page.getByTestId('project-wiki-drift-open-empty').click();
    const modal = page.getByTestId('project-wiki-drift-modal');
    await expect(modal).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('project-wiki-drift-cli')).toBeVisible();
    await expect(page.getByTestId('project-wiki-drift-model')).toBeVisible();
    await expect(page.getByTestId('project-wiki-drift-result')).toContainText('Knowledge page drift analysis');
    await expect(page.getByTestId('project-wiki-drift-start-cli')).toBeVisible();

    await page.getByTestId('project-wiki-drift-close').click();
    await expect(modal).toBeHidden();
  });

  test('Reload restores the open knowledge page and collapsed panels; source remains reachable', async ({ page }) => {
    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);
    await expect(page.getByTestId('project-wiki-section')).toBeVisible({ timeout: 10_000 });

    const firstFile = page.getByTestId('project-wiki-tree').locator('[data-testid^="project-wiki-file-"]').first();
    await expect(firstFile).toBeVisible({ timeout: 10_000 });
    await firstFile.click();

    const pathBefore = (await page.getByTestId('project-wiki-viewer-path').textContent())?.trim() ?? '';
    expect(pathBefore).not.toBe('');

    await page.getByTestId('project-wiki-toggle-nav').click();
    await expect(page.getByTestId('project-wiki-tree')).toHaveCount(0);
    await page.getByTestId('project-wiki-meta-toggle').click();
    await expect(page.getByTestId('project-wiki-meta-toggle')).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByTestId('project-wiki-meta-panel')).toBeHidden();
    await page.getByTestId('project-wiki-tab-source').click();
    await expect(page.getByTestId('project-wiki-source-editor')).toBeVisible();
    await page.getByTestId('project-shell-back').click();
    await expect(page.getByTestId('project-shell-sidebar-header')).toHaveCount(0);

    await page.reload({ waitUntil: 'networkidle' });
    await expect(page.getByTestId('project-wiki-section')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('project-wiki-viewer-path')).toContainText(pathBefore);
    await expect(page.getByTestId('project-wiki-tab-doc')).toHaveAttribute('aria-selected', 'true');
    await page.getByTestId('project-wiki-tab-source').click();
    await expect(page.getByTestId('project-wiki-source-editor')).toBeVisible();
    await expect(page.getByTestId('project-wiki-tree')).toHaveCount(0);
    await expect(page.getByTestId('project-wiki-meta-toggle')).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByTestId('project-wiki-meta-panel')).toBeHidden();
    await expect(page.getByTestId('project-shell-sidebar-header')).toHaveCount(0);
    await expect(page.getByTestId('project-shell-expand-nav')).toBeVisible();
  });

  test('Knowledge reader remains reachable on narrow project-hub viewports', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 900 });
    await page.goto(`/#/projects/${slugFor(projectName)}/wiki`);

    const section = page.getByTestId('project-wiki-section');
    await expect(section).toBeVisible({ timeout: 10_000 });

    const firstFile = page.getByTestId('project-wiki-tree').locator('[data-testid^="project-wiki-file-"]').first();
    await expect(firstFile).toBeVisible({ timeout: 10_000 });
    await firstFile.focus();
    await firstFile.press('Enter');

    await expect(page.getByTestId('project-wiki-reader')).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId('project-wiki-meta-panel')).toBeVisible();
    await expect(page.getByTestId('project-wiki-linked-elements')).toBeVisible();

    const hasHorizontalOverflow = await page.evaluate(
      () => document.documentElement.scrollWidth > window.innerWidth + 1
    );
    expect(hasHorizontalOverflow).toBe(false);

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-wiki-mobile-document.png'), fullPage: true });
  });

  test('Unknown project surfaces the backend 404 without crashing', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('body')).toBeVisible();

    // The wiki overview endpoint answers 404 for an unknown project; the
    // surface relies on this rather than blank-screening the user.
    const res = await page.request.get('/api/projects/__wiki-no-such-project__/wiki');
    expect(res.status()).toBe(404);
  });
});
