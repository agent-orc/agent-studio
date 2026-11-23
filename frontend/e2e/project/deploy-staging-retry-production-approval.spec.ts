import { expect, test, type Page } from '@playwright/test';
import { createHash, randomBytes } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { setTheme } from '../helpers/theme';

/**
 * F7 on the real backend (deploy-lifecycle profile): the automatic Staging run
 * of a release fails, a person deploys the release to Staging again from the
 * environment card, and the Production approval request that follows is
 * approved from the UI. Only the gate release and the deploy host are played
 * over HTTP; every person's step goes through the Deploy area.
 */

const PROJECT = 'Deploy Fixture';
const workspace = process.env.PW_E2E_WORKSPACE!;
const backend = process.env.PW_BACKEND_URL!;
const repo = path.join(workspace, 'projects', 'deploy-fixture');
const results = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'test-results', 'deploy-lifecycle');
const artifact = `registry.example/deploy-fixture@sha256:${createHash('sha256').update('deploy-fixture').digest('hex')}`;

interface HostJob { id: string; leaseToken: string; artifact: string }

/** The gate's release evidence for the commit, as the promotion train writes it. */
function writeReleaseEvidence(commit: string): void {
  const directory = path.join(workspace, 'release-evidence', commit);
  fs.mkdirSync(directory, { recursive: true });
  const archive = Buffer.from(`gate-built-release-for-${commit}`);
  const digest = createHash('sha256').update(archive).digest('hex');
  fs.writeFileSync(path.join(directory, 'release.tar.gz'), archive);
  fs.writeFileSync(path.join(directory, 'release.sha256'), `${digest}\n`);
  fs.writeFileSync(path.join(directory, 'full-gate.log'), 'PROMOTION_FULL_GATE=passed\n');
  fs.writeFileSync(path.join(directory, 'promotion-record.json'), JSON.stringify({
    status: 'promoted', gate: 'passed', gateRunId: 'release/deploy-e2e', candidateSha: commit, atomicPush: true,
    releasePackage: { status: 'written', sha256: digest },
  }));
  fs.writeFileSync(path.join(directory, 'deploy-artifact.json'), JSON.stringify({
    project: PROJECT, commit, artifact, sourceSha256: digest,
  }));
}

/** Bootstraps the Owner on the fresh stack; a Playwright retry signs in with the same password. */
async function signInOwner(page: Page): Promise<void> {
  const passwordFile = path.join(workspace, 'deploy-owner-password');
  if (!fs.existsSync(passwordFile))
    fs.writeFileSync(passwordFile, randomBytes(24).toString('base64url') + 'Aa1!', { mode: 0o600 });
  const password = fs.readFileSync(passwordFile, 'utf8');
  const status = await (await page.request.get('/api/auth/status')).json() as { bootstrapRequired: boolean };
  const signedIn = await page.request.post(status.bootstrapRequired ? '/api/auth/bootstrap' : '/api/auth/login', {
    headers: { 'X-Client-Id': 'local-default' },
    data: { username: 'deploy-owner', password, ...(status.bootstrapRequired ? { displayName: 'Deploy Owner' } : {}) },
  });
  expect(signedIn.ok(), `${signedIn.status()}: ${await signedIn.text()}`).toBe(true);
}

/** A runner credential with the release scope, the identity the gate posts releases with. */
async function releaseCredential(page: Page): Promise<string> {
  const enrollment = await page.request.post('/api/auth/runner-enrollments', {
    headers: { 'X-Client-Id': 'local-default' }, data: { name: 'deploy-e2e-gate', scopes: ['runner.release'] },
  });
  expect(enrollment.ok(), `${enrollment.status()}: ${await enrollment.text()}`).toBe(true);
  const { enrollmentCode } = await enrollment.json() as { enrollmentCode: string };
  const issued = await page.request.post('/api/auth/runner-enroll', {
    headers: { 'X-Client-Id': 'local-default' }, data: { code: enrollmentCode },
  });
  expect(issued.ok(), `${issued.status()}: ${await issued.text()}`).toBe(true);
  return (await issued.json() as { secret: string }).secret;
}

async function post(url: string, body: unknown, token?: string): Promise<Response> {
  return fetch(`${backend}${url}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Client-Id': 'local-default',
      ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: JSON.stringify(body),
  });
}

/** The deploy host claims its next job and reports the result. */
async function hostRuns(hostId: string, healthy: boolean): Promise<void> {
  let job: HostJob | null = null;
  await expect.poll(async () => {
    const claimed = await post('/api/deploy/jobs/claim', { hostId });
    job = claimed.ok ? await claimed.json() as HostJob | null : null;
    return job?.id ?? null;
  }, { timeout: 30_000, message: `a job for ${hostId}` }).not.toBeNull();
  const reported = await post(`/api/deploy/jobs/${encodeURIComponent(PROJECT)}/${job!.id}/report`, {
    leaseToken: job!.leaseToken, healthy, evidence: healthy ? 'health-ok' : 'deploy-step-failed',
  });
  expect(reported.status, `report for ${hostId}`).toBe(200);
}

async function openDeploy(page: Page): Promise<void> {
  await page.addInitScript(({ project }) => localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
    v: 1,
    tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName: project, section: 'deploy' }],
    activeKey: `hub:${project}`,
  })), { project: PROJECT });
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await expect(page.getByTestId('project-deploy-area')).toBeVisible({ timeout: 60_000 });
}

async function capture(page: Page, name: string, theme: 'light' | 'dark' = 'light'): Promise<void> {
  fs.mkdirSync(results, { recursive: true });
  await page.getByTestId('project-deploy-area').screenshot({ path: path.join(results, `${name}-1280-${theme}.png`) });
}

// The release, its runs and the Owner live on one disposable stack; a retry cannot replay them.
test.describe.configure({ retries: 0 });

test('a person retries Staging from the card, then approves the Production request in the UI', async ({ page }) => {
  test.setTimeout(240_000);
  await page.setViewportSize({ width: 1280, height: 1000 });
  page.on('dialog', dialog => dialog.accept());
  await signInOwner(page);

  // The gate releases the fixture commit; the lifecycle queues Staging, and its host fails it.
  const commit = execFileSync('git', ['-C', repo, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  writeReleaseEvidence(commit);
  const released = await post('/api/deploy/gate-releases', { project: PROJECT, artifact, commit },
    await releaseCredential(page));
  expect(released.status, await released.clone().text()).toBe(202);
  const { version } = await released.json() as { version: string };
  await hostRuns('deploy-e2e-stage', false);

  await openDeploy(page);
  await setTheme(page, 'light');
  const staging = page.getByTestId('deploy-environment-stage');
  const production = page.getByTestId('deploy-environment-production');
  await expect(staging.getByTestId('deploy-environment-last-run')).toContainText('Deploy failed');
  await expect(production.getByTestId('deploy-action-deploy-production')).toBeDisabled();
  await expect(production.getByTestId('deploy-action-deploy-production-reason'))
    .toContainText('requires a successful deployment of this release');
  await capture(page, 'f7-staging-failed-retry-offered');

  // The retry after the fix: Deploy to Staging on the card, no API call.
  await staging.getByTestId('deploy-action-deploy-stage').click();
  await expect(page.getByTestId('deploy-run-drawer')).toContainText('Queued');
  await page.getByTestId('deploy-run-drawer').getByRole('button', { name: /close/i }).first().click();
  await hostRuns('deploy-e2e-stage', true);

  // The person-started Staging success asks for the same Production approval as the automatic run.
  await page.getByTestId('deploy-refresh').click();
  await expect(production.getByTestId('deploy-environment-approval')).toContainText(`Awaiting approval: Release ${version}`);
  await expect(production.getByTestId('deploy-action-deploy-production')).toHaveCount(0);
  await capture(page, 'f7-production-approval-requested');
  await production.getByTestId('deploy-environment-review-approval').click();
  await page.getByTestId('deploy-run-approve').click();
  await expect(page.getByTestId('deploy-run-approval')).toContainText('Approved by');
  await expect(page.getByTestId('deploy-run-outcome')).toContainText('Queued');
  await page.getByTestId('deploy-run-drawer').getByRole('button', { name: /close/i }).first().click();

  await hostRuns('deploy-e2e-production', true);
  await page.getByTestId('deploy-refresh').click();
  await expect(production.getByTestId('deploy-environment-release')).toContainText(`Release ${version}`);
  // Production runs the release now: the card offers its redeploy.
  await expect(production.getByTestId('deploy-action-redeploy-production')).toBeEnabled();
  await capture(page, 'f7-production-deployed-redeploy-offered');
  await setTheme(page, 'dark');
  await capture(page, 'f7-production-deployed-redeploy-offered', 'dark');
});
