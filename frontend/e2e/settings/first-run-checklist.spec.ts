import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';

test('first-run checklist reflects live installation and repository reads', async ({ page }) => {
  await page.goto('/#/workspace/settings/first-run');
  const checklist = page.getByTestId('first-run-checklist');
  await expect(checklist).toBeVisible();

  const identity = await page.request.get('/api/v1/installation');
  const owner = page.getByTestId('first-run-owner');
  if (identity.ok()) {
    const response = await identity.json() as { ownerBootstrapped: boolean };
    if (!response.ownerBootstrapped) await expect(owner).toHaveAttribute('data-state', 'pending');
  } else {
    await expect(owner).toHaveAttribute('data-state', 'pending');
  }

  const projects = await page.request.get('/api/v1/projects');
  if (projects.ok()) {
    const entries = await projects.json() as { id: string; archived: boolean }[];
    if (!entries.some(project => !project.archived)) {
      await expect(page.getByTestId('first-run-project')).toHaveAttribute('data-state', 'pending');
      await expect(page.getByTestId('first-run-git')).toHaveAttribute('data-state', 'pending');
      await expect(page.getByTestId('first-run-create-proof')).toBeDisabled();
    }
  }

  await page.getByTestId('first-run-refresh').click();
  await expect(checklist).toBeVisible();
  const results = process.env.JOB_RESULTS_DIR;
  if (results) {
    mkdirSync(results, { recursive: true });
    await page.screenshot({ path: join(results, 'first-run-checklist.png'), fullPage: true });
  }
});
