import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

test.describe('CLI configuration card', () => {
  test('does not offer Copilot configuration for a Claude CLI error', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `claude-cli-error-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      promptMarkdown: '# Claude CLI error regression',
      targetState: '2-ready'
    });

    try {
      await page.route('**/api/v1/projects/*/tasks/*/start**', async (route) => {
        await route.fulfill({
          status: 400,
          contentType: 'application/json',
          body: JSON.stringify({ error: 'claude CLI is not installed or not on PATH' })
        });
      });

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
      await page.getByTestId('commandbar').getByRole('button', { name: /Start/ }).click();

      await expect(page.getByRole('heading', { name: 'Task action failed' })).toBeVisible();
      await expect(page.getByTestId('error-dialog-message')).toHaveText('claude CLI is not installed or not on PATH');
      await expect(page.getByRole('button', { name: /Configure CLI/ })).toHaveCount(0);
      await expect(page.getByText('GitHub Token')).toHaveCount(0);
      await page.screenshot({ path: 'e2e/_baselines/cli-config-hidden-for-claude.png', fullPage: true });
    } finally {
      await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });

  test('still offers Copilot configuration for a Copilot CLI error', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `copilot-cli-error-${Date.now()}`,
      watchPath,
      cliType: 'copilot',
      agent: 'copilot',
      promptMarkdown: '# Copilot CLI error regression',
      targetState: '2-ready'
    });

    try {
      await page.route('**/api/v1/projects/*/tasks/*/start**', async (route) => {
        await route.fulfill({
          status: 400,
          contentType: 'application/json',
          body: JSON.stringify({ error: 'copilot CLI is not installed or not on PATH' })
        });
      });

      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await expect(page.getByTestId('pane-protocol')).toBeVisible({ timeout: 10_000 });
      await page.getByTestId('commandbar').getByRole('button', { name: /Start/ }).click();

      await expect(page.getByRole('heading', { name: 'Task action failed' })).toBeVisible();
      await expect(page.getByRole('button', { name: /Configure CLI/ })).toBeVisible();
    } finally {
      await api(`/api/tasks/${encodeURIComponent(job.id)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' });
    }
  });
});
