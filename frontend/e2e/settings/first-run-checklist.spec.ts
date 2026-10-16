import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { test, expect } from '../fixtures/dev-backend';

test.use({ recoveryRepository: true });

test('first-run checklist reflects a real local checkout read and write proof', async ({ page }) => {
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
  if (await page.getByTestId('first-run-repository-url').isVisible()) {
    await page.getByTestId('first-run-repository-url').fill(origin);
    await page.getByTestId('first-run-register-repository').click();
  }
  await expect(page.getByTestId('first-run-project')).toHaveAttribute('data-state', 'ready');

  const auth = await page.request.get('/api/v1/studio/auth/status');
  const owner = page.getByTestId('first-run-owner');
  if (auth.ok()) {
    const response = await auth.json() as { authenticated: boolean; bootstrapRequired: boolean };
    await expect(owner).toHaveAttribute('data-state',
      response.authenticated && !response.bootstrapRequired ? 'ready' : 'pending');
  }

  const repository = await page.request.get(`/api/first-run/projects/${encodeURIComponent(project!.id)}/repository`);
  expect(repository.ok()).toBeTruthy();
  const live = await repository.json() as { probes: { admitted: boolean }[] };
  expect(live.probes.some(probe => probe.admitted)).toBeTruthy();
  await page.getByTestId('first-run-git-method').selectOption('token');
  await page.getByTestId('first-run-git-token').fill(['test', 'x'.repeat(36)].join('-'));
  await page.getByTestId('first-run-git-start').click();
  await expect(page.getByTestId('first-run-git-state')).toContainText('awaiting-verify');
  await page.getByTestId('first-run-git-verify').click();
  await expect(page.getByTestId('first-run-git-state')).toContainText('verified');
  await expect(page.getByTestId('first-run-git')).toHaveAttribute('data-state', 'ready');
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
