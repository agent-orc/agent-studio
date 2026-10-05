import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

/**
 * Regression for `bug-git-diff-renders-only-line-numbers-no-content`.
 *
 * Root cause: diff2html renders `.d2h-code-linenumber` as
 * `position: absolute`. Without a positioned ancestor in the surrounding
 * pane chain, the gutter elements get pinned to the viewport's initial
 * containing block, escape the `.git-view__diff` `overflow: auto` clip,
 * and visually overlap whatever is rendered below the pane (the commit
 * textarea, the buttons, anything else in the page area).
 *
 * The user sees this as "line numbers visible but content missing": the
 * gutter is rendered on top of the textarea (so line-num cells appear
 * at unexpected positions in the viewport), while the matching `.d2h-code-line`
 * content cells get clipped normally by `.git-view__diff`'s scroll.
 *
 * The fix is to add `position: relative` to `.git-view__diff` so the
 * absolute gutter cells are scoped to (and clipped by) the diff container.
 */
function buildLargeDiff(): string {
  const out: string[] = [];
  out.push('diff --git a/backend.Tests/OrchestratorChatProjectStateSnapshotTests.cs b/backend.Tests/OrchestratorChatProjectStateSnapshotTests.cs');
  out.push('index 1111111..2222222 100644');
  out.push('--- a/backend.Tests/OrchestratorChatProjectStateSnapshotTests.cs');
  out.push('+++ b/backend.Tests/OrchestratorChatProjectStateSnapshotTests.cs');
  out.push('@@ -71,7 +71,7 @@ public class OrchestratorChatProjectStateSnapshotTests');
  for (let i = 0; i < 3; i++) out.push('     // context line ' + i);
  out.push('-    OrchestratorChat.AppendProjectStateSnapshot(sb, "Runbook", projects);');
  out.push('+    OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook", projects);');
  for (let i = 0; i < 3; i++) out.push('     // context line tail ' + i);
  out.push('@@ -101,7 +101,7 @@ public class OrchestratorChatProjectStateSnapshotTests');
  for (let i = 0; i < 3; i++) out.push('     // second hunk context ' + i);
  out.push('-    OrchestratorChat.AppendProjectStateSnapshot(sb, "Other", projects);');
  out.push('+    OrchestratorChatService.AppendProjectStateSnapshot(sb, "Other", projects);');
  for (let i = 0; i < 3; i++) out.push('     // second hunk tail ' + i);
  // Padding so the diff comfortably exceeds the pane's visible height.
  for (let i = 0; i < 80; i++) out.push('     // filler context line ' + i);
  return out.join('\n') + '\n';
}

/**
 * Builds the exact shape the operator hit: a modified file followed by a
 * brand-new file, served as one unified diff. The frontend's
 * per-path `/git/diff` endpoint is built to filter to a single file, but
 * the underlying diff2html renderer must still handle multi-file output
 * correctly because the same renderer also drives the commit-detail
 * `/commit/diff` path, which can legitimately surface multi-file diffs
 * for tasks that committed several files at once.
 */
function buildMultiFileDiff(): string {
  const out: string[] = [];
  out.push('diff --git a/backend.Tests/OrchestratorChatProjectStateSnapshotTests.cs b/backend.Tests/OrchestratorChatProjectStateSnapshotTests.cs');
  out.push('index 1111111..2222222 100644');
  out.push('--- a/backend.Tests/OrchestratorChatProjectStateSnapshotTests.cs');
  out.push('+++ b/backend.Tests/OrchestratorChatProjectStateSnapshotTests.cs');
  out.push('@@ -71,7 +71,7 @@ public class OrchestratorChatProjectStateSnapshotTests');
  for (let i = 0; i < 3; i++) out.push('     // context line ' + i);
  out.push('-    OrchestratorChat.AppendProjectStateSnapshot(sb, "Runbook", projects);');
  out.push('+    OrchestratorChatService.AppendProjectStateSnapshot(sb, "Runbook", projects);');
  for (let i = 0; i < 3; i++) out.push('     // context line tail ' + i);
  // New file - whole body is added so every row is green.
  out.push('diff --git a/backend/Services/OrchestratorChatService.cs b/backend/Services/OrchestratorChatService.cs');
  out.push('new file mode 100644');
  out.push('index 0000000..3333333');
  out.push('--- /dev/null');
  out.push('+++ b/backend/Services/OrchestratorChatService.cs');
  out.push('@@ -0,0 +1,5 @@');
  out.push('+namespace OrchestratorApi.Services;');
  out.push('+');
  out.push('+public static class OrchestratorChatService');
  out.push('+{');
  out.push('+    public static void AppendProjectStateSnapshot() { }');
  out.push('+}');
  return out.join('\n') + '\n';
}

const STATUS_PAYLOAD = {
  isRepo: true,
  branch: 'feature/diff-large',
  filesChanged: 1,
  totalAdded: 2,
  totalRemoved: 2,
  files: [
    { path: 'backend.Tests/OrchestratorChatProjectStateSnapshotTests.cs', status: ' M', added: 2, removed: 2 }
  ],
  error: null
};

test.describe('Git pane — large-diff gutter must not escape the scroll container', () => {
  test('line-number gutter stays inside the diff container; the commit textarea is hit-testable', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `diff-large-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Large-diff render test',
      targetState: '2-ready'
    });

    try {
      // Worktree-isolation rule: the working-tree view only renders for the
      // runner's currently-active job. Forge the runner status so the
      // detail view treats our fixture job as the active one.
      await page.route('**/api/v1/studio/runner/status', async (route) => {
        const upstream = await route.fetch();
        const status = await upstream.json();
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            ...status,
            projects: Object.fromEntries(
              Object.entries(status.projects ?? {}).map(([name, p]: [string, unknown]) => [
                name,
                { ...(p as Record<string, unknown>), activeJobId: job.id }
              ])
            )
          })
        });
      });
      await page.route('**/api/tasks/*/git/status**', async (route) => {
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify(STATUS_PAYLOAD)
        });
      });
      await page.route('**/api/tasks/*/git/diff**', async (route) => {
        await route.fulfill({
          status: 200,
          contentType: 'text/plain',
          body: buildLargeDiff()
        });
      });

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });

      await page.getByTestId('pane-toggle-git').click();
      await expect(page.getByTestId('pane-git')).toBeVisible();
      await expect(page.getByTestId('git-files-count')).toHaveText('1 files');

      await page.getByTestId('git-files')
        .locator('[data-testid="git-tree-file"]')
        .filter({ hasText: 'OrchestratorChatProjectStateSnapshotTests.cs' })
        .click();

      const diff = page.getByTestId('git-diff');
      await expect(diff.locator('.d2h-file-wrapper')).toBeVisible({ timeout: 10_000 });

      // Sanity: the diff produced enough rows that the visible viewport
      // can't hold all of them - if it could, the clipping bug wouldn't
      // surface and we wouldn't be testing the regression at all.
      const totalGutters = await diff.locator('.d2h-code-linenumber').count();
      expect(totalGutters).toBeGreaterThan(80);

      // Find a gutter row whose layout position is below the diff
      // container's visible bottom edge, then hit-test the page at that
      // gutter's centre. Before the fix the gutter escapes the scroll
      // container (no positioned ancestor scopes the `position: absolute`),
      // so it is painted on top of whatever sits below the diff in the
      // pane (typically the commit textarea) and `elementFromPoint`
      // returns the gutter. With `position: relative` on `.git-view__diff`,
      // the gutter is clipped by the scroll container and the same point
      // resolves to the element actually rendered there (textarea / body).
      const probe = await page.evaluate(() => {
        const diffEl = document.querySelector('.git-view__diff') as HTMLElement | null;
        if (!diffEl) return null;
        const dr = diffEl.getBoundingClientRect();
        const gutters = Array.from(document.querySelectorAll('.d2h-code-linenumber')) as HTMLElement[];
        const candidates = gutters
          .map((g) => ({ g, r: g.getBoundingClientRect() }))
          .filter(({ r }) => r.top > dr.bottom + 10 && r.width > 0 && r.height > 0);
        if (!candidates.length) {
          return { diffBottom: dr.bottom, totalGutters: gutters.length, probed: 0, hits: [] as { x: number; y: number; tag: string; cls: string; escaped: boolean }[] };
        }
        const hits: { x: number; y: number; tag: string; cls: string; escaped: boolean }[] = [];
        // Sample up to 5 below-diff gutters to keep the test robust against
        // a single false-positive at a sub-pixel boundary.
        for (const { g, r } of candidates.slice(0, 5)) {
          const x = r.x + Math.max(r.width / 2, 1);
          const y = r.y + Math.max(r.height / 2, 1);
          const el = document.elementFromPoint(x, y) as HTMLElement | null;
          const escaped = !!el && (el === g || g.contains(el) || el.classList?.contains('d2h-code-linenumber') || el.classList?.contains('line-num1') || el.classList?.contains('line-num2'));
          hits.push({ x, y, tag: el?.tagName ?? 'none', cls: el?.className ?? '', escaped });
        }
        return { diffBottom: dr.bottom, totalGutters: gutters.length, probed: candidates.length, hits };
      });
      expect(probe).not.toBeNull();
      expect(probe!.probed, 'no below-diff gutter candidates found - diff too small to reproduce the bug').toBeGreaterThan(0);
      const escapedHits = probe!.hits.filter(h => h.escaped);
      expect(
        escapedHits.length,
        `${escapedHits.length}/${probe!.hits.length} gutter samples below the diff container are still painted (escape via position:absolute). Hits: ${JSON.stringify(probe!.hits)}`
      ).toBe(0);

      await page.screenshot({ path: 'test-results/git-diff-large-evidence-line-by-line.png', fullPage: false });

      // Sanity check the side-by-side renderer too: `.d2h-code-side-linenumber`
      // has the same `position: absolute` shape as `.d2h-code-linenumber`,
      // so the same fix must scope it. We trigger side-by-side by
      // maximizing the diff (in-pane diff maximize toggle).
      await page.getByTestId('git-diff-maximize').click();
      await expect(page.getByTestId('git-diff-wrap')).toHaveClass(/git-view__diff-wrap--maximized/);
      await expect(diff.locator('.d2h-file-side-diff').first()).toBeVisible({ timeout: 10_000 });

      const sideProbe = await page.evaluate(() => {
        const diffEl = document.querySelector('.git-view__diff') as HTMLElement | null;
        if (!diffEl) return null;
        const dr = diffEl.getBoundingClientRect();
        const gutters = Array.from(document.querySelectorAll('.d2h-code-side-linenumber')) as HTMLElement[];
        const hits: { tag: string; cls: string; escaped: boolean }[] = [];
        const candidates = gutters
          .map((g) => ({ g, r: g.getBoundingClientRect() }))
          .filter(({ r }) => r.top > dr.bottom + 10 && r.width > 0 && r.height > 0)
          .slice(0, 5);
        for (const { g, r } of candidates) {
          const x = r.x + Math.max(r.width / 2, 1);
          const y = r.y + Math.max(r.height / 2, 1);
          const el = document.elementFromPoint(x, y) as HTMLElement | null;
          const escaped = !!el && (el === g || g.contains(el) || el.classList?.contains('d2h-code-side-linenumber'));
          hits.push({ tag: el?.tagName ?? 'none', cls: el?.className ?? '', escaped });
        }
        return { totalGutters: gutters.length, probed: candidates.length, hits };
      });
      // In maximized mode the diff fills the viewport so there may be no
      // below-bottom gutter candidates - that is fine. We only assert
      // there is no escape when we did find candidates to probe.
      if (sideProbe && sideProbe.probed > 0) {
        const sideEscaped = sideProbe.hits.filter(h => h.escaped);
        expect(
          sideEscaped.length,
          `side-by-side: ${sideEscaped.length}/${sideProbe.hits.length} gutter samples escaped. Hits: ${JSON.stringify(sideProbe.hits)}`
        ).toBe(0);
      }
    } finally {
      await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });

  test('multi-file diff: header + content renders for every file, not just the first', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `diff-multi-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Multi-file diff test',
      targetState: '2-ready'
    });

    try {
      await page.route('**/api/v1/studio/runner/status', async (route) => {
        const upstream = await route.fetch();
        const status = await upstream.json();
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            ...status,
            projects: Object.fromEntries(
              Object.entries(status.projects ?? {}).map(([name, p]: [string, unknown]) => [
                name,
                { ...(p as Record<string, unknown>), activeJobId: job.id }
              ])
            )
          })
        });
      });
      await page.route('**/api/tasks/*/git/status**', async (route) => {
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify(STATUS_PAYLOAD)
        });
      });
      // Per-path diff endpoint - returns a multi-file payload regardless of
      // which file the user clicked, so the renderer is exercised on the
      // exact shape the bug screenshot showed.
      await page.route('**/api/tasks/*/git/diff**', async (route) => {
        await route.fulfill({
          status: 200,
          contentType: 'text/plain',
          body: buildMultiFileDiff()
        });
      });

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
      await page.getByTestId('pane-toggle-git').click();
      await expect(page.getByTestId('pane-git')).toBeVisible();

      // Maximize the pane so the split layout gives the diff the full
      // viewport - that is the shape where the bug screenshot was taken.
      await page.getByTestId('pane-maximize-git').click();

      await page.getByTestId('git-files')
        .locator('[data-testid="git-tree-file"]')
        .first()
        .click();

      const diff = page.getByTestId('git-diff');
      // Both files must render with their own d2h-file-wrapper.
      await expect(diff.locator('.d2h-file-wrapper')).toHaveCount(2, { timeout: 10_000 });

      // Acceptance criterion 4: at least one matching code-line content
      // span must be reachable per hunk via text. Pin separate matches so
      // a missing second-file render fails distinctly from a missing
      // first-file render.
      await expect(
        diff.locator('.d2h-code-line-ctn', { hasText: 'OrchestratorChatService.AppendProjectStateSnapshot(sb' })
      ).toHaveCount(1);
      await expect(
        diff.locator('.d2h-code-line-ctn', { hasText: 'public static class OrchestratorChatService' })
      ).toHaveCount(1);

      // Acceptance criterion 2: the new file must render its own header
      // (filename + ADDED tag) so the user has a separator. With our prior
      // `display: none` rule the second block had no visible header,
      // which is what the operator described as "GROSSE schwarze Fläche
      // ohne Trennlinie / Filename-Header".
      const newFileHeader = diff.locator(
        '.d2h-file-wrapper:has-text("OrchestratorChatService.cs") .d2h-file-header'
      );
      await expect(newFileHeader).toBeVisible();
      await expect(newFileHeader.locator('.d2h-tag.d2h-added')).toBeVisible();

      // Scroll the diff so the second file is in the viewport for the
      // evidence shot.
      await newFileHeader.scrollIntoViewIfNeeded();
      await page.screenshot({ path: 'test-results/git-diff-multi-file-evidence.png', fullPage: false });
    } finally {
      await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
