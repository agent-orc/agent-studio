import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, test, type Locator, type Page } from '@playwright/test';

/**
 * F3 of the playthrough: the local profile opens Studio without a session, yet
 * production approval, runner enrollment, and Access need a signed-in Owner.
 * On the real isolated backend this spec opens Access and approves a waiting
 * Production run only through the UI; the owner account is created in place.
 *
 * Setup outside the UI stays on the deploy side: a disposable repository that
 * declares stage and production, one release with a succeeded stage run in the
 * deploy store (no deploy host claims jobs on this stack), and the Production
 * request, which any local caller may make.
 */

const WORKSPACE = process.env.PW_E2E_WORKSPACE?.trim() ?? '';
const RESULTS = process.env.JOB_RESULTS_DIR?.trim() ?? '';
const CLIENT = { 'X-Client-Id': 'local-default' };
const OWNER = 'local-owner';
const RELEASE = 'rel-owner-approval';
const ARTIFACT = `registry.example.invalid/owner-approval@sha256:${'a'.repeat(64)}`;

function environment(id: 'stage' | 'production', port: number) {
  return {
    id, name: id === 'stage' ? 'Stage' : 'Production', kind: id, targetHostId: `${id}-host`,
    address: `https://${id}.example.invalid`, template: 'container', destination: String(port),
    healthUrl: `https://${id}.example.invalid/health`, approval: 'person', secretRefs: [] as string[],
  };
}

function seedRepository(repository: string): string {
  const declarations = join(repository, 'docs', 'deployments', 'environments');
  mkdirSync(declarations, { recursive: true });
  writeFileSync(join(declarations, 'stage.json'), JSON.stringify(environment('stage', 8081)));
  writeFileSync(join(declarations, 'production.json'), JSON.stringify(environment('production', 8082)));
  for (const args of [['init', '-q', '-b', 'develop'], ['config', 'user.name', 'Approval Fixture'],
    ['config', 'user.email', 'approval-fixture@example.invalid'], ['add', '.'], ['commit', '-qm', 'deploy: declare environments']]) {
    const result = spawnSync('git', args, { cwd: repository, encoding: 'utf8' });
    expect(result.status, `git ${args[0]}: ${result.stderr}`).toBe(0);
  }
  return spawnSync('git', ['rev-parse', 'HEAD'], { cwd: repository, encoding: 'utf8' }).stdout.trim();
}

/** The deploy store the backend re-reads on every request (DeployStore). */
function seedRelease(project: string, commitSha: string): void {
  const root = join(WORKSPACE, 'deployments', 'projects', encodeURIComponent(project.toLowerCase()));
  mkdirSync(join(root, 'releases'), { recursive: true });
  const created = new Date(Date.now() - 30 * 60_000).toISOString();
  const declarations = [environment('stage', 8081), environment('production', 8082)]
    .map(item => ({ ...item, containerPort: Number(item.destination) }));
  writeFileSync(join(root, 'releases', `${RELEASE}.json`), JSON.stringify({
    release: {
      id: RELEASE, projectId: project, version: '2026.11.17.1', commitSha, artifactDigests: [ARTIFACT],
      processVersion: 'owner-approval', variablesVersion: 'owner-approval', cardKeys: [], createdAtUtc: created, channel: 'default',
    },
    environments: declarations, lifecycle: [], tenants: [], variables: [],
  }));
  const finished = new Date(Date.now() - 20 * 60_000).toISOString();
  writeFileSync(join(root, 'deployments.json'), JSON.stringify([{
    id: 'run-owner-approval-stage', releaseId: RELEASE, environmentId: 'stage', state: 'succeeded', trigger: 'deploy',
    deployStep: 'start-container', needsApproval: false, requestedBy: 'local-operator',
    createdAtUtc: created, startedAtUtc: created, finishedAtUtc: finished,
  }]));
}

/** One password per workspace, so a later spec on the same stack can sign in again. */
function ownerPassword(): string {
  const file = join(WORKSPACE, 'local-owner-password');
  if (!existsSync(file)) writeFileSync(file, randomBytes(18).toString('base64url') + 'Aa1!', { mode: 0o600 });
  return readFileSync(file, 'utf8');
}

/** Fills whichever form the local owner control offers: Create owner on a fresh stack, Sign in after. */
async function signInInPlace(scope: Locator): Promise<void> {
  await scope.getByTestId('local-owner-username').fill(OWNER);
  const displayName = scope.getByTestId('local-owner-display-name');
  if (await displayName.isVisible()) await displayName.fill('Local Owner');
  await scope.getByTestId('local-owner-password').fill(ownerPassword());
  await scope.getByTestId('local-owner-submit').click();
}

async function capture(page: Page, name: string): Promise<void> {
  if (!RESULTS) return;
  mkdirSync(join(RESULTS, 'local-owner'), { recursive: true });
  await page.screenshot({ path: join(RESULTS, 'local-owner', `${name}-1280-light.png`), animations: 'disabled', caret: 'hide' });
}

test.describe('local profile owner actions', () => {
  test.skip(!WORKSPACE, 'Needs the isolated stack of npm run e2e.');

  test('creates the owner in place and approves a Production run through the UI', async ({ page }) => {
    test.setTimeout(180_000);
    await page.setViewportSize({ width: 1280, height: 900 });
    const status = await (await page.request.get('/api/auth/status')).json() as { profile: string; authenticated: boolean };
    expect(status).toMatchObject({ profile: 'local', authenticated: false });

    const project = `owner-approval-${randomBytes(3).toString('hex')}`;
    const repository = join(WORKSPACE, project);
    mkdirSync(repository, { recursive: true });
    const commitSha = seedRepository(repository);
    const projects = await (await page.request.get('/api/projects')).json() as { workspaceId: string }[];
    const registered = await page.request.post('/api/projects', { headers: CLIENT, data: {
      workspaceId: projects[0]?.workspaceId ?? 'ws-default', displayName: project, repositoryPath: repository,
    } });
    expect(registered.ok(), `${registered.status()}: ${await registered.text()}`).toBeTruthy();
    const { id } = await registered.json() as { id: string };
    seedRelease(project, commitSha);
    const deploy = `/api/projects/${encodeURIComponent(project)}/deploy`;
    await expect.poll(async () => {
      const response = await page.request.post(`${deploy}/releases/${RELEASE}/deployments`, {
        headers: CLIENT, data: { environmentId: 'production' } });
      return response.status();
    }, { timeout: 30_000 }).toBe(202);

    try {
      // Access names the next step as a control instead of a dead end.
      await page.goto('/#/workspace/settings/access');
      await expect(page.getByTestId('access-error')).toContainText('need a signed-in Owner');
      await expect(page.getByTestId('access-owner-sign-in').getByTestId('local-owner-open')).toBeVisible();
      await capture(page, 'access-without-owner');

      // The checklist names what the owner account is for and links to creating it.
      await page.goto('/#/workspace/settings/first-run');
      const ownerStep = page.getByTestId('first-run-owner');
      await expect(ownerStep.getByTestId('first-run-owner-local')).toContainText('production approval and run-host enrollment need the owner account');
      await expect(ownerStep.getByTestId('local-owner-open')).toBeVisible();
      await capture(page, 'first-run-owner-step');

      await page.goto(`/#/projects/${encodeURIComponent(id)}/deploy`);
      await expect(page.getByTestId('project-deploy-area')).toBeVisible({ timeout: 30_000 });
      await page.getByTestId('deploy-environment-review-approval').click();
      const drawer = page.getByTestId('deploy-run-drawer');
      await expect(drawer).toContainText('Production');
      await expect(page.getByTestId('deploy-run-outcome')).toContainText('Awaiting approval');

      const confirms: string[] = [];
      page.on('dialog', dialog => { confirms.push(dialog.message()); void dialog.accept(); });
      await page.getByTestId('deploy-run-approve').click();
      const ownerDialog = page.getByTestId('local-owner-dialog');
      await expect(ownerDialog.getByTestId('local-owner-purpose')).toHaveText('Production approval needs the owner account.');
      expect(confirms).toEqual([]);
      await capture(page, 'approve-asks-for-owner');
      await signInInPlace(ownerDialog);

      await expect(ownerDialog).toHaveCount(0);
      await expect(page.getByTestId('deploy-run-approval')).toContainText('Approved by', { timeout: 30_000 });
      expect(confirms).toEqual([expect.stringMatching(/^Approve Production run /)]);
      await capture(page, 'production-approved');
      const runs = await (await page.request.get(`${deploy}/deployments`)).json() as { environmentId: string; approvedBy: string | null }[]
        | { items: { environmentId: string; approvedBy: string | null }[] };
      const production = (Array.isArray(runs) ? runs : runs.items).find(run => run.environmentId === 'production');
      expect(production?.approvedBy).toBeTruthy();

      // The same session now opens Access, where runner enrollments live.
      await page.goto('/#/workspace/settings/access');
      await expect(page.getByTestId('access-summary')).toBeVisible();
      await expect(page.getByTestId('access-error')).toHaveCount(0);
      await capture(page, 'access-signed-in');
    } finally {
      await page.request.put(`/api/projects/${encodeURIComponent(id)}`, { headers: CLIENT, data: { archived: true } });
    }
  });
});
