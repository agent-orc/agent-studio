import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { test, expect } from '../fixtures/dev-backend';

test.use({ recoveryRepository: true, trace: 'off' });

test('first-run GitHub proof rejects a checkout URL rewrite to a scratch repository', async ({ page }) => {
  test.setTimeout(180_000);
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace).toBeTruthy();
  const repositoryPath = join(workspace!, 'recovery-repository');
  expect(repositoryPath.startsWith(tmpdir())).toBeTruthy();
  writeFileSync(join(workspace!, 'gitconfig'), '');
  const projects = await page.request.get('/api/projects');
  expect(projects.ok()).toBeTruthy();
  const entries = await projects.json() as { id: string; archived: boolean; repositoryPath: string }[];
  const existing = entries.find(entry => !entry.archived);
  const prepared = existing
    ? await page.request.put(`/api/projects/${encodeURIComponent(existing.id)}`, {
      headers: { 'X-Client-Id': 'local-default' }, data: { repositoryPath },
    })
    : await page.request.post('/api/projects', { headers: { 'X-Client-Id': 'local-default' }, data: {
      workspaceId: 'ws-default', displayName: 'First Run Proof', repositoryPath,
    } });
  expect(prepared.ok(), `${prepared.status()}: ${await prepared.text()}`).toBeTruthy();
  const registered = await page.request.get('/api/projects');
  const project = (await registered.json() as { id: string; archived: boolean; repositoryPath: string }[])
    .find(entry => !entry.archived && entry.repositoryPath === repositoryPath);
  expect(project).toBeTruthy();
  const bare = join(workspace!, 'first-run-bare.git');
  const origin = 'https://github.com/example/first-run-proof.git';
  const git = (cwd: string, ...args: string[]) => {
    const result = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true });
    expect(result.status, `git ${args[0]} failed: ${result.stderr}`).toBe(0);
  };
  git(workspace!, 'init', '--bare', bare);
  const currentOrigin = spawnSync('git', ['remote', 'get-url', 'origin'], {
    cwd: repositoryPath, encoding: 'utf8', windowsHide: true,
  });
  git(repositoryPath, 'remote', currentOrigin.status === 0 ? 'set-url' : 'add', 'origin', origin);
  git(repositoryPath, 'config', '--replace-all', `url.file://${bare}.insteadOf`, origin);
  await page.goto('/#/workspace/settings/first-run');
  const checklist = page.getByTestId('first-run-checklist');
  await expect(checklist).toBeVisible();
  await page.reload();
  await expect(checklist).toBeVisible();
  let registeredProjectId = project!.id;
  if (await page.getByTestId('first-run-repository-url').isVisible()) {
    const registration = page.waitForResponse(response => response.request().method() === 'PUT'
      && /\/api\/projects\/PROJ-\d+$/.test(new URL(response.url()).pathname), { timeout: 30_000 });
    await page.getByTestId('first-run-repository-url').fill(origin);
    await page.getByTestId('first-run-register-repository').click();
    const saved = await registration;
    expect(saved.ok(), `${saved.status()}: ${await saved.text()}`).toBeTruthy();
    const updated = await saved.json() as { id: string; repositoryUrl?: string; repositoryPath?: string };
    expect(updated.repositoryUrl).toBe(origin);
    expect(updated.repositoryPath).toBe(repositoryPath);
    registeredProjectId = updated.id;
  }
  await page.reload();
  await expect(checklist).toBeVisible();
  await expect(page.getByTestId('first-run-project')).toHaveAttribute('data-state', 'ready', { timeout: 60_000 });

  const auth = await page.request.get('/api/v1/studio/auth/status');
  const owner = page.getByTestId('first-run-owner');
  if (auth.ok()) {
    const response = await auth.json() as { authenticated: boolean; bootstrapRequired: boolean };
    await expect(owner).toHaveAttribute('data-state',
      response.authenticated && !response.bootstrapRequired ? 'ready' : 'pending');
  }

  const repository = await page.request.get(`/api/first-run/projects/${encodeURIComponent(registeredProjectId)}/repository`);
  expect(repository.ok()).toBeTruthy();
  const live = await repository.json() as { probes: { admitted: boolean }[] };
  expect(live.probes.some(probe => probe.admitted)).toBeFalsy();
  await page.getByTestId('first-run-git-method').selectOption('token');
  await page.getByTestId('first-run-git-token').fill(['test', 'x'.repeat(36)].join('-'));
  await page.getByTestId('first-run-git-start').click();
  await expect(page.getByTestId('first-run-git-state')).toContainText('awaiting-verify');
  await page.getByTestId('first-run-git-verify').click();
  await expect(page.getByTestId('first-run-git-state')).toContainText('denied', { timeout: 60_000 });
  await expect(page.getByTestId('first-run-git')).toHaveAttribute('data-state', 'pending');
  await page.getByTestId('first-run-git-method').selectOption('deploy-key');
  await page.getByTestId('first-run-git-start').click();
  await expect(page.getByTestId('first-run-deploy-key')).toContainText('ssh-ed25519');

  await page.getByTestId('first-run-refresh').click();
  await expect(checklist).toBeVisible();
  const results = process.env.JOB_RESULTS_DIR;
  if (results) {
    mkdirSync(results, { recursive: true });
    await page.screenshot({ path: join(results, 'first-run-checklist.png'), fullPage: true });
  }
});

test.describe('operator supplied first-run proof', () => {
  const origin = process.env.PW_E2E_FIRST_RUN_GITHUB_URL;
  const token = process.env.PW_E2E_FIRST_RUN_GITHUB_TOKEN;
  test.skip(!origin || !token, 'Requires an operator supplied disposable GitHub repository and test token.');
  test('clones a disposable GitHub repository and reaches integration', async ({ page }) => {
  test.setTimeout(16 * 60_000);
  expect(origin).toMatch(/^https:\/\/github\.com\/[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+\.git$/);
  const projects = await page.request.get('/api/projects');
  expect(projects.ok()).toBeTruthy();
  const existing = await projects.json() as { id: string; workspaceId: string; archived: boolean }[];
  for (const project of existing.filter(entry => !entry.archived)) {
    const archived = await page.request.put(`/api/projects/${encodeURIComponent(project.id)}`, {
      headers: { 'X-Client-Id': 'local-default' }, data: { archived: true },
    });
    expect(archived.ok()).toBeTruthy();
  }
  const registered = await page.request.post('/api/projects', {
    headers: { 'X-Client-Id': 'local-default' },
    data: { workspaceId: existing[0]?.workspaceId ?? 'ws-default',
      displayName: 'First Run Live Proof', repositoryUrl: origin },
  });
  expect(registered.ok(), `${registered.status()}: ${await registered.text()}`).toBeTruthy();

  await page.goto('/#/workspace/settings/first-run');
  await expect(page.getByTestId('first-run-owner')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-project')).toHaveAttribute('data-state', 'pending');
  await page.getByTestId('first-run-git-method').selectOption('token');
  await page.getByTestId('first-run-git-token').fill(token!);
  await page.getByTestId('first-run-git-start').click();
  await expect(page.getByTestId('first-run-git-state')).toContainText('awaiting-verify');
  await page.getByTestId('first-run-git-verify').click();
  await expect(page.getByTestId('first-run-git-state')).toContainText('verified', { timeout: 120_000 });
  await expect(page.getByTestId('first-run-project')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-git')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-providers')).toHaveAttribute('data-state', 'ready');
  await expect(page.getByTestId('first-run-host')).toHaveAttribute('data-state', 'ready');
  await page.getByTestId('first-run-demo-confirm').check();
  await page.getByTestId('first-run-create-proof').click();
  await expect(page.getByTestId('first-run-proof-id')).toBeVisible();
  await expect(async () => {
    await page.getByTestId('first-run-refresh').click();
    await expect(page.getByTestId('first-run-proof')).toHaveAttribute('data-state', 'ready');
  }).toPass({ timeout: 15 * 60_000, intervals: [5_000] });
  });
});
