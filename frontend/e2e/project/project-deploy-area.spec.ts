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
  act?: (action: string, deploymentId: string, body?: unknown) => Answer;
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
  await page.route(`**/api/projects/${PROJECT}/environments/*/volumes`, route => route.fulfill(json({
    database: null, volumes: [], schedules: [], audit: [], adapterReady: false,
  })));
  await page.route(`**/api/projects/${PROJECT}/environments/health`, route => answer(route, deploy.health ?? {
    capturedAt: new Date(DEPLOY_FIXTURE_NOW).toISOString(), probeOrigin: 'mock', environments: [],
  }));
  await page.route(`**${DEPLOY}/status`, route => answer(route, deploy.status));
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
      ? deploy.act(action, id, route.request().postDataJSON()) : MISSING);
  });
  await page.route(`**/api/projects/${PROJECT}/pipeline-definition`, route => answer(route, deploy.pipelineDefinition));
}

async function openDeploy(page: Page, deploy: DeployMocks, navigationTimeoutMs = 30_000): Promise<void> {
  page.on('dialog', dialog => dialog.accept());
  await page.clock.setFixedTime(new Date(DEPLOY_FIXTURE_NOW));
  await page.addInitScript(({ project }) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1,
    tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName: project, section: 'deploy' }],
    activeKey: `hub:${project}`,
  })), { project: PROJECT });
  await mockApp(page, deploy);
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

  test('Operations shows installation backups with the stale warning and the off-host copy in both themes (AGT-3574)', async ({ page }) => {
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
      ] : [],
      backups: [newest, backup('studio-backup-20261019T000000Z-9f8e7d', '2026-10-19T00:00:00+00:00', 'unverified', 'uploaded')],
      generatedAt: '2026-10-20T08:00:00+00:00',
    }));
    await page.getByTestId('deploy-tab-operations').click();
    const backups = page.getByTestId('operations-backups');
    await expect(backups.getByTestId('operations-backups-state')).toHaveAttribute('data-state', 'failing');
    await expect(backups.getByTestId('operations-backups-warnings').locator('[data-code="backup-stale"]')).toContainText('26 hours old');
    await expect(backups.getByTestId('operations-backups-age')).toHaveAttribute('data-stale', 'true');
    await expect(backups.getByTestId('operations-backups-age')).toContainText('26 h');
    await expect(backups.getByTestId('operations-backups-size')).toContainText('412.0 MB');
    await expect(backups.getByTestId('operations-backups-verification')).toContainText('Verified');
    await expect(backups.getByTestId('operations-backups-offhost')).toContainText('Copy failed');
    await expect(backups.getByTestId('operations-backups-offhost')).toContainText('sftp://studio@backup.example');
    await expect(backups.getByTestId('operations-backups-list').locator('tbody tr')).toHaveCount(2);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      fs.mkdirSync(results, { recursive: true });
      await backups.screenshot({ path: path.join(results, `operations-backups-stale-${theme}--mocked.png`) });
    }

    stale = false;
    await backups.getByTestId('operations-backups-refresh').click();
    await expect(backups.getByTestId('operations-backups-state')).toHaveAttribute('data-state', 'ok');
    await expect(backups.getByTestId('operations-backups-warnings')).toHaveCount(0);
    await expect(backups.getByTestId('operations-backups-offhost')).toContainText('Copied');
    await backups.screenshot({ path: path.join(results, 'operations-backups-current-dark--mocked.png') });
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
    }));
    await page.getByTestId('deploy-tab-operations').click();
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
    fs.mkdirSync(results, { recursive: true });
    // Tall viewports, so the screenshots hold the whole panel instead of the scrolled part.
    await page.setViewportSize({ width: 1280, height: 1800 });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await backups.screenshot({ path: path.join(results, `operations-backups-restored-${theme}--mocked.png`) });
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
    await expect(operations).toContainText('Telemetry is not available yet');
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
  await secrets.getByRole('button', { name: 'Bind DATABASE_URL' }).click();
  await secrets.getByTestId('project-secrets-approve').check();
  await expect(secrets.getByTestId('project-secrets-reference')).toHaveValue(`secret://${PROJECT}/live-eu/DATABASE_URL`);
  await expect(secrets.getByTestId('project-secrets-bind')).toBeEnabled();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await capture(page, `deploy-secrets-${theme}`);
  }
});
