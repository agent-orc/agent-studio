import { expect, test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3834 (playthrough AGT-3796 finding F7).
 *
 * Add task opened in the Prompt field at the bottom of the dialog, every tag,
 * lane, type and mode option was its own Tab stop (Title was 55 Tab stops from
 * Create), text fields showed focus only as a faint border tint, and focus fell
 * to the body after Create. The dialog now opens on Title, each pick group is
 * one Tab stop with arrow-key movement, fields show the accent focus ring, and
 * the new card takes focus after Create. Runs against the isolated stack; the
 * created card is deleted afterwards.
 */

const RESULTS_DIR = process.env['JOB_RESULTS_DIR']?.trim()
  ? path.resolve(process.env['JOB_RESULTS_DIR'])
  : path.resolve('test-results', 'add-task-keyboard-path');

/** Acceptance: Title to Create takes no more than 10 Tab stops. */
const MAX_TAB_STOPS = 10;

async function focusedTestId(page: Page): Promise<string | null> {
  return page.evaluate(() => {
    const el = document.activeElement as HTMLElement | null;
    return el?.getAttribute('data-testid') ?? el?.closest('[data-testid]')?.getAttribute('data-testid') ?? el?.tagName ?? null;
  });
}

async function focusOutline(page: Page): Promise<string> {
  return page.evaluate(() => {
    const style = getComputedStyle(document.activeElement as HTMLElement);
    return `${style.outlineStyle} ${style.outlineWidth}`;
  });
}

test('Add task is a short keyboard path from Title to Create, and focus lands on the new card', async ({ page }) => {
  mkdirSync(RESULTS_DIR, { recursive: true });
  const paths = await api<Array<{ path: string; name: string }>>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  const watchPath = paths[0].path;
  const title = `e2e-keyboard-path-${Date.now()}`;
  let createdId: string | undefined;
  page.on('response', async response => {
    if (response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/tasks' && response.ok()) {
      createdId = (await response.json() as { id: string }).id;
    }
  });

  try {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto('/');
    await dismissDevErrorDialog(page);
    await page.getByRole('button', { name: /add task/i }).first().click();
    const dialog = page.getByRole('dialog').filter({ has: page.getByTestId('create-dialog-header') });
    await expect(dialog).toBeVisible();

    // Opens on Title, the first field, with the accent focus ring.
    const titleInput = page.getByTestId('create-title');
    await expect(titleInput).toBeFocused();
    await expect(page.getByRole('textbox', { name: 'Title' })).toBeFocused();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await titleInput.focus();
      expect(await focusOutline(page), `Title focus ring (${theme})`).toBe('solid 2px');
      await dialog.screenshot({ path: path.join(RESULTS_DIR, `add-task-keyboard-open-${theme}.png`) });
    }

    await page.getByTestId('create-project-select').selectOption(watchPath);
    await titleInput.focus();
    await page.keyboard.type(title);
    await expect(page.getByTestId('create-submit')).toBeEnabled({ timeout: 15_000 });

    // Walk forward from Title until Create, recording every stop.
    const visited: string[] = [];
    for (let i = 0; i < 20; i++) {
      await page.keyboard.press('Tab');
      const id = await focusedTestId(page);
      visited.push(id ?? '?');
      if (id === 'create-submit') break;
    }
    test.info().annotations.push({ type: 'tab-stops', description: visited.join(' > ') });
    console.log(`[AGT-3834] Title to Create: ${visited.length} Tab stops: ${visited.join(' > ')}`);
    expect(visited.at(-1)).toBe('create-submit');
    expect(visited.length, visited.join(' > ')).toBeLessThanOrEqual(MAX_TAB_STOPS);

    // Each pick group is one stop that sits on its selected option.
    expect(visited).toContain('create-lane-1-preparation');
    expect(visited).toContain('create-task-type-chore');
    expect(visited).toContain('create-mode-coding');
    expect(visited.filter(id => id.startsWith('create-tag-')).length).toBeLessThanOrEqual(1);

    // Arrow keys move inside a group; Space selects.
    await page.getByTestId('create-task-type-chore').focus();
    await page.keyboard.press('ArrowLeft');
    await expect(page.getByTestId('create-task-type-feature')).toBeFocused();
    await page.keyboard.press('Space');
    await expect(page.getByTestId('create-task-type-feature')).toHaveAttribute('aria-pressed', 'true');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('create-task-type-feature').focus();
      await dialog.screenshot({ path: path.join(RESULTS_DIR, `add-task-keyboard-type-${theme}.png`) });
      await page.getByTestId('create-prompt').focus();
      expect(await focusOutline(page), `Prompt focus ring (${theme})`).toBe('solid 2px');
    }
    await setTheme(page, 'light');

    // Create from the keyboard; focus moves to the new card, not the body.
    await page.getByTestId('create-submit').focus();
    await page.keyboard.press('Enter');
    await expect(page.getByTestId('create-dialog-header')).toHaveCount(0, { timeout: 30_000 });
    await expect.poll(() => createdId, { timeout: 15_000 }).toBeTruthy();
    await expect.poll(() => page.evaluate(() => {
      const card = document.activeElement?.closest('[data-testid="task-card"]');
      return card?.textContent ?? null;
    }), { timeout: 15_000 }).toContain(title);
    await page.screenshot({ path: path.join(RESULTS_DIR, 'add-task-keyboard-after-create.png') });
  } finally {
    if (createdId) {
      await api(`/api/tasks/${encodeURIComponent(createdId)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' }).catch(() => {});
    }
  }
});
