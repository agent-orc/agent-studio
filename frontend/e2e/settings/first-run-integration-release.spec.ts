import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';
import { createFirstRunProofCard, prepareFirstRunProject, verifyFirstRunGit } from '../helpers/first-run-setup';

/**
 * AGT-3891 (F1): on the first-run stack with its fake coding and review hosts,
 * an integrated card becomes a release and starts the automatic Staging run.
 * The spec creates no release through the API; it only reads the UI.
 */
// A retry on the same stack integrates a delivery the shared origin already
// carries, a no-op integration without a gate verdict, so it cannot pass.
test.describe.configure({ retries: 0 });

test('an integrated card becomes a release with an automatic Staging run', async ({ page }) => {
  test.setTimeout(16 * 60_000);
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace).toBeTruthy();
  const projectName = await prepareFirstRunProject(page, workspace!, 'Integration Release');
  const slug = projectName.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');

  await page.goto('/#/workspace/settings/first-run');
  await expect(page.getByTestId('first-run-git-setup')).toBeVisible();
  await verifyFirstRunGit(page);
  await expect(page.getByTestId('first-run-providers')).toHaveAttribute('data-state', 'ready', { timeout: 120_000 });
  await createFirstRunProofCard(page);
  await expect(async () => {
    await page.getByTestId('first-run-refresh').click();
    await expect(page.getByTestId('first-run-proof')).toHaveAttribute('data-state', 'ready');
  }).toPass({ timeout: 15 * 60_000, intervals: [5_000] });

  await page.goto(`/#/projects/${slug}/deploy`);
  await expect(page.getByTestId('project-deploy-area')).toBeVisible({ timeout: 30_000 });
  await page.getByTestId('deploy-tab-releases').click();
  await expect(async () => {
    await page.getByTestId('deploy-refresh').click();
    await expect(page.getByTestId('deploy-next-release')).toHaveAttribute('data-state', 'released', { timeout: 5_000 });
  }).toPass({ timeout: 120_000, intervals: [2_000] });
  await expect(page.getByTestId('deploy-next-release-message')).toContainText(/Release \S+ is the tip [0-9a-f]{8} of main/);
  await expect(page.getByRole('list', { name: 'Releases, newest first' }).getByRole('listitem')).toHaveCount(1);
  await expect(page.getByTestId('deploy-release-detail')).toContainText('Cards');

  const results = process.env.JOB_RESULTS_DIR;
  if (results) {
    mkdirSync(results, { recursive: true });
    await page.screenshot({ path: join(results, 'integration-release-releases-light.png'), fullPage: true });
  }

  await page.getByTestId('deploy-tab-overview').click();
  const staging = page.locator('[data-testid^="deploy-run-row-"]').filter({ hasText: 'Staging' });
  await expect(staging).toHaveCount(1, { timeout: 30_000 });
  await expect(staging).toContainText('studio-integration');
  if (results) await page.screenshot({ path: join(results, 'integration-release-staging-run-light.png'), fullPage: true });
});
