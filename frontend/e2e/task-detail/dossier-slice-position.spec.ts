import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3702: a promoted Dossier slice names its position and its Dossier.
 *
 * Three slice cards are created through the isolated stack's API with the
 * `dossier` provenance and the `dependsOn` edges concept promotion writes:
 * slice 2 depends on slice 1, slice 3 on slices 1 and 2. Only the Dossier
 * catalogue is mocked, because the stack's repository carries no Dossier.
 * The board card and the detail view show `Slice n of m · Dossier NOTE-W4` as
 * a link into the Dossier view, the detail view lists the predecessor under
 * Depends on, and the Dossier board lists the slices in slice order although
 * the newest card was created first.
 */
interface WatchPath { name: string; path: string; rootPath: string; }

const DOSSIER_PATH = 'docs/sliced-flow/index.html';
const DOSSIER_KEY = 'NOTE-W4';
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim()
  ? path.resolve(process.env.JOB_RESULTS_DIR)
  : path.resolve('test-results', 'dossier-slice-position');
const RUN = `${Date.now()}-${Math.floor(Math.random() * 9999)}`;
const TITLES = ['Build the note store', 'Expose the note API', 'Show notes on the board'];

let watchPath: WatchPath;
/** Created slice cards in slice order. */
const slices: { id: string; key: string; title: string }[] = [];

function sliceId(index: number): string {
  return `e2e-dossier-slice-${index + 1}-${RUN}`;
}

function projectBoardUrl(name: string): string {
  const slug = name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  return `/#/projects/${slug}/board`;
}

async function routeCatalogue(page: Page): Promise<void> {
  const workbench = {
    id: 'sliced-flow', key: DOSSIER_KEY, title: 'Sliced flow', summary: '',
    status: 'decided', phase: null, updatedAtUtc: '2026-11-09T00:00:00Z',
    entryPath: DOSSIER_PATH, valid: true, error: null, sourceTaskKeys: [],
  };
  await page.route('**/api/workbenches*', route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({
      projectName: null, count: 1, currentCount: 1, historyCount: 0,
      items: [{ projectName: watchPath.name, workbench }],
    }),
  }));
  await page.route(/\/api\/projects\/[^/]+\/workbenches(\?|$)/, route => route.fulfill({
    contentType: 'application/json',
    body: JSON.stringify({ projectName: watchPath.name, includesHistory: true, count: 1, items: [workbench] }),
  }));
}

/** A screenshot behind the application error dialog proves nothing. */
async function expectNoAppError(page: Page): Promise<void> {
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
}

async function openDetail(page: Page, index: number): Promise<void> {
  await page.goto(`/?job=${encodeURIComponent(slices[index].id)}&watchPath=${encodeURIComponent(watchPath.path)}`);
  await dismissDevErrorDialog(page);
  await expect(page.getByTestId('overview-title-block')).toBeVisible({ timeout: 15_000 });
}

test.describe.serial('Dossier slice position (AGT-3702)', () => {
  test.beforeAll(async () => {
    mkdirSync(RESULTS_DIR, { recursive: true });
    const paths = await api<WatchPath[]>('/api/watch-paths?includeFixtures=true');
    if (!paths.length) throw new Error('No watch paths configured');
    watchPath = paths.find(candidate => /agent studio worktree|playwright/i.test(candidate.name)) ?? paths[0];
    const query = `?watchPath=${encodeURIComponent(watchPath.path)}`;

    // Newest first, so slice order cannot come from creation order.
    for (const index of [2, 1, 0]) {
      await api('/api/tasks', {
        method: 'POST',
        body: JSON.stringify({
          id: sliceId(index), title: `${TITLES[index]} ${RUN}`, watchPath: watchPath.path,
          agent: 'claude', cliType: 'claude', targetState: '1-preparation', fixture: false,
          dossier: { path: DOSSIER_PATH, implementationIndex: index, implementationCount: TITLES.length },
        }),
      });
    }
    for (const index of [0, 1, 2]) {
      const detail = await api<{ info: { key?: string | null } }>(
        `/api/tasks/${encodeURIComponent(sliceId(index))}${query}`);
      slices.push({ id: sliceId(index), key: detail.info.key ?? sliceId(index), title: `${TITLES[index]} ${RUN}` });
    }
    for (const [index, dependsOn] of [[1, [0]], [2, [0, 1]]] as const) {
      await api(`/api/tasks/${encodeURIComponent(slices[index].id)}/references${query}`, {
        method: 'PUT',
        body: JSON.stringify({ dependsOn: dependsOn.map(predecessor => slices[predecessor].key) }),
      });
    }
  });

  test.afterAll(async () => {
    for (const slice of slices) {
      await api(`/api/tasks/${encodeURIComponent(slice.id)}?watchPath=${encodeURIComponent(watchPath.path)}`,
        { method: 'DELETE' }).catch(() => undefined);
    }
  });

  test('the board card names the slice position and links into the Dossier', async ({ page }) => {
    await routeCatalogue(page);
    await page.goto(projectBoardUrl(watchPath.name));
    await dismissDevErrorDialog(page);

    const card = page.getByTestId('task-card').filter({ hasText: slices[1].title }).first();
    await expect(card).toBeVisible({ timeout: 15_000 });
    const link = card.getByTestId('task-card-dossier-link');
    await expect(link).toHaveText(`Slice 2 of 3 · Dossier ${DOSSIER_KEY}`);
    await expect(link).toHaveAttribute('href', /\/workbenches\/sliced-flow$/);

    await expectNoAppError(page);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await card.screenshot({ path: path.join(RESULTS_DIR, `dossier-slice-board-card-${theme}.png`) });
    }
  });

  test('the detail view shows the slice position, the Dossier link, and the predecessor', async ({ page }) => {
    await routeCatalogue(page);
    await openDetail(page, 1);

    const link = page.getByTestId('overview-title-subline').getByTestId('overview-title-dossier');
    await expect(link).toHaveText(`Slice 2 of 3 · Dossier ${DOSSIER_KEY}`);
    await expect(link).toHaveAttribute('href', /\/workbenches\/sliced-flow$/);

    const dependsOn = page.getByTestId('references-row-dependsOn');
    await dependsOn.scrollIntoViewIfNeeded();
    await expect(dependsOn.getByTestId(`reference-chip-${slices[0].key}`)).toBeVisible();

    await expectNoAppError(page);
    const titleBlock = page.getByTestId('overview-title-block');
    await titleBlock.scrollIntoViewIfNeeded();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await titleBlock.screenshot({ path: path.join(RESULTS_DIR, `dossier-slice-detail-${theme}.png`) });
    }

    await link.click();
    await expect(page.getByTestId('studio-tab-list')).toContainText('Sliced flow');
  });

  test('the detail line stays readable at 390 px', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await routeCatalogue(page);
    await openDetail(page, 2);

    const link = page.getByTestId('overview-title-dossier');
    await expect(link).toHaveText(`Slice 3 of 3 · Dossier ${DOSSIER_KEY}`);
    await link.scrollIntoViewIfNeeded();
    const box = await link.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(390);

    await expectNoAppError(page);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.screenshot({ path: path.join(RESULTS_DIR, `dossier-slice-detail-390-${theme}.png`) });
    }
  });

  test('the Dossier board lists the slices in slice order', async ({ page }) => {
    await page.addInitScript(() => localStorage.setItem('boardGroupByDossier', '1'));
    await routeCatalogue(page);
    await page.goto(projectBoardUrl(watchPath.name));
    await dismissDevErrorDialog(page);

    const group = page.locator(`[data-dossier-path="${DOSSIER_PATH}"]`);
    await expect(group.getByRole('heading', { name: 'Sliced flow' })).toBeVisible({ timeout: 15_000 });
    await expect(group.getByTestId('dossier-board-slice')).toHaveText([
      'Slice 1 of 3', 'Slice 2 of 3', 'Slice 3 of 3',
    ]);
    await expect(group.locator('li strong')).toHaveText(slices.map(slice => slice.key));

    await expectNoAppError(page);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await group.screenshot({ path: path.join(RESULTS_DIR, `dossier-slice-board-group-${theme}.png`) });
    }
  });
});
