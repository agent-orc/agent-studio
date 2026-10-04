import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';
import { setTheme, type Theme } from '../helpers/theme';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

/**
 * The detail endpoint can lag the create endpoint by ~1 s while the
 * scanner cache refreshes. Poll until the lookup the frontend will issue
 * from `restoreFromUrl` succeeds, so the subsequent `page.goto(...)`
 * lands on the detail pane instead of the board view.
 */
async function waitForDetailVisible(jobId: string, watchPath: string): Promise<void> {
  const deadline = Date.now() + 10_000;
  let lastError: unknown = null;
  while (Date.now() < deadline) {
    try {
      await api(`/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`);
      return;
    } catch (err) {
      lastError = err;
      await new Promise(r => setTimeout(r, 200));
    }
  }
  throw new Error(`Job ${jobId} not visible via detail endpoint within 10s: ${lastError}`);
}

const SAMPLE_DIFF = `diff --git a/frontend/src/app/foo.ts b/frontend/src/app/foo.ts
index 1111111..2222222 100644
--- a/frontend/src/app/foo.ts
+++ b/frontend/src/app/foo.ts
@@ -1 +1,2 @@
-export const a = 1;
+export const a = 2;
+export const b = 3;
`;

/**
 * Status payload spans several directories so the tree has something to
 * fold ("frontend/src/app" should collapse onto one row), an aggregated
 * folder count, and a leaf at a different depth.
 */
const LONG_TREE_FILES = [
  { path: 'frontend/src/app/foo.ts', status: ' M', added: 2, removed: 1 },
  { path: 'frontend/src/app/bar.ts', status: ' M', added: 1, removed: 0 },
  { path: 'README.md', status: ' M', added: 2, removed: 1 },
  { path: 'frontend/e2e/project/project-overview-dashboard/index.ts', status: ' M', added: 1, removed: 0 },
  { path: 'frontend/src/app/features/project/index.ts', status: ' M', added: 1, removed: 0 },
  { path: 'frontend/src/app/features/project-detail/components/project-overview-dashboard/index.ts', status: ' M', added: 1, removed: 0 },
  { path: 'frontend/src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.component.html', status: ' M', added: 2, removed: 1 },
  { path: 'frontend/src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.component.scss', status: ' M', added: 2, removed: 1 },
  { path: 'frontend/src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.component.ts', status: ' M', added: 2, removed: 1 },
  { path: 'frontend/src/app/features/project-detail/components/project-overview-dashboard/project-overview-dashboard.spec.ts', status: ' M', added: 2, removed: 1 },
  ...Array.from({ length: 18 }, (_, index) => ({
    path: `frontend/src/app/features/project-detail/components/project-overview-dashboard/sections/section-${String(index + 1).padStart(2, '0')}.ts`,
    status: ' M',
    added: index + 1,
    removed: index % 3,
  })),
] as const;

const STATUS_PAYLOAD = {
  isRepo: true,
  branch: 'feature/tree-view',
  filesChanged: LONG_TREE_FILES.length,
  totalAdded: LONG_TREE_FILES.reduce((sum, file) => sum + file.added, 0),
  totalRemoved: LONG_TREE_FILES.reduce((sum, file) => sum + file.removed, 0),
  files: LONG_TREE_FILES,
  error: null
};

const REVIEW_SHOT_DIR = path.join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'git-tree-splitter');

async function captureReviewShot(page: import('@playwright/test').Page, theme: Theme, width: 'wide' | 'narrow'): Promise<void> {
  await setTheme(page, theme);
  await page.screenshot({
    path: path.join(REVIEW_SHOT_DIR, `git-tree-${width}-${theme}--mocked.png`),
    fullPage: false,
  });
}

/**
 * Tree view + maximized split layout regression spec. The git pane was
 * a flat <ul>; the user wants to scan a change set as a directory tree
 * and, when fullscreening, see the tree on the left and the diff on the
 * right.
 */
test.describe('Git pane — tree view and split layout', () => {
  test.beforeAll(() => fs.mkdirSync(REVIEW_SHOT_DIR, { recursive: true }));

  test('renders a directory tree, folds single-child chains, and splits left/right when maximized', async ({ page }) => {
    const watch = await pickWatchPath();
    const watchPath = watch.path;
    const projectName = watch.name ?? 'Runbook';
    const job = await createJob({
      title: `git-tree-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Git tree test',
      targetState: '2-ready'
    });

    try {
      // The git pane's worktree path only renders for the project's
      // active job (otherwise: "Working-tree changes belong to whichever
      // task the agent is currently editing"). Mock the runner status so
      // our fixture becomes "active" for the project.
      await page.route('**/api/v1/studio/runner/status', async (route) => {
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            projects: {
              [projectName]: {
                projectName,
                mode: 'manual',
                activeJobId: job.id,
                activeExecution: null,
                queuedJobIds: []
              }
            }
          })
        });
      });
      await page.route('**/api/tasks/*/git/status**', async (route) => {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(STATUS_PAYLOAD) });
      });
      await page.route('**/api/tasks/*/git/diff**', async (route) => {
        await route.fulfill({ status: 200, contentType: 'text/plain', body: SAMPLE_DIFF });
      });

      await waitForDetailVisible(job.id, watchPath);

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
      await page.getByTestId('studio-pane-toggle-git').click();
      await expect(page.getByTestId('pane-git')).toBeVisible();

      // Tree shape: top-level rows are README.md (file) and frontend/; below
      // that, the single-child source chain folds to src/app/.
      const tree = page.getByTestId('git-files');
      await expect(tree.locator('[data-testid="git-tree-folder"]').filter({ hasText: 'src/app/' })).toBeVisible();
      await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'README.md' })).toBeVisible();

      // Folder is auto-expanded for small change sets, so leaves are visible.
      await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'foo.ts' })).toBeVisible();
      await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'bar.ts' })).toBeVisible();

      // Click foo.ts to load the diff.
      await tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'foo.ts' }).click();
      const diff = page.getByTestId('git-diff');
      await expect(diff.locator('.d2h-file-wrapper')).toBeVisible({ timeout: 10_000 });

      await page.screenshot({ path: 'e2e/_baselines/git-tree-inpane.png', fullPage: false });

      // Maximize the pane: expect tree on the left, diff on the right.
      await page.locator('[data-testid="pane-maximize-git"]:visible').click();
      const treeCol = page.getByTestId('git-tree-col');
      const diffCol = page.getByTestId('git-diff-col');
      await expect(treeCol).toBeVisible();
      await expect(diffCol).toBeVisible();

      const treeBox = await treeCol.boundingBox();
      const diffBox = await diffCol.boundingBox();
      expect(treeBox).not.toBeNull();
      expect(diffBox).not.toBeNull();
      // Tree column is to the left of the diff column.
      expect(treeBox!.x).toBeLessThan(diffBox!.x);
      // Diff column gets the lion's share of the width.
      expect(diffBox!.width).toBeGreaterThan(treeBox!.width);
      // Side-by-side diff once the pane is maximized.
      await expect(page.getByTestId('git-diff-mode-toggle')).toHaveText('Side-by-side');

      // Regression: in maximized split mode, the tree must stretch to fill
      // its column. The SCSS `:host([data-fill="true"]) .git-tree` rule
      // depends on the component reflecting the [fill] input onto the host.
      // When that reflection is missing, the <ul> stays capped at
      // max-height: 30vh, leaving a tall empty band under the tree.
      const treeUlBox = await tree.boundingBox();
      expect(treeUlBox).not.toBeNull();
      expect(treeUlBox!.height).toBeGreaterThan(treeBox!.height * 0.9);

      await page.screenshot({ path: 'e2e/_baselines/git-tree-split-maximized.png', fullPage: false });

      const splitter = page.getByTestId('git-tree-splitter');

      // Wide is the already-working reference. The tree keeps its requested
      // width, the diff receives the remaining space, and both themes retain
      // the same geometry.
      await page.setViewportSize({ width: 1440, height: 900 });
      const wideTreeBox = await treeCol.boundingBox();
      expect(wideTreeBox).not.toBeNull();
      expect(wideTreeBox!.width).toBeCloseTo(300, 0);
      for (const theme of ['light', 'dark'] as const) {
        await captureReviewShot(page, theme, 'wide');
      }

      // The divider paints as a one-pixel hairline, while ::before supplies a
      // much larger transparent hit target. The line itself must reach the
      // pane body's lower edge instead of stopping at its bottom padding.
      const splitterGeometry = await splitter.evaluate((element) => {
        const rect = element.getBoundingClientRect();
        const hit = getComputedStyle(element, '::before');
        const body = element.closest('[data-testid="git-view-body"]')!.getBoundingClientRect();
        return {
          lineWidth: rect.width,
          hitWidth: Number.parseFloat(hit.width),
          bottomGap: body.bottom - rect.bottom,
        };
      });
      expect(splitterGeometry.lineWidth).toBeLessThanOrEqual(2);
      expect(splitterGeometry.hitWidth).toBeGreaterThanOrEqual(9);
      expect(Math.abs(splitterGeometry.bottomGap)).toBeLessThanOrEqual(1);

      // Reproduce the reported pressure case. Duplicate index.ts basenames
      // produce the pale project/ and project-overview-dashboard/ hints from
      // the user capture; none may paint or hit-test inside the diff pane.
      await page.setViewportSize({ width: 720, height: 720 });
      for (const theme of ['light', 'dark'] as const) {
        await captureReviewShot(page, theme, 'narrow');
      }

      const assertStrictPaneClipping = async () => {
        const result = await page.evaluate(() => {
          const treeCol = document.querySelector<HTMLElement>('[data-testid="git-tree-col"]')!;
          const diffCol = document.querySelector<HTMLElement>('[data-testid="git-diff-col"]')!;
          const divider = document.querySelector<HTMLElement>('[data-testid="git-tree-splitter"]')!;
          const treeRect = treeCol.getBoundingClientRect();
          const diffRect = diffCol.getBoundingClientRect();
          const dividerRect = divider.getBoundingClientRect();
          const hintRects = [...document.querySelectorAll<HTMLElement>('[data-testid="git-tree-dir-hint"]')]
            .map(element => element.getBoundingClientRect());
          const probes = hintRects.map(rect => {
            const y = Math.min(Math.max(rect.top + rect.height / 2, diffRect.top + 1), diffRect.bottom - 1);
            return document.elementsFromPoint(diffRect.left + 2, y)
              .some(element => element.closest('[data-testid="git-tree-col"]'));
          });
          return {
            treeRight: treeRect.right,
            diffLeft: diffRect.left,
            dividerLeft: dividerRect.left,
            dividerRight: dividerRect.right,
            diffWidth: diffRect.width,
            maxHintRight: Math.max(...hintRects.map(rect => rect.right), treeRect.left),
            hintHitInDiff: probes.some(Boolean),
            treeOverflow: getComputedStyle(treeCol).overflow,
          };
        });
        expect(result.treeRight).toBeLessThanOrEqual(result.dividerRight + 1);
        expect(result.diffLeft).toBeGreaterThanOrEqual(result.dividerLeft - 1);
        expect(result.diffWidth).toBeGreaterThan(120);
        expect(result.maxHintRight).toBeLessThanOrEqual(result.treeRight + 1);
        expect(result.hintHitInDiff).toBe(false);
        expect(result.treeOverflow).toBe('hidden');
      };

      await assertStrictPaneClipping();
      await tree.evaluate(element => { element.scrollTop = element.scrollHeight; });
      await assertStrictPaneClipping();

      // A slightly roomier but still constrained pane leaves meaningful drag
      // travel. Start four pixels off the visible hairline to prove that the
      // transparent hit area, not just the one-pixel element, captures input.
      await page.setViewportSize({ width: 900, height: 720 });
      const narrowTreeBefore = await treeCol.boundingBox();
      const narrowSplitter = await splitter.boundingBox();
      expect(narrowTreeBefore && narrowSplitter).toBeTruthy();
      await page.mouse.move(narrowSplitter!.x - 4, narrowSplitter!.y + narrowSplitter!.height / 2);
      await page.mouse.down();
      await page.mouse.move(narrowSplitter!.x - 36, narrowSplitter!.y + narrowSplitter!.height / 2, { steps: 6 });
      await page.mouse.up();
      const narrowTreeAfterLeft = await treeCol.boundingBox();
      expect(narrowTreeAfterLeft).not.toBeNull();
      expect(narrowTreeAfterLeft!.width).toBeLessThan(narrowTreeBefore!.width);

      const splitterAfterLeft = await splitter.boundingBox();
      expect(splitterAfterLeft).not.toBeNull();
      await page.mouse.move(splitterAfterLeft!.x + 4, splitterAfterLeft!.y + splitterAfterLeft!.height / 2);
      await page.mouse.down();
      await page.mouse.move(splitterAfterLeft!.x + 30, splitterAfterLeft!.y + splitterAfterLeft!.height / 2, { steps: 6 });
      await page.mouse.up();
      const narrowTreeAfterRight = await treeCol.boundingBox();
      expect(narrowTreeAfterRight).not.toBeNull();
      expect(narrowTreeAfterRight!.width).toBeGreaterThan(narrowTreeAfterLeft!.width);
      await page.setViewportSize({ width: 720, height: 720 });
      await assertStrictPaneClipping();

      // Collapse the folded folder and confirm the leaves disappear.
      await tree.locator('[data-testid="git-tree-folder"]').filter({ hasText: 'src/app/' }).click();
      await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'foo.ts' })).toHaveCount(0);
      await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'README.md' })).toBeVisible();
    } finally {
      await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
