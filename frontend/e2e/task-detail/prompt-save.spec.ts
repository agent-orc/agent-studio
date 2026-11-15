import { test, expect } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, getJobDetail, listJobs } from '../helpers/jobs';

/**
 * Prompt editor — saving must work via Ctrl+S anywhere on the page (no need
 * to focus the editor first), and the editor must give brief visual feedback
 * (gray idle → purple while dirty → green flash on save).
 */

interface WatchPath { path: string; name?: string }

async function pickWatchPath(): Promise<string> {
  const paths = await api<WatchPath[]>('/api/watch-paths');
  if (!paths.length) throw new Error('No watch paths configured');
  return paths[0].path;
}

async function createPromptJob(): Promise<{ id: string; watchPath: string }> {
  const watchPath = await pickWatchPath();
  const created = await createJob({
    title: `e2e-prompt-save-${Date.now()}`,
    watchPath,
    cliType: 'claude',
    agent: 'claude',
    promptMarkdown: '# Initial prompt\n\nHello.',
    targetState: '2-ready'
  });
  return { id: created.id, watchPath };
}

async function deleteJob(id: string, watchPath: string): Promise<void> {
  try {
    await api(`/api/tasks/${encodeURIComponent(id)}?watchPath=${encodeURIComponent(watchPath)}`, {
      method: 'DELETE'
    });
  } catch {
    // best-effort cleanup
  }
}

test.describe('Prompt editor — Ctrl+S save & visual feedback', () => {
  test('Ctrl+S saves from anywhere and flashes the editor green', async ({ page }) => {
    const job = await createPromptJob();

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);

      // F48: prompt.md is rendered inside a Files-tab card; click Edit to
      // surface the rich editor (card is already expanded when only the
      // prompt is present).
      const promptEdit = page.getByTestId('file-card-prompt-edit');
      await expect(promptEdit).toBeVisible({ timeout: 10_000 });
      await promptEdit.click();

      const editor = page.getByTestId('prompt-editor');
      await expect(editor).toBeVisible({ timeout: 10_000 });
      await expect(editor).toHaveAttribute('data-state', 'idle');

      // Switch to Markdown mode so we can type into a plain textarea — more
      // reliable across browsers than driving TipTap's contenteditable.
      await editor.getByTestId('prompt-editor-mode-toggle').click();
      await page.getByTestId('prompt-editor-mode-menu-item-source').click();
      const source = page.getByTestId('prompt-editor-source');
      await expect(source).toBeVisible();

      const newBody = `# Updated by e2e\n\nrun-${Date.now()}`;
      await source.fill(newBody);

      // Editor should now report a dirty state through the shared status
      // indicator (AGT-3403).
      await expect(editor).toHaveAttribute('data-state', 'dirty');
      await expect(page.getByTestId('prompt-editor-save-state')).toHaveAttribute('data-tone', 'warning');
      await expect(page.getByTestId('prompt-editor-save-state')).toHaveText('Unsaved changes');

      // Move focus OFF the editor — Ctrl+S should still work globally.
      await page.locator('body').click({ position: { x: 2, y: 2 } });

      await page.keyboard.press('Control+s');

      // Saved state should appear (data-state=saved + success indicator).
      await expect(editor).toHaveAttribute('data-state', 'saved', { timeout: 3_000 });
      await expect(page.getByTestId('prompt-editor-save-state')).toHaveAttribute('data-tone', 'success');

      // Backend should have received the update.
      await expect.poll(async () => {
        const detail = await getJobDetail(job.id, job.watchPath);
        return detail.promptMarkdown ?? '';
      }, { timeout: 5_000 }).toContain('Updated by e2e');

      // After ~1.5s the green flash fades back to idle.
      await expect(editor).toHaveAttribute('data-state', 'idle', { timeout: 3_000 });
    } finally {
      await deleteJob(job.id, job.watchPath);
    }
  });

  test('Save button click also triggers the green flash', async ({ page }) => {
    const job = await createPromptJob();

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);

      // F48: open the prompt editor from the Files-tab card.
      await page.getByTestId('file-card-prompt-edit').click();

      const editor = page.getByTestId('prompt-editor');
      await expect(editor).toBeVisible({ timeout: 10_000 });

      await editor.getByTestId('prompt-editor-mode-toggle').click();
      await page.getByTestId('prompt-editor-mode-menu-item-source').click();
      const source = page.getByTestId('prompt-editor-source');
      await source.fill(`# Click-save ${Date.now()}`);
      await expect(editor).toHaveAttribute('data-state', 'dirty');

      await page.getByTestId('prompt-editor-save').click();
      await expect(editor).toHaveAttribute('data-state', 'saved', { timeout: 3_000 });
    } finally {
      await deleteJob(job.id, job.watchPath);
    }
  });

  test('autosave: changes persist without any manual save action', async ({ page }) => {
    const job = await createPromptJob();

    try {
      await page.goto(`/?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);

      // F48: open the prompt editor from the Files-tab card.
      await page.getByTestId('file-card-prompt-edit').click();

      const editor = page.getByTestId('prompt-editor');
      await expect(editor).toBeVisible({ timeout: 10_000 });

      await editor.getByTestId('prompt-editor-mode-toggle').click();
      await page.getByTestId('prompt-editor-mode-menu-item-source').click();
      const source = page.getByTestId('prompt-editor-source');
      await expect(source).toBeVisible();

      const newBody = `# Autosaved by e2e\n\nrun-${Date.now()}`;
      await source.fill(newBody);

      // No Ctrl+S, no button click — wait for autosave (debounce is 600ms).
      await expect(editor).toHaveAttribute('data-state', 'saved', { timeout: 4_000 });
      await expect(page.getByTestId('prompt-editor-save-state')).toHaveAttribute('data-tone', 'success');

      // Backend must have the updated content.
      await expect.poll(async () => {
        const detail = await getJobDetail(job.id, job.watchPath);
        return detail.promptMarkdown ?? '';
      }, { timeout: 5_000 }).toContain('Autosaved by e2e');
    } finally {
      await deleteJob(job.id, job.watchPath);
    }
  });
});

// Sanity: keep the helper used so unused-import linters don't trip. listJobs
// is exported by the helper module and intentionally not used in this spec.
void listJobs;
