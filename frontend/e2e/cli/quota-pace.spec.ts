import { expect, test } from '@playwright/test';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

test('edit the Codex weekly pace and restore it to steady', async ({ page }, testInfo) => {
  await page.goto('/');
  await page.getByTestId('studio-titlebar-settings').click();
  await page.getByTestId('workspace-settings-rail-caps').click();
  const group = page.getByRole('group', { name: 'Codex Weekly pace' });
  const row = page.getByTestId('pace-row-codex-weekly');
  const deadline = row.getByLabel('Use by (UTC)');
  const endpoint = '/api/model-selection/policy/pace/codex/Weekly';
  const saved = () => page.waitForResponse(response => response.url().endsWith(endpoint)
    && response.request().method() === 'PUT' && response.ok());

  await expect(group.getByRole('button', { name: 'Steady until reset' })).toHaveAttribute('aria-pressed', 'true');
  await group.getByRole('button', { name: 'Use by…' }).click();
  await expect(row.getByRole('button', { name: 'Save time' })).toBeDisabled();
  await deadline.fill('2026-11-28T21:49');
  const [write] = await Promise.all([saved(), row.getByRole('button', { name: 'Save time' }).click()]);
  expect(write.request().postDataJSON()).toEqual({ mode: 'useBy', useBy: '2026-11-28T21:49:00.000Z' });

  await page.getByTestId('cli-admin-refresh').click();
  await expect(deadline).toHaveValue('2026-11-28T21:49');
  await expect(row.getByRole('paragraph')).toContainText(/new work may use up to [\d.]+%/);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await row.screenshot({ path: join(process.env.JOB_RESULTS_DIR ?? testInfo.outputDir, `codex-weekly-pace-${theme}.png`) });
  }
  await Promise.all([saved(), group.getByRole('button', { name: 'Unpaced' }).click()]);
  await expect(deadline).toBeHidden();
  await Promise.all([saved(), group.getByRole('button', { name: 'Steady until reset' }).click()]);
  await expect(group.getByRole('button', { name: 'Steady until reset' })).toHaveAttribute('aria-pressed', 'true');
});
