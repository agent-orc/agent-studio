import { createHmac } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { basename, join } from 'node:path';
import { test, expect, request as playwrightRequest } from '@playwright/test';
import { setTheme } from '../helpers/theme';

/**
 * F4: the Owner enrolls a run host from Hosts > Add host. The code carries
 * only runner.deploy and the chosen project, the page prints the host commands
 * with the id to use, and the run host is listed online after its first deploy
 * claim, which is the only contact a run host makes. Runs on the `run-host`
 * profile: a fresh networked monolith over real TLS; nothing is mocked.
 */
const resultDir = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'run-host-enrollment');

test.describe.configure({ mode: 'serial', retries: 0 });

function base32(secret: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const char of secret.replace(/=+$/, '').toUpperCase()) bits += alphabet.indexOf(char).toString(2).padStart(5, '0');
  const bytes: number[] = [];
  for (let index = 0; index + 8 <= bits.length; index += 8) bytes.push(parseInt(bits.slice(index, index + 8), 2));
  return Buffer.from(bytes);
}

function totp(secret: string, at = Date.now()): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 30_000)));
  const digest = createHmac('sha1', base32(secret)).update(counter).digest();
  const offset = digest[digest.length - 1] & 0x0f;
  return String((digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, '0');
}

test('a run host enrolled from the Hosts wizard is online after its first claim', async ({ page }) => {
  test.setTimeout(180_000);
  const workspace = process.env.PW_E2E_WORKSPACE!;
  expect(workspace).toBeTruthy();
  mkdirSync(resultDir, { recursive: true });
  await page.setViewportSize({ width: 1280, height: 900 });

  // Owner with a second factor, through the browser's own cookie jar.
  const password = ['deploy', 'owner', 'pass', 'phrase', 'long'].join('-');
  const bootstrap = await page.request.post('/api/auth/bootstrap', { headers: { 'Sec-Fetch-Site': 'same-origin' },
    data: { username: 'run-host-owner', displayName: 'Owner', password, bootstrapCode: `${basename(workspace)}-owner-code` } });
  expect(bootstrap.ok(), `${bootstrap.status()}: ${await bootstrap.text()}`).toBeTruthy();
  const ownerHeaders = { 'X-CSRF-Token': (await bootstrap.json()).csrfToken as string, 'Sec-Fetch-Site': 'same-origin' };
  const factor = await page.request.post('/api/auth/totp/start', { headers: ownerHeaders });
  expect(factor.ok(), await factor.text()).toBeTruthy();
  const confirmed = await page.request.post('/api/auth/totp/confirm', { headers: ownerHeaders,
    data: { code: totp((await factor.json()).secret) } });
  expect(confirmed.ok(), await confirmed.text()).toBeTruthy();

  // Hosts > Add host > Run host (deploy), bound to one project.
  await page.goto('/#/workspace/settings/remote-hosts');
  await page.getByTestId('remote-hosts-add').click();
  const wizard = page.getByTestId('add-host-wizard');
  await wizard.getByTestId('add-host-kind-deploy').click();
  const enrollment = wizard.getByTestId('run-host-enrollment');
  await expect(enrollment).toBeVisible();
  await enrollment.getByTestId('run-host-name').fill('run-host-1');
  const project = enrollment.getByTestId('run-host-projects').getByRole('checkbox').first();
  await project.check();
  const boundProject = (await project.getAttribute('data-testid'))!.replace(/^run-host-project-/, '');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await wizard.screenshot({ path: join(resultDir, `wizard-run-host-1280-${theme}.png`) });
  }
  const created = page.waitForResponse(response => response.url().endsWith('/api/auth/runner-enrollments')
    && response.request().method() === 'POST');
  await enrollment.getByTestId('run-host-create').click();
  const issued = await (await created).json() as { enrollmentCode: string; scopes: string[]; projects: string[] };
  expect(issued.scopes).toEqual(['runner.deploy']);
  expect(issued.projects).toEqual([boundProject]);
  const command = enrollment.getByTestId('run-host-command');
  await expect(command).toContainText(`agent-host --enroll ${new URL(page.url()).origin} ${issued.enrollmentCode}`);
  await expect(command).toContainText('"$RUNNER_ID"');
  await expect(command).toContainText('agent-runner-deploy.service');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await wizard.screenshot({ path: join(resultDir, `wizard-run-host-command-1280-${theme}.png`) });
  }
  await enrollment.getByTestId('run-host-done').click();
  await expect(wizard).toHaveCount(0);

  // The host enrolls with the code and makes its first deploy claim.
  const host = await playwrightRequest.newContext({ baseURL: process.env.PW_BASE_URL, ignoreHTTPSErrors: true });
  try {
    const enrolled = await host.post('/api/auth/runner-enroll', { data: { code: issued.enrollmentCode } });
    expect(enrolled.ok(), await enrolled.text()).toBeTruthy();
    const identity = await enrolled.json() as { runnerId: string; secret: string; scopes: string[] };
    expect(identity.scopes).toEqual(['runner.deploy']);
    const claim = await host.post('/api/deploy/jobs/claim', {
      headers: { Authorization: `Bearer ${identity.secret}` }, data: { hostId: identity.runnerId } });
    expect(claim.ok(), `${claim.status()}: ${await claim.text()}`).toBeTruthy();

    // Hosts lists the run host online, with its last claim time and no "Not enrolled" tag.
    await page.goto('/#/workspace/settings/remote-hosts');
    const role = page.getByTestId('remote-host-role-row').filter({ hasText: 'run-host-1' });
    await expect(role).toBeVisible({ timeout: 30_000 });
    await expect(role.getByTestId('remote-host-role-label')).toContainText('Run host');
    await expect(role).toHaveAttribute('data-status', 'online');
    const machine = page.getByTestId('remote-host-primary-row').filter({ hasText: 'run-host-1' });
    await expect(machine.getByTestId('remote-host-not-enrolled')).toHaveCount(0);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await page.getByTestId('remote-hosts-table').screenshot({ path: join(resultDir, `hosts-run-host-online-1280-${theme}.png`) });
    }

    const listed = await page.request.get('/api/deploy/run-hosts');
    expect(listed.ok(), await listed.text()).toBeTruthy();
    expect(await listed.json()).toEqual([expect.objectContaining({
      id: identity.runnerId, name: 'run-host-1', state: 'reachable', projects: [boundProject],
    })]);
  } finally {
    await host.dispose();
  }
});
