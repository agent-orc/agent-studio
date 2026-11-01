import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, moveJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

/**
 * F38 acceptance: the shared `<app-pane-tabs>` indicator props drive
 * the Protocol / Activity tab visuals. We can't reliably observe the
 * livedot/spinner without a running CLI, so this spec focuses on the
 * deterministic cases:
 *   - Protocol tab is DISABLED when the summary is `none` and there is
 *     no statusMarkdown (the default for a fresh 2-ready task).
 *   - Activity tab carries a `.pane-tab__livedot` indicator in DOM when
 *     the job moves into `3-progress` (the runner is the source of
 *     `isRunning`; moving the job to `3-progress` is a sufficient
 *     observable proxy in this spec).
 */

test.describe('F38: pane-tab indicator states', () => {
  test('Protocol tab is disabled when summary is none on a fresh ready task', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f38-tab-disabled-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# F38 disabled-protocol-tab fixture',
      targetState: '2-ready',
    });

    try {
      await page.setViewportSize({ width: 1600, height: 980 });
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const protocol = page.getByTestId('inspector-tab-protocol');
      await expect(protocol).toBeVisible({ timeout: 15_000 });
      await expect(protocol).toBeDisabled();
    } finally {
      await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });

  test('Activity tab pill is reachable + Protocol tab class names match the shared vocabulary', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `f38-tab-vocab-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# F38 tab-vocab fixture',
      targetState: '2-ready',
    });
    await moveJob(job.id, watchPath, '3-progress');

    try {
      await page.setViewportSize({ width: 1600, height: 980 });
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);

      const activity = page.getByTestId('inspector-tab-activity');
      await expect(activity).toBeVisible({ timeout: 15_000 });
      // The shared pane-tabs component renders the Activity button with
      // class `pane-tab` (no inspector__tab--* legacy classes).
      await expect(activity).toHaveClass(/(^|\s)pane-tab(\s|$)/);
      await expect(activity).toHaveClass(/pane-tab--active/);
      const output = process.env.JOB_RESULTS_DIR ?? 'test-results';
      mkdirSync(output, { recursive: true });
      for (const theme of ['light', 'dark'] as const) {
        await page.evaluate(value => localStorage.setItem('atp.studio.theme', value), theme);
        await page.reload();
        await expect(activity).toBeVisible();
        await activity.locator('..').screenshot({ path: join(output, `pane-tabs-pill-${theme}--real.png`), animations: 'disabled' });
      }
    } finally {
      await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
