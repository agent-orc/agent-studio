import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Add Task dialog - "Enhance" button next to the Prompt field.
 *
 * Enhance is the headline action in the new dialog: it calls
 * /api/prompt/enhance AND /api/title/generate in parallel, then writes
 * the refined prompt, generated title, registry-matched tags, and a
 * sensible target lane directly into the bound form fields. There is no
 * preview / Apply / Discard step - the fields are the preview. The
 * "Also suggested" hint surfaces tag suggestions that didn't resolve to
 * a workspace registry entry.
 *
 * The mocked tag id "ui-ux" is seeded in the live tag registry
 * (workspace/tags.json) so we can assert that an applied chip lights up.
 * Unknown suggestions ("frontend", "ui-improvement") fall into the hint
 * line; the same suggestions appear in the canned response below.
 */

const RESULTS_CANDIDATES = [
  'C:/Projects/agent-taskboard-workspace/projects/agent-taskboard/3-progress/enhance-task-dialog-with-auto-generation-and-lane-selection/results',
  path.resolve(__dirname, '../../test-results')
];

async function ensureResultsDir(): Promise<string> {
  for (const c of RESULTS_CANDIDATES) {
    try {
      await fs.promises.mkdir(c, { recursive: true });
      return c;
    } catch {
      // try next
    }
  }
  return path.resolve(__dirname, '../../test-results');
}

test.describe('Add Task - Enhance prompt button', () => {
  test('button is disabled until a prompt is typed', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const enhance = page.getByTestId('create-enhance-prompt');
    await expect(enhance).toBeVisible();
    await expect(enhance).toBeDisabled();

    const prompt = page.getByTestId('create-prompt');
    await prompt.fill('Add an Enhance button that fills every field in one click.');
    await expect(enhance).toBeEnabled();

    await prompt.fill('     \n   ');
    await expect(enhance).toBeDisabled();
  });

  test('clicking Enhance fills prompt + title + matched tags + lane', async ({ page }, testInfo) => {
    await page.route('**/api/prompt/enhance', async (route) => {
      const req = route.request().postDataJSON?.();
      expect(typeof req?.prompt).toBe('string');
      expect((req?.prompt ?? '').length).toBeGreaterThan(0);
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          refinedPrompt: 'Add an Enhance button to the Create-task dialog that calls Haiku and pre-fills the title, refined prompt, tags, and lane in one action.',
          intent: 'One-click Enhance pre-fills every field',
          // "ui-ux" matches the live registry entry; the other two
          // surface in the "Also suggested" hint.
          tags: ['ui-ux', 'frontend', 'ui-improvement']
        })
      });
    });
    await page.route('**/api/title/generate', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ title: 'Enhance button fills every field' })
      });
    });

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const prompt = page.getByTestId('create-prompt');
    const titleInput = page.getByTestId('create-title');
    const enhance = page.getByTestId('create-enhance-prompt');

    await prompt.fill(
      'Also, ich hätte gern einen Knopf der mein Prompt aufpoliert und mir die Tags und das Intent und den Title dazu liefert.'
    );

    const resultsDir = await ensureResultsDir();
    const beforeBuf = await dialog.screenshot();
    await testInfo.attach('enhance-before', { body: beforeBuf, contentType: 'image/png' });
    await fs.promises.writeFile(path.join(resultsDir, 'enhance-before.png'), beforeBuf);

    await enhance.click();

    // Title and prompt fields are filled directly - no preview / Apply step.
    await expect(titleInput).toHaveValue('Enhance button fills every field', { timeout: 5_000 });
    const refinedPrompt = await prompt.inputValue();
    expect(refinedPrompt).toContain('Add an Enhance button to the Create-task dialog');
    expect(refinedPrompt).not.toContain('Intent:');
    expect(refinedPrompt).not.toContain('Tags:');

    // Summary appears with intent + matched tags + unknown suggestions.
    const summary = page.getByTestId('create-enhance-summary');
    await expect(summary).toBeVisible();
    await expect(page.getByTestId('create-enhance-intent')).toContainText('Enhance pre-fills');
    await expect(page.getByTestId('create-enhance-applied-tags')).toContainText('UI / UX');
    await expect(page.getByTestId('create-enhance-unknown-tags')).toContainText('frontend');
    await expect(page.getByTestId('create-enhance-unknown-tags')).toContainText('ui-improvement');

    // Matching registry chip is now active.
    const uiUxChip = page.getByTestId('create-tag-ui-ux');
    await expect(uiUxChip).toHaveClass(/create-tag-picker__chip--active/);

    // Lane defaults to Ready post-Enhance (was Preparation).
    const readyLane = page.getByTestId('create-lane-2-ready');
    await expect(readyLane).toHaveAttribute('aria-pressed', 'true');

    const afterBuf = await dialog.screenshot();
    await testInfo.attach('enhance-after', { body: afterBuf, contentType: 'image/png' });
    await fs.promises.writeFile(path.join(resultsDir, 'enhance-after.png'), afterBuf);
  });

  test('summary dismiss button hides the summary without touching the form', async ({ page }) => {
    await page.route('**/api/prompt/enhance', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          refinedPrompt: 'Refined version that should stay in the prompt field.',
          intent: 'Stay in the field',
          tags: ['ui-ux']
        })
      });
    });
    await page.route('**/api/title/generate', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ title: 'Stay-in-field check' })
      });
    });

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const prompt = page.getByTestId('create-prompt');
    const titleInput = page.getByTestId('create-title');
    const enhance = page.getByTestId('create-enhance-prompt');
    await prompt.fill('Something we want enhanced and titled.');

    await enhance.click();
    const summary = page.getByTestId('create-enhance-summary');
    await expect(summary).toBeVisible({ timeout: 5_000 });
    const filledPrompt = await prompt.inputValue();
    const filledTitle = await titleInput.inputValue();

    await page.getByTestId('create-enhance-summary-dismiss').click();
    await expect(summary).toHaveCount(0);
    // Prompt + title stay as they were after Enhance.
    await expect(prompt).toHaveValue(filledPrompt);
    await expect(titleInput).toHaveValue(filledTitle);
  });

  test('backend error surfaces inline without crashing the dialog', async ({ page }) => {
    await page.route('**/api/prompt/enhance', async (route) => {
      await route.fulfill({
        status: 502,
        contentType: 'application/problem+json',
        body: JSON.stringify({ title: 'Bad Gateway', detail: 'Haiku refused' })
      });
    });
    // Title-generation succeeds in this test - the enhance error path
    // must not crash the dialog even when its sibling call succeeded.
    await page.route('**/api/title/generate', async (route) => {
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ title: 'Should not appear' })
      });
    });

    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const prompt = page.getByTestId('create-prompt');
    await prompt.fill('Anything that triggers the backend error path.');

    const enhance = page.getByTestId('create-enhance-prompt');
    await enhance.click();

    const errorBanner = page.getByTestId('create-enhance-error');
    await expect(errorBanner).toBeVisible({ timeout: 5_000 });
    // Summary should NOT appear when enhance itself failed, even if the
    // title sibling succeeded.
    await expect(page.getByTestId('create-enhance-summary')).toHaveCount(0);
    await expect(dialog).toBeVisible();
  });
});

test.describe('Add Task - Lane selector', () => {
  test('renders Backlog / Preparation / Ready and toggles selection', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const dialog = page.locator('.create-dialog');
    await expect(dialog).toBeVisible();

    const lanePicker = page.getByTestId('create-lane-picker');
    await expect(lanePicker).toBeVisible();

    const backlog = page.getByTestId('create-lane-0-backlog');
    const prep = page.getByTestId('create-lane-1-preparation');
    const ready = page.getByTestId('create-lane-2-ready');

    // Default open lands on Preparation.
    await expect(prep).toHaveAttribute('aria-pressed', 'true');
    await expect(backlog).toHaveAttribute('aria-pressed', 'false');
    await expect(ready).toHaveAttribute('aria-pressed', 'false');

    // Switch to Backlog and confirm header updates.
    await backlog.click();
    await expect(backlog).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByTestId('create-dialog-header')).toContainText(/Backlog/i);

    // Switch to Ready and confirm header again.
    await ready.click();
    await expect(ready).toHaveAttribute('aria-pressed', 'true');
    await expect(page.getByTestId('create-dialog-header')).toContainText(/Ready/i);
  });

  test('only manual-create lanes are offered (no Progress / Review)', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: /add task/i }).first().click();

    const lanePicker = page.getByTestId('create-lane-picker');
    await expect(lanePicker).toBeVisible();

    // Disallowed lanes must not surface in the picker.
    await expect(page.getByTestId('create-lane-3-progress')).toHaveCount(0);
    await expect(page.getByTestId('create-lane-4-auto-review')).toHaveCount(0);
    await expect(page.getByTestId('create-lane-5-human-review')).toHaveCount(0);
    await expect(page.getByTestId('create-lane-6-completed')).toHaveCount(0);
  });
});
