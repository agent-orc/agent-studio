import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

async function waitForDetailVisible(jobId: string, watchPath: string): Promise<void> {
  const deadline = Date.now() + 10_000;
  let lastError: unknown = null;
  while (Date.now() < deadline) {
    try {
      await api(`/api/tasks/${encodeURIComponent(jobId)}?watchPath=${encodeURIComponent(watchPath)}`);
      return;
    } catch (err) {
      lastError = err;
      await new Promise((r) => setTimeout(r, 200));
    }
  }
  throw new Error(`Job ${jobId} not visible within 10s: ${lastError}`);
}

// README.md changed in two commits -> the (aggregated) diff concatenates two
// same-file `diff --git` blocks. The coalescing fix must render ONE file
// header with both hunks under it (AGT-2008 #3).
const README_DIFF = `diff --git a/README.md b/README.md
index 1111111..2222222 100644
--- a/README.md
+++ b/README.md
@@ -1,3 +1,4 @@
 # Project
+Intro paragraph added in commit 1.
 Getting started.
diff --git a/README.md b/README.md
index 2222222..3333333 100644
--- a/README.md
+++ b/README.md
@@ -20,3 +21,4 @@
 ## Usage
+Usage notes added in commit 2.
 Run the thing.
`;

const STATUS_PAYLOAD = {
  isRepo: true,
  branch: 'feature/git-preview',
  filesChanged: 3,
  totalAdded: 7,
  totalRemoved: 2,
  files: [
    { path: 'README.md', status: ' M', added: 2, removed: 0 },
    { path: 'docs/start/README.md', status: ' M', added: 3, removed: 1 },
    { path: 'site/index.html', status: 'A ', added: 2, removed: 0 },
  ],
  error: null,
};

const README_MD = '# Project\n\nIntro paragraph added in commit 1.\n\n## Usage\n\n- one\n- two\n';
const INDEX_HTML = `<!doctype html><html><body>
  <h1>Landing page</h1><p>Interactive sandbox preview.</p>
  <output id="script-status">waiting</output>
  <script>
    document.body.dataset.scriptRan = 'true';
    document.querySelector('#script-status').textContent = 'switcher active';
    try {
      void window.parent.document.body;
      document.body.dataset.parentAccess = 'allowed';
    } catch {
      document.body.dataset.parentAccess = 'blocked';
    }
  </script>
</body></html>`;

/**
 * AGT-2008: the git-diff surface gains (2) directory disambiguation for
 * colliding filenames, (3) one grouped header per file, and (1) a rendered
 * md/html preview. Git endpoints are stubbed so the test is independent of the
 * watched repo's real working tree.
 */
test.describe('Git pane — preview, path disambiguation, and diff grouping (AGT-2008)', () => {
  test('disambiguates colliding names, groups same-file hunks, and previews md/html', async ({ page }) => {
    const watch = await pickWatchPath();
    const watchPath = watch.path;
    const projectName = watch.name ?? 'Runbook';
    const job = await createJob({
      title: `git-preview-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Git preview test',
      targetState: '2-ready',
    });

    try {
      // The worktree view only renders for the project's active job.
      await page.route('**/api/v1/studio/runner/status', async (route) => {
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ projects: { [projectName]: { projectName, mode: 'manual', activeJobId: job.id, activeExecution: null, queuedJobIds: [] } } }),
        });
      });
      await page.route('**/api/tasks/*/git/status**', async (route) => {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(STATUS_PAYLOAD) });
      });
      await page.route('**/api/tasks/*/git/diff**', async (route) => {
        await route.fulfill({ status: 200, contentType: 'text/plain', body: README_DIFF });
      });
      await page.route('**/api/tasks/*/git/file**', async (route) => {
        const url = new URL(route.request().url());
        const p = url.searchParams.get('path');
        const content = p === 'site/index.html' ? INDEX_HTML : README_MD;
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ content, isBinary: false }) });
      });

      await waitForDetailVisible(job.id, watchPath);
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
      await page.getByTestId('pane-toggle-git').dispatchEvent('click');
      await expect(page.getByTestId('pane-git')).toBeVisible();

      // (2) Path disambiguation: both README.md rows carry a parent-dir hint;
      // the unique index.html does not.
      const hints = page.locator('[data-testid="git-tree-dir-hint"]');
      await expect(hints).toHaveCount(2);
      await expect(hints.filter({ hasText: 'root' })).toHaveCount(1);
      await expect(hints.filter({ hasText: 'docs/' })).toHaveCount(1);

      // Select the root README.md.
      const tree = page.getByTestId('git-files');
      await tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'README.md' }).first().click();

      // (3) Grouping: two `diff --git` blocks for README.md render under ONE header.
      const diff = page.getByTestId('git-diff');
      await expect(diff.locator('.d2h-file-wrapper')).toHaveCount(1, { timeout: 10_000 });
      await expect(diff.locator('.d2h-file-header')).toHaveCount(1);

      // (1) Markdown preview: toggle Preview -> rendered <cac-markdown> body.
      await page.getByTestId('git-preview-toggle').click();
      await expect(page.getByTestId('git-preview-markdown')).toBeVisible();
      await expect(page.getByTestId('git-preview-markdown')).toContainText('Usage');
      // Toggle back to the diff.
      await page.getByTestId('git-preview-toggle').click();
      await expect(page.getByTestId('git-diff')).toBeVisible();

      // (1) HTML preview: scripts run, while the opaque origin still blocks
      // access to the Studio parent document.
      await tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'index.html' }).first().click();
      await page.getByTestId('git-preview-toggle').click();
      const frame = page.getByTestId('git-preview-html');
      await expect(frame).toBeVisible();
      await expect(frame).toHaveAttribute('sandbox', 'allow-scripts');
      const preview = page.frameLocator('[data-testid="git-preview-html"]');
      await expect(preview.locator('body')).toHaveAttribute('data-script-ran', 'true');
      await expect(preview.locator('body')).toHaveAttribute('data-parent-access', 'blocked');
      await expect(preview.locator('#script-status')).toHaveText('switcher active');
    } finally {
      await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
