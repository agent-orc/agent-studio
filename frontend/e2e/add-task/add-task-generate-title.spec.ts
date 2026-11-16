import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Add Task dialog — "Generate title" button in the Prompt actions toolbar (beside the Title field until AGT-3834).
 *
 * The button calls POST /api/title/generate, which spawns a Haiku
 * subprocess. To keep this spec deterministic and free, we intercept
 * the request and return a canned response. A separate @billable spec
 * could exercise the live path; for the regression suite an intercept
 * is enough.
 */

const SCREENSHOT_DIR = path.resolve(__dirname, '../../C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/3-progress/prompt-automatischer-title/results').replace(/\\/g, '/');

async function ensureResultsDir(): Promise<string> {
  // Best-effort: keep screenshots next to the task evidence so the
  // protocol pane can show them. Fall back to test-results/ if the
  // job folder isn't reachable from the runner.
  const candidates = [
    'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/3-progress/prompt-automatischer-title/results',
    path.resolve(__dirname, '../../test-results')
  ];
  for (const c of candidates) {
    try {
      await fs.promises.mkdir(c, { recursive: true });
      return c;
    } catch {
      // try the next candidate
    }
  }
  return path.resolve(__dirname, '../../test-results');
}

test.describe('Add Task — Generate title button', () => {
  test('button is disabled until a prompt is typed', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const generate = page.getByTestId('create-generate-title');
    await expect(generate).toBeVisible();
    await expect(generate).toBeDisabled();

    const prompt = page.getByTestId('create-prompt');
    await prompt.fill('Add a small button next to the title that generates a title via Haiku.');
    await expect(generate).toBeEnabled();

    // Whitespace-only must not enable the button.
    await prompt.fill('     \n   ');
    await expect(generate).toBeDisabled();
  });

  test('clicking Generate fills the Title field with the API response', async ({ page }, testInfo) => {
    await page.route('**/api/title/generate', async (route) => {
      const req = route.request().postDataJSON?.();
      // Sanity: the dialog must forward the prompt.
      expect(typeof req?.prompt).toBe('string');
      expect((req?.prompt ?? '').length).toBeGreaterThan(0);
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ title: 'Add Generate-title button' })
      });
    });

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const titleInput = page.getByTestId('create-title');
    const prompt = page.getByTestId('create-prompt');
    const generate = page.getByTestId('create-generate-title');

    await prompt.fill(
      'Also, ich möchte nicht immer zwingend einen Titel eingeben müssen. ' +
      'Ich möchte, dass ich bei "Title" die Option habe, den Titel automatisch zu generieren.'
    );

    const resultsDir = await ensureResultsDir();
    const beforeBuf = await dialog.screenshot();
    await testInfo.attach('generate-title-before', { body: beforeBuf, contentType: 'image/png' });
    await fs.promises.writeFile(path.join(resultsDir, 'generate-title-before.png'), beforeBuf);

    await generate.click();

    await expect(titleInput).toHaveValue('Add Generate-title button', { timeout: 5_000 });

    const afterBuf = await dialog.screenshot();
    await testInfo.attach('generate-title-after', { body: afterBuf, contentType: 'image/png' });
    await fs.promises.writeFile(path.join(resultsDir, 'generate-title-after.png'), afterBuf);
  });

  test('backend error surfaces inline without crashing the dialog', async ({ page }) => {
    await page.route('**/api/title/generate', async (route) => {
      await route.fulfill({
        status: 502,
        contentType: 'application/problem+json',
        body: JSON.stringify({ title: 'Bad Gateway', detail: 'Haiku refused to play' })
      });
    });

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const prompt = page.getByTestId('create-prompt');
    await prompt.fill('Anything that triggers the backend error path.');

    const generate = page.getByTestId('create-generate-title');
    await generate.click();

    const errorBanner = page.getByTestId('create-generate-title-error');
    await expect(errorBanner).toBeVisible({ timeout: 5_000 });
    await expect(dialog).toBeVisible(); // dialog stays open
  });
});
