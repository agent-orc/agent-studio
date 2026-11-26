import { spawn } from 'node:child_process';
import { createHmac } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { test, expect, request as playwrightRequest, type Page } from '@playwright/test';
import { setTheme } from '../helpers/theme';

/**
 * ES-3 (Dossier operator-elevated-session, decision ES-D2 option S2) on the
 * networked-https stack: `studio-op login` starts a terminal sign-in without
 * a credential and prints its code, the Owner signs in through the link and
 * confirms on Confirm terminal sign-in with a fresh authenticator code, the
 * terminal writes the session file with mode 600 without printing it, and
 * `studio-op auth status` reports the session. Settings > Access then shows
 * the device label, key thumbprint and address at confirmation.
 */
const repository = resolve(__dirname, '..', '..', '..');
const bin = join(repository, 'tools', 'studio-op', 'studio-op.mjs');
const resultDir = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'terminal-sign-in');
const password = ['terminal', 'sign', 'in', 'passphrase'].join('-');

function base32(secret: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const char of secret.replace(/=+$/, '').toUpperCase()) bits += alphabet.indexOf(char).toString(2).padStart(5, '0');
  const bytes: number[] = [];
  for (let index = 0; index + 8 <= bits.length; index += 8) bytes.push(parseInt(bits.slice(index, index + 8), 2));
  return Buffer.from(bytes);
}

/** RFC 6238 code, the same algorithm the authenticator app uses. */
function totp(secret: string, at = Date.now()): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 30_000)));
  const digest = createHmac('sha1', base32(secret)).update(counter).digest();
  const offset = digest[digest.length - 1] & 0x0f;
  return String((digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, '0');
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any -- the CLI's JSON document is checked field by field
type CliJson = any;
interface Run { code: number | null; stdout: string; stderr: string; json: CliJson }

function studioOp(args: string[], home: string): { done: Promise<Run>; stderr: () => string } {
  // Only what the CLI needs, plus trust for the stack's self-signed certificate.
  const env: Record<string, string> = { PATH: process.env.PATH ?? '', HOME: home, USERPROFILE: home,
    NODE_EXTRA_CA_CERTS: process.env.PW_E2E_TLS_CERT ?? '' };
  for (const name of ['TPM2OPENSSL_TCTI', 'OPENSSL_MODULES', 'LD_LIBRARY_PATH']) {
    if (process.env[name]) env[name] = process.env[name]!;
  }
  const child = spawn(process.execPath, [bin, ...args], { env, windowsHide: true });
  let stdout = '';
  let stderr = '';
  child.stdout.on('data', chunk => { stdout += chunk; });
  child.stderr.on('data', chunk => { stderr += chunk; });
  const done = new Promise<Run>(settle => child.on('close', code => {
    let json: CliJson = null;
    try { json = JSON.parse(stdout); } catch { /* not a JSON document */ }
    settle({ code, stdout, stderr, json });
  }));
  return { done, stderr: () => stderr };
}

async function until<T>(read: () => T | null | undefined, label: string, timeoutMs = 30_000): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = read();
    if (value) return value;
    if (Date.now() > deadline) throw new Error(`Timed out waiting for ${label}`);
    await new Promise(settle => setTimeout(settle, 200));
  }
}

/** The page scrolls inside its own overlay, so each capture uses a viewport tall enough for the whole form. */
async function capture(page: Page, name: string): Promise<void> {
  const panel = page.getByTestId('terminal-sign-in');
  const viewport = page.viewportSize()!;
  await page.setViewportSize({ width: 1280, height: 1100 });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
    await panel.screenshot({ path: join(resultDir, `${name}-${theme}--real.png`), animations: 'disabled' });
  }
  await page.setViewportSize({ width: 390, height: 1500 });
  await setTheme(page, 'light');
  await page.screenshot({ path: join(resultDir, `${name}-390-light--real.png`), fullPage: true, animations: 'disabled' });
  await setTheme(page, 'dark');
  await page.screenshot({ path: join(resultDir, `${name}-390-dark--real.png`), fullPage: true, animations: 'disabled' });
  await page.setViewportSize(viewport);
  await setTheme(page, 'light');
}

// One fresh stack per run: a retry would meet an Owner that already exists.
test.describe.configure({ mode: 'serial', retries: 0 });

test('studio-op login yields a session only after the Owner confirms with a fresh second factor', async ({ page }) => {
  test.setTimeout(240_000);
  const baseURL = process.env.PW_BASE_URL!;
  const workspace = process.env.PW_E2E_WORKSPACE!;
  expect(workspace, 'run through npm run e2e so the networked-https stack is isolated').toBeTruthy();
  expect(process.env.PW_E2E_TLS_CERT, 'the networked-https profile serves TLS with a test certificate').toBeTruthy();
  mkdirSync(resultDir, { recursive: true });

  // The Owner: bootstrap, enroll an authenticator, create the operator agent.
  const anonymous = await playwrightRequest.newContext({ baseURL, ignoreHTTPSErrors: true, timeout: 30_000 });
  const bootstrap = await anonymous.post('/api/auth/bootstrap', { headers: { 'Sec-Fetch-Site': 'same-origin' },
    data: { username: 'studio-owner', displayName: 'Owner', password, bootstrapCode: `${basename(workspace)}-owner-code` } });
  expect(bootstrap.ok(), `${bootstrap.status()}: ${await bootstrap.text()}`).toBeTruthy();
  const session = bootstrap.headers()['set-cookie']?.match(/__Host-agentstudio-session=([^;,\s]+)/)?.[1];
  const csrf = (await bootstrap.json()).csrfToken as string;
  const owner = await playwrightRequest.newContext({ baseURL, ignoreHTTPSErrors: true, timeout: 30_000, extraHTTPHeaders: {
    Cookie: `__Host-agentstudio-session=${session}`, 'X-CSRF-Token': csrf, 'Sec-Fetch-Site': 'same-origin' } });
  const factor = await owner.post('/api/auth/totp/start');
  expect(factor.ok(), await factor.text()).toBeTruthy();
  const secret = (await factor.json()).secret as string;
  const enrolledStep = Math.floor(Date.now() / 30_000);
  const confirmFactor = await owner.post('/api/auth/totp/confirm', { data: { code: totp(secret) } });
  expect(confirmFactor.ok(), await confirmFactor.text()).toBeTruthy();
  const recoveryCode = ((await confirmFactor.json()).recoveryCodes as string[])[0];
  const agent = await owner.post('/api/auth/iam/operator-agents', { data: { name: 'ops console',
    bindings: [{ role: 'operator-agent', scope: { kind: 'workspace' } }] } });
  expect(agent.ok(), await agent.text()).toBeTruthy();
  const agentId = (await agent.json()).id as string;

  // The terminal: an agent user's home with only the Studio URL configured.
  const home = mkdtempSync(join(tmpdir(), 'terminal-sign-in-home-'));
  const folder = join(home, '.config', 'agent-studio');
  mkdirSync(folder, { recursive: true, mode: 0o700 });
  writeFileSync(join(folder, 'studio-op.json'), JSON.stringify({ url: baseURL }), { mode: 0o600 });
  const login = studioOp(['login', '--agent', 'ops console', '--label', 'opsagent@e2e-workstation',
    '--class', 'cards.accept-override', '--class', 'project.testing', '--json'], home);
  const code = await until(() => login.stderr().match(/Code: +([A-Z]{4}-[A-Z]{4})/)?.[1], 'the sign-in code');
  const link = await until(() => login.stderr().match(/Open: +(\S+)/)?.[1], 'the confirmation link');
  expect(link).toBe(`${baseURL}/#/terminal-sign-in?code=${code.replace('-', '')}`);
  expect(login.stderr()).toMatch(/[▀▄█]{20,}/);
  writeFileSync(join(resultDir, 'studio-op-login-terminal.txt'), login.stderr());

  // The Owner opens the link in a browser that is not signed in yet: sign-in first keeps the link.
  await page.goto(link);
  const gate = page.getByTestId('auth-gate');
  await expect(gate.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await gate.getByLabel('Username').fill('studio-owner');
  await gate.getByLabel('Password').fill(password);
  await gate.getByLabel('Authenticator or recovery code').fill(recoveryCode);
  await gate.getByRole('button', { name: 'Sign in', exact: true }).click();

  const panel = page.getByTestId('terminal-sign-in');
  await expect(panel.getByRole('heading', { name: 'Confirm terminal sign-in' })).toBeVisible();
  await expect(panel.getByTestId('terminal-sign-in-code')).toHaveText(code);
  await expect(panel.getByTestId('terminal-sign-in-device')).toHaveText('opsagent@e2e-workstation');
  await expect(panel.getByTestId('terminal-sign-in-known')).toContainText('Unknown device');
  const bindingLabel = login.stderr().includes('Device binding: tpm (') ? 'TPM key (not yet verified on real hardware)'
    : login.stderr().includes('Device binding: secure-enclave (') ? 'Secure Enclave key (not yet verified on real hardware)' : 'Memory key';
  await expect(panel.getByTestId('terminal-sign-in-binding')).toHaveText(bindingLabel);
  await expect(panel.getByTestId('terminal-sign-in-address-match')).toContainText('Same network as this browser');
  await expect(panel.getByTestId('terminal-sign-in-agent')).toHaveValue(agentId);
  await expect(panel.getByTestId('terminal-sign-in-lifetime')).toHaveValue('24');
  await expect(panel.getByTestId('terminal-sign-in-lifetime').locator('option')).toHaveText(['8 hours', '24 hours']);
  await expect(panel.getByTestId('terminal-sign-in-class-project.testing')).toBeChecked();
  await expect(panel.getByTestId('terminal-sign-in-confirm')).toBeDisabled();
  await capture(page, 'confirm-terminal-sign-in');

  // Confirming is a change: like every change it waits for the live-update connection after sign-in.
  await expect(page.getByTestId('offline-banner')).toHaveCount(0, { timeout: 30_000 });
  // Without the tick nothing can be confirmed; without a fresh factor the server refuses.
  await panel.getByTestId('terminal-sign-in-code-match').check();
  await panel.getByTestId('terminal-sign-in-class-project.testing').uncheck();
  await panel.getByTestId('terminal-sign-in-lifetime').selectOption('8');
  await page.route('**/api/auth/step-up', route => route.fulfill({ status: 204 }), { times: 1 });
  await panel.getByTestId('terminal-sign-in-factor').fill('000000');
  await panel.getByTestId('terminal-sign-in-confirm').click();
  await expect(panel.getByTestId('terminal-sign-in-error')).toHaveAttribute('data-code', 'step-up-required');

  // A fresh authenticator step, then the real confirmation.
  await until(() => Math.floor(Date.now() / 30_000) > enrolledStep, 'a fresh authenticator step', 35_000);
  await panel.getByTestId('terminal-sign-in-factor').fill(totp(secret));
  await panel.getByTestId('terminal-sign-in-confirm').click();
  await expect(panel.getByTestId('terminal-sign-in-confirmed')).toContainText(`operator-agent:${agentId}/session:`);
  await panel.screenshot({ path: join(resultDir, 'confirmed-light--real.png'), animations: 'disabled' });

  const result = await login.done;
  expect(result.code, result.stderr).toBe(0);
  expect(result.json.ok).toBe(true);
  expect(result.json.classes).toEqual(['cards.accept-override']);
  const sessionFile = join(folder, 'operator-session');
  const token = readFileSync(sessionFile, 'utf8').trim();
  expect(token).toMatch(/^oas\.[0-9a-f]{32}\./);
  expect(statSync(sessionFile).mode & 0o777).toBe(0o600);
  expect(result.stdout).not.toContain(token);
  expect(result.stderr).not.toContain(token);

  const status = await studioOp(['auth', 'status', '--json'], home).done;
  expect(status.code, status.stderr).toBe(0);
  expect(status.json.credential).toMatchObject({ name: 'operator-session', kind: 'operator-session', prefix: 'oas', mode: '600' });
  expect(status.json.server.authenticated).toBe(true);
  expect(status.stdout).not.toContain(token);
  writeFileSync(join(resultDir, 'studio-op-auth-status.json'), status.stdout);

  // Settings > Access: the session row names the device, its key thumbprint and the address at confirmation.
  await panel.getByTestId('terminal-sign-in-open-access').click();
  await expect(panel).toHaveCount(0);
  const sessionId = status.json.credential.id as string;
  const device = page.getByTestId(`access-operator-session-device-${sessionId}`);
  await expect(device).toContainText('opsagent@e2e-workstation');
  await expect(device).toContainText(bindingLabel);
  await expect(page.getByTestId(`access-operator-session-thumbprint-${sessionId}`)).toHaveAttribute('title', /^[A-Za-z0-9_-]{43}$/);
  await expect(page.getByTestId(`access-operator-session-address-${sessionId}`)).toContainText('at confirmation');
  await expect(page.getByTestId(`access-operator-devices-${agentId}`)).toContainText('opsagent@e2e-workstation');
  await page.getByTestId(`access-operator-agent-${agentId}`).scrollIntoViewIfNeeded();
  // The Settings path alone retains an unbound bearer.
  const legacy = await owner.post(`/api/auth/iam/operator-agents/${agentId}/sessions`, { data: { name: 'Settings compatibility', lifetimeHours: 1 } });
  expect(legacy.ok()).toBe(true);
  const legacyBody = await legacy.json();
  await page.reload();
  const unbound = page.getByTestId(`access-operator-session-device-${legacyBody.sessionId}`);
  await expect(unbound).toContainText('Unbound');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.getByTestId(`access-operator-agent-${agentId}`).screenshot({ path: join(resultDir, `settings-access-session-row-${theme}--real.png`) });
  }
  const copied = await playwrightRequest.newContext({ baseURL: baseURL!, ignoreHTTPSErrors: true, extraHTTPHeaders: { Authorization: `Bearer ${token}` } });
  for (const route of ['/api/operator/decisions', '/api/tasks', '/api/security/posture', '/api/auth/me']) {
    const refused = await copied.get(route);
    expect(refused.status()).toBe(401);
    expect((await refused.json()).error).toBe('dpop-required');
  }
  await copied.dispose();
  // End the session so its memory-only resident process exits too.
  expect((await owner.delete(`/api/auth/iam/operator-agents/${agentId}/sessions/${sessionId}`)).ok()).toBe(true);
  const ended = await studioOp(['auth', 'status', '--json'], home).done;
  expect(ended.json.server.status).toBe(401);
  expect(ended.json.server.authenticated).toBe(false);
  rmSync(home, { recursive: true, force: true });

  // The code worked once: confirming it again is refused.
  const again = await owner.post('/api/auth/iam/operator-sign-ins/lookup', { data: { code } });
  expect(again.status()).toBe(409);
  expect((await again.json()).error).toBe('signin-code-used');
});
