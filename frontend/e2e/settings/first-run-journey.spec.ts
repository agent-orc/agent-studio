import { spawnSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { createFirstRunProofCard, prepareFirstRunProject, verifyFirstRunGit } from '../helpers/first-run-setup';

test('fresh project reaches an integrated proof card with live Git and runner probes', async ({ page }) => {
  test.setTimeout(16 * 60_000);
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace).toBeTruthy();
  await prepareFirstRunProject(page, workspace!, 'First Run Journey');

  await page.goto('/#/workspace/settings/first-run');
  await expect(page.getByTestId('first-run-owner')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-project')).toHaveAttribute('data-state', 'pending');
  await expect(page.getByTestId('first-run-git-setup')).toBeVisible();
  await verifyFirstRunGit(page);
  await expect(page.getByTestId('first-run-project')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-git')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-providers')).toHaveAttribute('data-state', 'ready', { timeout: 120_000 });
  await expect(page.getByTestId('first-run-host')).toHaveAttribute('data-state', 'ready');
  const results = process.env.JOB_RESULTS_DIR;
  if (results) {
    mkdirSync(results, { recursive: true });
    await page.screenshot({ path: join(results, 'first-run-journey-light.png'), fullPage: true });
    await page.evaluate(() => localStorage.setItem('atp.studio.theme', 'dark'));
    await page.reload();
    await expect(page.getByTestId('first-run-git')).toHaveAttribute('data-state', 'ready');
    await page.screenshot({ path: join(results, 'first-run-journey-dark.png'), fullPage: true });
  }

  const bare = join(workspace!, 'first-run-origin.git');
  const remote = spawnSync('/usr/bin/git', ['--git-dir', bare, 'for-each-ref',
    '--format=%(refname)', 'refs/heads/agent-studio-access-probe'],
    { encoding: 'utf8', windowsHide: true });
  expect(remote.status).toBe(0);
  expect(remote.stdout.trim()).toBe('');

  await createFirstRunProofCard(page);
  await expect(async () => {
    await page.getByTestId('first-run-refresh').click();
    await expect(page.getByTestId('first-run-proof')).toHaveAttribute('data-state', 'ready');
  }).toPass({ timeout: 15 * 60_000, intervals: [5_000] });
  const integrated = spawnSync('/usr/bin/git', ['--git-dir', bare, 'show',
    'main:docs/project-overview.md'], { encoding: 'utf8', windowsHide: true });
  expect(integrated.status, integrated.stderr).toBe(0);
  expect(integrated.stdout).toContain('This small repository verifies the Studio setup journey.');
  if (results) {
    await page.screenshot({ path: join(results, 'first-run-journey-integrated.png'), fullPage: true });
  }
});
