import { expect, test, type Page, type Route } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';
import type { ProjectEnvironments } from '../../src/app/models/project-overview.model';
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
  productionDownHealthFixture,
  productionRollbackFixture,
  RELEASE_1,
  RELEASE_2,
  RELEASE_3,
} from '../../src/app/features/project-detail/testing/deploy-fixtures';
import { healthResponse } from '../../src/testing/environment-health';

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
// AGT-3879 running Studio instance fixtures; imports hoist, so this sits apart from the shared import block.
import { instanceDeployFixture } from '../../src/app/features/project-detail/testing/instance-deploy-fixtures';
/** Refreshes the area after moving the pinned clock: the AGT-3879 instance section re-reads when the area clock moves. */
async function refreshLater(page: Page, seconds: number): Promise<void> {
  await page.clock.setFixedTime(new Date(DEPLOY_FIXTURE_NOW + seconds * 1000));
  await page.getByTestId('deploy-refresh').click();
}
type Answer = unknown | typeof MISSING | { status: number; body: unknown };
interface DeployMocks {
  environments: unknown;
  status: Answer;
  releases: Answer;
  deployments: Answer;
  pipelineDefinition: Answer;
  health?: Answer;
  /** POST approve, reject, or cancel; records each call. */
  act?: (action: string, deploymentId: string, body?: unknown, csrf?: string) => Answer;
  /** POST the lifecycle rollback route of an environment (F13). */
  rollback?: (environmentId: string, body?: unknown) => Answer;
  /** Called when anything posts the older AGT-3176 rollback route. */
  legacyRollback?: (environmentId: string) => void;
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

async function mockApp(page: Page, deploy: DeployMocks, missingCheckout = false): Promise<void> {
  await page.route('**/api/**', route => route.fulfill(json([])).catch(() => undefined));
  await page.route('**/hubs/v1/studio/negotiate**', route => route.fulfill(json({
    negotiateVersion: 1, connectionId: 'deploy-area-mock', connectionToken: 'deploy-area-mock',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }],
  })));
  await page.routeWebSocket('**/hubs/v1/studio**', socket => {
    socket.onMessage(message => {
      if (typeof message === 'string' && message.includes('"protocol":"json"')) socket.send('{}\u001e');
    });
  });
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
  await page.route(url => url.pathname === '/api/v1/operations/structure', route => route.fulfill(json({ nodes: [], links: [] })));
  await page.route(url => url.pathname === '/api/v1/operations/structure/library', route => route.fulfill(json({ registries: [], consumers: [] })));
  await page.route('**/api/diagnostics/api-stalls**', route => route.fulfill(json({
    status: 'ok', intervalMs: 1000, maxDelayMs: 0, alarmThresholdMs: 1000, alarmWindowMinutes: 5, recent: [],
  })));
  await page.route('**/api/security/posture', route => route.fulfill(json({ error: 'permission-denied' }, 403)));
  await page.route('**/api/workspaces/*/quota/admission', route => route.fulfill(json({ error: 'unavailable' }, 503)));
  const project = {
    sourceType: 'local-folder', id: 'PROJ-WEB', displayName: PROJECT, shortCode: 'WEB', workspaceId: 'ws',
    color: null, cliDefault: null, modelDefault: null, sortOrder: 0, storageLocation: STORAGE,
    repositoryPath: missingCheckout ? null : '/srv/repo/website',
    rootPath: missingCheckout ? null : '/srv/repo/website',
    repositoryUrl: missingCheckout ? 'https://github.com/example/website.git' : null,
    urls: [], ownershipMappings: [],
    archived: false, createdAt: '2026-10-01T00:00:00Z',
  };
  await page.route('**/api/v1/workspaces**', route => route.fulfill(json([{ id: 'ws', displayName: 'Workspace', projects: [project] }])));
  // An empty project list opens the first-run settings over the restored Deploy tab.
  await page.route('**/api/v1/projects', route => route.fulfill(json([project])));
  await page.route(`**/api/projects/${PROJECT}/environments`, route => route.fulfill(json(deploy.environments)));
  await page.route(`**/api/projects/${PROJECT}/environments/*/volumes`, route => route.fulfill(json({
    database: null, volumes: [], schedules: [], audit: [], adapterReady: false,
  })));
  await page.route(`**/api/projects/${PROJECT}/environments/*/databases`, route => route.fulfill(json({ databases: [] })));
  await page.route(`**/api/projects/${PROJECT}/environments/health`, route => answer(route, deploy.health ?? {
    capturedAt: new Date(DEPLOY_FIXTURE_NOW).toISOString(), probeOrigin: 'mock', environments: [],
  }));
  await page.route(`**${DEPLOY}/secret-readiness**`, route => answer(route, {
    project: PROJECT, releaseId: null, environments: [],
  }));
  await page.route(`**${DEPLOY}/status`, route => answer(route, deploy.status));
  // F14: every environment has its secrets bound unless a test says otherwise; the catch-all `[]` is no answer here.
  await page.route(url => url.pathname === `${DEPLOY}/secret-readiness`, route => answer(route, {
    project: PROJECT, releaseId: new URL(route.request().url()).searchParams.get('releaseId'), environments: [],
  }));
  // A predicate, not a glob: the artifact query value contains "/", which a glob "*" does not match.
  await page.route(url => url.pathname === `${DEPLOY}/releases/page`, route => {
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
  await page.route(`**${DEPLOY}/releases/*/page?*`, route => {
    if (deploy.releases === MISSING || (deploy.releases && typeof deploy.releases === 'object' && 'status' in deploy.releases))
      return answer(route, deploy.releases);
    const releases = deploy.releases as { id: string }[];
    const id = route.request().url().split('/').at(-2)!;
    const index = releases.findIndex(item => item.id === id);
    if (index < 0) return answer(route, { status: 404, body: { error: 'Unknown release.' } });
    const offset = Math.floor(index / 25) * 25;
    return answer(route, { items: releases.slice(offset, offset + 25), offset, limit: 25, total: releases.length });
  });
  await page.route(`**${DEPLOY}/releases/*/notes`, route => {
    if (!Array.isArray(deploy.releases)) return answer(route, MISSING);
    const id = route.request().url().split('/').at(-2);
    const release = deploy.releases.find(item => item.id === id);
    return release ? answer(route, { releaseId: release.id, version: release.version, preview: true,
      notes: release.cardKeys.map(cardKey => ({ cardKey, text: cardKey === 'WEB-35' ? 'Faster project search' : `Delivered ${cardKey}` })) })
      : answer(route, MISSING);
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
  await page.route(new RegExp(`${DEPLOY}/deployments/[^/]+/(approve|reject|cancel|intervention)$`), route => {
    const [id, action] = route.request().url().split('/').slice(-2);
    return answer(route, route.request().method() === 'POST' && deploy.act
      ? deploy.act(action, id, route.request().postDataJSON(), route.request().headers()['x-csrf-token']) : MISSING);
  });
  await page.route(`**/api/projects/${PROJECT}/pipeline-definition`, route => answer(route, deploy.pipelineDefinition));
  await page.route(new RegExp(`${DEPLOY}/environments/[^/]+/rollback$`), route => {
    const id = route.request().url().split('/').at(-2)!;
    return answer(route, route.request().method() === 'POST' && deploy.rollback
      ? deploy.rollback(id, route.request().postDataJSON()) : MISSING);
  });
  await page.route(new RegExp(`/api/projects/${PROJECT}/environments/[^/]+/rollback$`), route => {
    deploy.legacyRollback?.(route.request().url().split('/').at(-2)!);
    return answer(route, { status: 202, body: {} });
  });
}

async function openDeploy(page: Page, deploy: DeployMocks, navigationTimeoutMs = 30_000, missingCheckout = false): Promise<void> {
  page.on('dialog', dialog => dialog.accept());
  await page.clock.setFixedTime(new Date(DEPLOY_FIXTURE_NOW));
  await page.addInitScript(({ project }) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1,
    tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName: project, section: 'deploy' }],
    activeKey: `hub:${project}`,
  })), { project: PROJECT });
  await mockApp(page, deploy, missingCheckout);
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: navigationTimeoutMs });
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
  test('Stop and Remove explain retention and require a later host confirmation', async ({ page }) => {
    const environments: ProjectEnvironments = deployEnvironmentsFixture();
    const calls: string[] = [];
    await openDeploy(page, fullMocks({ environments }));
    await page.route(`**/api/projects/${PROJECT}/environments/stage/stop`, async route => {
      calls.push('stop');
      await route.fulfill(json({}));
    });
    await page.route(`**/api/projects/${PROJECT}/environments/stage/run-target-removal/confirm`, async route => {
      calls.push('host-removal');
      await route.fulfill(json({}));
    });
    await page.route('**/api/tasks', async route => {
      calls.push('card');
      expect(route.request().postDataJSON().promptMarkdown).toContain('Do not add a removal tombstone');
      await route.fulfill(json({ id: 'WEB-100' }));
    });

    const stage = page.getByRole('article', { name: 'Staging' });
    await expect(stage.getByRole('button', { name: 'Stop Staging' })).toBeVisible();
    await expect(stage.getByRole('button', { name: 'Remove Staging' })).toBeVisible();
    await stage.getByRole('button', { name: 'Stop Staging' }).click();
    await expect(page.getByTestId('deploy-environment-action-message')).toContainText('Stop queued');
    await stage.getByRole('button', { name: 'Remove Staging' }).click();
    await expect(page.getByTestId('deploy-environment-action-message')).toContainText('WEB-100');
    expect(calls).toEqual(['stop', 'card']);

    environments.runTargetRemovals = [{ environmentId: 'stage', hostId: 'run-host', kind: 'stage', status: 'pending-confirmation' }];
    await page.getByTestId('deploy-refresh').click();
    await expect(page.getByTestId('deploy-pending-removal')).toContainText('app stays on the host');
    await setTheme(page, 'light');
    await capture(page, 'deploy-environment-removal-pending-light');
    await page.getByTestId('deploy-pending-removal').screenshot({ path: path.join(results, 'deploy-pending-removal-light--mocked.png') });
    await setTheme(page, 'dark');
    await capture(page, 'deploy-environment-removal-pending-dark');
    await page.getByTestId('deploy-pending-removal').screenshot({ path: path.join(results, 'deploy-pending-removal-dark--mocked.png') });
    await page.getByTestId('deploy-pending-removal').getByRole('button', { name: 'Confirm host removal' }).click();
    expect(calls).toEqual(['stop', 'card', 'host-removal']);
  });

  test('persists signed-in cookies across a browser restart and approves Production', async ({ browser, baseURL }) => {
    const first = await browser.newContext({ baseURL });
    const signInPage = await first.newPage();
    await openDeploy(signInPage, fullMocks());
    await signInPage.route('**/api/auth/login', route => route.fulfill({
      ...json({ authenticated: true, csrfToken: 'restart-proof' }),
      headers: { 'Set-Cookie': 'agentstudio-csrf=restart-proof; Path=/; Max-Age=604800; SameSite=Strict' },
    }));
    const loginStatus = await signInPage.evaluate(async () => (await fetch('/api/auth/login', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: 'owner', password: 'test-password' }),
    })).status);
    expect(loginStatus).toBe(200);
    await first.addCookies([{ name: 'agentstudio-session', value: 'owner-session', url: baseURL!,
      expires: Math.floor(Date.now() / 1000) + 604800, httpOnly: true, sameSite: 'Strict' }]);
    const stored = await first.storageState();
    expect(stored.cookies.find(cookie => cookie.name === 'agentstudio-session')?.expires).toBeGreaterThan(0);
    expect(stored.cookies.find(cookie => cookie.name === 'agentstudio-csrf')?.expires).toBeGreaterThan(0);
    await first.close();

    let approved = false;
    const restarted = await browser.newContext({ baseURL, storageState: stored });
    try {
      const page = await restarted.newPage();
      let deployments = deployDeploymentsFixture();
      const mocks = fullMocks({ act: (action, id, _body, csrf) => {
        if (action !== 'approve') return MISSING;
        approved = csrf === 'restart-proof';
        if (!approved) return { status: 401, body: { error: 'Sign in again to approve.' } };
        deployments = deployments.map(run => run.id === id ? { ...run, state: 'queued', approvedBy: 'owner' } : run);
        return deployments.find(run => run.id === id);
      } });
      Object.defineProperty(mocks, 'deployments', { get: () => deployments });
      await openDeploy(page, mocks);
      await page.getByTestId('deploy-tab-history').click();
      await page.getByTestId('deploy-run-open-run-c-production').click();
      await page.getByTestId('deploy-run-approve').click();
      await expect(page.getByTestId('deploy-run-action-error')).toHaveCount(0);
      await expect(page.getByTestId('deploy-run-outcome')).toHaveText('Queued');
      expect(approved).toBe(true);
    } finally {
      await restarted.close();
    }
  });
  test('failed Production deployment leaves its running release uncertain', async ({ page }) => {
    const environments = deployEnvironmentsFixture();
    const production = environments.environments[1];
    production.deployedArtifact = ARTIFACT_B;
    production.health = 'failed';
    production.history.unshift({ at: '2026-10-19T11:59:00Z', status: 'failed', artifact: ARTIFACT_B,
      previousArtifact: ARTIFACT_A, evidence: 'health-http-failed' });
    const statuses = deployStatusFixture();
    statuses[1].releaseUncertain = true;
    const failed = { ...deployDeploymentsFixture()[0], id: 'failed-production', environmentId: 'production',
      releaseId: deployReleasesFixture()[1].id, state: 'failed', trigger: 'deploy',
      createdAtUtc: '2026-10-19T11:58:00Z', finishedAtUtc: '2026-10-19T11:59:00Z' };
    statuses[1].currentDeployment = failed;
    await openDeploy(page, fullMocks({ environments, status: statuses,
      deployments: [failed, ...deployDeploymentsFixture()] }));
    const card = page.getByRole('article', { name: 'Production' });
    await expect(card.getByTestId('deploy-environment-release')).toHaveText('Running release uncertain. A failed release may be running.');
    await expect(card.getByTestId('deploy-environment-age')).toHaveText('Unknown');
    await setTheme(page, 'light');
    await capture(page, 'production-running-release-uncertain-light');
    await setTheme(page, 'dark');
    await capture(page, 'production-running-release-uncertain-dark');
  });
  test('URL project without checkout shows one Deploy state and stops checkout-dependent reads', async ({ page }) => {
    let reads = 0;
    page.on('request', request => {
      if (request.url().includes('/api/projects/Website/deploy/')
        || request.url().includes('/api/projects/Website/workbenches')) reads++;
    });
    await openDeploy(page, fullMocks(), 30_000, true);
    await expect(page.getByTestId('deploy-checkout-missing')).toContainText('server checkout');
    await page.getByTestId('deploy-tab-pipeline').click();
    await page.getByTestId('deploy-tab-steps').click();
    await expect(page.getByTestId('deploy-source-notices')).toHaveCount(0);
    const before = reads;
    await page.waitForTimeout(1_200);
    expect(reads).toBe(before);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await capture(page, `agt-3822-checkout-missing-${theme}`);
    }
  });

  test('shows release card verdicts and opens the stored release range in both themes', async ({ page }) => {
    const releases = deployReleasesFixture();
    const release = releases[0];
    release.cardKeys = ['WEB-35', 'WEB-36', 'WEB-37'];
    release.quality = {
      cards: [{ key: 'WEB-35', verdict: 'pass' }, { key: 'WEB-36', verdict: 'concerns' },
        { key: 'WEB-37', verdict: 'block' }],
      pass: 1, concerns: 1, block: 1, notReviewed: 0, worstVerdict: 'block',
      rangeVerdict: 'not-reviewed',
      range: { repositoryId: 'repo-website', baseSha: 'a'.repeat(40), headSha: release.commitSha, requiresConfirmation: false },
      rangeUnavailableReason: null,
    };
    let reviewRequests = 0;
    let openedUrl: string | null = null;
    await page.route(/\/change-review(?:\?|$)/, route => {
      reviewRequests++;
      return route.fulfill({ status: 500, body: 'Unexpected review request' });
    });
    await page.context().route('**/api/integrations/quality-studio/open**', route => {
      openedUrl = route.request().url();
      return route.fulfill({ status: 200, contentType: 'text/html', body: '<!doctype html><title>Opened</title>' });
    });
    await openDeploy(page, fullMocks({ releases }));
    await page.getByTestId('deploy-tab-releases').click();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.getByTestId(`deploy-release-quality-${release.id}`)).toContainText('Quality: Block · 1 pass, 1 concerns, 1 block, 0 not reviewed');
      await expect(page.getByTestId('deploy-release-quality-counts')).toContainText('1 pass · 1 concerns · 1 block · 0 not reviewed');
      await expect(page.getByRole('list', { name: 'Card Quality verdicts' }).getByRole('listitem')).toHaveCount(3);
      await expect(page.getByTestId('deploy-release-range-verdict')).toContainText('Not reviewed');
      await capture(page, `deploy-release-quality-${theme}`);
    }
    await page.getByTestId('deploy-release-open-quality').click();
    await expect.poll(() => openedUrl).not.toBeNull();
    const target = new URL(new URL(openedUrl!).searchParams.get('return')!, 'https://quality.example');
    expect(target.searchParams.get('base')).toBe('a'.repeat(40));
    expect(target.searchParams.get('head')).toBe(release.commitSha);
    expect(reviewRequests).toBe(0);
  });

  test('shows a stored release range verdict in both themes without starting a review', async ({ page }) => {
    const releases = deployReleasesFixture();
    releases[0].quality = {
      cards: [], pass: 0, concerns: 0, block: 0, notReviewed: 0,
      worstVerdict: 'not-reviewed', rangeVerdict: 'concerns',
      range: { repositoryId: 'repo-website', baseSha: 'a'.repeat(40),
        headSha: releases[0].commitSha, requiresConfirmation: false },
      rangeUnavailableReason: null,
    };
    let reviewRequests = 0;
    await page.route(/\/change-review(?:\?|$)/, route => {
      reviewRequests++;
      return route.fulfill({ status: 500, body: 'Unexpected review request' });
    });
    await openDeploy(page, fullMocks({ releases }));
    await page.getByTestId('deploy-tab-releases').click();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await expect(page.getByTestId('deploy-release-range-verdict')).toContainText('Release range: Concerns');
      await capture(page, `deploy-release-stored-range-${theme}`);
    }
    expect(reviewRequests).toBe(0);
  });

  test('shows the pre-delivery verdict and candidate SHA on the release header', async ({ page }) => {
    const releases = deployReleasesFixture();
    releases[0].preDeliveryVerdict = { project: PROJECT, commitSha: 'c'.repeat(40),
      commandDigest: 'd'.repeat(64), status: 'green', finishedAtUtc: '2026-10-19T12:00:00Z' };
    await openDeploy(page, fullMocks({ releases }));
    await page.getByTestId('deploy-tab-releases').click();
    await expect(page.getByTestId('deploy-release-verdict')).toContainText(`Full suite: green · SHA ${'c'.repeat(40)}`);
    await setTheme(page, 'light');
    await capture(page, 'release-verdict-light');
    await setTheme(page, 'dark');
    await capture(page, 'release-verdict-dark');
  });

  test('opens the Runbooks tab with current-page semantics', async ({ page }) => {
    test.setTimeout(180_000);
    await openDeploy(page, fullMocks(), 90_000);
    await page.route(`**${DEPLOY}/runbooks`, route => route.fulfill(json([
      { name: 'restart', steps: ['restart-container'], environments: [] },
    ])));
    await page.route(`**${DEPLOY}/runbooks/runs?*`, route => route.fulfill(json([])));

    const tab = page.getByTestId('deploy-tab-runbooks');
    await expect(tab).toBeEnabled();
    await tab.click();
    await expect(tab).toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('deploy-tab-overview')).not.toHaveAttribute('aria-current', 'page');
    await expect(page.getByTestId('project-deploy-area').getByTestId('environment-runbooks').first()).toContainText('restart');
    await setTheme(page, 'light');
    await capture(page, 'runbooks-tab-light');
    await setTheme(page, 'dark');
    await capture(page, 'runbooks-tab-dark');
  });

  test('names people in deploy history, the run drawer, and runbooks by display name (the playthrough)', async ({ page }) => {
    test.setTimeout(240_000);
    const OWNER = 'usr_ecaff70110';
    const MAINTAINER = 'usr_0b1d2c3e4f';
    const deployments = deployDeploymentsFixture().map(item => item.id === 'run-a-production'
      ? { ...item, requestedBy: MAINTAINER, approvedBy: OWNER } : item);
    await openDeploy(page, fullMocks({ deployments }), 90_000);
    const person = (id: string, displayName: string) => ({
      id, displayName, emoji: null, colour: null, kind: 'human', registeredAt: '2026-10-01T00:00:00Z',
      lastSeenAt: null, tokenBudgetMonthly: null, notes: null,
    });
    await page.route('**/api/clients/', route => route.fulfill(json([person(OWNER, 'Ada Owner'), person(MAINTAINER, 'Sam Maintainer')])));
    await page.route(`**${DEPLOY}/runbooks`, route => route.fulfill(json([
      { name: 'restart', steps: ['restart-container'], environments: [] },
    ])));
    await page.route(`**${DEPLOY}/runbooks/runs?*`, route => route.fulfill(json([
      { id: 'runbook-1', name: 'restart', state: 'succeeded', requestedBy: MAINTAINER, approvedBy: OWNER, steps: [] },
    ])));
    await page.route(url => url.pathname === `${DEPLOY}/secret-readiness`, route => route.fulfill(json({
      project: PROJECT, releaseId: null, environments: [],
    })));
    await page.route(`**${DEPLOY}/operations`, route => route.fulfill(json({
      project: PROJECT, since: '2026-09-20T00:00:00Z', until: '2026-10-20T00:00:00Z', environments: [],
      dora: { successfulProductionDeploys: 0, deploysPerWeek: 0, medianCardToProductionHours: null,
        cardsMeasured: 0, changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
    })));
    await page.reload({ waitUntil: 'domcontentloaded' });
    await expect(page.getByTestId('project-deploy-area')).toBeVisible({ timeout: 60_000 });

    await page.getByTestId('deploy-tab-history').click();
    await expect(page.getByTestId('deploy-run-row-run-a-production')).toContainText('Sam Maintainer');
    await page.getByTestId('deploy-run-open-run-a-production').click();
    const drawer = page.getByTestId('deploy-run-drawer');
    await expect(drawer.getByTestId('deploy-run-approval')).toHaveText('Approved by Ada Owner');
    await expect(drawer).not.toContainText('usr_');
    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
    for (const theme of ['light', 'dark'] as const) {
      await page.setViewportSize({ width: 1280, height: 900 });
      await setTheme(page, theme);
      await page.screenshot({ path: path.join(results, `deploy-run-display-names-1280-${theme}--mocked.png`) });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await setTheme(page, 'light');
    await page.screenshot({ path: path.join(results, 'deploy-run-display-names-390--mocked.png') });
    await page.keyboard.press('Escape');
    await expect(drawer).toHaveCount(0);

    await page.setViewportSize({ width: 1280, height: 900 });
    await page.getByTestId('deploy-tab-runbooks').click();
    const runbooks = page.getByTestId('project-deploy-area').getByTestId('environment-runbooks').first();
    await expect(runbooks).toContainText('requested by Sam Maintainer');
    await expect(runbooks).toContainText('approved by Ada Owner');
    await expect(runbooks).not.toContainText('usr_');
    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await capture(page, `runbooks-display-names-1280-${theme}`);
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await setTheme(page, 'light');
    await capture(page, 'runbooks-display-names-390');
  });

  test('Operations rail opens an older release on a fresh Deploy mount without fetching the first page', async ({ page }) => {
    const base = deployReleasesFixture()[0];
    const releases = Array.from({ length: 40 }, (_, index) => ({ ...base,
      id: `release-${index}`, version: `version-${index}`,
      createdAtUtc: new Date(DEPLOY_FIXTURE_NOW - index * 60_000).toISOString(),
    }));
    await openDeploy(page, fullMocks({ releases }));
    await page.route(`**${DEPLOY}/operations`, route => answer(route, {
      project: PROJECT, since: '2026-09-20T00:00:00Z', until: '2026-10-20T00:00:00Z',
      environments: [{ environment: { id: 'production', name: 'Production', kind: 'production' },
        telemetryAvailable: false, metrics: null,
        markers: [{ deploymentId: 'run-old', releaseId: 'release-35', version: 'version-35',
          atUtc: '2026-10-19T10:00:00Z', trigger: 'deploy', state: 'succeeded', cardKeys: [] }] }],
      dora: { successfulProductionDeploys: 1, deploysPerWeek: 0.25, medianCardToProductionHours: null,
        cardsMeasured: 0, changeFailureRate: 0, medianRecoveryHours: null, recoveriesMeasured: 0 },
    }));
    await page.getByTestId(`studio-explorer-project-operations-${PROJECT}`).click();
    await expect(page.getByTestId('operations-release-release-35')).toBeVisible();
    const firstPageRequests: string[] = [];
    await page.route(`**${DEPLOY}/releases/page?*`, route => {
      firstPageRequests.push(route.request().url());
      return answer(route, { items: releases.slice(0, 25), offset: 0, limit: 25, total: releases.length });
    });
    const containing: string[] = [];
    await page.route(`**${DEPLOY}/releases/*/page?*`, async route => {
      containing.push(route.request().url());
      const offset = 25;
      await answer(route, { items: releases.slice(offset), offset, limit: 25, total: releases.length });
    });
    await page.getByTestId('operations-release-release-35').click();
    await expect(page.getByTestId('deploy-release-detail')).toContainText('version-35');
    expect(containing).toHaveLength(1);
    expect(firstPageRequests).toHaveLength(0);
    await expect(page.getByTestId('deploy-release-detail')).toContainText('version-35');
    await capture(page, 'operations-older-release');
  });

  test('Operations shows a slow trace waterfall and opens it from a log line', async ({ page }) => {
    await openDeploy(page, fullMocks());
    const traceId = '11111111111111111111111111111111';
    await page.route(`**${DEPLOY}/operations`, route => answer(route, {
      project: PROJECT, since: '2026-09-20T00:00:00Z', until: '2026-10-20T00:00:00Z',
      environments: [{ environment: { id: 'production', name: 'Production', kind: 'production' },
        telemetryAvailable: false, metrics: null, markers: [] }],
      dora: { successfulProductionDeploys: 0, deploysPerWeek: null, medianCardToProductionHours: null,
        cardsMeasured: 0, changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
    }));
    await page.route(`**${DEPLOY}/operations/logs**`, route => answer(route, { telemetryAvailable: true,
      lines: [{ at: '2026-10-20T00:00:00Z', level: 'ERROR', message: 'Dependency timeout',
        releaseId: null, traceId }] }));
    await page.route(`**/environments/production/telemetry/traces`, route => answer(route, [
      { traceId, startedAt: '2026-10-20T00:00:00Z', durationMs: 900, failed: true,
        name: 'GET /checkout', spanCount: 2 },
    ]));
    await page.route(`**/environments/production/telemetry/traces/${traceId}`, route => answer(route, [
      { spanId: '2222222222222222', parentSpanId: null, name: 'GET /checkout', kind: 'SERVER',
        startedAt: '2026-10-20T00:00:00Z', durationMs: 900, failed: true, attributes: {} },
      { spanId: '3333333333333333', parentSpanId: '2222222222222222', name: 'inventory HTTP', kind: 'CLIENT',
        startedAt: '2026-10-20T00:00:00.100Z', durationMs: 780, failed: true, attributes: {} },
    ]));
    await page.getByTestId(`studio-explorer-project-operations-${PROJECT}`).click();
    await page.getByTestId('operations-search-traces').click();
    await expect(page.getByTestId('operations-traces')).toContainText('GET /checkout');
    await page.getByTestId('operations-search-logs').click();
    await page.getByTestId('operations-log-trace').click();
    await expect(page.getByTestId('operations-span-waterfall')).toContainText('inventory HTTP');
    fs.mkdirSync(results, { recursive: true });
    await setTheme(page, 'light');
    await page.getByTestId('project-operations').screenshot({ path: path.join(results, 'operations-trace-light--mocked.png') });
    await setTheme(page, 'dark');
    await page.getByTestId('project-operations').screenshot({ path: path.join(results, 'operations-trace-dark--mocked.png') });
  });

  test('Operations renders stored trends and a paged log hit in both themes and at 390 px', async ({ page }) => {
    await openDeploy(page, fullMocks());
    const metrics = {
      responseP50Ms: 24, responseP95Ms: 91, errorRate: 0.25, restarts: null, requests: 12,
      sessions: null, activeUsers: null, cpuCores: 0.4, cpuReservedCores: null,
      memoryBytes: 4096, memoryReservedBytes: null, diskBytes: 8192, diskReservedBytes: null,
      networkBytes: null, topEndpoints: [{ path: '/orders', requests: 12 }], dailyUsage: [],
      samples: [
        { at: '2026-10-19T09:00:00Z', responseP95Ms: 51, errorRate: 0, requests: 3, cpuCores: 0.2, memoryBytes: 3072, diskBytes: 7168 },
        { at: '2026-10-19T10:00:00Z', responseP95Ms: 91, errorRate: 0.25, requests: 9, cpuCores: 0.4, memoryBytes: 4096, diskBytes: 8192 },
      ],
    };
    await page.route(`**${DEPLOY}/operations`, route => answer(route, {
      project: PROJECT, since: '2026-09-20T00:00:00Z', until: '2026-10-20T00:00:00Z',
      environments: ['stage', 'production'].map(id => ({
        environment: { id, name: id === 'stage' ? 'Staging' : 'Production', kind: id },
        telemetryAvailable: true, metrics, markers: [],
      })),
      dora: { successfulProductionDeploys: 0, deploysPerWeek: null, medianCardToProductionHours: null,
        cardsMeasured: 0, changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
    }));
    await page.route(`**${DEPLOY}/operations/logs?*`, route => {
      const cursor = new URL(route.request().url()).searchParams.get('cursor');
      return answer(route, cursor ? { telemetryAvailable: true, nextCursor: null,
        lines: [{ at: '2026-10-19T10:10:00Z', level: 'ERROR', message: 'upstream timeout', releaseId: 'v2' }] }
        : { telemetryAvailable: true, nextCursor: 'next-page', lines: [] });
    });
    await page.getByTestId('deploy-tab-operations').click();
    const operations = page.getByTestId('project-operations');
    await expect(operations.getByTestId('operations-production')).toContainText('12');
    await expect(operations.getByRole('img', { name: /Response p95 trend/ })).toHaveCount(2);
    await operations.getByTestId('operations-search-logs').click();
    await operations.getByTestId('operations-more-logs').click();
    await expect(operations).toContainText('upstream timeout');
    fs.mkdirSync(results, { recursive: true });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await operations.getByRole('img', { name: /Response p95 trend/ }).first().scrollIntoViewIfNeeded();
      await page.screenshot({ path: path.join(results, `operations-telemetry-trends-1280-${theme}.png`) });
      await operations.getByTestId('operations-logs').scrollIntoViewIfNeeded();
      await page.screenshot({ path: path.join(results, `operations-telemetry-logs-1280-${theme}.png`) });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await operations.getByRole('img', { name: /Response p95 trend/ }).first().scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(results, 'operations-telemetry-trends-390-dark.png') });
    await operations.getByTestId('operations-logs').scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(results, 'operations-telemetry-logs-390-dark.png') });
    await expect(operations.getByTestId('operations-logs')).toContainText('upstream timeout');
  });

  test('project Operations links to installation backups in Settings with backup and host CA warnings in both themes and at 390 px (AGT-3574, AGT-3736)', async ({ page }) => {
    await openDeploy(page, fullMocks());
    await page.route(`**${DEPLOY}/operations`, route => answer(route, {
      project: PROJECT, since: '2026-09-20T00:00:00Z', until: '2026-10-20T00:00:00Z', environments: [],
      dora: { successfulProductionDeploys: 0, deploysPerWeek: null, medianCardToProductionHours: null,
        cardsMeasured: 0, changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
    }));
    const backup = (id: string, createdAt: string, verification: string, offHost: string) => ({
      id, createdAt, sizeBytes: 412 * 1024 * 1024, sha256: 'a'.repeat(64), fileCount: 18_240,
      verification, offHost, unsettledFiles: 0,
    });
    const newest = backup('studio-backup-20261019T060000Z-a1b2c3', '2026-10-19T06:00:00+00:00', 'verified', 'failed');
    let stale = true;
    await page.route('**/api/v1/management/backups', route => answer(route, {
      state: stale ? 'failing' : 'ok', configured: true, running: false, keyId: '3f2a9c0d11e4b7a8', secretKeyId: '0b1c2d3e4f5a6b7c',
      intervalHours: 6, verifyIntervalHours: 24, staleAfterHours: 12, retentionCount: 12,
      nextBackupAt: '2026-10-20T06:00:00+00:00',
      lastGood: newest, lastGoodAgeHours: stale ? 26 : 2, stale,
      lastRun: { at: '2026-10-20T06:00:00+00:00', trigger: 'schedule', outcome: stale ? 'failed' : 'succeeded',
        backupId: null, error: stale ? 'Backup source workspace is not readable.' : null, durationMilliseconds: 900 },
      lastVerification: { at: '2026-10-19T06:10:00+00:00', backupId: newest.id, outcome: 'verified',
        detail: 'Restored 18240 files and 3 checkout records into an empty staging host; every hash matched.' },
      offHost: { kind: 'sftp', target: 'sftp://studio@backup.example:22/srv/studio-backups', state: stale ? 'failed' : 'current',
        lastUploadedId: 'studio-backup-20261019T000000Z-9f8e7d', lastUploadedAt: '2026-10-19T00:01:00+00:00',
        lastError: stale ? 'sftp exited with 255: Connection timed out' : null },
      warnings: stale ? [
        { code: 'backup-stale', severity: 'danger', message: 'The newest good backup is 26 hours old; the threshold is 12 hours.' },
        { code: 'backup-failed', severity: 'danger', message: 'The last backup run failed: Backup source workspace is not readable.' },
        { code: 'offhost-failed', severity: 'danger', message: 'The off-host copy failed: sftp exited with 255: Connection timed out' },
        { code: 'host-ca-mismatch', severity: 'danger', message: 'The configured host CA does not match the restored host certificate state. Install the escrowed CA before issuing certificates.' },
      ] : [],
      backups: [stale ? newest : { ...newest, offHost: 'uploaded' }, backup('studio-backup-20261019T000000Z-9f8e7d', '2026-10-19T00:00:00+00:00', 'unverified', 'uploaded')],
      generatedAt: '2026-10-20T08:00:00+00:00',
    }));
    await page.getByTestId('deploy-tab-operations').click();
    await expect(page.getByTestId('project-operations').getByTestId('operations-backups')).toHaveCount(0);
    await page.getByTestId('project-operations').getByRole('link', { name: 'Settings → Operations' }).click();
    await expect(page.getByTestId('workspace-operations')).toBeVisible();
    const backups = page.getByTestId('operations-backups');
    await expect(backups.getByTestId('operations-backups-content-list')).toHaveAttribute('href', /studio-backup-restore\.md#credential-content-list$/);
    await expect(backups.getByTestId('operations-backups-recovery-dependency')).toContainText('Quality Studio data and registry are included when present');
    await expect(backups.getByTestId('operations-backups-recovery-dependency')).toContainText('repository remotes');
    await expect(backups.getByTestId('operations-backups-state')).toHaveAttribute('data-state', 'failing');
    await expect(backups.getByTestId('operations-backups-warnings').locator('[data-code="backup-stale"]')).toContainText('26 hours old');
    await expect(backups.getByTestId('operations-backups-warnings').locator('[data-code="host-ca-mismatch"]')).toContainText('escrowed CA');
    await expect(backups.getByTestId('operations-backups-age')).toHaveAttribute('data-stale', 'true');
    await expect(backups.getByTestId('operations-backups-age')).toContainText('26 h');
    await expect(backups.getByTestId('operations-backups-size')).toContainText('412.0 MB');
    await expect(backups.getByTestId('operations-backups-verification')).toContainText('Verified');
    await expect(backups.getByTestId('operations-backups-offhost')).toContainText('Copy failed');
    await expect(backups.getByTestId('operations-backups-offhost-id')).toContainText(newest.id);
    await expect(backups.getByTestId('operations-backups-offhost')).toContainText('sftp://studio@backup.example');
    await expect(backups.getByTestId('operations-backups-list').locator('tbody tr')).toHaveCount(2);
    await expect(backups.getByTestId('operations-backups-list').locator('tbody tr').first().getByRole('link', { name: /Archive content list/ }))
      .toHaveAttribute('href', `/api/v1/management/backups/${newest.id}/contents`);
    // The runbook compares the escrowed copies against these ids (AGT-3738).
    await expect(backups.getByTestId('operations-backups-key-id')).toContainText('3f2a9c0d11e4b7a8');
    await expect(backups.getByTestId('operations-backups-secret-key-id')).toContainText('0b1c2d3e4f5a6b7c');
    const handoff = backups.getByTestId('operations-backups-handoff');
    await expect(handoff).toContainText('Do not start the stack before restore');
    await expect(handoff).toContainText('Host CA private and public keys');
    await expect(handoff).toContainText('Project Git remotes');
    await expect(handoff).toContainText('Operations Server');
    await expect(handoff).toContainText('Quality Studio');
    await expect(handoff.getByRole('link', { name: 'Fetch both files from the off-host target' }))
      .toHaveAttribute('href', /#fresh-host-step-2$/);
    await expect(handoff).toContainText('Confirm this archive includes operations/; offline fencing required');
    await expect(handoff.getByRole('link', { name: 'Restore authority, re-enrol remote agents and check revocations' }))
      .toHaveAttribute('href', /#fresh-host-step-8$/);
    const downloadPromise = page.waitForEvent('download');
    await handoff.getByRole('button', { name: 'Download owner handoff' }).click();
    const download = await downloadPromise;
    expect(download.suggestedFilename()).toBe('studio-fresh-host-recovery.txt');
    const handoffPath = await download.path();
    const handoffText = fs.readFileSync(handoffPath!, 'utf8');
    expect(handoffText).toContain('Archive SHA-256');
    expect(handoffText).toContain('Not checked by Studio');
    expect(handoffText).toContain('#restore-onto-a-new-host');
    expect(handoffText).not.toContain('PRIVATE KEY');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      fs.mkdirSync(results, { recursive: true });
      await backups.screenshot({ path: path.join(results, `operations-host-ca-mismatch-${theme}--mocked.png`) });
      await page.setViewportSize({ width: 1280, height: 1600 });
      await handoff.screenshot({ path: path.join(results, `operations-recovery-handoff-${theme}--mocked.png`) });
      await page.setViewportSize({ width: 1280, height: 800 });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    expect(await backups.evaluate(element => element.getBoundingClientRect().width)).toBeLessThanOrEqual(390);
    await backups.screenshot({ path: path.join(results, 'operations-host-ca-mismatch-390--mocked.png') });
    await page.setViewportSize({ width: 390, height: 2200 });
    await handoff.screenshot({ path: path.join(results, 'operations-recovery-handoff-390--mocked.png') });
    await page.setViewportSize({ width: 1280, height: 800 });

    stale = false;
    await backups.getByTestId('operations-backups-refresh').click();
    await expect(backups.getByTestId('operations-backups-state')).toHaveAttribute('data-state', 'ok');
    await expect(backups.getByTestId('operations-backups-warnings')).toHaveCount(0);
    await expect(backups.getByTestId('operations-backups-offhost')).toContainText('Copied');
    await expect(backups.getByTestId('operations-backups-list').locator('tbody tr').first()).toContainText('Copied');
    await backups.screenshot({ path: path.join(results, 'operations-backups-current-dark--mocked.png') });

    await page.setViewportSize({ width: 390, height: 844 });
    await setTheme(page, 'light');
    await expect(backups.getByTestId('operations-backups-secret-key-id')).toBeVisible();
    await backups.screenshot({ path: path.join(results, 'operations-backups-current-390-light--mocked.png') });
    await setTheme(page, 'dark');
    await backups.screenshot({ path: path.join(results, 'operations-backups-current-390-dark--mocked.png') });
  });

  test('Operations shows the restore receipt, unknown off-host archives and the verification scope after a restore (AGT-3734)', async ({ page }) => {
    await openDeploy(page, fullMocks());
    await page.route(`**${DEPLOY}/operations`, route => answer(route, {
      project: PROJECT, since: '2026-10-11T00:00:00Z', until: '2026-11-10T00:00:00Z', environments: [],
      dora: { successfulProductionDeploys: 0, deploysPerWeek: null, medianCardToProductionHours: null,
        cardsMeasured: 0, changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
    }));
    const lostArchive = (stamp: string, suffix: string) => ({
      id: `studio-backup-${stamp}-${suffix}`, fileName: `studio-backup-${stamp}-${suffix}.tar.gz.sbk`,
      sizeBytes: 412 * 1024 * 1024, modifiedAt: `${stamp.slice(0, 4)}-${stamp.slice(4, 6)}-${stamp.slice(6, 8)}T${stamp.slice(9, 11)}:01:00+00:00`,
    });
    const restored = 'studio-backup-20261109T120000Z-c3d4e5';
    let listable = true;
    await page.route('**/api/v1/management/backups', route => answer(route, {
      state: 'warning', configured: true, running: false, keyId: '3f2a9c0d11e4b7a8', secretKeyId: '0b1c2d3e4f5a6b7c',
      intervalHours: 6, verifyIntervalHours: 24, staleAfterHours: 12, retentionCount: 12,
      nextBackupAt: '2026-11-10T09:05:00+00:00',
      lastGood: null, lastGoodAgeHours: null, stale: false, lastRun: null,
      lastVerification: { at: '2026-11-09T12:10:00+00:00', backupId: restored, outcome: 'verified', detail: null,
        proved: [
          `The archive ${restored} matches the checksum recorded when it was made.`,
          'The backup key on this host (fingerprint 3f2a9c0d11e4b7a8) opens and authenticates it.',
          'Every restored file matches its recorded SHA-256 hash in an empty staging folder.',
        ],
        notProved: [
          'Project checkouts were not cloned from their origins; a real restore needs read access to every remote.',
          "The owner's escrowed copies of the backup key and the secret store key were not checked; compare their fingerprints with backup-key.sh id and secret-key.sh id.",
        ] },
      offHost: listable
        ? { kind: 'directory', target: '/mnt/studio-backups', state: 'idle', lastUploadedId: null, lastUploadedAt: null, lastError: null,
            listing: { state: 'listed', detail: null, archiveCount: 3, notInCatalog: [
              lostArchive('20261109T120000Z', 'c3d4e5'), lostArchive('20261109T060000Z', 'b2c3d4'), lostArchive('20261109T000000Z', 'a1b2c3'),
            ] } }
        : { kind: 'sftp', target: 'sftp://studio@backup.example:22/srv/studio-backups', state: 'idle', lastUploadedId: null,
            lastUploadedAt: null, lastError: null,
            listing: { state: 'not-listable', archiveCount: 0, notInCatalog: [],
              detail: 'This host may only write to the sftp target, so it cannot list the archives there. List them at the target with an account that may read it.' } },
      warnings: [{ code: 'backup-missing', severity: 'warning', message: `No good backup exists on this host since the restore of ${restored}.` }],
      backups: [],
      generatedAt: '2026-11-10T09:00:00+00:00',
      lastRestore: {
        backupId: restored, archiveFileName: `${restored}.tar.gz.sbk`, archiveSha256: 'b'.repeat(64),
        archiveCreatedAt: '2026-11-09T12:00:00+00:00', archiveHost: 'studio-host-a', archiveStudioVersion: '1.42.0',
        restoredAt: '2026-11-10T08:40:00+00:00', restoredBy: 'owner@studio-host-b', keyId: '3f2a9c0d11e4b7a8',
        secretKeyId: '0b1c2d3e4f5a6b7c', secretKeyCopyId: '0b1c2d3e4f5a6b7c', filesRestored: 18_240, checkoutsCloned: 3, skippedSources: [],
      },
      operationsRecovery: { backupId: restored, fencedAt: '2026-11-10T08:41:00+00:00', items: [
        { kind: 'reenrol', id: 'host-a', complete: false, oldPrincipalIds: ['old-a'] },
        { kind: 'agent-revocation', id: 'host-a', complete: false, oldPrincipalIds: ['old-a'] },
        { kind: 'reenrol', id: 'host-b', complete: false, oldPrincipalIds: ['old-b'] },
        { kind: 'agent-revocation', id: 'host-b', complete: false, oldPrincipalIds: ['old-b'] },
        { kind: 'host-certificate-revocations', id: 'installation', complete: false, oldPrincipalIds: [] },
        { kind: 'operator-revocations', id: 'installation', complete: false, oldPrincipalIds: [] },
      ] },
    }));
    await page.getByTestId('deploy-tab-operations').click();
    const projectOperations = page.getByTestId('project-operations');
    await expect(projectOperations.getByTestId('operations-backups')).toHaveCount(0);
    const settingsOperations = projectOperations.getByRole('link', { name: 'Settings → Operations' });
    await expect(settingsOperations).toHaveAttribute('href', '#/workspace/settings/operations');
    await settingsOperations.click();
    await expect(page).toHaveURL(/#\/workspace\/settings\/operations$/);
    await expect(page.getByTestId('workspace-operations')).toBeVisible();
    const backups = page.getByTestId('operations-backups');
    const receipt = backups.getByTestId('operations-backups-restore');
    await expect(receipt).toContainText('owner@studio-host-b');
    await expect(receipt).toContainText(restored);
    await expect(receipt).toContainText('studio-host-a');
    await expect(receipt).toContainText('3f2a9c0d11e4b7a8');
    await expect(backups.getByTestId('operations-backups-restore-loss')).toContainText('is not in this installation');
    await expect(backups.getByTestId('operations-backups-offhost-listing')).toContainText('3 of 3 archives that this host does not know');
    await expect(backups.getByTestId('operations-backups-offhost-unknown').locator('tbody tr')).toHaveCount(3);
    await expect(backups.getByTestId('operations-backups-verification-proved')).toContainText('key on this host');
    await expect(backups.getByTestId('operations-backups-verification-not-proved')).toContainText('backup-key.sh id');
    const recoverySection = backups.getByTestId('operations-recovery');
    const recoveryChecks = recoverySection.getByTestId('operations-recovery-checks').locator('li');
    await expect(recoveryChecks).toHaveCount(6);
    await expect(backups.getByTestId('operations-recovery-checks').locator('li[data-complete="false"]')).toHaveCount(6);
    await expect(backups.getByTestId('operations-recovery')).toContainText('Re-enrol remote agent host-b');
    fs.mkdirSync(results, { recursive: true });
    // Tall viewports, so the screenshots hold the whole panel instead of the scrolled part.
    await page.setViewportSize({ width: 1280, height: 1800 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await backups.screenshot({ path: path.join(results, `operations-backups-restored-${theme}--mocked.png`) });
      await recoverySection.screenshot({ path: path.join(results, `operations-recovery-checks-${theme}--mocked.png`) });
    }
    await page.setViewportSize({ width: 390, height: 3000 });
    await expect(receipt).toBeVisible();
    // The panel fits its column: long archive names and warnings wrap instead of widening it.
    const widths = await backups.evaluate(element => ({
      panel: element.getBoundingClientRect().width, column: element.parentElement!.parentElement!.getBoundingClientRect().width,
    }));
    expect(widths.panel).toBeLessThanOrEqual(widths.column + 1);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await backups.screenshot({ path: path.join(results, `operations-backups-restored-390-${theme}--mocked.png`) });
    }

    listable = false;
    await page.setViewportSize({ width: 1280, height: 900 });
    await backups.getByTestId('operations-backups-refresh').click();
    await expect(backups.getByTestId('operations-backups-offhost-listing')).toHaveAttribute('data-listing-state', 'not-listable');
    await expect(backups.getByTestId('operations-backups-offhost-listing-detail')).toContainText('cannot list the archives');
    await expect(backups.getByTestId('operations-backups-offhost-unknown')).toHaveCount(0);
    await setTheme(page, 'light');
    await backups.screenshot({ path: path.join(results, 'operations-backups-write-only-target-light--mocked.png') });
  });

  test('Telemetry tab creates an ingest token shown once, shows last use and revokes in both themes', async ({ page }) => {
    await openDeploy(page, fullMocks());
    // Fake credential-shaped values are built at runtime.
    const secret = ['otl', 'stage-new', Math.random().toString(36).slice(2)].join('.');
    const created: unknown[] = [];
    const revoked: string[] = [];
    const token = (overrides: Record<string, unknown>) => ({
      id: 'stage-deploy', name: 'Studio deploy', project: PROJECT, environment: 'stage', signals: ['metrics', 'logs'],
      createdAt: '2026-10-18T08:00:00Z', createdBy: 'studio:deploy', expiresAt: null,
      lastUsedAt: '2026-10-20T09:55:00Z', revokedAt: null, ...overrides,
    });
    let tokens = [token({})];
    await page.route(`**/api/projects/${PROJECT}/environments/*/telemetry/tokens**`, route => {
      const request = route.request();
      if (request.method() === 'POST') {
        created.push(request.postDataJSON());
        const added = token({ id: 'stage-new', name: 'shop-api', createdBy: 'owner', lastUsedAt: null, createdAt: '2026-10-20T10:00:00Z' });
        tokens = [...tokens, added];
        return answer(route, { token: added, secret, otlpEndpoint: 'https://studio.example/api/telemetry/otlp',
          otlpHeaders: `Authorization=Bearer ${secret}` });
      }
      if (request.method() === 'DELETE') {
        const id = request.url().split('/').at(-1)!;
        revoked.push(id);
        tokens = tokens.map(item => item.id === id ? { ...item, revokedAt: '2026-10-20T10:05:00Z' } : item);
        return route.fulfill({ status: 204, body: '' });
      }
      return answer(route, request.url().includes('/environments/stage/') ? tokens : []);
    });
    await page.getByTestId('deploy-tab-telemetry').click();
    const panel = page.getByTestId('project-telemetry-tokens');
    await expect(panel.getByTestId('telemetry-tokens-environment')).toHaveValue('stage');
    const managed = panel.getByTestId('telemetry-token-stage-deploy');
    await expect(managed.getByTestId('telemetry-token-managed')).toHaveText('Managed by deploys');
    await expect(managed.getByTestId('telemetry-token-last-seen')).toContainText('Oct 20, 2026');

    await panel.getByTestId('telemetry-token-name').fill('shop-api');
    await panel.getByTestId('telemetry-token-logs').uncheck();
    await panel.getByTestId('telemetry-token-create').click();
    await expect(panel.getByTestId('telemetry-token-settings')).toContainText(`OTEL_EXPORTER_OTLP_HEADERS=Authorization=Bearer ${secret}`);
    await expect(panel.getByTestId('telemetry-token-settings')).toContainText('OTEL_EXPORTER_OTLP_ENDPOINT=https://studio.example/api/telemetry/otlp');
    expect(created).toEqual([{ name: 'shop-api', signals: ['metrics'], expiresAt: null }]);
    await expect(panel.getByTestId('telemetry-token-stage-new').getByTestId('telemetry-token-last-seen')).toHaveText('Never');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      fs.mkdirSync(results, { recursive: true });
      await panel.screenshot({ path: path.join(results, `telemetry-tokens-created-${theme}--mocked.png`) });
    }
    await panel.getByTestId('telemetry-token-dismiss').click();
    await expect(panel.getByTestId('telemetry-token-created')).toHaveCount(0);
    await expect(panel).not.toContainText(secret);

    await managed.getByTestId('telemetry-token-revoke').click();
    await managed.getByTestId('telemetry-token-confirm-revoke').click();
    await expect(managed).toHaveAttribute('data-state', 'revoked');
    expect(revoked).toEqual(['stage-deploy']);
    await panel.getByTestId('telemetry-tokens-environment').selectOption('production');
    await expect(panel.getByTestId('telemetry-tokens-empty')).toContainText('No ingest tokens for production yet.');
    await panel.screenshot({ path: path.join(results, 'telemetry-tokens-production-dark--mocked.png') });
  });

  test('Operations shows deploy markers, DORA and telemetry empty state in both themes', async ({ page }) => {
    await openDeploy(page, fullMocks());
    const release = deployReleasesFixture()[0];
    await page.route(`**${DEPLOY}/releases`, route => answer(route, deployReleasesFixture()));
    await page.route(`**${DEPLOY}/operations`, route => answer(route, {
      project: PROJECT, since: '2026-09-20T00:00:00Z', until: '2026-10-20T00:00:00Z',
      environments: [{ environment: { id: 'production', name: 'Production', kind: 'production' },
        telemetryAvailable: false, metrics: null,
        markers: [{ deploymentId: 'rollback-1', releaseId: release.id, version: release.version,
          atUtc: '2026-10-19T10:00:00Z', trigger: 'rollback', state: 'succeeded', cardKeys: release.cardKeys }] },
        { environment: { id: 'stage', name: 'Staging', kind: 'staging' },
          telemetryAvailable: false, metrics: null, markers: [] }],
      dora: { successfulProductionDeploys: 2, deploysPerWeek: 0.5, medianCardToProductionHours: 12,
        cardsMeasured: 1, changeFailureRate: 0.25, medianRecoveryHours: 3, recoveriesMeasured: 1 },
    }));
    await page.route(`**${DEPLOY}/operations/logs?*`, route => answer(route, { telemetryAvailable: false, lines: [] }));
    await page.getByTestId('deploy-tab-operations').click();
    const operations = page.getByTestId('project-operations');
    await expect(operations).toContainText('Telemetry is turned off in this Studio');
    await expect(operations.getByTestId('operations-dora')).toContainText('0.5 per week');
    await expect(page.getByTestId('hosted-data-panel')).toContainText('No persistent volumes declared.');
    await operations.getByTestId('operations-search-logs').click();
    await expect(operations.getByTestId('operations-logs-empty')).toBeVisible();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      fs.mkdirSync(results, { recursive: true });
      await operations.screenshot({ path: path.join(results, `operations-${theme}--mocked.png`) });
    }
    await page.route(`**${DEPLOY}/operations/logs?*`, route => answer(route, { telemetryAvailable: true,
      lines: [{ at: '2026-10-20T00:00:00Z', level: 'error', message: 'Old production error', releaseId: null }] }));
    await operations.getByTestId('operations-search-logs').click();
    await expect(operations).toContainText('Old production error');
    await operations.getByTestId('operations-log-environment').selectOption('stage');
    await expect(operations).not.toContainText('Old production error');
    let finishSearch: (() => void) | undefined;
    await page.route(`**${DEPLOY}/operations/logs?*`, async route => {
      await new Promise<void>(resolve => { finishSearch = resolve; });
      await answer(route, { telemetryAvailable: true,
        lines: [{ at: '2026-10-20T00:00:00Z', level: 'error', message: 'Stale stage error', releaseId: null }] });
    });
    await operations.getByTestId('operations-search-logs').click();
    await expect.poll(() => Boolean(finishSearch)).toBe(true);
    await operations.getByPlaceholder('Search text').fill('new');
    const staleResponse = page.waitForResponse(response => response.url().includes('/operations/logs') && response.status() === 200);
    finishSearch!();
    await staleResponse;
    await expect(operations).not.toContainText('Stale stage error');
    await operations.getByTestId(`operations-release-${release.id}`).click();
    await expect(page.getByTestId('deploy-release-detail')).toContainText(release.version);
  });

  test('host crash is visible without application telemetry at desktop and phone widths', async ({ page }) => {
    const environments = deployEnvironmentsFixture();
    const production = environments.environments.find(item => item.declaration.id === 'production')!;
    production.declaration.template = 'container';
    production.declaration.destination = '8080';
    production.observed = { state: 'crash-loop', restarts: 102, crashOutput: ['FATAL: configuration missing'] };
    production.runtimeIncident = { id: 'production/crash', kind: 'crash-loop',
      startedAt: '2026-10-19T12:00:00Z', updatedAt: '2026-10-19T12:01:00Z', restarts: 102, endedAt: null };
    await openDeploy(page, fullMocks({ environments }));
    await expect(page.getByTestId('deploy-environment-production').getByTestId('deploy-environment-health'))
      .toContainText('Crash loop');
    await page.route(`**${DEPLOY}/operations`, route => answer(route, {
      project: PROJECT, since: '2026-09-20T00:00:00Z', until: '2026-10-20T00:00:00Z',
      environments: [{ environment: { id: 'production', name: 'Production', kind: 'production' },
        telemetryAvailable: false, metrics: null, markers: [], observed: production.observed,
        runtimeIncident: production.runtimeIncident }],
      dora: { successfulProductionDeploys: 0, deploysPerWeek: null, medianCardToProductionHours: null,
        cardsMeasured: 0, changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
    }));
    await page.getByTestId('deploy-tab-operations').click();
    await expect(page.getByTestId('operations-host-restarts')).toContainText('102');
    await expect(page.getByTestId('operations-crash-output')).toContainText('FATAL: configuration missing');
    for (const [width, height, theme] of [[1280, 800, 'light'], [1280, 800, 'dark'],
      [390, 844, 'light'], [390, 844, 'dark']] as const) {
      await page.setViewportSize({ width, height });
      await setTheme(page, theme);
      fs.mkdirSync(results, { recursive: true });
      await page.getByTestId('operations-crash-output').scrollIntoViewIfNeeded();
      await expect(page.getByTestId('operations-crash-output')).toBeInViewport();
      await page.screenshot({ path: path.join(results,
        `host-crash-operations-${width}-${theme}--mocked.png`) });
      await page.getByTestId('deploy-tab-overview').click();
      await expect(page.getByTestId('deploy-environment-production')).toContainText('Crash loop');
      await page.getByTestId('deploy-environment-production').scrollIntoViewIfNeeded();
      await expect(page.getByTestId('deploy-environment-production')).toBeInViewport();
      await page.screenshot({ path: path.join(results,
        `host-crash-card-${width}-${theme}--mocked.png`) });
      await page.getByTestId('deploy-tab-operations').click();
    }
  });

  test('shows guided failure on the environment card and sends a reasoned skip', async ({ page }) => {
    const deployments = deployDeploymentsFixture().map(item => item.id === 'run-b-stage' ? {
      ...item, state: 'waiting-for-intervention', createdAtUtc: new Date(DEPLOY_FIXTURE_NOW).toISOString(),
      startedAtUtc: new Date(DEPLOY_FIXTURE_NOW).toISOString(), finishedAtUtc: null,
      failedStep: 'health', interventionExpiresAtUtc: new Date(DEPLOY_FIXTURE_NOW + 3_600_000).toISOString(),
    } : item);
    const status = deployStatusFixture();
    status[0] = { ...status[0], currentDeployment: deployments.find(item => item.id === 'run-b-stage')!,
      intervention: { deploymentId: 'run-b-stage', failedStep: 'health',
        expiresAtUtc: new Date(DEPLOY_FIXTURE_NOW + 3_600_000).toISOString(), decision: null, decidedBy: null } };
    const decisions: unknown[] = [];
    const mocks = fullMocks({ status, deployments, act: (action, id, body) => {
      expect(action).toBe('intervention');
      expect(id).toBe('run-b-stage');
      decisions.push(body);
      return deployments.find(item => item.id === id)!;
    } });
    await openDeploy(page, mocks);
    const staging = page.getByRole('article', { name: 'Staging' });
    await expect(staging.getByTestId('deploy-environment-last-run')).toContainText('waiting for intervention');
    await expect(staging.getByTestId('deploy-environment-intervention')).toContainText('Waiting for intervention');
    await staging.getByTestId('deploy-environment-open-run').click();
    const drawer = page.getByTestId('deploy-run-drawer');
    await expect(drawer.getByTestId('deploy-run-intervention')).toContainText('Health check failed');
    await expect(drawer.getByTestId('deploy-run-skip')).toBeDisabled();
    await setTheme(page, 'light');
    await capture(page, 'guided-failure-light');
    await setTheme(page, 'dark');
    await capture(page, 'guided-failure-dark');
    await drawer.getByTestId('deploy-run-skip-reason').fill('Known optional check');
    await drawer.getByTestId('deploy-run-skip').click();
    await expect.poll(() => decisions.length).toBe(1);
    expect(decisions).toEqual([{ action: 'skip', reason: 'Known optional check' }]);
  });

  test('opens an older pending intervention while a newer run is the last run', async ({ page }) => {
    const deployments = deployDeploymentsFixture().map(item => item.id === 'run-b-stage' ? {
      ...item, state: 'waiting-for-intervention', finishedAtUtc: null, failedStep: 'health',
      interventionExpiresAtUtc: new Date(DEPLOY_FIXTURE_NOW + 3_600_000).toISOString(),
    } : item);
    const status = deployStatusFixture();
    status[0] = { ...status[0], intervention: { deploymentId: 'run-b-stage', failedStep: 'health',
      expiresAtUtc: new Date(DEPLOY_FIXTURE_NOW + 3_600_000).toISOString(), decision: null, decidedBy: null } };
    await openDeploy(page, fullMocks({ status, deployments }));

    const staging = page.getByRole('article', { name: 'Staging' });
    await expect(staging.getByTestId('deploy-environment-last-run')).toContainText('Redeploy cancelled');
    await expect(staging.getByTestId('deploy-environment-intervention')).toContainText('Waiting for intervention');
    await setTheme(page, 'light');
    await capture(page, 'older-guided-card-light');
    await setTheme(page, 'dark');
    await capture(page, 'older-guided-card-dark');
    await staging.getByTestId('deploy-environment-intervention').click();
    await expect(page.getByTestId('deploy-run-drawer')).toContainText('Health check failed');
    await setTheme(page, 'light');
    await capture(page, 'older-guided-failure-light');
    await setTheme(page, 'dark');
    await capture(page, 'older-guided-failure-dark');
  });

  test('shows the running Studio instance, its live release, and what the next deploy brings (AGT-3879)', async ({ page }) => {
    // The Studio's own project declares no environments; the instance is its Production.
    const empty = { project: PROJECT, environments: [], promotions: [], jobs: [], hosts: [] };
    await openDeploy(page, fullMocks({ environments: empty, status: [], releases: [], deployments: [] }));
    await page.route(`**${DEPLOY}/instance`, route => answer(route, instanceDeployFixture()));
    await refreshLater(page, 1);
    const panel = page.getByTestId('instance-deploy');
    await expect(panel).toBeVisible();
    await expect(page.getByTestId('instance-deploy-version')).toHaveText('0.9.5-5faca674a');
    await expect(page.getByTestId('instance-deploy-live-since')).toContainText('2026-11-16 03:35 UTC');
    await expect(page.getByTestId('instance-deploy-brought')).toContainText('AGT-3839');
    await expect(page.getByTestId('instance-deploy-pending').locator('li')).toHaveText([
      /AGT-3861/, /AGT-3870/, /AGT-3879/,
    ]);
    for (const width of [1280, 390] as const) {
      await page.setViewportSize({ width, height: 900 });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        const overflow = await panel.evaluate(element => {
          const box = element.getBoundingClientRect();
          return [...element.querySelectorAll('*')].filter(child => child.getBoundingClientRect().right > box.right + 1)
            .map(child => `${child.tagName.toLowerCase()}.${child.className}`).slice(0, 3);
        });
        expect(overflow).toEqual([]);
        fs.mkdirSync(results, { recursive: true });
        await panel.screenshot({ path: path.join(results, `agt-3879-instance-deploy-${width}-${theme}.png`) });
      }
    }
  });

  test('names the step a failed or stalled Studio deploy stopped at (AGT-3879)', async ({ page }) => {
    let run: 'failed' | 'stalled' = 'failed';
    await openDeploy(page, fullMocks());
    await page.route(`**${DEPLOY}/instance`, route => answer(route, instanceDeployFixture(run)));
    await refreshLater(page, 1);
    await expect(page.getByTestId('instance-deploy-run-state')).toContainText('Deploy failed at host:runner-1');
    for (const width of [1280, 390] as const) {
      await page.setViewportSize({ width, height: 900 });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await page.getByTestId('instance-deploy').screenshot({ path: path.join(results, `agt-3879-instance-deploy-failed-${width}-${theme}.png`) });
      }
    }
    run = 'stalled';
    await refreshLater(page, 2);
    await expect(page.getByTestId('instance-deploy-run-state')).toContainText('Deploy stalled after host:runner-1 · no report for 30 min');
    await page.getByTestId('instance-deploy').screenshot({ path: path.join(results, 'agt-3879-instance-deploy-stalled-390-dark.png') });
  });

  test('shows no Studio instance panel for another project (AGT-3879)', async ({ page }) => {
    await openDeploy(page, fullMocks());
    await page.route(`**${DEPLOY}/instance`, route => answer(route,
      { status: 404, body: { error: `'${PROJECT}' is not the source project of this Studio instance.` } }));
    await refreshLater(page, 1);
    await expect(page.getByTestId('deploy-environments')).toBeVisible();
    await expect(page.getByTestId('instance-deploy')).toHaveCount(0);
    await expect(page.getByTestId('instance-deploy-notice')).toHaveCount(0);
  });

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
    await expect(staging.getByRole('button', { name: 'Stop Staging' })).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(staging.getByRole('button', { name: 'Remove Staging' })).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(production.getByRole('button', { name: 'Open last run for Production' })).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(production.getByRole('button', { name: 'Review the pending approval for Production' })).toBeFocused();
  });

  test('status-card actions open runs beyond the loaded deployment page', async ({ page }) => {
    const sample = deployDeploymentsFixture()[0];
    const runs = Array.from({ length: 30 }, (_, index) => ({
      ...sample, id: `other-run-${index}`, environmentId: 'other',
      createdAtUtc: new Date(DEPLOY_FIXTURE_NOW - index * 60_000).toISOString(),
    }));
    await openDeploy(page, fullMocks({ deployments: runs }));
    const drawer = page.getByTestId('deploy-run-drawer');

    await page.getByRole('button', { name: 'Open last run for Staging' }).click();
    await expect(drawer).toContainText('Redeploy to Staging');
    await drawer.getByTestId('sidesheet-close').click();
    await page.getByRole('button', { name: 'Review the pending approval for Production' }).click();
    await expect(drawer).toContainText('Deploy to Production');
    await expect(drawer.getByTestId('deploy-run-approve')).toBeVisible();
    await capture(page, 'deploy-status-run-outside-page-1280-light');
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

  test('M2: every environment surface reads Production down from the open incident, with release version and age', async ({ page }) => {
    test.setTimeout(180_000);
    // Every deploy report and /deploy/status still say healthy; the control plane has an open incident on Production.
    await openDeploy(page, fullMocks({ health: productionDownHealthFixture() }), 90_000);
    await page.route(`**/api/projects/${PROJECT}/deployment/summary`, route => route.fulfill(json({
      project: PROJECT, available: false, reason: 'No deploy-stable history is available for this project.',
      source: 'logs/stable-restarts.jsonl', lastDeployment: null, history: [], pendingCount: null, pendingCommits: [],
      targets: [], defaultEvidenceRun: null,
    })));
    await page.route(`**${DEPLOY}/runbooks`, route => route.fulfill(json([{ name: 'restart', steps: ['restart-container'], environments: [] }])));
    await page.route(`**${DEPLOY}/runbooks/runs?*`, route => route.fulfill(json([])));
    await page.route(`**${DEPLOY}/operations`, route => answer(route, {
      project: PROJECT, since: '2026-09-20T00:00:00Z', until: '2026-10-20T00:00:00Z', environments: [],
      dora: { successfulProductionDeploys: 0, deploysPerWeek: 0, medianCardToProductionHours: null,
        cardsMeasured: 0, changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
    }));
    const area = page.getByTestId('project-deploy-area');
    const expectProductionDown = async (production: ReturnType<Page['getByTestId']>, healthId: string) => {
      await expect(production.getByTestId(healthId)).toHaveText('Down');
      await expect(production.getByTestId(healthId)).toHaveAttribute('data-tone', 'danger');
      await expect(production).not.toContainText('Healthy');
    };

    const surfaces: { name: string; production: string; open: () => Promise<void>; check: () => Promise<void> }[] = [
      { name: 'overview', production: 'deploy-environment-production', open: () => page.getByTestId('deploy-tab-overview').click(), check: async () => {
        await expectProductionDown(area.getByTestId('deploy-environment-production'), 'deploy-environment-health');
        await expect(area.getByTestId('deploy-environment-production').getByTestId('deploy-environment-release')).toHaveText('Release 2026.10.18.1');
      } },
      { name: 'tools', production: 'environment-production', open: async () => { await page.getByTestId('deploy-tab-overview').click(); await page.getByTestId('deploy-open-tools').click(); }, check: async () => {
        const production = area.getByTestId('environment-production');
        await expectProductionDown(production, 'environment-state-health');
        await expect(production.getByTestId('environment-state-release')).toHaveText('Release 2026.10.18.1 deployed 13 h ago');
        await expect(production.getByTestId('environment-state-digest')).toHaveText('website@sha256:aaaaaaaaaaaa');
        await expect(area.getByTestId('environment-stage').getByTestId('environment-state-health')).toHaveText('Up');
      } },
      { name: 'runbooks', production: 'project-environment-production', open: () => page.getByTestId('deploy-tab-runbooks').click(), check: async () => {
        const production = area.getByTestId('project-environment-production');
        await expectProductionDown(production, 'environment-state-health');
        await expect(production.getByTestId('environment-state-release')).toHaveText('Release 2026.10.18.1 deployed 13 h ago');
        await expect(production.getByTestId('environment-runbooks')).toContainText('restart');
        await expect(production).not.toContainText('registry.example/website@sha256');
      } },
      { name: 'operations', production: 'environment-health-production', open: () => page.getByTestId('deploy-tab-operations').click(), check: async () => {
        await expect(area.getByTestId('environment-health-production').getByTestId('environment-health-status')).toHaveText('Down');
      } },
    ];

    for (const [width, height] of [[1280, 900], [390, 844]] as const) {
      await page.setViewportSize({ width, height });
      for (const theme of width === 1280 ? (['light', 'dark'] as const) : (['light'] as const)) {
        await setTheme(page, theme);
        for (const surface of surfaces) {
          await surface.open();
          await surface.check();
          if (width === 390) expect((await area.boundingBox())?.width).toBeLessThanOrEqual(390);
          await area.getByTestId(surface.production).scrollIntoViewIfNeeded();
          await capture(page, `m2-production-down-${surface.name}-${width}-${theme}`);
        }
      }
    }
  });

  test('F17: after approval and the host report, the Production card follows without a reload', async ({ page }) => {
    let environments = deployEnvironmentsFixture();
    let status = deployStatusFixture();
    let deployments = deployDeploymentsFixture();
    const confirms: string[] = [];
    page.on('dialog', dialog => confirms.push(dialog.message()));
    const mocks = fullMocks({
      act: (_action, id) => {
        // The server queues the run and closes the AGT-3176 promotion of its artifact.
        deployments = deployments.map(item => item.id === id ? { ...item, state: 'queued', approvedBy: 'local.owner' } : item);
        status = status.map(item => item.currentDeployment?.id === id ? { ...item, currentDeployment: deployments.find(run => run.id === id)! } : item);
        environments = { ...environments, promotions: environments.promotions.map(item => item.status === 'pending' ? { ...item, status: 'approved' } : item) };
        return deployments.find(item => item.id === id);
      },
    });
    for (const [key, read] of [['environments', () => environments], ['status', () => status], ['deployments', () => deployments]] as const)
      Object.defineProperty(mocks, key, { get: read });
    await openDeploy(page, mocks);

    const card = page.getByTestId('deploy-environment-production');
    await expect(card.getByTestId('deploy-environment-approval')).toBeVisible();
    await card.getByRole('button', { name: 'Review the pending approval for Production' }).click();
    await page.getByTestId('deploy-run-approve').click();
    await expect(page.getByTestId('deploy-run-outcome')).toHaveText('Queued');
    expect(confirms).toEqual(['Approve deploying release 2026.10.19.2 (1 card) to Production?']);
    await expect(card.getByTestId('deploy-environment-approval')).toHaveCount(0, { timeout: 5_000 });

    // The host reports healthy; nobody reloads the page.
    const [stage] = environments.environments;
    deployments = deployments.map(item => item.id === 'run-c-production'
      ? { ...item, state: 'succeeded', startedAtUtc: '2026-10-19T11:56:00Z', finishedAtUtc: '2026-10-19T11:58:00Z' } : item);
    const production = deployments.find(item => item.id === 'run-c-production')!;
    status = status.map(item => item.environment.id === 'production'
      ? { ...item, currentRelease: deployReleasesFixture()[0], currentDeployment: production, newerRelease: null, cardsSinceLastDeploy: [] } : item);
    environments = { ...environments, promotions: environments.promotions.map(item => item.status === 'approved' ? { ...item, status: 'deployed' } : item),
      environments: environments.environments.map(item => item.declaration.id === 'production' ? { ...item, deployedArtifact: stage.deployedArtifact,
        history: [{ at: '2026-10-19T11:58:00Z', status: 'healthy', artifact: stage.deployedArtifact!, previousArtifact: ARTIFACT_A, evidence: 'health-ok' }, ...item.history] } : item) };
    await expect(card.getByTestId('deploy-environment-release')).toContainText('2026.10.19.2', { timeout: 5_000 });
    await expect(page.getByTestId('deploy-run-outcome')).toHaveText('Succeeded', { timeout: 5_000 });
    await capture(page, 'deploy-production-live-after-approve-1280-light');
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
        await expect(page.getByRole('list', { name: 'Releases, newest first' }).getByRole('button').first()).toContainText('default');
        await expect(page.getByTestId('deploy-release-notes')).toContainText('Faster project search');
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

  test.describe('rollback (F13)', () => {
    const rollbackMocks = (incidentOnly: boolean, posts: unknown[], legacy: string[], extra: Partial<DeployMocks> = {}) => {
      const scenario = productionRollbackFixture(incidentOnly);
      const target = incidentOnly ? RELEASE_2 : RELEASE_1;
      const queued = { ...scenario.deployments[0], id: 'run-production-rollback', releaseId: target.id, trigger: 'rollback',
        state: 'queued', startedAtUtc: null, finishedAtUtc: null, approvedBy: 'local.owner', createdAtUtc: '2026-10-19T12:00:00Z',
        steps: [{ name: 'approval', state: 'succeeded', startedAtUtc: '2026-10-19T12:00:00Z', finishedAtUtc: '2026-10-19T12:00:00Z', logRef: '' },
          { name: 'publish-static', state: 'pending', startedAtUtc: null, finishedAtUtc: null, logRef: '' }] };
      const deployments = [...scenario.deployments];
      return fullMocks({
        environments: scenario.environments, status: scenario.statuses, deployments,
        rollback: (_id, body) => { posts.push(body); deployments.unshift(queued); return { status: 202, body: queued }; },
        legacyRollback: id => legacy.push(id),
        ...extra,
      });
    };

    test('Production card names target, age and runtime health; the rollback asks first and uses the lifecycle route', async ({ page }) => {
      const posts: unknown[] = [];
      const legacy: string[] = [];
      const nativeDialogs: string[] = [];
      page.on('dialog', dialog => nativeDialogs.push(dialog.message()));
      await openDeploy(page, rollbackMocks(false, posts, legacy));

      const card = page.getByTestId('deploy-environment-production');
      const offer = card.getByTestId('rollback-offer-production');
      await expect(offer.getByTestId('rollback-offer-target')).toHaveText(`Release ${RELEASE_1.version}`);
      await expect(offer.getByTestId('rollback-offer-age')).toHaveText('1 d ago');
      await expect(offer.getByTestId('rollback-offer-health')).toHaveText('Ran without incident');
      await expect(offer.getByTestId('rollback-offer-skipped')).toHaveText(`Skips Release ${RELEASE_2.version}: incident while running.`);
      await expect(offer.getByTestId('rollback-offer-health')).toHaveAttribute('data-tone', 'success');

      for (const [width, height] of [[1280, 800], [390, 844]] as const) {
        await page.setViewportSize({ width, height });
        for (const theme of ['light', 'dark'] as const) {
          await setTheme(page, theme);
          if (width === 390) expect((await card.boundingBox())?.width).toBeLessThanOrEqual(390);
          await capture(page, `deploy-rollback-card-${width}-${theme}`);
          await offer.getByTestId('rollback-offer-button').click();
          const dialog = page.locator('[role="alertdialog"]:visible');
          await expect(dialog).toContainText(`Roll back Production to Release ${RELEASE_1.version}?`);
          await expect(dialog.getByTestId('confirm-dialog-items')).toContainText(`Current: Release ${RELEASE_3.version}`);
          await expect(dialog.getByTestId('confirm-dialog-items')).toContainText(`Target: Release ${RELEASE_1.version}, created 1 d ago`);
          await expect(dialog.getByTestId('confirm-dialog-items')).toContainText('Runtime health of the target: ran without incident');
          fs.mkdirSync(results, { recursive: true });
          await page.screenshot({ path: path.join(results, `deploy-rollback-confirm-${width}-${theme}--mocked.png`) });
          await dialog.getByTestId('confirm-dialog-cancel').click();
          await expect(dialog).toBeHidden();
        }
      }
      expect(posts).toEqual([]);

      await page.setViewportSize({ width: 1280, height: 800 });
      await offer.getByTestId('rollback-offer-button').click();
      await page.locator('[role="alertdialog"]:visible').getByTestId('confirm-dialog-confirm').click();
      await expect.poll(() => posts).toEqual([{ approve: true, acceptIncidentTarget: false }]);
      const drawer = page.getByTestId('deploy-run-drawer');
      await expect(drawer).toContainText('Rollback to Production');
      await expect(drawer).toContainText(`Release ${RELEASE_1.version}`);
      expect(legacy).toEqual([]);
      expect(nativeDialogs).toEqual([]);
    });

    test('run drawer and deployment tools offer the same confirmed rollback', async ({ page }) => {
      const posts: unknown[] = [];
      const legacy: string[] = [];
      await openDeploy(page, rollbackMocks(false, posts, legacy));
      await page.getByTestId('deploy-environment-production').getByTestId('deploy-environment-open-run').click();
      const drawer = page.getByTestId('deploy-run-drawer');
      await expect(drawer.getByTestId('deploy-run-rollback')).toContainText(`Release ${RELEASE_1.version}`);
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await capture(page, `deploy-rollback-drawer-1280-${theme}`);
      }
      await page.setViewportSize({ width: 390, height: 844 });
      await capture(page, 'deploy-rollback-drawer-390-dark');
      await page.setViewportSize({ width: 1280, height: 800 });
      await drawer.getByTestId('sidesheet-close').click();

      await page.getByTestId('deploy-open-tools').click();
      const tools = page.getByTestId('environment-production');
      await expect(tools.getByTestId('rollback-offer-target')).toHaveText(`Release ${RELEASE_1.version}`);
      await expect(page.getByTestId('environment-stage').getByRole('button', { name: 'Roll back' })).toHaveCount(0);
      await tools.getByTestId('rollback-offer-button').click();
      await expect(page.locator('[role="alertdialog"]:visible')).toContainText(`Roll back Production to Release ${RELEASE_1.version}?`);
      await page.locator('[role="alertdialog"]:visible').getByTestId('confirm-dialog-confirm').click();
      await expect.poll(() => posts).toEqual([{ approve: true, acceptIncidentTarget: false }]);
      expect(legacy).toEqual([]);
    });

    test('an open incident offers the rollback; a target that had an incident must be typed out', async ({ page }) => {
      const posts: unknown[] = [];
      await openDeploy(page, rollbackMocks(true, posts, [], { health: { ...healthResponse, capturedAt: '2026-10-19T12:00:00Z' } }));
      await page.route(`**${DEPLOY}/operations`, route => answer(route, {
        project: PROJECT, since: '2026-09-20T00:00:00Z', until: '2026-10-20T00:00:00Z', environments: [],
        dora: { successfulProductionDeploys: 0, deploysPerWeek: 0, medianCardToProductionHours: null,
          cardsMeasured: 0, changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
      }));
      await page.getByTestId('deploy-tab-operations').click();
      const incident = page.getByTestId('environment-health-production');
      await expect(incident.getByTestId('environment-health-incident')).toBeVisible();
      const offer = incident.getByTestId('rollback-offer-production');
      await expect(offer.getByTestId('rollback-offer-target')).toHaveText(`Release ${RELEASE_2.version}`);
      await expect(offer.getByTestId('rollback-offer-health')).toHaveText('Had an incident while running');
      await expect(offer.getByTestId('rollback-offer-health')).toHaveAttribute('data-tone', 'warning');
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await incident.screenshot({ path: path.join(results, `deploy-rollback-incident-1280-${theme}--mocked.png`) });
      }
      await page.setViewportSize({ width: 390, height: 844 });
      await incident.screenshot({ path: path.join(results, 'deploy-rollback-incident-390-dark--mocked.png') });
      await page.setViewportSize({ width: 1280, height: 800 });

      await offer.getByTestId('rollback-offer-button').click();
      const dialog = page.locator('[role="alertdialog"]:visible');
      await expect(dialog.getByTestId('confirm-dialog-typed-prompt')).toContainText(`Release ${RELEASE_2.version} had an incident while running`);
      await expect(dialog.getByTestId('confirm-dialog-confirm')).toBeDisabled();
      await page.screenshot({ path: path.join(results, 'deploy-rollback-incident-confirm-1280-dark--mocked.png') });
      await dialog.getByTestId('confirm-dialog-typed-input').fill(RELEASE_2.version);
      await dialog.getByTestId('confirm-dialog-confirm').click();
      await expect.poll(() => posts).toEqual([{ approve: true, acceptIncidentTarget: true }]);
    });
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

  test('shows project.yml environments without deploy steps and offers the deploy-step form (F5)', async ({ page }) => {
    const environment = (name: string, order: number, isProtected: boolean) => ({
      path: `environments[${order}]`, name, order, protected: isProtected, automatic: !isProtected,
      approval: isProtected ? ['Owner', 'Maintainer'] : [], requires: isProtected ? 'staging' : null, steps: [], variables: [],
    });
    await openDeploy(page, {
      environments: { project: PROJECT, environments: [], promotions: [], failureCards: [] },
      status: [], releases: [], deployments: [],
      pipelineDefinition: {
        project: PROJECT, path: '.agent-studio/project.yml', exists: true, schemaVersion: 2, valid: true, issues: [],
        definitionSha256: 'a'.repeat(64), migrationAvailable: false, deployStepsAvailable: true,
        diagram: {
          schemaVersion: 2, gate: { source: 'commands', build: ['npm run build'], test: ['npm test'], testSuites: [] },
          artifacts: [], environmentsDefaulted: false, lifecycleDefaulted: false,
          environments: [environment('staging', 0, false), environment('production', 1, true)],
        },
      },
    });

    await expect(page.getByTestId('deploy-environments-empty')).toHaveCount(0);
    await expect(page.getByTestId('deploy-environment-declared-staging')).toContainText('No deploy step yet');
    await expect(page.getByTestId('deploy-environment-declared-production')).toContainText('protected');
    await expect(page.getByTestId('deploy-environments')).not.toContainText('docs/deployments/environments');
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await capture(page, `deploy-overview-declared-no-steps-1440-${theme}`);
    }
    await page.getByTestId('deploy-add-deploy-step').click();
    const form = page.getByTestId('deploy-steps-form');
    await expect(form.getByTestId('deploy-steps-env-staging')).toBeVisible();
    await expect(form.getByTestId('deploy-steps-env-production')).toBeVisible();
    await form.getByTestId('deploy-steps-type-container').check();
    await expect(form.getByTestId('deploy-steps-env-staging').getByTestId('deploy-steps-compose')).toBeVisible();
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await capture(page, `deploy-overview-deploy-step-form-1440-${theme}`);
    }
  });
});

test('Project Secrets shows approval for a custom-id Production environment and keeps values write-only', async ({ page }) => {
  const environments = deployEnvironmentsFixture();
  environments.environments[1].declaration.id = 'live-eu';
  await openDeploy(page, fullMocks({ environments }));
  await page.route('**/api/secrets', route => route.fulfill({ json: {
    store: 'builtin', keySource: 'key-file', canWriteWorkspace: true, secrets: [], unreadable: [],
  } }));
  await page.route(/\/api\/projects\/[^/]+\/secret-bindings\/[^/]+$/, async route => {
    const url = new URL(route.request().url());
    const environment = url.pathname.split('/')[5];
    const reference = `secret://${PROJECT}/live-eu/DATABASE_URL`;
    await route.fulfill({ json: {
      project: PROJECT, environment, ready: environment !== 'live-eu', bindings: [],
      missing: environment === 'live-eu'
        ? [{ name: 'DATABASE_URL', reference, scope: `${PROJECT}/live-eu`, reason: 'not-bound' }] : [],
    } });
  });
  await page.getByTestId('deploy-tab-secrets').click();
  const secrets = page.getByTestId('project-secrets-page');
  await expect(secrets).toBeVisible();
  await secrets.getByTestId('project-secrets-environment').selectOption('live-eu');
  await expect(secrets.getByTestId('project-secrets-missing')).toContainText('not-bound');
  await expect(secrets.getByTestId('project-secrets-bind-DATABASE_URL')).toHaveText('Bind and approve DATABASE_URL');
  await secrets.getByTestId('project-secrets-edit-DATABASE_URL').click();
  await secrets.getByTestId('project-secrets-approve').check();
  await expect(secrets.getByTestId('project-secrets-reference')).toHaveValue(`secret://${PROJECT}/live-eu/DATABASE_URL`);
  await expect(secrets.getByTestId('project-secrets-bind')).toBeEnabled();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await capture(page, `deploy-secrets-${theme}`);
  }
});
