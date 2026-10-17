import { expect, test } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { api } from '../helpers/api';
import { setTheme } from '../helpers/theme';

const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results');
let projectName = '';

test.beforeAll(async () => {
  fs.mkdirSync(results, { recursive: true });
  const paths = await api<{ name: string; path: string }[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  projectName = paths.find(p => /agent.?task/i.test(p.name))?.name ?? paths[0].name;
});

test('Deploy area shows the P1 views and preserves review captures in both themes', async ({ page }) => {
  const slug = projectName.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  await page.goto(`/#/projects/${slug}/deploy`);
  await expect(page.getByTestId('project-deploy-area')).toBeVisible();
  await expect(page.getByTestId('project-shell-rail-deploy')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('deploy-environments').locator('article')).toHaveCount(2);
  await expect(page.getByTestId('deploy-environment-production')).toContainText('Newer release available');
  await expect(page.getByTestId('project-deploy-area')).toContainText('Preview data');
  await expect(page.getByRole('button', { name: 'Variables · P2' })).toBeDisabled();

  for (const [width, height] of [[1440, 900], [1920, 1080]]) {
    await page.setViewportSize({ width, height });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      for (const view of ['overview', 'releases', 'pipeline'] as const) {
        await page.getByTestId(`deploy-tab-${view}`).click();
        if (view === 'releases') {
          await page.getByTestId('deploy-release-release-2').click();
          await expect(page.getByTestId('deploy-release-detail')).toContainText('Approved by Owner');
        }
        if (view === 'pipeline') await expect(page.getByTestId('deploy-stage-production')).toContainText('Health check');
        await page.screenshot({ path: path.join(results, `deploy-${view}-${width}x${height}-${theme}.png`) });
      }
    }
  }
  await page.getByTestId('deploy-tab-overview').click();
  await page.getByRole('button', { name: 'Log', exact: true }).first().click();
  await expect(page.getByTestId('deploy-log-staging')).toContainText('Health check');
  await page.getByRole('button', { name: 'Existing deployment tools' }).click();
  await expect(page.getByTestId('project-deployment-panel')).toBeVisible();
});
