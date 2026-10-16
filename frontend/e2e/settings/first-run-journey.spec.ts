import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '@playwright/test';

const ownerPassword = randomBytes(24).toString('base64url') + 'Aa1!';

test('fresh project reaches an integrated proof card with live Git and runner probes', async ({ page }) => {
  test.setTimeout(16 * 60_000);
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace).toBeTruthy();
  const auth = await page.request.get('/api/auth/status');
  expect(auth.ok()).toBeTruthy();
  const current = await auth.json() as { bootstrapRequired: boolean };
  const signedIn = await page.request.post(current.bootstrapRequired
    ? '/api/auth/bootstrap' : '/api/auth/login', {
    headers: { 'X-Client-Id': 'local-default' },
    data: current.bootstrapRequired
      ? { username: 'first-run-owner', displayName: 'First Run Owner', password: ownerPassword }
      : { username: 'first-run-owner', password: ownerPassword },
  });
  expect(signedIn.ok(), `${signedIn.status()}: ${await signedIn.text()}`).toBeTruthy();
  const origin = 'https://github.com/agent-studio/first-run-e2e.git';
  await expect.poll(async () => {
    const clients = await page.request.get('/api/clients');
    if (!clients.ok()) return false;
    const entries = await clients.json() as { id: string }[];
    return entries.some(entry => entry.id === 'first-run-coding');
  }, { timeout: 120_000 }).toBe(true);
  const projects = await page.request.get('/api/projects');
  expect(projects.ok()).toBeTruthy();
  const existing = await projects.json() as { id: string; workspaceId: string; archived: boolean }[];
  for (const project of existing.filter(entry => !entry.archived)) {
    const archived = await page.request.put(`/api/projects/${encodeURIComponent(project.id)}`, {
      headers: { 'X-Client-Id': 'local-default' }, data: { archived: true },
    });
    expect(archived.ok()).toBeTruthy();
  }
  const projectName = `First Run Journey ${randomBytes(4).toString('hex')}`;
  const registered = await page.request.post('/api/projects', {
    headers: { 'X-Client-Id': 'local-default' },
    data: { workspaceId: existing[0]?.workspaceId ?? 'ws-default',
      displayName: projectName, repositoryUrl: origin, executionRunner: 'first-run-coding' },
  });
  expect(registered.ok(), `${registered.status()}: ${await registered.text()}`).toBeTruthy();
  const project = await registered.json() as { id: string; repositoryPath?: string };
  expect(project.repositoryPath).toBeFalsy();

  await page.goto('/#/workspace/settings/first-run');
  await expect(page.getByTestId('first-run-owner')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-project')).toHaveAttribute('data-state', 'pending');
  await expect(page.getByTestId('first-run-git-setup')).toBeVisible();
  await page.getByTestId('first-run-git-method').selectOption('token');
  await page.getByTestId('first-run-git-token').fill(['test', 'x'.repeat(36)].join('-'));
  await page.getByTestId('first-run-git-start').click();
  await expect(page.getByTestId('first-run-git-state')).toContainText('awaiting-verify');
  await page.getByTestId('first-run-git-verify').click();
  await expect(page.getByTestId('first-run-git-state')).toContainText('verified', { timeout: 120_000 });
  await expect(page.getByTestId('first-run-project')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-git')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-providers')).toHaveAttribute('data-state', 'ready', { timeout: 120_000 });
  await expect(page.getByTestId('first-run-host')).toHaveAttribute('data-state', 'ready');
  const results = process.env.JOB_RESULTS_DIR;
  if (results) {
    mkdirSync(results, { recursive: true });
    await page.screenshot({ path: join(results, 'first-run-journey-light.png'), fullPage: true });
    await page.evaluate(() => localStorage.setItem('atp.studio.theme', 'dark'));
    await page.reload();
    await expect(page.getByTestId('first-run-git')).toHaveAttribute('data-state', 'ready');
    await page.screenshot({ path: join(results, 'first-run-journey-dark.png'), fullPage: true });
  }

  const bare = join(workspace!, 'first-run-origin.git');
  const remote = spawnSync('/usr/bin/git', ['--git-dir', bare, 'for-each-ref',
    '--format=%(refname)', 'refs/heads/agent-studio-access-probe'],
    { encoding: 'utf8', windowsHide: true });
  expect(remote.status).toBe(0);
  expect(remote.stdout.trim()).toBe('');

  await page.getByTestId('first-run-demo-confirm').check();
  await page.getByTestId('first-run-create-proof').click();
  await expect(page.getByTestId('first-run-proof-id')).toBeVisible();
  await expect(async () => {
    await page.getByTestId('first-run-refresh').click();
    await expect(page.getByTestId('first-run-proof')).toHaveAttribute('data-state', 'ready');
  }).toPass({ timeout: 15 * 60_000, intervals: [5_000] });
  const integrated = spawnSync('/usr/bin/git', ['--git-dir', bare, 'show',
    'main:docs/project-overview.md'], { encoding: 'utf8', windowsHide: true });
  expect(integrated.status, integrated.stderr).toBe(0);
  expect(integrated.stdout).toContain('This small repository verifies the Studio setup journey.');
  if (results) {
    await page.screenshot({ path: join(results, 'first-run-journey-integrated.png'), fullPage: true });
  }
});
