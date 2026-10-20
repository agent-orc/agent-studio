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
  // Deploy is a project main item in the Explorer, no longer a Deck rail entry.
  await expect(page.getByTestId('project-shell-rail-deploy')).toHaveCount(0);
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

test('Project Secrets shows missing references, binds Production after approval, and keeps values write-only', async ({ page }) => {
  const slug = projectName.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  let bound = false;
  await page.route('**/api/secrets', route => route.fulfill({ json: {
    store: 'builtin', keySource: 'key-file', canWriteWorkspace: true, secrets: [], unreadable: [],
  } }));
  await page.route('**/api/projects/*/environments', route => route.fulfill({ json: {
    project: projectName, environments: [
      { declaration: { id: 'stage' } }, { declaration: { id: 'production' } },
    ], promotions: [], jobs: [], failureCards: [],
  } }));
  await page.route(/\/api\/projects\/[^/]+\/secret-bindings\/[^/]+(?:\/[^/]+)?$/, async route => {
    const url = new URL(route.request().url());
    const environment = url.pathname.split('/')[5];
    if (route.request().method() === 'PUT') {
      const body = route.request().postDataJSON() as { reference: string; approve: boolean };
      expect(body).toEqual({ reference: `secret://${projectName}/production/DATABASE_URL`, approve: true });
      bound = true;
      await route.fulfill({ json: { name: 'DATABASE_URL', reference: body.reference, approvedBy: 'owner' } });
      return;
    }
    const reference = `secret://${projectName}/production/DATABASE_URL`;
    await route.fulfill({ json: {
      project: projectName, environment, ready: environment !== 'production' || bound,
      bindings: bound ? [{ name: 'DATABASE_URL', reference, approvedBy: 'owner' }] : [],
      missing: environment === 'production' && !bound
        ? [{ name: 'DATABASE_URL', reference, scope: `${projectName}/production`, reason: 'not-bound' }] : [],
    } });
  });
  await page.goto(`/#/projects/${slug}/deploy`);
  await page.getByTestId('deploy-tab-secrets').click();
  const secrets = page.getByTestId('project-secrets-page');
  await expect(secrets).toBeVisible();
  await secrets.getByTestId('project-secrets-environment').selectOption('production');
  await expect(secrets.getByTestId('project-secrets-missing')).toContainText('not-bound');
  await secrets.getByRole('button', { name: 'Bind DATABASE_URL' }).click();
  await secrets.getByTestId('project-secrets-approve').check();
  await secrets.getByTestId('project-secrets-bind').click();
  await expect(secrets.getByTestId('project-secrets-preflight')).toContainText('Ready for deploy');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: path.join(results, `deploy-secrets-${theme}.png`) });
  }
});
