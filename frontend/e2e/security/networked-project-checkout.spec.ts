import { createHmac, X509Certificate } from 'node:crypto';
import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { closeSync, existsSync, mkdirSync, openSync, readFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { test, expect, type Page } from '@playwright/test';
import { setTheme } from '../helpers/theme';

const results = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'networked-project-checkout');
const originUrl = 'https://github.com/agent-studio/first-run-e2e.git';

function totp(secret: string): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const char of secret.replace(/=+$/, ''))
    bits += alphabet.indexOf(char).toString(2).padStart(5, '0');
  const bytes: number[] = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30_000)));
  const digest = createHmac('sha1', Buffer.from(bytes)).update(counter).digest();
  const offset = digest[digest.length - 1] & 15;
  return String((digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, '0');
}

async function enroll(page: Page, workspace: string, role: 'coding' | 'review'): Promise<ChildProcess> {
  await page.goto('/#/workspace/settings/access');
  const access = page.getByTestId('access-area');
  await expect(access.getByTestId('access-runners')).toBeVisible();
  await access.getByTestId('access-enrollment-name').fill(`networked-${role}`);
  await access.getByTestId('access-enrollment-projects').getByLabel('All projects').check();
  const created = page.waitForResponse(response => response.url().endsWith('/api/auth/runner-enrollments')
    && response.request().method() === 'POST');
  await access.getByTestId('access-enrollment-create').click();
  const payload = await (await created).json() as { enrollmentCode: string };
  const code = payload.enrollmentCode;
  expect(code).toMatch(/^enr\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/);
  const issued = access.getByTestId('access-enrollment-code');
  await expect(issued).toContainText(code);

  const repoRoot = resolve(__dirname, '../../..');
  const dll = join(repoRoot, 'runner/bin/e2e-publish/agent-host.dll');
  const config = join(workspace, `${role}-enrollment`);
  const cert = join(workspace, 'localhost.crt');
  const env = { ...process.env, RUNNER_ENROLLMENT_CONFIG_DIR: config, SSL_CERT_FILE: cert };
  const joined = spawnSync('dotnet', [dll, '--enroll', process.env.PW_BASE_URL!, code], {
    env, encoding: 'utf8', timeout: 30_000,
  });
  expect(joined.status, joined.stderr).toBe(0);
  const installation = readFileSync(join(config, 'installation.env'), 'utf8');
  const runnerId = installation.match(/^RUNNER_ID=(.+)$/m)?.[1];
  expect(runnerId).toBeTruthy();

  const work = join(workspace, `${role}-work`);
  const state = join(workspace, `${role}-state`);
  mkdirSync(work, { recursive: true });
  mkdirSync(state, { recursive: true });
  const fingerprint = new X509Certificate(readFileSync(cert)).fingerprint256.replaceAll(':', '');
  const bin = join(workspace, 'bin');
  const log = openSync(join(workspace, `${role}-runner.log`), 'a', 0o600);
  const child = spawn('dotnet', [dll, '--poll'], {
    env: {
      ...process.env, PATH: `${bin}:${process.env.PATH}`, SSL_CERT_FILE: cert,
      GIT_CONFIG_GLOBAL: join(workspace, 'gitconfig'),
      CODEX_HOME: join(workspace, 'config/codex'),
      CLAUDE_CONFIG_DIR: join(workspace, 'config/claude'),
      RUNNER_SERVER_URL: process.env.PW_BASE_URL!, RUNNER_ID: runnerId!,
      RUNNER_NAME: `networked-${role}`, RUNNER_HOSTNAME: 'networked-e2e-host',
      RUNNER_ROLE: role, RUNNER_AUTH_TOKEN_FILE: join(config, 'runner.token'),
      RUNNER_TLS_CERTIFICATE_SHA256: fingerprint,
      RUNNER_CODEX_CLI_BIN: join(bin, role === 'review' ? 'review/codex' : 'codex'),
      RUNNER_CLAUDE_CLI_BIN: join(bin, role === 'review' ? 'review/claude' : 'claude'),
      RUNNER_GIT_REMOTE: originUrl, RUNNER_GIT_PUSH_REMOTE: originUrl,
      RUNNER_WORKDIR: work, RUNNER_REVIEW_WORKDIR: join(workspace, 'review-attempts'),
      RUNNER_STATE_DIR: state, RUNNER_POLL_SECONDS: '1', RUNNER_HEARTBEAT_SECONDS: '2',
      RUNNER_CLAIM_MAX_LOAD_PER_CORE: '100', RUNNER_MAX_PARALLELISM: '1',
      RUNNER_HOST_CODING_SLOTS: role === 'coding' ? '1' : '0',
      RUNNER_HOST_REVIEW_SLOTS: role === 'review' ? '1' : '0',
      AGENT_STUDIO_CLEAN_CONTEXT_ROOT: join(workspace, `${role}-clean-context`),
      ...(role === 'review' ? { LD_PRELOAD: join(workspace, 'review-cpu-max.so') } : {}),
    },
    stdio: ['ignore', log, log],
  });
  closeSync(log);
  expect(child.pid).toBeTruthy();
  return child;
}

test('URL project creates a server checkout and its first coding card integrates', async ({ page }) => {
  test.setTimeout(16 * 60_000);
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace).toBeTruthy();
  expect(existsSync(join(workspace!, 'first-run-origin.git'))).toBe(true);
  mkdirSync(results, { recursive: true });
  const children: ChildProcess[] = [];
  try {
    await page.goto('/');
    const gate = page.getByTestId('auth-gate');
    await gate.getByLabel('Username').fill('checkout-owner');
    await gate.getByLabel('Display name').fill('Checkout Owner');
    await gate.getByLabel('Installer owner code').fill(`${basename(workspace!)}-owner-code`);
    await gate.getByLabel('Password').fill('networked checkout proof passphrase');
    await gate.getByRole('button', { name: 'Create owner' }).click();
    await gate.getByRole('button', { name: 'Set up authenticator' }).click();
    const secret = (await gate.getByTestId('totp-secret').textContent())!.trim();
    await gate.getByLabel('Authenticator code').fill(totp(secret));
    await gate.getByRole('button', { name: 'Verify authenticator' }).click();
    await gate.getByRole('button', { name: 'I stored the codes' }).click();
    await expect(gate).toHaveCount(0);

    children.push(await enroll(page, workspace!, 'coding'));
    children.push(await enroll(page, workspace!, 'review'));
    await page.waitForTimeout(12_000);

    await page.goto('/');
    await page.getByTestId('studio-workspace-ws-default-add-project').click();
    const dialog = page.getByTestId('onboard-project-dialog');
    await expect(dialog).toBeVisible();
    await dialog.getByTestId('onboard-project-display-name').fill('Networked checkout proof');
    await dialog.getByTestId('onboard-project-short-code').fill('NCP');
    await dialog.getByTestId('onboard-project-repository-url').fill(originUrl);
    await expect(dialog).toContainText('Studio clones this URL on the server');
    const runner = dialog.getByTestId('onboard-project-runner');
    await expect(runner.locator('option', { hasText: 'networked-coding' })).toBeAttached({ timeout: 60_000 });
    await runner.selectOption({ label: 'networked-coding' });
    await expect(dialog.getByTestId('onboard-project-submit')).toBeEnabled();
    await dialog.getByTestId('onboard-project-submit').click();
    await expect(dialog).toHaveCount(0, { timeout: 60_000 });
    await page.reload();

    await page.goto('/#/projects/networked-checkout-proof/settings');
    const basics = page.getByTestId('project-basics-card');
    await expect(basics.getByTestId('project-basics-repository-path'))
      .toHaveValue(/repository-checkouts\/PROJ-\d+/, { timeout: 30_000 });
    const checkout = await basics.getByTestId('project-basics-repository-path').inputValue();
    expect(existsSync(join(checkout, '.git'))).toBe(true);
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await basics.screenshot({ path: join(results, `project-checkout-${theme}.png`) });
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await basics.screenshot({ path: join(results, 'project-checkout-390.png') });
    await page.setViewportSize({ width: 1440, height: 900 });

    await page.getByRole('tab', { name: 'All projects · Board' }).click();
    await expect(page.getByTestId('studio-board-add-task')).toBeVisible();
    await page.getByTestId('studio-board-add-task').click();
    await page.getByTestId('create-title').fill('Networked checkout proof card');
    await page.getByTestId('create-project-select').selectOption({ label: 'Networked checkout proof' });
    await page.getByTestId('create-lane-2-ready').click();
    await page.getByTestId('create-agent').click();
    await page.getByTestId('create-agent-picker-cli-claude').click();
    await page.getByTestId('create-agent-picker-model-claude-opus-5').click();
    await expect(page.getByTestId('create-agent')).toContainText('opus 5');
    await page.getByTestId('create-prompt').fill('Create docs/project-overview.md with one sentence describing this demo repository.');
    await page.getByTestId('create-submit').click();
    await expect(page.getByTestId('create-submit')).toHaveCount(0);
    await page.getByText('Networked checkout proof card').first().click();
    await page.getByTestId('studio-triage-action-run-now').click();

    const bare = join(workspace!, 'first-run-origin.git');
    await expect.poll(() => {
      const read = spawnSync('/usr/bin/git', ['--git-dir', bare, 'show', 'main:docs/project-overview.md'],
        { encoding: 'utf8' });
      return read.status === 0 && read.stdout.includes('This small repository verifies the Studio setup journey.');
    }, { timeout: 12 * 60_000, intervals: [5_000] }).toBe(true);
    await page.screenshot({ path: join(results, 'first-card-integrated.png'), fullPage: true });
  } finally {
    for (const child of children) child.kill('SIGTERM');
  }
});
