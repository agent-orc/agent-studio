import { expect, test, type Page } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';
import {
  DEPLOY_FIXTURE_NOW,
  deployEnvironmentsFixture,
  deployLifecycleFixture,
  deployReleasesFixture,
} from '../../src/app/features/project-detail/testing/deploy-fixtures';

/**
 * Project Deploy area on a fully mocked backend (AGT-3226).
 *
 * The environments API (AGT-3176) and the project.yml lifecycle are served
 * from the shared test fixture; the release route is either a 404 (release
 * service not deployed) or the fixture releases. The browser clock is pinned
 * so ages and the period filter are deterministic.
 */

const PROJECT = 'Website';
const STORAGE = '/srv/tasks/website';
const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results', 'project-deploy-area');

type DeployMocks = { environments: unknown; releases: unknown | 404; lifecycle: unknown };

async function mockApp(page: Page, deploy: DeployMocks): Promise<void> {
  const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });
  await page.route('**/api/**', route => route.fulfill(json([])).catch(() => undefined));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill(json({
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  })));
  await page.route('**/api/watch-paths**', route => route.fulfill(json([{ name: PROJECT, path: STORAGE, rootPath: '/srv/repo/website' }])));
  await page.route('**/api/v1/studio/board**', route => route.fulfill(json({
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [], failedPickup: [],
    review: [], autoReview: [], humanReview: [], completed: [], archive: [],
  })));
  await page.route('**/api/environment', route => route.fulfill(json({ isDev: false, devTools: {} })));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill(json({ projects: {} })));
  await page.route(/\/workbenches(\?|$)/, route => route.fulfill(json({ items: [] })));
  await page.route('**/api/usage/cockpit**', route => route.fulfill(json({}, 503)));
  await page.route('**/api/cli/quota**', route => route.fulfill(json({ ttlSeconds: 600, snapshots: [] })));
  await page.route('**/api/cli/usage**', route => route.fulfill(json({ sessions: [] })));
  await page.route('**/api/v1/workspaces**', route => route.fulfill(json([{
    id: 'ws', displayName: 'Workspace', projects: [{
      sourceType: 'local-folder', id: 'PROJ-WEB', displayName: PROJECT, shortCode: 'WEB', workspaceId: 'ws',
      color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: STORAGE,
      repositoryPath: '/srv/repo/website', rootPath: '/srv/repo/website', urls: [], ownershipMappings: [],
      archived: false, createdAt: '2026-10-01T00:00:00Z',
    }],
  }])));
  await page.route('**/api/projects/*/environments', route => route.fulfill(json(deploy.environments)));
  await page.route('**/api/projects/*/releases', route => route.fulfill(
    deploy.releases === 404 ? json({ error: 'Not found' }, 404) : json(deploy.releases)));
  await page.route('**/api/projects/*/execution', route => route.fulfill(json({
    repositoryDefinition: null, definitionSha256: null, issues: [], preparationWarnings: [], lastManifest: null,
    override: null, source: 'subject-commit', ...(deploy.lifecycle as object),
  })));
}

async function openDeploy(page: Page, deploy: DeployMocks): Promise<void> {
  await page.clock.setFixedTime(new Date(DEPLOY_FIXTURE_NOW));
  await page.addInitScript(({ project }) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1,
    tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName: project, section: 'deploy' }],
    activeKey: `hub:${project}`,
  })), { project: PROJECT });
  await mockApp(page, deploy);
  await page.goto('/');
  await page.evaluate(() => document.querySelectorAll('[data-testid="error-dialog-overlay"], vite-error-overlay')
    .forEach(element => ((element as HTMLElement).style.display = 'none')));
  await expect(page.getByTestId('project-deploy-area')).toBeVisible({ timeout: 60_000 });
}

async function capture(page: Page, name: string): Promise<void> {
  fs.mkdirSync(results, { recursive: true });
  await page.getByTestId('project-deploy-area').screenshot({ path: path.join(results, `${name}--mocked.png`) });
}

test.describe('Project Deploy area (mocked)', () => {
  test('works with partial data: environments and lifecycle, no release service, no health checks', async ({ page }) => {
    await openDeploy(page, { environments: deployEnvironmentsFixture(), releases: 404, lifecycle: deployLifecycleFixture() });
    const area = page.getByTestId('project-deploy-area');

    await expect(area).not.toContainText('Preview data');
    const staging = page.getByRole('article', { name: 'Staging' });
    const production = page.getByRole('article', { name: 'Production' });
    await expect(staging.getByTestId('deploy-environment-release')).toHaveText('website@sha256:bbbbbbbbbbbb');
    await expect(staging.getByTestId('deploy-environment-age')).toHaveText('12 min ago');
    await expect(staging.getByTestId('deploy-environment-health')).toHaveText('No checks');
    await expect(staging.getByTestId('deploy-environment-cards')).toHaveText('Card lists arrive with release records');
    await expect(staging.getByTestId('deploy-environment-last-run')).toContainText('Deploy succeeded');
    await expect(production.getByTestId('deploy-environment-approval')).toContainText('Awaiting approval');

    // Focus order follows the environment order: Staging, then Production.
    await staging.getByRole('button', { name: 'Open last run for Staging' }).focus();
    await page.keyboard.press('Tab');
    await expect(production.getByRole('button', { name: 'Open last run for Production' })).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(production.getByRole('button', { name: 'Review the pending approval for Production' })).toBeFocused();

    await page.getByTestId('deploy-tab-releases').click();
    await expect(page.getByTestId('deploy-releases-unavailable')).toContainText('No release records on this server yet');

    await page.getByTestId('deploy-tab-pipeline').click();
    await expect(page.getByTestId('deploy-stage-gate')).toContainText('npm run build');
    await expect(page.getByTestId('deploy-stage-production')).toContainText('Approval: Owner or Maintainer');
  });

  test('filters the deploy history and opens a run with its step log', async ({ page }) => {
    await openDeploy(page, { environments: deployEnvironmentsFixture(), releases: deployReleasesFixture(), lifecycle: deployLifecycleFixture() });
    await page.getByTestId('deploy-tab-history').click();
    const rows = page.locator('[data-testid^="deploy-run-row-"]');

    await expect(rows).toHaveCount(5);
    await page.getByTestId('deploy-history-filter-environment').selectOption('production');
    await expect(rows).toHaveCount(1);
    await page.getByTestId('deploy-history-filter-environment').selectOption('all');
    await page.getByTestId('deploy-history-filter-outcome').selectOption('failed');
    await expect(rows).toHaveCount(1);
    await page.getByTestId('deploy-history-filter-outcome').selectOption('all');
    await page.getByTestId('deploy-history-filter-period').selectOption('24h');
    await expect(page.getByTestId('deploy-history-count')).toHaveText('4 of 5 runs');
    await page.getByTestId('deploy-history-reset').click();
    await expect(rows).toHaveCount(5);

    const opener = page.getByTestId('deploy-run-open-job-c-stage');
    await opener.click();
    const drawer = page.getByTestId('deploy-run-drawer');
    await expect(drawer).toContainText('Deploy to Staging');
    await expect(drawer.getByTestId('deploy-run-outcome')).toHaveText('Failed');
    await expect(drawer.getByTestId('deploy-run-steps').locator('li')).toHaveCount(3);
    await expect(drawer).toContainText('Health check failed');
    await expect(drawer.getByTestId('sidesheet-close')).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(drawer).toHaveCount(0);
    await expect(opener).toBeFocused();
  });

  test('captures every view in light and dark at 1280 and 1920 px', async ({ page }) => {
    await openDeploy(page, { environments: deployEnvironmentsFixture(), releases: deployReleasesFixture(), lifecycle: deployLifecycleFixture() });

    for (const [width, height] of [[1280, 800], [1920, 1080]] as const) {
      await page.setViewportSize({ width, height });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await page.getByTestId('deploy-tab-overview').click();
        await expect(page.getByRole('article', { name: 'Staging' })).toContainText('Release 2026.10.19.1');
        await capture(page, `deploy-overview-${width}-${theme}`);
        await page.getByTestId('deploy-tab-releases').click();
        await expect(page.getByTestId('deploy-release-detail')).toContainText('WEB-33, WEB-34, WEB-35');
        await capture(page, `deploy-releases-${width}-${theme}`);
        await page.getByTestId('deploy-tab-pipeline').click();
        await expect(page.getByTestId('deploy-stage-staging')).toBeVisible();
        await capture(page, `deploy-pipeline-${width}-${theme}`);
        await page.getByTestId('deploy-tab-history').click();
        await page.getByTestId('deploy-run-open-job-a-stage-rollback').click();
        await expect(page.getByTestId('deploy-run-drawer')).toContainText('Rollback to Staging');
        await capture(page, `deploy-history-drawer-${width}-${theme}`);
        await page.getByTestId('deploy-run-drawer').getByTestId('sidesheet-close').click();
      }
    }
  });

  test('explains every empty panel for a project without deploy data', async ({ page }) => {
    await openDeploy(page, {
      environments: { project: PROJECT, environments: [], promotions: [], jobs: [], failureCards: [] },
      releases: 404,
      lifecycle: { valid: true, lifecycle: { declared: false, nodes: [] } },
    });

    await expect(page.getByTestId('deploy-environments-empty')).toContainText('No environments declared');
    await page.getByTestId('deploy-tab-releases').click();
    await expect(page.getByTestId('deploy-releases-unavailable')).toBeVisible();
    await page.getByTestId('deploy-tab-pipeline').click();
    await expect(page.getByTestId('deploy-pipeline-undeclared')).toContainText('No deploy lifecycle declared');
    await page.getByTestId('deploy-tab-history').click();
    await expect(page.getByTestId('deploy-history-empty')).toContainText('No deploy runs yet');
    await setTheme(page, 'light');
    await capture(page, 'deploy-history-empty-light');
  });
});
