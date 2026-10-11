import { test, expect } from '@playwright/test';
import { api, BACKEND } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPathEntry { path: string; name: string }

/**
 * Regression for the "Protokoll fehlgeschlagen" UX gap: the failure reason
 * used to live only in a tooltip on a header badge, so the user saw "fail"
 * without ever learning *why*. The banner is now rendered inline above (or
 * in place of) the markdown body, with the actual error text and a retry
 * button that re-runs the summarisation.
 *
 * This spec creates a fresh job (no cli-output.log on disk), regenerates,
 * waits for the failure to be recorded, then asserts the banner is visible
 * with the backend's German error string and a working retry button.
 */
test.describe('Protocol pane — summary failure banner', () => {
  test('shows inline error banner with retry when summary fails', async ({ page }) => {
    const watchPaths = await api<WatchPathEntry[]>('/api/watch-paths');
    test.skip(watchPaths.length === 0, 'No watch paths configured');
    const watchPath = watchPaths[0].path;

    const slug = `e2e-summary-failure-${Date.now()}`;
    const created = await createJob({
      id: slug,
      title: 'E2E summary failure banner',
      watchPath,
      agent: 'claude',
      cliType: 'claude',
      promptMarkdown: 'placeholder',
      targetState: '1-preparation'
    });

    try {
      // Trigger a regenerate against a job that has no cli-output.log on disk —
      // the backend records a Failed SummaryState with the German prerequisite
      // message instead of returning 400.
      const res = await page.request.post(
        `${BACKEND}/api/tasks/${encodeURIComponent(created.id)}/summary/regenerate?watchPath=${encodeURIComponent(watchPath)}`
      );
      expect(res.status()).toBe(202);

      await page.setViewportSize({ width: 1600, height: 1100 });
      await page.goto(
        `/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(watchPath)}`
      );

      // F38 removed the redundant header badge — the failure state is
      // surfaced by the inline banner inside the Protocol tab body, and
      // the Protocol tab itself stays enabled in the `failed` state.
      const protocolTab = page.getByTestId('inspector-tab-protocol');
      await expect(protocolTab).toBeVisible({ timeout: 15_000 });
      await expect(protocolTab).toBeEnabled();
      await protocolTab.click();

      const banner = page.getByTestId('protocol-summary-error');
      await expect(banner).toBeVisible();
      await expect(banner).toContainText('Result could not be generated');
      const message = page.getByTestId('protocol-summary-error-message');
      await expect(message).toContainText('cli-output.log');

      const retry = page.getByTestId('protocol-regenerate-summary');
      await expect(retry).toBeVisible();
      await expect(retry).toBeEnabled();
      await expect(retry).toContainText('Try again');

      await page.screenshot({
        path: 'test-results/protocol-summary-failure-banner.png',
        fullPage: false
      });
    } finally {
      await api(
        `/api/tasks/${encodeURIComponent(created.id)}?watchPath=${encodeURIComponent(watchPath)}`,
        { method: 'DELETE' }
      ).catch(() => { /* best-effort cleanup */ });
    }
  });
});
