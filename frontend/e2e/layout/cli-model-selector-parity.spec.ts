import { test, expect, type Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob } from '../helpers/jobs';

/**
 * Cross-surface parity for the unified `<app-cli-model-selector>` chip
 * (see `docs/quality/frontend/audits/cli-model-selector-audit.md`). The same control renders in
 * the status bar (defaults), the create-task dialog (agent for new task),
 * the job-detail command-deck (agent for this job), the overview Agent row
 * (configure agent), and the code-review panel
 * (review agent).
 *
 * This spec opens two distinct call-sites - the status-bar defaults chip
 * and the create-task dialog Agent chip - and asserts the popover wears
 * the same shape (eyebrow + cli section + model section + cancel + done)
 * and lists every CLI without filtering. That is the load-bearing
 * regression: any homogenisation work that excludes a CLI from one
 * site, drops the eyebrow / footer, or diverges the popover layout
 * fails this spec.
 */

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function deleteJob(id: string, watchPath: string): Promise<void> {
  try {
    await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE',
    });
  } catch { /* best-effort cleanup */ }
}

async function assertSelectorShape(
  page: Page,
  pickerTestId: string,
): Promise<void> {
  const picker = page.getByTestId(pickerTestId);
  await expect(picker, `picker "${pickerTestId}" should be visible`).toBeVisible();
  await expect(picker).toHaveAttribute('role', 'dialog');

  // Every popover carries the same four building blocks.
  await expect(picker.getByTestId(`${pickerTestId}-current`)).toBeVisible();
  await expect(picker.getByTestId(`${pickerTestId}-cli-pills`)).toBeVisible();
  await expect(picker.getByTestId(`${pickerTestId}-model-pills`).or(
    picker.getByTestId(`${pickerTestId}-loading`)).or(
    picker.getByTestId(`${pickerTestId}-empty`)).or(
    picker.getByTestId(`${pickerTestId}-error`))).toBeVisible();
  await expect(picker.getByTestId(`${pickerTestId}-refresh`)).toBeVisible();
  await expect(picker.getByTestId(`${pickerTestId}-cancel`)).toBeVisible();
  await expect(picker.getByTestId(`${pickerTestId}-done`)).toBeVisible();

  // All four CLIs are always offered - no site-specific filtering.
  for (const cli of ['copilot', 'claude', 'codex', 'gemini'] as const) {
    await expect(
      picker.getByTestId(`${pickerTestId}-cli-${cli}`),
      `cli pill "${cli}" should exist in picker "${pickerTestId}"`,
    ).toBeVisible();
  }
}

test.describe('CLI + model selector parity across sites', () => {
  test('status-bar defaults chip and create-task dialog agent chip share the same popover', async ({ page }) => {
    await page.setViewportSize({ width: 1600, height: 900 });
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    // Site 1: status-bar defaults chip.
    const defaultsChip = page.getByTestId('studio-titlebar-defaults');
    await expect(defaultsChip).toBeVisible();
    await defaultsChip.click();
    await assertSelectorShape(page, 'studio-titlebar-defaults-picker');
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('studio-titlebar-defaults-picker')).not.toBeVisible();

    // Site 2: create-task dialog agent chip.
    await page.getByRole('button', { name: /add task/i }).first().click();
    const createAgentChip = page.getByTestId('create-agent');
    await expect(createAgentChip).toBeVisible();
    await createAgentChip.click();
    await assertSelectorShape(page, 'create-agent-picker');
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('create-agent-picker')).not.toBeVisible();
    // Close the dialog.
    await page.keyboard.press('Escape');
  });

  test('command-deck chip on a job-detail uses the shared popover shape', async ({ page }) => {
    const watchPath = await pickWatchPath();
    const job = await createJob({
      title: `selector-parity-${Date.now()}`,
      watchPath,
      cliType: 'claude',
      agent: 'claude',
      model: 'claude-opus-4-7',
      promptMarkdown: '# selector parity',
      targetState: '2-ready',
    });
    try {
      await page.setViewportSize({ width: 1600, height: 900 });
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(watchPath)}`);
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(500);

      // Site 3: command-deck agent chip.
      const cmdChip = page.getByTestId('commandbar-agent');
      await expect(cmdChip).toBeVisible();
      await cmdChip.click();
      await assertSelectorShape(page, 'commandbar-agent-picker');
      await page.keyboard.press('Escape');
    } finally {
      await deleteJob(job.id, watchPath);
    }
  });
});
