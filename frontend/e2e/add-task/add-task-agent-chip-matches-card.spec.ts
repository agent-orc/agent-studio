import { expect, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { cpus, loadavg } from 'node:os';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { getJob } from '../helpers/jobs';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3709 (playthrough AGT-3649 finding F16).
 *
 * The Add task agent chip names the model and thinking level the card is
 * created with, and Create closes quickly on an idle stack. Before the fix the
 * chip read "No model" and then a stale local default while the card ran the
 * server's policy pick, and a cold model catalogue started the CLI inside
 * `POST /api/tasks` (about 9 s server-side). Runs against the isolated stack;
 * the created card is deleted afterwards.
 */

const RESULTS_DIR = process.env['JOB_RESULTS_DIR']?.trim()
  ? path.resolve(process.env['JOB_RESULTS_DIR'])
  : path.resolve('test-results', 'add-task-agent-chip');

/** Acceptance: Create closes within 2 s on an idle stack. */
const CREATE_CLOSE_BUDGET_MS = 2000;

/**
 * The budget is an idle-stack promise. On a shared runner whose load average
 * exceeds its core count the timing is recorded but not enforced, so the spec
 * does not fail on neighbours' work.
 */
function hostIsIdle(): boolean {
  return loadavg()[0] < cpus().length;
}

test('the agent chip shows the model the card is created with, and Create closes within 2 s', async ({ page }) => {
  mkdirSync(RESULTS_DIR, { recursive: true });
  const paths = await api<Array<{ path: string; name: string }>>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  const watchPath = paths[0].path;
  const title = `e2e-agent-chip-${Date.now()}`;
  let createdId: string | undefined;
  let posted: Record<string, unknown> | undefined;
  page.on('request', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/tasks') {
      posted = request.postDataJSON() as Record<string, unknown>;
    }
  });
  let respondedAt = 0;
  let serverTiming = '';
  page.on('response', async response => {
    if (response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/tasks' && response.ok()) {
      respondedAt = Date.now();
      serverTiming = (await response.allHeaders())['server-timing'] ?? '';
      createdId = (await response.json() as { id: string }).id;
    }
  });

  try {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto('/');
    await dismissDevErrorDialog(page);
    await page.getByRole('button', { name: /add task/i }).first().click();
    await page.getByTestId('create-project-select').selectOption(watchPath);
    await page.getByTestId('create-task-type-feature').click();
    await page.getByTestId('create-mode-concept').click();
    await page.getByTestId('create-title').fill(title);

    // Create is enabled only once the chip can no longer change.
    const submit = page.getByTestId('create-submit');
    await expect(submit).toBeEnabled({ timeout: 15_000 });
    const chip = page.getByTestId('create-agent');
    await expect(chip).not.toContainText('No model');
    const chipModel = await chip.getAttribute('data-model');
    const chipLevel = await chip.getAttribute('data-thinking-level');
    expect(chipModel, 'the chip names a model before Create').toBeTruthy();

    const dialog = page.getByRole('dialog').filter({ has: page.getByTestId('create-dialog-header') });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await dialog.screenshot({ path: path.join(RESULTS_DIR, `add-task-agent-chip-${theme}.png`) });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: path.join(RESULTS_DIR, 'add-task-agent-chip-390-dark.png') });
    await page.setViewportSize({ width: 1280, height: 900 });
    await setTheme(page, 'light');

    const started = Date.now();
    await submit.click();
    await expect(page.getByTestId('create-dialog-header')).toHaveCount(0, { timeout: 30_000 });
    const closeMs = Date.now() - started;
    const timing = `close ${closeMs} ms, response after ${respondedAt - started} ms, server-timing "${serverTiming}"`;
    test.info().annotations.push({ type: 'create-timing', description: timing });
    console.log(`[AGT-3709] ${timing}`);
    if (hostIsIdle()) {
      expect(closeMs, `Create closed after ${closeMs} ms`).toBeLessThan(CREATE_CLOSE_BUDGET_MS);
    } else {
      test.info().annotations.push({
        type: 'create-budget-not-enforced',
        description: `host load ${loadavg()[0].toFixed(1)} on ${cpus().length} cores`,
      });
    }

    expect(posted?.['model']).toBe(chipModel);
    expect(posted?.['mode']).toBe('concept');
    await expect.poll(() => createdId).toBeTruthy();
    const card = await getJob(createdId!, watchPath);
    expect(card.model).toBe(chipModel);
    if (chipLevel) expect(card.thinkingLevel).toBe(chipLevel);
  } finally {
    if (createdId) {
      await api(`/api/tasks/${encodeURIComponent(createdId)}?watchPath=${encodeURIComponent(watchPath)}`, { method: 'DELETE' }).catch(() => {});
    }
  }
});
