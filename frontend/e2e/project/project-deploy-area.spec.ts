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
  const release = (id: string, version: string, cardKeys: string[]) => ({
    id, projectId: projectName, version, commitSha: 'a'.repeat(40), artifactDigests: ['sha256:' + 'b'.repeat(64)],
    processVersion: 'process', variablesVersion: 'variables', cardKeys,
    createdAtUtc: '2026-10-16T10:00:00Z', channel: 'default',
  });
  const releases = [release('release-4', '2026.10.16.4', ['WEB-36']),
    release('release-3', '2026.10.16.3', ['WEB-33', 'WEB-34']),
    release('release-2', '2026.10.15.2', ['WEB-29'])];
  const stage = { id: 'stage', projectId: projectName, name: 'Staging', order: 0, kind: 'staging' };
  const production = { id: 'production', projectId: projectName, name: 'Production', order: 1, kind: 'production' };
  const run = (id: string, releaseId: string, environmentId: string, approvedBy: string | null) => ({
    id, releaseId, environmentId, state: 'succeeded', startedAtUtc: '2026-10-16T10:00:00Z',
    finishedAtUtc: '2026-10-16T10:05:00Z', approvedBy,
    steps: [{ name: 'Health check', state: 'succeeded', startedAtUtc: '2026-10-16T10:04:00Z',
      finishedAtUtc: '2026-10-16T10:05:00Z', logRef: 'health-log' }],
  });
  const deployments = [run('stage-3', 'release-3', 'stage', null), run('prod-2', 'release-2', 'production', 'Owner')];
  await page.route('**/api/projects/**/deploy/**', route => {
    const pathname = new URL(route.request().url()).pathname;
    const body = pathname.endsWith('/status') ? [
      { environment: stage, currentRelease: releases[1], currentDeployment: deployments[0],
        newerRelease: releases[0], cardsSinceLastDeploy: ['WEB-36'], health: 'healthy' },
      { environment: production, currentRelease: releases[2], currentDeployment: deployments[1],
        newerRelease: releases[1], cardsSinceLastDeploy: ['WEB-33', 'WEB-34'], health: 'healthy' },
    ] : pathname.endsWith('/releases') ? releases : pathname.endsWith('/deployments') ? deployments
      : pathname.endsWith('/notes') ? { releaseId: pathname.split('/').at(-2), version: '2026.10.15.2',
        preview: false, notes: [{ cardKey: 'WEB-29', text: 'Clearer account settings' }] } : [];
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
  });
  const slug = projectName.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  await page.goto(`/#/projects/${slug}/deploy`);
  await expect(page.getByTestId('project-deploy-area')).toBeVisible();
  // Deploy is a project main item in the Explorer, no longer a Deck rail entry.
  await expect(page.getByTestId('project-shell-rail-deploy')).toHaveCount(0);
  await expect(page.getByTestId('deploy-environments').locator('article')).toHaveCount(2);
  await expect(page.getByTestId('deploy-environment-production')).toContainText('Newer release available');
  await expect(page.getByTestId('project-deploy-area')).not.toContainText('Preview data');
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
          await expect(page.getByTestId('deploy-release-notes')).toContainText('Clearer account settings');
        }
        if (view === 'pipeline') await expect(page.getByTestId('deploy-stage-production')).toContainText('Health check');
        await page.screenshot({ path: path.join(results, `deploy-${view}-${width}x${height}-${theme}.png`) });
      }
    }
  }
  await page.getByTestId('deploy-tab-steps').click();
  await expect(page.getByTestId('project-detail-pipeline')).toBeVisible();
  await page.getByTestId('deploy-tab-overview').click();
  await page.getByRole('button', { name: 'Log', exact: true }).first().click();
  await expect(page.getByTestId('deploy-log-staging')).toContainText('Health check');
  await page.getByRole('button', { name: 'Existing deployment tools' }).click();
  await expect(page.getByTestId('project-deployment-panel')).toBeVisible();
});

test('Run steps stay in the route and retired Pipeline and Observability URLs move to the new areas', async ({ page }) => {
  const slug = projectName.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  const hash = () => new URL(page.url()).hash;
  await page.goto(`/#/projects/${slug}/pipeline`);
  await expect(page.getByTestId('deploy-tab-steps')).toHaveAttribute('aria-current', 'page');
  await expect.poll(hash).toMatch(/^#\/projects\/[^/?&]+\/deploy\?view=steps(&|$)/);
  await page.reload();
  await expect(page.getByTestId('deploy-tab-steps')).toHaveAttribute('aria-current', 'page');
  await expect(page.getByTestId('project-detail-pipeline')).toBeVisible();

  await page.getByTestId('deploy-tab-releases').click();
  await expect.poll(hash).toMatch(/^#\/projects\/[^/?&]+\/deploy(&|$)/);
  await page.getByTestId('deploy-tab-steps').click();
  await expect.poll(hash).toMatch(/\/deploy\?view=steps(&|$)/);
  await page.reload();
  await expect(page.getByTestId('deploy-tab-steps')).toHaveAttribute('aria-current', 'page');

  await page.goto(`/#/projects/${slug}/observability`);
  await expect(page.getByTestId('project-operations-area')).toBeVisible();
  await expect.poll(hash).toMatch(/^#\/projects\/[^/?&]+\/operations(&|$)/);
});
