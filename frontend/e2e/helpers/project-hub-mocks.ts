import { expect, type Page, type Route } from '@playwright/test';
import {
  DEPLOY_FIXTURE_NOW,
  deployDeploymentsFixture,
  deployEnvironmentsFixture,
  deployReleasesFixture,
  deployStatusFixture,
  deploymentLogFixture,
  pipelineDefinitionFixture,
} from '../../src/app/features/project-detail/testing/deploy-fixtures';

/** Fully mocked project hub backend, shared by specs that open a project hub section without a live API. */
export const PROJECT = 'Website';
const STORAGE = '/srv/tasks/website';
const DEPLOY = `/api/projects/${PROJECT}/deploy`;

const MISSING = Symbol('missing route');
type Answer = unknown | typeof MISSING | { status: number; body: unknown };
export interface DeployMocks {
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

export async function mockApp(page: Page, deploy: DeployMocks, missingCheckout = false): Promise<void> {
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
  await page.route(`**/api/projects/${PROJECT}/deploy/operations`, route => route.fulfill(json({
    project: PROJECT, since: '2026-10-10T00:00:00Z', until: '2026-11-24T00:00:00Z', environments: [],
    dora: { successfulProductionDeploys: 0, deploysPerWeek: null, medianCardToProductionHours: null, cardsMeasured: 0,
      changeFailureRate: null, medianRecoveryHours: null, recoveriesMeasured: 0 },
  })));
  await page.route(new RegExp(`/api/projects/${PROJECT}/environments/[^/]+/rollback$`), route => {
    deploy.legacyRollback?.(route.request().url().split('/').at(-2)!);
    return answer(route, { status: 202, body: {} });
  });
}

export async function openHub(page: Page, deploy: DeployMocks, section: string, readyTestId: string): Promise<void> {
  page.on('dialog', dialog => dialog.accept());
  await page.clock.setFixedTime(new Date(DEPLOY_FIXTURE_NOW));
  await page.addInitScript(({ project, section }) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1,
    tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName: project, section }],
    activeKey: `hub:${project}`,
  })), { project: PROJECT, section });
  await mockApp(page, deploy);
  await page.goto('/', { waitUntil: 'domcontentloaded', timeout: 30_000 });
  await page.evaluate(() => document.querySelectorAll('[data-testid="error-dialog-overlay"], vite-error-overlay')
    .forEach(element => ((element as HTMLElement).style.display = 'none')));
  await expect(page.getByTestId(readyTestId)).toBeVisible({ timeout: 60_000 });
}


export const fullMocks = (extra: Partial<DeployMocks> = {}): DeployMocks => ({
  environments: deployEnvironmentsFixture(), status: deployStatusFixture(), releases: deployReleasesFixture(),
  deployments: deployDeploymentsFixture(), pipelineDefinition: pipelineDefinitionFixture(), ...extra,
});
