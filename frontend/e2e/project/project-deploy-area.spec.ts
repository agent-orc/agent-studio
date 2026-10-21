import { expect, test, type Page, type Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';
import {
  ARTIFACT_A,
  ARTIFACT_B,
  DEPLOY_FIXTURE_NOW,
  deployDeploymentsFixture,
  deployEnvironmentsFixture,
  deployReleasesFixture,
  deployStatusFixture,
  deploymentLogFixture,
  pipelineDefinitionFixture,
} from '../../src/app/features/project-detail/testing/deploy-fixtures';

/**
 * Project Deploy area on a fully mocked backend (AGT-3226).
 *
 * Routes follow the real contracts: the AGT-3176 environments API, the AGT-3201
 * deploy routes under /api/projects/{p}/deploy (status, releases, deployments,
 * the step log, approve, reject, cancel), AGT-3223 environment health, and the
 * AGT-3221 pipeline definition. A missing route answers 404 without a
 * body, as ASP.NET does for an unmapped path. The browser clock is pinned so
 * ages and the period filter are deterministic.
 */

const PROJECT = 'Website';
const STORAGE = '/srv/tasks/website';
const DEPLOY = `/api/projects/${PROJECT}/deploy`;
const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results', 'project-deploy-area');

const MISSING = Symbol('missing route');
type Answer = unknown | typeof MISSING | { status: number; body: unknown };
interface DeployMocks {
  environments: unknown;
  status: Answer;
  releases: Answer;
  deployments: Answer;
  pipelineDefinition: Answer;
  health?: Answer;
  /** POST approve, reject, or cancel; records each call. */
  act?: (action: string, deploymentId: string) => Answer;
}

const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });

function answer(route: Route, value: Answer): Promise<void> {
  if (value === MISSING) return route.fulfill({ status: 404, body: '' });
  if (value && typeof value === 'object' && 'status' in value && 'body' in value && typeof value.status === 'number') {
    return route.fulfill(json(value.body, value.status));
  }
  return route.fulfill(json(value));
}

const AGT3201 = {
  status: deployStatusFixture(), releases: deployReleasesFixture(), deployments: deployDeploymentsFixture(),
};
const HOST_ONLY = { status: MISSING, releases: MISSING, deployments: MISSING };

async function mockApp(page: Page, deploy: DeployMocks): Promise<void> {
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
  const project = {
    sourceType: 'local-folder', id: 'PROJ-WEB', displayName: PROJECT, shortCode: 'WEB', workspaceId: 'ws',
    color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: STORAGE,
    repositoryPath: '/srv/repo/website', rootPath: '/srv/repo/website', urls: [], ownershipMappings: [],
    archived: false, createdAt: '2026-10-01T00:00:00Z',
  };
  await page.route('**/api/v1/workspaces**', route => route.fulfill(json([{ id: 'ws', displayName: 'Workspace', projects: [project] }])));
  // An empty project list opens the first-run settings over the restored Deploy tab.
  await page.route('**/api/v1/projects', route => route.fulfill(json([project])));
  await page.route(`**/api/projects/${PROJECT}/environments`, route => route.fulfill(json(deploy.environments)));
  await page.route(`**/api/projects/${PROJECT}/environments/health`, route => answer(route, deploy.health ?? {
    capturedAt: new Date(DEPLOY_FIXTURE_NOW).toISOString(), probeOrigin: 'mock', environments: [],
  }));
  await page.route(`**${DEPLOY}/status`, route => answer(route, deploy.status));
  await page.route(`**${DEPLOY}/releases/page?*`, route => {
    if (deploy.releases === MISSING || (deploy.releases && typeof deploy.releases === 'object' && 'status' in deploy.releases))
      return answer(route, deploy.releases);
    const params = new URL(route.request().url()).searchParams;
    let releases = deploy.releases as ReturnType<typeof deployReleasesFixture>;
    const digest = params.get('artifact')?.split('@').at(-1);
    if (digest) releases = releases.filter(item => item.artifactDigests.some(value => value.split('@').at(-1) === digest));
    if (params.has('notAfterUtc')) releases = releases.filter(item => Date.parse(item.createdAtUtc) <= Date.parse(params.get('notAfterUtc')!));
    const offset = Number(params.get('offset') ?? 0);
    const limit = Number(params.get('limit') ?? 25);
    return answer(route, { items: releases.slice(offset, offset + limit), offset, limit, total: releases.length });
  });
  await page.route(`**${DEPLOY}/deployments/page?*`, route => {
    if (deploy.deployments === MISSING || (deploy.deployments && typeof deploy.deployments === 'object' && 'status' in deploy.deployments))
      return answer(route, deploy.deployments);
    const params = new URL(route.request().url()).searchParams;
    let runs = deploy.deployments as ReturnType<typeof deployDeploymentsFixture>;
    if (params.has('environmentId')) runs = runs.filter(run => run.environmentId === params.get('environmentId'));
    if (params.has('state')) runs = runs.filter(run => run.state === params.get('state'));
    if (params.has('sinceUtc')) runs = runs.filter(run => Date.parse(run.createdAtUtc) >= Date.parse(params.get('sinceUtc')!));
    const offset = Number(params.get('offset') ?? 0);
    return answer(route, { items: runs.slice(offset, offset + 25), offset, limit: 25, total: runs.length });
  });
  await page.route(new RegExp(`${DEPLOY}/deployments/(?!page)[^/?]+$`), route => {
    const id = decodeURIComponent(route.request().url().split('/').at(-1)!);
    const runs = deploy.deployments as ReturnType<typeof deployDeploymentsFixture>;
    const run = Array.isArray(runs) ? runs.find(item => item.id === id) : undefined;
    return run ? answer(route, run) : answer(route, { status: 404, body: { error: `Unknown deployment '${id}'.` } });
  });
  await page.route(`**${DEPLOY}/deployments/*/log`, route => {
    const id = route.request().url().split('/').at(-2)!;
    return deploy.deployments === MISSING ? answer(route, MISSING) : answer(route, { deploymentId: id, lines: deploymentLogFixture(id) });
  });
  await page.route(new RegExp(`${DEPLOY}/deployments/[^/]+/(approve|reject|cancel)$`), route => {
    const [id, action] = route.request().url().split('/').slice(-2);
    return answer(route, route.request().method() === 'POST' && deploy.act ? deploy.act(action, id) : MISSING);
  });
  await page.route(`**/api/projects/${PROJECT}/pipeline-definition`, route => answer(route, deploy.pipelineDefinition));
}

async function openDeploy(page: Page, deploy: DeployMocks): Promise<void> {
  page.on('dialog', dialog => dialog.accept());
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

const fullMocks = (extra: Partial<DeployMocks> = {}): DeployMocks => ({
  environments: deployEnvironmentsFixture(), ...AGT3201, pipelineDefinition: pipelineDefinitionFixture(),
  ...extra,
});

test.describe('Project Deploy area (mocked)', () => {
  test('works with partial data: AGT-3176 environments only, AGT-3201 and AGT-3221 routes missing', async ({ page }) => {
    await openDeploy(page, { environments: deployEnvironmentsFixture(), ...HOST_ONLY, pipelineDefinition: MISSING });
    const area = page.getByTestId('project-deploy-area');

    await expect(area).not.toContainText('Preview data');
    const staging = page.getByRole('article', { name: 'Staging' });
    const production = page.getByRole('article', { name: 'Production' });
    await expect(staging.getByTestId('deploy-environment-release')).toHaveText('website@sha256:cccccccccccc');
    await expect(staging.getByTestId('deploy-environment-age')).toHaveText('12 min ago');
    await expect(staging.getByTestId('deploy-environment-health')).toHaveText('Healthy');
    await expect(staging.getByTestId('deploy-environment-cards')).toHaveText('Card lists arrive with release records');
    await expect(staging.getByTestId('deploy-environment-last-run')).toContainText('Deploy succeeded');
    await expect(production.getByTestId('deploy-environment-approval')).toContainText('Awaiting approval: website@sha256:cccccccccccc');

    await page.getByTestId('deploy-tab-releases').click();
    await expect(page.getByTestId('deploy-releases-unavailable')).toContainText('No release records on this server yet');
    await page.getByTestId('deploy-tab-history').click();
    await expect(page.getByTestId('deploy-history-unavailable')).toContainText('No deploy run records on this server yet');
    await page.getByTestId('deploy-tab-pipeline').click();
    await expect(page.getByTestId('deploy-pipeline-unavailable')).toBeVisible();
    await setTheme(page, 'light');
    await capture(page, 'deploy-pipeline-fallback-1280-light');
  });

  test('status cards: labels, health in words, and focus order Staging before Production', async ({ page }) => {
    await openDeploy(page, fullMocks());
    const staging = page.getByRole('article', { name: 'Staging' });
    const production = page.getByRole('article', { name: 'Production' });

    await expect(staging.getByTestId('deploy-environment-release')).toHaveText('Release 2026.10.19.2');
    await expect(staging.getByTestId('deploy-environment-last-run')).toContainText('Redeploy cancelled');
    await expect(staging.getByText('Health: Healthy')).toHaveCount(1);
    await expect(production.getByTestId('deploy-environment-newer')).toHaveText('Release 2026.10.19.2 is ready with 3 cards');
    await expect(production.getByTestId('deploy-environment-approval')).toContainText('Release 2026.10.19.2, requested by local.dev');

    await staging.getByRole('button', { name: 'Open last run for Staging' }).focus();
    await page.keyboard.press('Tab');
    await expect(production.getByRole('button', { name: 'Open last run for Production' })).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(production.getByRole('button', { name: 'Review the pending approval for Production' })).toBeFocused();
  });

  test('history filters AGT-3201 runs; the drawer shows steps with their log and acts on a waiting approval', async ({ page }) => {
    const calls: string[] = [];
    let deployments = deployDeploymentsFixture();
    const mocks = fullMocks({
      act: (action, id) => {
        calls.push(`${action}:${id}`);
        if (action === 'cancel') return { status: 403, body: { error: 'Approval requires the Owner or Maintainer role.' } };
        deployments = deployments.map(item => item.id === id ? { ...item, state: 'queued', approvedBy: 'local.owner' } : item);
        return deployments.find(item => item.id === id);
      },
    });
    // The deployments route answers the current list, so the approval shows after the reload.
    Object.defineProperty(mocks, 'deployments', { get: () => deployments });
    await openDeploy(page, mocks);
    await page.getByTestId('deploy-tab-history').click();
    const rows = page.locator('[data-testid^="deploy-run-row-"]');

    await expect(rows).toHaveCount(7);
    await page.getByTestId('deploy-history-filter-environment').selectOption('production');
    await expect(rows).toHaveCount(2);
    await page.getByTestId('deploy-history-filter-environment').selectOption('all');
    await page.getByTestId('deploy-history-filter-outcome').selectOption('cancelled');
    await expect(rows).toHaveCount(1);
    await page.getByTestId('deploy-history-filter-outcome').selectOption('all');
    await page.getByTestId('deploy-history-filter-period').selectOption('24h');
    await expect(page.getByTestId('deploy-history-count')).toHaveText('6 of 6 runs');
    await page.getByTestId('deploy-history-reset').click();
    await expect(rows).toHaveCount(7);

    const opener = page.getByTestId('deploy-run-open-run-b-stage');
    await opener.click();
    const drawer = page.getByTestId('deploy-run-drawer');
    await expect(drawer).toContainText('Deploy to Staging');
    await expect(drawer.getByTestId('deploy-run-outcome')).toHaveText('Failed');
    await expect(drawer.getByTestId('deploy-run-step-health')).toContainText('Health check failed (health-http-failed).');
    await expect(drawer.getByTestId('sidesheet-close')).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(drawer).toHaveCount(0);
    await expect(opener).toBeFocused();

    await page.getByTestId('deploy-run-open-run-c-production').click();
    await expect(drawer.getByTestId('deploy-run-approval')).toHaveText('Waiting for an Owner or Maintainer');
    await drawer.getByTestId('deploy-run-cancel').click();
    await expect(drawer.getByTestId('deploy-run-action-error')).toHaveText('Approval requires the Owner or Maintainer role.');
    await drawer.getByTestId('deploy-run-approve').click();
    await expect(drawer.getByTestId('deploy-run-outcome')).toHaveText('Queued');
    await expect(drawer.getByTestId('deploy-run-approval')).toHaveText('Approved by local.owner');
    expect(calls).toEqual(['cancel:run-c-production', 'approve:run-c-production']);
  });

  test('pipeline: AGT-3221 static-site and container steps, and the parsed diagram of an invalid project.yml', async ({ page }) => {
    await openDeploy(page, fullMocks());
    await page.getByTestId('deploy-tab-pipeline').click();
    await expect(page.getByTestId('pipeline-node-staging')).toContainText('Static site');
    await expect(page.getByTestId('pipeline-node-staging')).toContainText('/srv/www/website');
    await expect(page.getByTestId('pipeline-node-production')).toContainText('/srv/api/compose.yml (api)');

    await page.unroute(`**/api/projects/${PROJECT}/pipeline-definition`);
    await page.route(`**/api/projects/${PROJECT}/pipeline-definition`, route => answer(route, {
      ...pipelineDefinitionFixture(), valid: false,
      issues: [{ path: 'deploy[0]', code: 'invalid-step', message: 'Invalid step', line: 12 }],
    }));
    await page.getByTestId('deploy-refresh').click();
    await page.getByTestId('deploy-tab-pipeline').click();
    await expect(page.getByTestId('deploy-pipeline-issues')).toContainText('project.yml has 1 validation issue');
    await expect(page.getByTestId('pipeline-node-staging')).toContainText('Static site');
    await setTheme(page, 'dark');
    await capture(page, 'deploy-pipeline-invalid-1280-dark');
  });

  test('never masks a failing deploy route as "not on this server"', async ({ page }) => {
    await openDeploy(page, fullMocks({
      deployments: { status: 500, body: { error: 'Deployment state is unreadable.' } },
      releases: { status: 404, body: { error: "Unknown project 'Website'" } },
    }));

    await page.getByTestId('deploy-tab-history').click();
    await expect(page.getByTestId('deploy-history-error')).toContainText('Deployment state is unreadable.');
    await page.getByTestId('deploy-tab-releases').click();
    await expect(page.getByTestId('deploy-releases-error')).toContainText("Unknown project 'Website'");
    await expect(page.getByTestId('deploy-releases-unavailable')).toHaveCount(0);
    await page.getByTestId('deploy-tab-overview').click();
    await expect(page.getByTestId('deploy-source-notices')).toContainText('Deployment state is unreadable.');
    await expect(page.getByTestId('deploy-source-notices')).toContainText("Unknown project 'Website'");
    await expect(page.getByRole('article', { name: 'Staging' })).toBeVisible();
  });

  test('loads one bounded slice at a time for a project with a large history', async ({ page }) => {
    const sample = deployDeploymentsFixture()[0];
    const release = deployReleasesFixture()[0];
    const runs = Array.from({ length: 80 }, (_, index) => ({
      ...sample, id: `long-run-${index}`, createdAtUtc: new Date(DEPLOY_FIXTURE_NOW - index * 60_000).toISOString(),
    }));
    const releases = Array.from({ length: 40 }, (_, index) => ({
      ...release, id: `long-release-${index}`, createdAtUtc: new Date(DEPLOY_FIXTURE_NOW - index * 60_000).toISOString(),
    }));
    const requestedOffsets: string[] = [];
    await openDeploy(page, fullMocks({ deployments: runs, releases }));
    await page.route(`**${DEPLOY}/deployments/page?*`, async route => {
      const params = new URL(route.request().url()).searchParams;
      requestedOffsets.push(params.get('offset') ?? '');
      const offset = Number(params.get('offset') ?? 0);
      await answer(route, { items: runs.slice(offset, offset + 25), offset, limit: 25, total: runs.length });
    });
    await page.getByTestId('deploy-tab-history').click();
    await expect(page.locator('[data-testid^="deploy-run-row-"]')).toHaveCount(25);
    await page.getByTestId('deploy-history-next').click();
    await expect(page.getByTestId('deploy-history-count')).toHaveText('50 of 80 runs');
    expect(requestedOffsets).toContain('25');
    await page.getByTestId('deploy-tab-releases').click();
    await expect(page.getByRole('list', { name: 'Releases, newest first' }).getByRole('button')).toHaveCount(25);
    await page.getByTestId('deploy-releases-next').click();
    await expect(page.getByRole('list', { name: 'Releases, newest first' }).getByRole('button')).toHaveCount(15);
  });

  test('acts on a status run beyond the first page and names a migrated host artifact by a bounded lookup', async ({ page }) => {
    const calls: string[] = [];
    const stage = deployDeploymentsFixture().find(run => run.id === 'run-c-stage')!;
    const newer = Array.from({ length: 30 }, (_, index) => ({
      ...stage, id: `newer-stage-${index}`, createdAtUtc: new Date(DEPLOY_FIXTURE_NOW - index * 60_000).toISOString(),
    }));
    const release = deployReleasesFixture()[0];
    const padding = Array.from({ length: 40 }, (_, index) => ({
      ...release, id: `newer-release-${index}`, createdAtUtc: new Date(DEPLOY_FIXTURE_NOW - index * 60_000).toISOString(),
    }));
    const environments = deployEnvironmentsFixture();
    const production = environments.environments[1];
    production.deployedArtifact = ARTIFACT_B;
    production.history.unshift({ at: '2026-10-19T11:30:00Z', status: 'healthy', artifact: ARTIFACT_B,
      previousArtifact: ARTIFACT_A, evidence: 'host-migration' });
    const lookups: string[] = [];
    page.on('request', request => {
      const url = new URL(request.url());
      if (url.pathname.endsWith('/deploy/releases/page') && url.searchParams.has('artifact')) lookups.push(url.search);
    });
    await openDeploy(page, fullMocks({
      environments, deployments: [...newer, ...deployDeploymentsFixture()], releases: [...padding, ...deployReleasesFixture()],
      act: (action, id) => { calls.push(`${action}:${id}`); return deployDeploymentsFixture().find(item => item.id === id); },
    }));

    const card = page.getByTestId('deploy-environment-production');
    await expect(card).toContainText('Release 2026.10.19.1');
    expect(lookups).toHaveLength(1);
    expect(new URLSearchParams(lookups[0]).get('limit')).toBe('1');
    await card.getByRole('button', { name: 'Review the pending approval for Production' }).click();
    await expect(page.getByTestId('deploy-run-drawer')).toContainText('Production');
    await page.getByTestId('deploy-run-approve').click();
    await expect.poll(() => calls).toEqual(['approve:run-c-production']);
  });

  test('captures every view in light and dark at 1280 and 1920 px', async ({ page }) => {
    await openDeploy(page, fullMocks());

    for (const [width, height] of [[390, 844], [1280, 800], [1920, 1080]] as const) {
      await page.setViewportSize({ width, height });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await page.getByTestId('deploy-tab-overview').click();
        await expect(page.getByRole('article', { name: 'Staging' })).toContainText('Release 2026.10.19.2');
        if (width === 390) {
          const areaBounds = await page.getByTestId('project-deploy-area').boundingBox();
          expect(areaBounds?.width).toBeGreaterThan(280);
          const bounds = await page.getByRole('article', { name: 'Staging' }).boundingBox();
          expect(bounds?.width).toBeLessThanOrEqual(390);
        }
        await capture(page, `deploy-overview-${width}-${theme}`);
        await page.getByTestId('deploy-tab-releases').click();
        await expect(page.getByTestId('deploy-release-detail')).toContainText('WEB-35');
        await capture(page, `deploy-releases-${width}-${theme}`);
        await page.getByTestId('deploy-tab-pipeline').click();
        await expect(page.getByTestId('pipeline-node-staging')).toBeVisible();
        await capture(page, `deploy-pipeline-${width}-${theme}`);
        await page.getByTestId('deploy-tab-history').click();
        await page.getByTestId('deploy-run-open-run-c-production').click();
        await expect(page.getByTestId('deploy-run-drawer')).toContainText('Deploy to Production');
        await capture(page, `deploy-history-drawer-${width}-${theme}`);
        await page.getByTestId('deploy-run-drawer').getByTestId('sidesheet-close').click();
      }
    }
  });

  test('explains every empty panel for a project without deploy data', async ({ page }) => {
    await openDeploy(page, {
      environments: { project: PROJECT, environments: [], promotions: [], failureCards: [] },
      status: [], releases: [], deployments: [],
      pipelineDefinition: MISSING,
    });

    await expect(page.getByTestId('deploy-environments-empty')).toContainText('No environments declared');
    await page.getByTestId('deploy-tab-releases').click();
    await expect(page.getByTestId('deploy-releases-empty')).toContainText('No releases yet');
    await page.getByTestId('deploy-tab-pipeline').click();
    await expect(page.getByTestId('deploy-pipeline-unavailable')).toContainText('Pipeline definition unavailable');
    await page.getByTestId('deploy-tab-history').click();
    await expect(page.getByTestId('deploy-history-empty')).toContainText('No deploy runs yet');
    await setTheme(page, 'light');
    await capture(page, 'deploy-history-empty-light');
  });
});
