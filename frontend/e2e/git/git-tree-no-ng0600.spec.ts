import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<WatchPath> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0];
}

/**
 * The detail endpoint can lag the create endpoint by ~1 s while the
 * scanner cache refreshes (mutation invalidates, next read rescans, but
 * a fresh fixture occasionally needs a second tick before the
 * `watchPath`-filtered lookup matches). Poll until the lookup the
 * frontend will issue from `restoreFromUrl` succeeds, so the
 * subsequent `page.goto(...)` lands on the detail pane instead of the
 * board view.
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

/**
 * The git pane was throwing NG0600 ("Writing to signals is not allowed in
 * a `computed`") on every render of GitFileTreeComponent because the
 * `visibleRows` computed seeded default expansion via `expanded.set(...)`.
 * The fix lifts the seeding into a pure derivation — this spec locks the
 * regression by asserting no pageerror fires while the tree renders.
 */
const STATUS_PAYLOAD = {
  isRepo: true,
  branch: 'feature/ng0600-regression',
  filesChanged: 3,
  totalAdded: 5,
  totalRemoved: 2,
  files: [
    { path: 'frontend/src/app/foo.ts', status: ' M', added: 2, removed: 1 },
    { path: 'frontend/src/app/bar.ts', status: ' M', added: 1, removed: 0 },
    { path: 'README.md',               status: ' M', added: 2, removed: 1 }
  ],
  error: null
};

test('git file tree renders without NG0600 (signal write inside computed)', async ({ page }) => {
  const pageErrors: string[] = [];
  page.on('pageerror', err => pageErrors.push(err.stack ?? err.message));

  const watch = await pickWatchPath();
  const watchPath = watch.path;
  const projectName = watch.name ?? 'Runbook';
  const job = await createJob({
    title: `git-tree-ng0600-${Date.now()}`,
    watchPath,
    cliType: 'claude',
    agent: 'claude',
    promptMarkdown: '# NG0600 regression',
    targetState: '2-ready'
  });

  try {
    await waitForDetailVisible(job.id, watchPath);

    // The git pane's worktree path only renders for the project's
    // active job (otherwise: "Working-tree changes belong to whichever
    // task the agent is currently editing"). Mock the runner status so
    // our fixture becomes "active" for the project and the file tree
    // actually renders.
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

    await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
    await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
    await page.getByTestId('pane-toggle-git').click();
    await expect(page.getByTestId('pane-git')).toBeVisible();

    const tree = page.getByTestId('git-files');
    await expect(tree.locator('[data-testid="git-tree-folder"]').filter({ hasText: 'frontend/src/app/' })).toBeVisible();
    await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'README.md' })).toBeVisible();

    // Toggle the folded folder both ways so the user-override path also runs.
    const folder = tree.locator('[data-testid="git-tree-folder"]').filter({ hasText: 'frontend/src/app/' });
    await folder.click();
    await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'foo.ts' })).toHaveCount(0);
    await folder.click();
    await expect(tree.locator('[data-testid="git-tree-file"]').filter({ hasText: 'foo.ts' })).toBeVisible();

    const ng0600 = pageErrors.filter(msg => msg.includes('NG0600'));
    expect(ng0600, `Unexpected NG0600 pageerror(s):\n${ng0600.join('\n---\n')}`).toEqual([]);
    expect(pageErrors, `Unexpected pageerror(s):\n${pageErrors.join('\n---\n')}`).toEqual([]);
  } finally {
    await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
  }
});
